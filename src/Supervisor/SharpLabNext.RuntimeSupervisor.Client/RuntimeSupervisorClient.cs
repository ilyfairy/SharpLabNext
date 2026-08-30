using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SharpLabNext.Contracts;

namespace SharpLabNext.RuntimeSupervisor.Client;

public sealed class RuntimeSupervisorClient : IRuntimeSupervisorClient
{
    internal const string RuntimeSessionIdHeaderName = "X-SharpLabNext-Runtime-Session-Id";
    private static readonly JsonSerializerOptions JsonOptions = ContractJson.CreateSerializerOptions();
    private readonly HttpClient _httpClient;
    private readonly RuntimeSupervisorClientSettings _settings;

    public RuntimeSupervisorClient(HttpClient httpClient, RuntimeSupervisorClientSettings settings)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        _httpClient = httpClient;
        _settings = settings;
    }

    public Task<OperationHandle> StartRunAsync(RunRequest request, CancellationToken cancellationToken = default) =>
        StartRunAsync(request, runtimeSessionId: null, cancellationToken);

    public Task<OperationHandle> StartRunAsync(RunRequest request, string? runtimeSessionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return StartAsync("/internal/v1/jobs/run", request, request.RequestId, runtimeSessionId, cancellationToken);
    }

    public Task<OperationHandle> StartJitAsync(JitRequest request, CancellationToken cancellationToken = default) =>
        StartJitAsync(request, runtimeSessionId: null, cancellationToken);

    public Task<OperationHandle> StartJitAsync(JitRequest request, string? runtimeSessionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return StartAsync("/internal/v1/jobs/jit", request, request.RequestId, runtimeSessionId, cancellationToken);
    }

    public async Task<OperationState?> GetOperationAsync(string operationId, CancellationToken cancellationToken = default)
    {
        ValidateOperationId(operationId);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/internal/v1/operations/{Uri.EscapeDataString(operationId)}");
        using var response = await SendControlAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var state = await ReadRequiredJsonAsync<OperationState>(response, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(state.OperationId, operationId, StringComparison.Ordinal))
        {
            throw ProtocolFailure("Runtime supervisor operation state used a different operation ID.");
        }

        return state;
    }

    public async IAsyncEnumerable<OperationEvent> WatchEventsAsync(string operationId, long fromSequence = 0, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateOperationId(operationId);
        ArgumentOutOfRangeException.ThrowIfNegative(fromSequence);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/internal/v1/operations/{Uri.EscapeDataString(operationId)}/events?FromSequence={fromSequence}");
        using var response = await SendStreamingAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: false);

        var data = new StringBuilder();
        var previousSequence = fromSequence;
        var acceptedSeen = fromSequence > 0;
        var terminalSeen = false;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length == 0)
                {
                    continue;
                }

                var operationEvent = ParseEvent(data.ToString());
                data.Clear();
                ValidateEvent(operationId, fromSequence, operationEvent, ref previousSequence, ref acceptedSeen, ref terminalSeen);
                yield return operationEvent;
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var value = line.AsSpan("data:".Length);
            if (!value.IsEmpty && value[0] == ' ')
            {
                value = value[1..];
            }

            var separatorLength = data.Length == 0 ? 0 : 1;
            if (data.Length + separatorLength + value.Length > _settings.MaximumEventCharacters)
            {
                throw ProtocolFailure("Runtime supervisor event exceeded the configured size limit.");
            }

            if (separatorLength != 0)
            {
                data.Append('\n');
            }

            data.Append(value);
        }

        if (data.Length != 0)
        {
            var operationEvent = ParseEvent(data.ToString());
            ValidateEvent(operationId, fromSequence, operationEvent, ref previousSequence, ref acceptedSeen, ref terminalSeen);
            yield return operationEvent;
        }

        if (!acceptedSeen)
        {
            throw ProtocolFailure("Runtime supervisor event stream ended before its required events were received.");
        }

        if (!terminalSeen)
        {
            var state = await GetOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (state is null || !IsTerminal(state.Status) || state.LastSequence != previousSequence)
            {
                throw ProtocolFailure("Runtime supervisor event stream ended before its required events were received.");
            }
        }
    }

    public async Task<CancelResult> CancelAsync(string operationId, string? reason = null, CancellationToken cancellationToken = default)
    {
        ValidateOperationId(operationId);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/internal/v1/operations/{Uri.EscapeDataString(operationId)}/cancel")
        {
            Content = JsonContent.Create(new CancelOperationRequest(operationId, reason), options: JsonOptions)
        };
        using var response = await SendControlAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new CancelResult(operationId, CancelDisposition.NotFound, 0);
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var result = await ReadRequiredJsonAsync<CancelResult>(response, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(result.OperationId, operationId, StringComparison.Ordinal))
        {
            throw ProtocolFailure("Runtime supervisor cancellation response used a different operation ID.");
        }

        return result;
    }

    public async Task ReleaseSessionAsync(string runtimeSessionId, CancellationToken cancellationToken = default)
    {
        ValidateRuntimeSessionId(runtimeSessionId);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/internal/v1/sessions/{Uri.EscapeDataString(runtimeSessionId)}/release");
        using var response = await SendControlAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OperationHandle> StartAsync<TRequest>(string path, TRequest job, string requestId, string? runtimeSessionId, CancellationToken cancellationToken)
    {
        if (runtimeSessionId is not null)
            ValidateRuntimeSessionId(runtimeSessionId);
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(job, options: JsonOptions)
        };
        if (runtimeSessionId is not null)
            request.Headers.Add(RuntimeSessionIdHeaderName, runtimeSessionId);
        using var response = await SendControlAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var handle = await ReadRequiredJsonAsync<OperationHandle>(response, cancellationToken).ConfigureAwait(false);
        ValidateOperationId(handle.OperationId);
        if (string.IsNullOrWhiteSpace(handle.RequestId) || (!handle.IsExisting && !string.Equals(handle.RequestId, requestId, StringComparison.Ordinal)))
        {
            throw ProtocolFailure("Runtime supervisor operation handle used a different request ID.");
        }

        return handle;
    }

    private async Task<HttpResponseMessage> SendControlAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_settings.ControlRequestTimeout);
        try
        {
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure("runtime-supervisor-control-timeout", WorkerErrorCategory.DeadlineExceeded, "The runtime supervisor did not respond in time.", retryable: true, statusCode: null, exception);
        }
        catch (HttpRequestException exception)
        {
            throw Unavailable(exception);
        }
    }

    private async Task<HttpResponseMessage> SendStreamingAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw Unavailable(exception);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? code = null;
        string? publicMessage = null;
        string? traceId = null;
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                body,
                new JsonDocumentOptions { MaxDepth = 16 },
                cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            code = GetString(root, "Error") ?? GetString(root, "Code") ?? GetString(root, "Title");
            publicMessage = GetString(root, "Message") ?? GetString(root, "Detail");
            traceId = GetString(root, "TraceId");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException) { }

        var statusCode = (int)response.StatusCode;
        var category = statusCode switch
        {
            400 => WorkerErrorCategory.InvalidArgument,
            404 => WorkerErrorCategory.NotFound,
            408 => WorkerErrorCategory.DeadlineExceeded,
            422 => WorkerErrorCategory.IncompatibleArtifact,
            413 or 429 => WorkerErrorCategory.ResourceExhausted,
            499 => WorkerErrorCategory.Cancelled,
            501 => WorkerErrorCategory.UnsupportedCapability,
            502 or 503 or 504 => WorkerErrorCategory.Unavailable,
            _ => WorkerErrorCategory.Internal
        };
        var retryable = category is WorkerErrorCategory.DeadlineExceeded
            or WorkerErrorCategory.Unavailable
            or WorkerErrorCategory.Internal;
        throw Failure(code ?? $"runtime-supervisor-http-{statusCode}", category, publicMessage ?? PublicMessage(category), retryable, statusCode, innerException: null, traceId);
    }

    private static void ValidateEvent(string operationId, long fromSequence, OperationEvent operationEvent, ref long previousSequence, ref bool acceptedSeen, ref bool terminalSeen)
    {
        if (terminalSeen)
        {
            throw ProtocolFailure("Runtime supervisor emitted an event after a terminal event.");
        }

        if (!string.Equals(operationEvent.OperationId, operationId, StringComparison.Ordinal))
        {
            throw ProtocolFailure("Runtime supervisor event used a different operation ID.");
        }

        if (operationEvent.Sequence <= previousSequence)
        {
            throw ProtocolFailure("Runtime supervisor event sequence was not strictly increasing.");
        }

        if (operationEvent.Payload is AcceptedOperationEventPayload)
        {
            if (fromSequence != 0 || acceptedSeen || previousSequence != 0)
            {
                throw ProtocolFailure("Runtime supervisor accepted event was out of order.");
            }

            acceptedSeen = true;
        }
        else if (!acceptedSeen)
        {
            throw ProtocolFailure("Runtime supervisor event stream did not begin with an accepted event.");
        }

        previousSequence = operationEvent.Sequence;
        terminalSeen = operationEvent.Payload is CompletedOperationEventPayload or FailedOperationEventPayload;
    }

    private static OperationEvent ParseEvent(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<OperationEvent>(json, JsonOptions) ?? throw ProtocolFailure("Runtime supervisor event was empty.");
        }
        catch (JsonException exception)
        {
            throw ProtocolFailure("Runtime supervisor event JSON was invalid.", exception);
        }
    }

    private static async Task<T> ReadRequiredJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false) ?? throw ProtocolFailure("Runtime supervisor returned an empty JSON response.");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw ProtocolFailure("Runtime supervisor returned invalid JSON.", exception);
        }
    }

    private static void ValidateOperationId(string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        if (operationId.Length > 128 || operationId.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
        {
            throw new ArgumentException("The runtime supervisor operation ID is malformed.", nameof(operationId));
        }
    }

    private static void ValidateRuntimeSessionId(string runtimeSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeSessionId);
        if (runtimeSessionId.Length > 128 || runtimeSessionId.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
        {
            throw new ArgumentException("The runtime supervisor session ID is malformed.", nameof(runtimeSessionId));
        }
    }

    private static RuntimeSupervisorClientException ProtocolFailure(string message, Exception? innerException = null) =>
        Failure("runtime-supervisor-protocol-invalid", WorkerErrorCategory.Internal, message, retryable: false, statusCode: null, innerException);

    private static RuntimeSupervisorClientException Unavailable(Exception innerException) =>
        Failure("runtime-supervisor-unavailable", WorkerErrorCategory.Unavailable, "The runtime supervisor is unavailable.", retryable: true, statusCode: null, innerException);

    private static RuntimeSupervisorClientException Failure(string code, WorkerErrorCategory category, string publicMessage, bool retryable, int? statusCode, Exception? innerException, string? traceId = null) =>
        new(new WorkerError(code, category, publicMessage, retryable, false, traceId ?? "runtime-supervisor-client", "runtime-supervisor", "unknown"), statusCode, innerException);

    private static string? GetString(JsonElement root, string propertyName) =>
        ContractJson.GetString(root, propertyName);

    private static bool IsTerminal(OperationStatus status) =>
        status is OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled;

    private static string PublicMessage(WorkerErrorCategory category) => category switch
    {
        WorkerErrorCategory.InvalidArgument => "The runtime supervisor rejected the job request.",
        WorkerErrorCategory.NotFound => "The requested runtime job or artifact was not found.",
        WorkerErrorCategory.UnsupportedCapability => "The selected runtime capability is not available.",
        WorkerErrorCategory.IncompatibleArtifact => "The selected runtime cannot load this artifact.",
        WorkerErrorCategory.DeadlineExceeded => "The runtime supervisor request deadline elapsed.",
        WorkerErrorCategory.ResourceExhausted => "The runtime job exceeded a configured limit.",
        WorkerErrorCategory.Cancelled => "The runtime job was cancelled.",
        WorkerErrorCategory.Unavailable => "The runtime supervisor is unavailable.",
        _ => "The runtime supervisor failed to process the request."
    };
}
