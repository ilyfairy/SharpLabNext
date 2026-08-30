using System.Collections.Concurrent;
using System.Reflection;
using EleCho.ILSense;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.IL;

public sealed class IlLanguageSessionManager(IlReferenceSetProvider referenceSets, IlLanguageService languageService, IlWorkerSettings settings) : IDisposable
{
    private static readonly string IlSenseVersion = ResolveIlSenseVersion();
    private readonly ConcurrentDictionary<string, IlLanguageSession> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    public async Task<LanguageSession> OpenAsync(OpenLanguageSessionRequest request, CancellationToken cancellationToken)
    {
        if (!StringComparer.Ordinal.Equals(request.LspVersion, ContractSchemaVersions.Lsp))
            throw new IlLspInvalidParamsException($"LSP version '{request.LspVersion}' is not supported.");
        var validated = IlWorkspaceValidator.Validate(new BuildRequest(request.RequestId, request.RequestId, request.PipelineResolutionId, request.ToolchainId, request.ReferenceSetId, request.Workspace, DateTimeOffset.UtcNow.AddMilliseconds(settings.CompilationLimits.MaxBuildMilliseconds), request.Workspace.BuildOptions, BuildTarget.CompileCheck), settings.CompilationLimits);
        if (!StringComparer.Ordinal.Equals(request.LanguageId, validated.Snapshot.LanguageId))
            throw new IlLspInvalidParamsException("Session and workspace language IDs must match.");
        var metadataCatalog = await referenceSets.GetCatalogAsync(request.ReferenceSetId, cancellationToken).ConfigureAwait(false);
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RemoveExpired();
            if (_sessions.Count >= settings.LspLimits.MaxSessions)
                throw new IlLspLimitExceededException("The worker has reached its IL language-session limit.");
            var sessionId = $"lsp_{Guid.NewGuid():N}";
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(settings.LspLimits.SessionTtlMinutes);
            var session = new IlLanguageSession(sessionId, validated, languageService, metadataCatalog, settings.CompilationLimits, settings.LspLimits, request.ReferenceSetId, expiresAt);
            if (!_sessions.TryAdd(sessionId, session))
                throw new InvalidOperationException("A unique IL language-session ID could not be allocated.");
            return new LanguageSession(sessionId, "il", settings.Identity.ToolchainId, $"Mobius.ILasm/{settings.Identity.CompilerVersion}+EleCho.ILSense/{IlSenseVersion}", ContractSchemaVersions.Lsp, validated.Snapshot.Revision, validated.Snapshot.SelectionRevision, expiresAt);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public bool TryGet(string sessionId, out IlLanguageSession? session)
    {
        if (_sessions.TryGetValue(sessionId, out session) && !session.IsExpired)
            return true;
        session = null;
        return false;
    }

    public bool Close(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
            return false;
        session.Dispose();
        return true;
    }

    public void Dispose()
    {
        foreach (var id in _sessions.Keys)
            Close(id);
        _lifecycleLock.Dispose();
    }

    private void RemoveExpired()
    {
        foreach (var pair in _sessions)
        {
            if (pair.Value.IsExpired && _sessions.TryRemove(pair.Key, out var expired))
                expired.Dispose();
        }
    }

    private static string ResolveIlSenseVersion()
    {
        var assembly = typeof(IILLanguageEngine).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var metadataSeparator = informationalVersion.IndexOf('+', StringComparison.Ordinal);
            return metadataSeparator < 0
                ? informationalVersion : informationalVersion[..metadataSeparator];
        }

        return assembly.GetName().Version?.ToString(3) ?? "unknown";
    }
}
