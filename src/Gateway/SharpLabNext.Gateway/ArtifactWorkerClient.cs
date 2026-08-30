using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SharpLabNext.Contracts;

namespace SharpLabNext.Gateway;

public interface IArtifactWorkerClient
{
    Task<OperationHandle> StartTransformAsync(TransformArtifactRequest request, CancellationToken cancellationToken = default);

    Task<OperationHandle> StartRenderAsync(RenderArtifactRequest request, CancellationToken cancellationToken = default);

    Task<OperationHandle> StartVerifyAsync(VerifyArtifactRequest request, CancellationToken cancellationToken = default);

    Task<OperationState?> GetOperationAsync(string operationId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OperationEvent>> GetEventsAsync(string operationId, long fromSequence, CancellationToken cancellationToken = default);

    Task<CancelResult> CancelAsync(string operationId, string? reason = null, CancellationToken cancellationToken = default);
}

public sealed record ArtifactWorkerClientSettings(string WorkerId, string ExpectedReleaseId, string? ExpectedWorkerImageId);

public sealed class ArtifactWorkerClient(HttpClient httpClient, ArtifactPipelineOptions options, ArtifactWorkerClientSettings settings) : IArtifactWorkerClient
{
    private static readonly JsonSerializerOptions JsonOptions = ContractJson.CreateSerializerOptions();
    private WorkerDescriptor? _descriptor;

    public Task<OperationHandle> StartTransformAsync(TransformArtifactRequest request, CancellationToken cancellationToken = default) => StartAsync("/api/v1/artifact-transforms", request, request.RequestId, cancellationToken);

    public Task<OperationHandle> StartRenderAsync(RenderArtifactRequest request, CancellationToken cancellationToken = default) => StartAsync("/api/v1/artifact-renders", request, request.RequestId, cancellationToken);

    public Task<OperationHandle> StartVerifyAsync(VerifyArtifactRequest request, CancellationToken cancellationToken = default) => StartAsync("/api/v1/verifications", request, request.RequestId, cancellationToken);

    public async Task<OperationState?> GetOperationAsync(string operationId, CancellationToken cancellationToken = default)
    {
        ValidateOperationId(operationId);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/operations/{Uri.EscapeDataString(operationId)}");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var state = await ReadRequiredJsonAsync<OperationState>(response, cancellationToken).ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(state.OperationId, operationId))
            throw ProtocolFailure("Artifact worker operation state used a different operation ID.");
        return state;
    }

    public async Task<IReadOnlyList<OperationEvent>> GetEventsAsync(string operationId, long fromSequence, CancellationToken cancellationToken = default)
    {
        ValidateOperationId(operationId);
        ArgumentOutOfRangeException.ThrowIfNegative(fromSequence);
        var descriptor = await GetDescriptorAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/operations/{Uri.EscapeDataString(operationId)}/events?FromSequence={fromSequence}");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var events = await ReadRequiredJsonAsync<OperationEvent[]>(response, cancellationToken).ConfigureAwait(false);
        if (events.Length > options.MaximumEventsPerPoll)
            throw ProtocolFailure("Artifact worker returned too many events in one poll.");
        var previousSequence = fromSequence;
        foreach (var operationEvent in events)
        {
            if (!StringComparer.Ordinal.Equals(operationEvent.OperationId, operationId) || operationEvent.Sequence <= previousSequence)
            {
                throw ProtocolFailure("Artifact worker returned an invalid operation event.");
            }
            if (operationEvent.Payload is TypedResultOperationEventPayload typed)
                ValidateProcessorIdentity(typed.Result, descriptor);
            previousSequence = operationEvent.Sequence;
        }
        return events;
    }

    public async Task<CancelResult> CancelAsync(string operationId, string? reason = null, CancellationToken cancellationToken = default)
    {
        ValidateOperationId(operationId);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/operations/{Uri.EscapeDataString(operationId)}/cancel")
        {
            Content = JsonContent.Create(new CancelOperationRequest(operationId, reason), options: JsonOptions)
        };
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new CancelResult(operationId, CancelDisposition.NotFound, 0);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var result = await ReadRequiredJsonAsync<CancelResult>(response, cancellationToken).ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(result.OperationId, operationId))
            throw ProtocolFailure("Artifact worker cancellation response used a different operation ID.");
        return result;
    }

