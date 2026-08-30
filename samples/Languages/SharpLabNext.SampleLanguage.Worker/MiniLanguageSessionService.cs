using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Sdk;

namespace SharpLabNext.SampleLanguage.Worker;

public sealed class MiniLanguageSessionService(MiniLanguageWorkerIdentity workerIdentity, LanguageWorkerCapabilityManifest manifest) : ILanguageWorkerSessionService
{
    private readonly ConcurrentDictionary<string, MiniLanguageSessionState> _sessions = new(StringComparer.Ordinal);

    public Task<LanguageSession> OpenAsync(OpenLanguageSessionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MiniLanguageBuildService.ValidateOutputKind(request.Workspace.BuildOptions.OutputKind);
        var sessionId = $"mini_{Guid.NewGuid():N}";
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
        var session = new LanguageSession(sessionId, MiniLanguageCompiler.LanguageId, MiniLanguageCompiler.ToolchainId, $"{workerIdentity.ToolchainId}/{workerIdentity.CompilerVersion}", ContractSchemaVersions.Lsp, request.Workspace.Revision, request.Workspace.SelectionRevision, expiresAt);
        var state = new MiniLanguageSessionState(session, request.Workspace);
        if (!_sessions.TryAdd(sessionId, state))
            throw new InvalidOperationException("A unique MiniLang session ID could not be allocated.");
        return Task.FromResult(session);
    }

    public Task<bool> CloseAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryRemove(sessionId, out var state))
            return Task.FromResult(false);
        state.Dispose();
        return Task.FromResult(true);
    }

    public async Task RunAsync(string sessionId, WebSocket socket, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
        {
            throw new LanguageWorkerRequestException("session-not-found", "The MiniLang language session does not exist.", StatusCodes.Status404NotFound);
        }
        if (!state.TryAttach())
            throw new LanguageWorkerRequestException("session-in-use", "The MiniLang language session already has an LSP connection.", StatusCodes.Status409Conflict);

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, state.CancellationToken);
            var connection = new MiniLanguageLspConnection(socket, state, manifest.Limits.MaximumLspMessageBytes);
            await connection.RunAsync(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            state.Detach();
        }
    }
}

internal sealed class MiniLanguageSessionState : IDisposable
{
    private readonly CancellationTokenSource _closed = new();
    private int _attached;

    public MiniLanguageSessionState(LanguageSession session, WorkspaceSnapshot workspace)
    {
        Session = session;
        Workspace = workspace;
    }

    public LanguageSession Session { get; }

    public WorkspaceSnapshot Workspace { get; }

    public CancellationToken CancellationToken => _closed.Token;

    public bool TryAttach() => Interlocked.CompareExchange(ref _attached, 1, 0) == 0;

    public void Detach() => Volatile.Write(ref _attached, 0);

    public void Dispose()
    {
        _closed.Cancel();
        _closed.Dispose();
    }
}
