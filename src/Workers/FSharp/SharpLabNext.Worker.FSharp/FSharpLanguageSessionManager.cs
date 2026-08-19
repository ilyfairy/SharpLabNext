using System.Collections.Concurrent;
using SharpLabNext.Contracts;
using SharpLabNext.Worker.FSharp.Compiler;

namespace SharpLabNext.Worker.FSharp;

public sealed class FSharpLanguageSessionManager(
    FSharpReferenceSetProvider referenceSets,
    FSharpCompilerFacade compiler,
    FSharpWorkerSettings settings) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, FSharpLanguageSession> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    public async Task<LanguageSession> OpenAsync(
        OpenLanguageSessionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.LspVersion != ContractSchemaVersions.Lsp)
            throw new FSharpLspInvalidParamsException($"LSP version '{request.LspVersion}' is not supported.");
        var validated = FSharpWorkspaceValidator.Validate(
            new BuildRequest(
                request.RequestId,
                request.RequestId,
                request.PipelineResolutionId,
                request.ToolchainId,
                request.ReferenceSetId,
                request.Workspace,
                DateTimeOffset.UtcNow.AddMilliseconds(settings.CompilationLimits.MaxBuildMilliseconds),
                request.Workspace.BuildOptions,
                BuildTarget.CompileCheck),
            settings.CompilationLimits);
        if (request.LanguageId != validated.Snapshot.LanguageId)
            throw new FSharpLspInvalidParamsException("Session and workspace language IDs must match.");
        var references = await referenceSets.GetAsync(request.ReferenceSetId, cancellationToken).ConfigureAwait(false);

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RemoveExpiredAsync();
            if (_sessions.Count >= settings.LspLimits.MaxSessions)
                throw new FSharpLspLimitExceededException("The worker has reached its language session limit.");
            var sessionId = $"lsp_{Guid.NewGuid():N}";
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(settings.LspLimits.SessionTtlMinutes);
            var temporary = await TemporaryFSharpWorkspace.CreateAsync(
                Path.Combine(settings.WorkRoot, "sessions"),
                validated.OrderedFiles,
                cancellationToken);
            try
            {
                var sourcePaths = validated.OrderedFiles.Select(file => temporary.Paths[file.Path]).ToArray();
                var projectInput = FSharpBuildService.CreateProjectInput(
                    temporary.Root,
                    sourcePaths,
                    references,
                    validated.Options,
                    validated.Snapshot.Revision);
                foreach (var file in validated.OrderedFiles)
                {
                    var rejected = await FSharpSourceSafety.FindRejectedDirectiveAsync(
                        compiler,
                        projectInput,
                        temporary.Paths[file.Path],
                        file.Text,
                        cancellationToken).ConfigureAwait(false);
                    if (rejected is not null)
                    {
                        throw new FSharpLspInvalidParamsException(
                            $"F# directive '#{rejected}' is not allowed in managed workspaces.");
                    }
                }
            }
            catch
            {
                await temporary.DisposeAsync();
                throw;
            }
            var session = new FSharpLanguageSession(
                sessionId,
                validated,
                references,
                temporary,
                compiler,
                settings.CompilationLimits,
                settings.LspLimits,
                expiresAt);
            if (!_sessions.TryAdd(sessionId, session))
            {
                await session.DisposeAsync();
                throw new InvalidOperationException("A unique F# language session ID could not be allocated.");
            }
            return new LanguageSession(
                sessionId,
                "fsharp",
                settings.Identity.ToolchainId,
                $"{settings.Identity.ToolchainId}/{settings.Identity.CompilerVersion}+fsharp-core/{settings.Identity.FSharpCorePackageVersion}",
                ContractSchemaVersions.Lsp,
                validated.Snapshot.Revision,
                validated.Snapshot.SelectionRevision,
                expiresAt);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public bool TryGet(string sessionId, out FSharpLanguageSession? session)
    {
        if (_sessions.TryGetValue(sessionId, out session) && !session.IsExpired)
            return true;
        session = null;
        return false;
    }

    public async Task<bool> CloseAsync(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
            return false;
        await session.DisposeAsync();
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys)
            await CloseAsync(id);
        _lifecycleLock.Dispose();
    }

    private async Task RemoveExpiredAsync()
    {
        foreach (var pair in _sessions)
        {
            if (pair.Value.IsExpired && _sessions.TryRemove(pair.Key, out var expired))
                await expired.DisposeAsync();
        }
    }
}