    private async Task<OperationHandle> StartAsync<TRequest>(string path, TRequest job, string requestId, CancellationToken cancellationToken)
    {
        _ = await GetDescriptorAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(job, options: JsonOptions)
        };
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var handle = await ReadRequiredJsonAsync<OperationHandle>(response, cancellationToken).ConfigureAwait(false);
        ValidateOperationId(handle.OperationId);
        if (string.IsNullOrWhiteSpace(handle.RequestId) || (!handle.IsExisting && !StringComparer.Ordinal.Equals(handle.RequestId, requestId)))
        {
            throw ProtocolFailure("Artifact worker operation handle used a different request ID.");
        }
        return handle;
    }

    private async Task<WorkerDescriptor> GetDescriptorAsync(CancellationToken cancellationToken)
    {
        if (_descriptor is not null)
            return _descriptor;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/worker/describe");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var descriptor = await ReadRequiredJsonAsync<WorkerDescriptor>(response, cancellationToken).ConfigureAwait(false);
        ValidateDescriptor(descriptor);
        _descriptor = descriptor;
        return descriptor;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ControlRequestTimeout);
        try
        {
            return await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw Failure("artifact-worker-control-timeout", WorkerErrorCategory.DeadlineExceeded, "The artifact worker did not respond in time.", retryable: true, safeToRetry: true, exception);
        }
        catch (HttpRequestException exception)
        {
            throw Failure("artifact-worker-unavailable", WorkerErrorCategory.Unavailable, "The artifact worker is unavailable.", retryable: true, safeToRetry: true, exception);
        }
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        string? code = null;
        string? message = null;
        string? traceId = null;
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(body, new JsonDocumentOptions { MaxDepth = 16 }, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            code = GetString(root, "Code") ?? GetString(root, "Title");
            message = GetString(root, "Detail") ?? GetString(root, "Message");
            traceId = GetString(root, "TraceId");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException) { }

        var statusCode = (int)response.StatusCode;
        var category = statusCode switch
        {
            400 => WorkerErrorCategory.InvalidArgument,
            404 => WorkerErrorCategory.NotFound,
            408 => WorkerErrorCategory.DeadlineExceeded,
            413 or 429 => WorkerErrorCategory.ResourceExhausted,
            499 => WorkerErrorCategory.Cancelled,
            501 => WorkerErrorCategory.UnsupportedCapability,
            502 or 503 or 504 => WorkerErrorCategory.Unavailable,
            _ => WorkerErrorCategory.Internal
        };
        var retryable = category is WorkerErrorCategory.DeadlineExceeded
            or WorkerErrorCategory.Unavailable
            or WorkerErrorCategory.Internal;
        throw new ArtifactWorkerClientException(new WorkerError(code ?? $"artifact-worker-http-{statusCode}", category, message ?? PublicMessage(category), retryable, retryable, traceId ?? "artifact-worker-client", settings.WorkerId, "unknown"), statusCode);
    }

    private async Task<T> ReadRequiredJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false) ?? throw ProtocolFailure("Artifact worker returned an empty JSON response.");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw ProtocolFailure("Artifact worker returned invalid JSON.", exception);
        }
    }

    private static void ValidateOperationId(string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        if (operationId.Length > 128 || operationId.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
        {
            throw new ArgumentException("The artifact worker operation ID is malformed.", nameof(operationId));
        }
    }

    private void ValidateDescriptor(WorkerDescriptor descriptor)
    {
        if (!string.Equals(descriptor.Service.Id, settings.WorkerId, StringComparison.Ordinal) || descriptor.Service.Kind != ServiceKind.ArtifactWorker || descriptor.WorkerKind != WorkerKind.ArtifactProcessor || !descriptor.ProfileIds.Contains(settings.WorkerId, StringComparer.Ordinal))
        {
            throw ProtocolFailure("The endpoint did not describe the expected artifact worker.");
        }

        if (!string.Equals(descriptor.Service.ReleaseId, settings.ExpectedReleaseId, StringComparison.Ordinal))
            throw ProtocolFailure("The artifact worker release does not match the Gateway release.");

        if (string.IsNullOrWhiteSpace(descriptor.WorkerImageId) || descriptor.WorkerImageId.Length > 512 || settings.ExpectedWorkerImageId is not null && !string.Equals(descriptor.WorkerImageId, settings.ExpectedWorkerImageId, StringComparison.Ordinal))
        {
            throw ProtocolFailure("The artifact worker image identity is not approved.");
        }

        if (descriptor.NegotiatedProtocol.Major != ProtocolVersion.WorkerV1.Major || descriptor.Service.Protocol.Major != ProtocolVersion.WorkerV1.Major || !descriptor.SupportedProtocolVersions.Any(static version => version.Major == ProtocolVersion.WorkerV1.Major))
        {
            throw Failure("artifact-worker-protocol-incompatible", WorkerErrorCategory.UnsupportedCapability, "The artifact worker protocol is incompatible with the Gateway.", retryable: false, safeToRetry: false);
        }
    }

    private void ValidateProcessorIdentity(OperationResult result, WorkerDescriptor descriptor)
    {
        var identity = result switch
        {
            TransformArtifactResult transform => transform.Identity,
            RenderArtifactResult render => render.Identity,
            VerifyArtifactResult verification => verification.Identity,
            _ => throw ProtocolFailure("The artifact worker returned an unexpected typed result.")
        };
        if (identity is null)
            throw ProtocolFailure("The artifact worker result omitted its processor identity.");

        if (!string.Equals(identity.ReleaseId, settings.ExpectedReleaseId, StringComparison.Ordinal) || !string.Equals(identity.ProcessorId, settings.WorkerId, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(identity.ProcessorVersion) || identity.ProcessorVersion.Length > 512 || !string.Equals(identity.WorkerImageId, descriptor.WorkerImageId, StringComparison.Ordinal))
        {
            throw ProtocolFailure("The artifact worker result identity did not match the selected processor.");
        }
    }

    private ArtifactWorkerClientException ProtocolFailure(string message, Exception? innerException = null) => Failure("artifact-worker-protocol-invalid", WorkerErrorCategory.Internal, message, retryable: false, safeToRetry: false, innerException);

    private ArtifactWorkerClientException Failure(string code, WorkerErrorCategory category, string message, bool retryable, bool safeToRetry, Exception? innerException = null) => new(new WorkerError(code, category, message, retryable, safeToRetry, "artifact-worker-client", settings.WorkerId, "unknown"), null, innerException);

    private static string? GetString(JsonElement root, string propertyName) => ContractJson.GetString(root, propertyName);

    private static string PublicMessage(WorkerErrorCategory category) => category switch
    {
        WorkerErrorCategory.InvalidArgument => "The artifact worker rejected the request.",
        WorkerErrorCategory.NotFound => "The requested artifact operation was not found.",
        WorkerErrorCategory.UnsupportedCapability => "The selected artifact capability is not available.",
        WorkerErrorCategory.DeadlineExceeded => "The artifact worker request deadline elapsed.",
        WorkerErrorCategory.ResourceExhausted => "The artifact operation exceeded a configured limit.",
        WorkerErrorCategory.Cancelled => "The artifact operation was cancelled.",
        WorkerErrorCategory.Unavailable => "The artifact worker is unavailable.",
        _ => "The artifact worker failed to process the request."
    };
}

public sealed class ArtifactWorkerClientException : Exception
{
    public ArtifactWorkerClientException(WorkerError error, int? statusCode = null, Exception? innerException = null) : base(error.PublicMessage, innerException)
    {
        Error = error;
        StatusCode = statusCode;
    }

    public WorkerError Error { get; }

    public int? StatusCode { get; }
}
