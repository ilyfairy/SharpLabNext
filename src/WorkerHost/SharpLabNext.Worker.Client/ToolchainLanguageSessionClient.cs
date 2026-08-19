using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.Client;

public sealed class ToolchainLanguageSessionClient
{
    private static readonly JsonSerializerOptions JsonOptions = ContractJson.CreateSerializerOptions();

    private readonly HttpClient _httpClient;
    private readonly ToolchainWorkerClientSettings _settings;
    private readonly string? _serviceToken;

    public ToolchainLanguageSessionClient(
        HttpClient httpClient,
        ToolchainWorkerClientSettings settings,
        string? serviceToken = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _serviceToken = string.IsNullOrWhiteSpace(serviceToken) ? null : serviceToken;
        if (_httpClient.BaseAddress is null)
            throw new ArgumentException("The worker HTTP client must have a base address.", nameof(httpClient));
        if (_serviceToken is not null)
            _httpClient.DefaultRequestHeaders.Authorization = new("Bearer", _serviceToken);
    }

    public async Task<LanguageSession> OpenAsync(
        OpenLanguageSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var descriptorClient = new ToolchainWorkerClient(_httpClient, _settings);
        var descriptor = await descriptorClient.DescribeAsync(cancellationToken).ConfigureAwait(false);
        ValidateLanguageSessionCapability(descriptor, request.ToolchainId);
        ValidateReferenceSet(descriptor, request.ReferenceSetId);

        using var message = CreateRequest(HttpMethod.Post, "/api/v1/language-sessions");
        message.Content = JsonContent.Create(request, options: JsonOptions);
        using var response = await SendAsync(message, cancellationToken).ConfigureAwait(false);
        var session = await response.Content
            .ReadFromJsonAsync<LanguageSession>(JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw ProtocolFailure("Worker language session response was empty.", descriptor.WorkerImageId);
        ValidateSession(request, session, descriptor.WorkerImageId);
        return session;
    }

    public async Task CloseAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        using var request = CreateRequest(
            HttpMethod.Delete,
            $"/api/v1/language-sessions/{Uri.EscapeDataString(sessionId)}");
        using var response = await SendAsync(request, cancellationToken, allowNotFound: true).ConfigureAwait(false);
    }

    public async Task<ClientWebSocket> ConnectAsync(
        string sessionId,
        TimeSpan keepAliveInterval,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = keepAliveInterval;
        if (_serviceToken is not null)
            socket.Options.SetRequestHeader("Authorization", $"Bearer {_serviceToken}");

        var uri = CreateWebSocketUri(
            _httpClient.BaseAddress!,
            $"/api/v1/language-sessions/{Uri.EscapeDataString(sessionId)}/lsp");
        try
        {
            await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        if (_serviceToken is not null)
            request.Headers.Authorization = new("Bearer", _serviceToken);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        bool allowNotFound = false)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw Failure(
                "worker-unavailable",
                WorkerErrorCategory.Unavailable,
                "The language worker is unavailable.",
                retryable: true,
                safeToRetry: true,
                statusCode: null,
                exception);
        }

        if (response.IsSuccessStatusCode || allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
            return response;

        try
        {
            var failure = await CreateHttpFailureAsync(response, cancellationToken).ConfigureAwait(false);
            throw failure;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async Task<ToolchainWorkerException> CreateHttpFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string? code = null;
        string? detail = null;
        string? traceId = null;
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                body,
                new JsonDocumentOptions { MaxDepth = 16 },
                cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            code = GetString(root, "Code") ?? GetString(root, "Title");
            detail = GetString(root, "Detail");
            traceId = GetString(root, "TraceId");
        }
        catch (JsonException)
        {
        }

        var statusCode = (int)response.StatusCode;
        var category = statusCode switch
        {
            400 => WorkerErrorCategory.InvalidArgument,
            404 => WorkerErrorCategory.NotFound,
            408 => WorkerErrorCategory.DeadlineExceeded,
            413 or 429 => WorkerErrorCategory.ResourceExhausted,
            499 => WorkerErrorCategory.Cancelled,
            503 => WorkerErrorCategory.Unavailable,
            _ => WorkerErrorCategory.Internal
        };
        var retryable = category is WorkerErrorCategory.DeadlineExceeded
            or WorkerErrorCategory.Unavailable
            or WorkerErrorCategory.Internal;
        return Failure(
            code ?? $"worker-http-{statusCode}",
            category,
            detail ?? "The language worker could not open the session.",
            retryable,
            retryable,
            statusCode,
            innerException: null,
            traceId);
    }

