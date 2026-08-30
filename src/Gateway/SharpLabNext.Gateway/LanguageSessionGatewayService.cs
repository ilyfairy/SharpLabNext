using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using SharpLabNext.Catalog;
using SharpLabNext.Contracts;
using SharpLabNext.Observability;
using SharpLabNext.Worker.Client;

namespace SharpLabNext.Gateway;

public sealed record GatewayLanguageSessionResponse(
    string SessionId,
    string LanguageId,
    string ToolchainId,
    string CompilerBuildIdentity,
    string LspVersion,
    long WorkspaceRevision,
    long SelectionRevision,
    DateTimeOffset ExpiresAtUtc,
    string WebSocketUrl,
    IReadOnlyList<string> Capabilities);

public sealed class GatewayLanguageSessionException(string code, string message, int statusCode, Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;

    public int StatusCode { get; } = statusCode;
}

public sealed class LanguageSessionGatewayService(LanguageSessionGatewayOptions options, LanguageWorkerEndpointRegistry endpoints, PipelineResolutionRegistry resolutions, CatalogDocument catalog, GatewayDependencyHealthService dependencyHealth, IHttpClientFactory httpClientFactory, ILogger<LanguageSessionGatewayService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, GatewayLanguageSessionState> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots = new(options.MaxSessions, options.MaxSessions);

    public async Task<GatewayLanguageSessionResponse> OpenAsync(OpenLanguageSessionRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var resolution = resolutions.Get(request.PipelineResolutionId, DateTimeOffset.UtcNow) ?? throw Failure("invalid-pipeline-resolution", "Resolve the selection again before opening a language session.", StatusCodes.Status400BadRequest);
        ValidateResolution(request, resolution);
        var snapshot = await dependencyHealth.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var unavailableReason = GatewayPipelineAvailability.GetUnavailableReason(snapshot, resolution, requireArtifactStore: false);
        if (unavailableReason is not null)
        {
            throw Failure("profile-unavailable", unavailableReason, StatusCodes.Status503ServiceUnavailable);
        }

        var toolchain = catalog.Toolchains.FirstOrDefault(item => string.Equals(item.Id, request.ToolchainId, StringComparison.Ordinal));
        if (toolchain is null || !toolchain.Availability.IsSelectable || !toolchain.Capabilities.Contains("lsp", StringComparer.Ordinal))
        {
            throw Failure("language-server-unavailable", "The selected toolchain does not have an installed language server.", StatusCodes.Status503ServiceUnavailable);
        }

        var workerId = resolution.PipelinePlan.LanguageWorkerId;
        if (!endpoints.TryGet(workerId, out var endpoint) || endpoint is null)
        {
            throw Failure("language-worker-unavailable", "The selected language worker is not configured on this Gateway.", StatusCodes.Status503ServiceUnavailable);
        }

        if (!await _slots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw Failure("language-session-limit", "The Gateway language session limit has been reached.", StatusCodes.Status429TooManyRequests);
        }

        LanguageSession? upstream = null;
        try
        {
            var client = CreateClient(endpoint);
            upstream = await client.OpenAsync(request, cancellationToken).ConfigureAwait(false);
            var sessionId = $"glsp_{Guid.NewGuid():N}";
            var expiresAt = Min(upstream.ExpiresAtUtc, DateTimeOffset.UtcNow.Add(options.MaximumSessionLifetime));
            var state = new GatewayLanguageSessionState(sessionId, upstream.SessionId, endpoint, upstream.LanguageId, upstream.ToolchainId, upstream.CompilerBuildIdentity, upstream.LspVersion, upstream.WorkspaceRevision, upstream.SelectionRevision, expiresAt);
            if (!_sessions.TryAdd(sessionId, state))
                throw new InvalidOperationException("A unique Gateway language session ID could not be allocated.");
            SharpLabNextTelemetry.Metrics.SessionStarted(state.LanguageId, state.ToolchainId);

            return new GatewayLanguageSessionResponse(sessionId, state.LanguageId, state.ToolchainId, state.CompilerBuildIdentity, state.LspVersion, state.WorkspaceRevision, state.SelectionRevision, state.ExpiresAtUtc, $"/api/v1/language-sessions/{Uri.EscapeDataString(sessionId)}/lsp", resolution.EffectiveCapabilities.LanguageServerCapabilities);
        }
        catch (ToolchainWorkerException exception)
        {
            if (upstream is not null)
                await CloseUpstreamIgnoringFailureAsync(endpoint!, upstream.SessionId).ConfigureAwait(false);
            _slots.Release();
            throw MapWorkerFailure(exception);
        }
        catch
        {
            if (upstream is not null)
                await CloseUpstreamIgnoringFailureAsync(endpoint!, upstream.SessionId).ConfigureAwait(false);
            _slots.Release();
            throw;
        }
    }

    public GatewayLanguageSessionConnection Attach(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var state) || state.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            throw Failure("language-session-not-found", "The language session does not exist or has expired.", StatusCodes.Status404NotFound);
        if (!state.TryAttach())
            throw Failure("language-session-in-use", "The language session already has an active connection.", StatusCodes.Status409Conflict);
        return new GatewayLanguageSessionConnection(state);
    }

    public async Task<ClientWebSocket> ConnectUpstreamAsync(GatewayLanguageSessionState state, CancellationToken cancellationToken)
    {
        var client = CreateClient(state.Endpoint);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, state.Closed);
        timeout.CancelAfter(options.ConnectTimeout);
        try
        {
            return await client.ConnectAsync(state.UpstreamSessionId, options.KeepAliveInterval, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !state.Closed.IsCancellationRequested)
        {
            throw Failure("language-worker-timeout", "The language worker did not accept the WebSocket connection in time.", StatusCodes.Status504GatewayTimeout);
        }
        catch (WebSocketException exception)
        {
            throw Failure("language-worker-unavailable", "The language worker WebSocket is unavailable.", StatusCodes.Status502BadGateway, exception);
        }
    }

    public Task<bool> CloseAsync(string sessionId, CancellationToken cancellationToken) =>
        CloseAsyncCore(sessionId, SharpLabNextTelemetryOutcome.Succeeded, cancellationToken);

    private async Task<bool> CloseAsyncCore(string sessionId, SharpLabNextTelemetryOutcome outcome, CancellationToken cancellationToken)
    {
        if (!_sessions.TryRemove(sessionId, out var state))
            return false;

        state.Close();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.CloseTimeout);
            await CreateClient(state.Endpoint).CloseAsync(state.UpstreamSessionId, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ToolchainWorkerException or OperationCanceledException)
        {
            LanguageSessionGatewayLog.UpstreamCloseFailed(logger, exception, sessionId);
        }
        finally
        {
            state.Dispose();
            _slots.Release();
            SharpLabNextTelemetry.Metrics.SessionEnded(state.LanguageId, state.ToolchainId, outcome);
        }
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.ReapInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var session in _sessions.Values)
                {
                    if (session.ExpiresAtUtc <= now)
                    {
                        await CloseAsyncCore(session.SessionId, SharpLabNextTelemetryOutcome.TimedOut, stoppingToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        foreach (var sessionId in _sessions.Keys)
            await CloseAsyncCore(sessionId, SharpLabNextTelemetryOutcome.Cancelled, cancellationToken).ConfigureAwait(false);
    }

    private ToolchainLanguageSessionClient CreateClient(LanguageWorkerEndpoint endpoint)
    {
        var httpClient = httpClientFactory.CreateClient();
        httpClient.BaseAddress = endpoint.BaseAddress;
        httpClient.Timeout = Timeout.InfiniteTimeSpan;
        return new ToolchainLanguageSessionClient(httpClient, new ToolchainWorkerClientSettings(endpoint.WorkerId, endpoint.ExpectedReleaseId, endpoint.ExpectedWorkerImageId, endpoint.ExpectedReferenceSetDigests), endpoint.ServiceToken);
    }

    private void ValidateRequest(OpenLanguageSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.PipelineResolutionId) || string.IsNullOrWhiteSpace(request.LanguageId) || string.IsNullOrWhiteSpace(request.ToolchainId) || string.IsNullOrWhiteSpace(request.ReferenceSetId) || request.Workspace is null)
        {
            throw Failure("invalid-language-session", "The language session request is incomplete.", StatusCodes.Status400BadRequest);
        }
        if (!string.Equals(request.LspVersion, ContractSchemaVersions.Lsp, StringComparison.Ordinal))
            throw Failure("unsupported-lsp-version", "Only LSP 3.17 is supported.", StatusCodes.Status400BadRequest);

        if (request.RequestId.Length > 128 || request.PipelineResolutionId.Length > 256 || request.LanguageId.Length > 128 || request.ToolchainId.Length > 128 || request.ReferenceSetId.Length > 128)
        {
            throw Failure("invalid-language-session", "The language session identity is too long.", StatusCodes.Status400BadRequest);
        }

        var workspace = request.Workspace;
        if (workspace.SchemaVersion != ContractSchemaVersions.WorkspaceSnapshot || workspace.Revision < 0 || workspace.SelectionRevision < 0 || workspace.Files is null || workspace.SourceOrder is null || workspace.BuildOptions is null)
        {
            throw Failure("invalid-language-session", "The workspace snapshot is invalid or uses an unsupported schema version.", StatusCodes.Status400BadRequest);
        }

        if (workspace.Files.Count is 0 || workspace.Files.Count > options.MaxWorkspaceFiles)
        {
            throw Failure("language-session-source-limit", $"A language session must contain between 1 and {options.MaxWorkspaceFiles} files.", StatusCodes.Status413PayloadTooLarge);
        }

        var totalBytes = 0L;
        foreach (var file in workspace.Files)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.Path) || file.Text is null || file.Version < 0)
            {
                throw Failure("invalid-language-session", "Every workspace file must have a path, non-negative version, and source text.", StatusCodes.Status400BadRequest);
            }

            var fileBytes = Encoding.UTF8.GetByteCount(file.Text);
            if (fileBytes > options.MaxFileSourceUtf8Bytes)
            {
                throw Failure("language-session-source-limit", $"Workspace file '{file.Path}' exceeds the Gateway source limit.", StatusCodes.Status413PayloadTooLarge);
            }

            totalBytes += fileBytes;
            if (totalBytes > options.MaxTotalSourceUtf8Bytes)
            {
                throw Failure("language-session-source-limit", "The workspace exceeds the Gateway total source limit.", StatusCodes.Status413PayloadTooLarge);
            }
        }
    }

    private static void ValidateResolution(OpenLanguageSessionRequest request, ResolveSelectionResponse resolution)
    {
        var selection = resolution.EffectiveSelection;
        if (!string.Equals(selection.LanguageId, request.LanguageId, StringComparison.Ordinal) ||
            !string.Equals(selection.ToolchainId, request.ToolchainId, StringComparison.Ordinal) ||
            !string.Equals(selection.ReferenceSetId, request.ReferenceSetId, StringComparison.Ordinal) ||
            !string.Equals(resolution.PipelinePlan.ReferenceSetId, request.ReferenceSetId, StringComparison.Ordinal) ||
            !string.Equals(request.Workspace.LanguageId, request.LanguageId, StringComparison.Ordinal) ||
            !string.Equals(request.Workspace.ReferenceSetId, request.ReferenceSetId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(resolution.PipelinePlan.LanguageWorkerId))
        {
            throw Failure("pipeline-mismatch", "The language session does not match the server-resolved pipeline.", StatusCodes.Status400BadRequest);
        }
    }

    private static GatewayLanguageSessionException MapWorkerFailure(ToolchainWorkerException exception)
    {
        var statusCode = exception.StatusCode ?? exception.Error.Category switch
        {
            WorkerErrorCategory.InvalidArgument => StatusCodes.Status400BadRequest,
            WorkerErrorCategory.NotFound => StatusCodes.Status404NotFound,
            WorkerErrorCategory.UnsupportedCapability => StatusCodes.Status422UnprocessableEntity,
            WorkerErrorCategory.ResourceExhausted => StatusCodes.Status429TooManyRequests,
            WorkerErrorCategory.DeadlineExceeded => StatusCodes.Status504GatewayTimeout,
            WorkerErrorCategory.Cancelled => 499,
            WorkerErrorCategory.Unavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status502BadGateway
        };
        return Failure(exception.Error.Code, exception.Error.PublicMessage, statusCode, exception);
    }

    private async Task CloseUpstreamIgnoringFailureAsync(LanguageWorkerEndpoint endpoint, string upstreamSessionId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(options.CloseTimeout);
            await CreateClient(endpoint).CloseAsync(upstreamSessionId, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ToolchainWorkerException or OperationCanceledException)
        {
            LanguageSessionGatewayLog.UpstreamRollbackFailed(logger, exception, upstreamSessionId);
        }
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;

    private static GatewayLanguageSessionException Failure(string code, string message, int statusCode, Exception? innerException = null) =>
        new(code, message, statusCode, innerException);
}

public sealed class GatewayLanguageSessionState : IDisposable
{
    private readonly CancellationTokenSource _closed = new();
    private int _attached;

    public GatewayLanguageSessionState(string sessionId, string upstreamSessionId, LanguageWorkerEndpoint endpoint, string languageId, string toolchainId, string compilerBuildIdentity, string lspVersion, long workspaceRevision, long selectionRevision, DateTimeOffset expiresAtUtc)
    {
        SessionId = sessionId;
        UpstreamSessionId = upstreamSessionId;
        Endpoint = endpoint;
        LanguageId = languageId;
        ToolchainId = toolchainId;
        CompilerBuildIdentity = compilerBuildIdentity;
        LspVersion = lspVersion;
        WorkspaceRevision = workspaceRevision;
        SelectionRevision = selectionRevision;
        ExpiresAtUtc = expiresAtUtc;
    }

    public string SessionId { get; }
    public string UpstreamSessionId { get; }
    public LanguageWorkerEndpoint Endpoint { get; }
    public string LanguageId { get; }
    public string ToolchainId { get; }
    public string CompilerBuildIdentity { get; }
    public string LspVersion { get; }
    public long WorkspaceRevision { get; }
    public long SelectionRevision { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
    public CancellationToken Closed => _closed.Token;

    public bool TryAttach() => Interlocked.CompareExchange(ref _attached, 1, 0) == 0;

    public void Close()
    {
        try
        {
            _closed.Cancel();
        }
        catch (ObjectDisposedException) { }
    }

    public void Dispose() => _closed.Dispose();
}

public sealed class GatewayLanguageSessionConnection(GatewayLanguageSessionState state) : IDisposable
{
    private GatewayLanguageSessionState? _state = state;

    public GatewayLanguageSessionState State => _state ?? throw new ObjectDisposedException(nameof(GatewayLanguageSessionConnection));

    public void Dispose() => Interlocked.Exchange(ref _state, null);
}

internal static partial class LanguageSessionGatewayLog
{
    [LoggerMessage(1, LogLevel.Warning, "Could not close upstream language session {SessionId}.")]
    public static partial void UpstreamCloseFailed(ILogger logger, Exception exception, string sessionId);

    [LoggerMessage(2, LogLevel.Warning, "Could not roll back upstream language session {SessionId}.")]
    public static partial void UpstreamRollbackFailed(ILogger logger, Exception exception, string sessionId);
}
