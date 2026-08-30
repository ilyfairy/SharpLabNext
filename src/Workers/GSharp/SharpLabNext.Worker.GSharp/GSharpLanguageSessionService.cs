using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Sdk;

namespace SharpLabNext.Worker.GSharp;

public sealed class GSharpLanguageSessionService(GSharpWorkerSettings settings, LanguageWorkerCapabilityManifest manifest, ILoggerFactory loggerFactory) : ILanguageWorkerSessionService
{
    private readonly ConcurrentDictionary<string, GSharpLanguageSessionState> _sessions = new(StringComparer.Ordinal);

    public Task<LanguageSession> OpenAsync(OpenLanguageSessionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var toolchain = settings.GetToolchain(request.ToolchainId);
        if (!manifest.ToolchainIds.Contains(toolchain.ToolchainId, StringComparer.Ordinal))
            throw new LanguageWorkerRequestException("wrong-toolchain", "The request does not target the G# worker.");
        GSharpWorkspaceValidator.ValidateOutputKind(request.Workspace.BuildOptions.OutputKind);
        var sessionId = $"gsharp_{Guid.NewGuid():N}";
        var session = new LanguageSession(sessionId, GSharpToolchain.LanguageId, toolchain.ToolchainId, $"{toolchain.CompilerVersion}@{toolchain.CompilerCommit}", ContractSchemaVersions.Lsp, request.Workspace.Revision, request.Workspace.SelectionRevision, DateTimeOffset.UtcNow.Add(settings.ProcessLimits.SessionTtl));
        var state = new GSharpLanguageSessionState(session, request.Workspace, toolchain);
        if (!_sessions.TryAdd(sessionId, state))
            throw new InvalidOperationException("A unique G# language session ID could not be allocated.");
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
        if (!_sessions.TryGetValue(sessionId, out var state) || state.Session.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            if (state is not null && _sessions.TryRemove(sessionId, out var expired))
                expired.Dispose();
            throw new LanguageWorkerRequestException("session-not-found", "The G# language session does not exist or has expired.", StatusCodes.Status404NotFound);
        }
        if (!state.TryAttach())
        {
            throw new LanguageWorkerRequestException("session-in-use", "The G# language session already has an LSP connection.", StatusCodes.Status409Conflict);
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, state.CancellationToken);
            var bridge = new GSharpLspProcessBridge(settings, manifest.Limits.MaximumLspMessageBytes, loggerFactory.CreateLogger<GSharpLspProcessBridge>());
            await bridge.RunAsync(socket, state, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            state.Detach();
        }
    }
}

internal sealed class GSharpLanguageSessionState : IDisposable
{
    private readonly CancellationTokenSource _closed = new();
    private int _attached;

    public GSharpLanguageSessionState(LanguageSession session, WorkspaceSnapshot workspace, GSharpToolchainProfile toolchain)
    {
        Session = session;
        Workspace = workspace;
        Toolchain = toolchain;
    }

    public LanguageSession Session { get; }

    public WorkspaceSnapshot Workspace { get; }

    public GSharpToolchainProfile Toolchain { get; }

    public CancellationToken CancellationToken => _closed.Token;

    public bool TryAttach() => Interlocked.CompareExchange(ref _attached, 1, 0) == 0;

    public void Detach() => Volatile.Write(ref _attached, 0);

    public void Dispose()
    {
        _closed.Cancel();
        _closed.Dispose();
    }
}