    private void ValidateLanguageSessionCapability(WorkerDescriptor descriptor, string toolchainId)
    {
        if (!descriptor.ProfileIds.Contains(toolchainId, StringComparer.Ordinal))
            throw ProtocolFailure("The language worker does not host the requested toolchain profile.", descriptor.WorkerImageId);
        var available = descriptor.Capabilities.Any(item =>
            string.Equals(item.Id, "lsp", StringComparison.Ordinal) &&
            item.Available &&
            item.ProfileIds.Contains(toolchainId, StringComparer.Ordinal));
        if (!available)
        {
            throw Failure(
                "worker-capability-unavailable",
                WorkerErrorCategory.UnsupportedCapability,
                "The selected toolchain does not provide an available language server.",
                retryable: false,
                safeToRetry: false,
                statusCode: null,
                innerException: null,
                workerImageId: descriptor.WorkerImageId);
        }
    }

    private void ValidateReferenceSet(WorkerDescriptor descriptor, string referenceSetId)
    {
        if (_settings.ExpectedReferenceSetDigests is not { Count: > 0 } expected)
            return;
        if (!expected.TryGetValue(referenceSetId, out var digest) ||
            descriptor.ReferenceSets?.Any(item =>
                string.Equals(item.Id, referenceSetId, StringComparison.Ordinal) &&
                string.Equals(item.Digest, digest, StringComparison.Ordinal)) != true)
        {
            throw ProtocolFailure(
                "The requested reference set is not attested for this language worker.",
                descriptor.WorkerImageId);
        }
    }

    private static void ValidateSession(
        OpenLanguageSessionRequest request,
        LanguageSession session,
        string workerImageId)
    {
        if (string.IsNullOrWhiteSpace(session.SessionId) ||
            !string.Equals(session.LanguageId, request.LanguageId, StringComparison.Ordinal) ||
            !string.Equals(session.ToolchainId, request.ToolchainId, StringComparison.Ordinal) ||
            !string.Equals(session.LspVersion, request.LspVersion, StringComparison.Ordinal) ||
            session.WorkspaceRevision != request.Workspace.Revision ||
            session.SelectionRevision != request.Workspace.SelectionRevision ||
            session.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            throw new ToolchainWorkerException(new WorkerError(
                "worker-protocol-invalid",
                WorkerErrorCategory.Internal,
                "The language worker returned an invalid session descriptor.",
                Retryable: false,
                SafeToRetry: false,
                "worker-client",
                "unknown",
                workerImageId));
        }
    }

    private ToolchainWorkerException ProtocolFailure(string message, string? workerImageId = null) =>
        Failure(
            "worker-protocol-invalid",
            WorkerErrorCategory.Internal,
            message,
            retryable: false,
            safeToRetry: false,
            statusCode: null,
            innerException: null,
            workerImageId: workerImageId);

    private ToolchainWorkerException Failure(
        string code,
        WorkerErrorCategory category,
        string publicMessage,
        bool retryable,
        bool safeToRetry,
        int? statusCode,
        Exception? innerException,
        string? traceId = null,
        string? workerImageId = null) =>
        new(
            new WorkerError(
                code,
                category,
                publicMessage,
                retryable,
                safeToRetry,
                traceId ?? "worker-client",
                _settings.WorkerId,
                workerImageId ?? _settings.ExpectedWorkerImageId ?? "unknown"),
            statusCode,
            innerException);

    private static string? GetString(JsonElement root, string propertyName) =>
        ContractJson.GetString(root, propertyName);

    private static Uri CreateWebSocketUri(Uri baseAddress, string path)
    {
        var httpUri = new Uri(baseAddress, path);
        var builder = new UriBuilder(httpUri)
        {
            Scheme = httpUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Port = httpUri.IsDefaultPort ? -1 : httpUri.Port
        };
        return builder.Uri;
    }
}
