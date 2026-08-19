using System.Text.Json.Serialization;

namespace SharpLabNext.Contracts;

public sealed record OperationHandle(
    string OperationId,
    string RequestId,
    DateTimeOffset CreatedAtUtc,
    bool IsExisting);

public sealed record WatchOperationRequest(
    string OperationId,
    long FromSequence = 0);

public sealed record GetOperationRequest(string OperationId);

public sealed record CancelOperationRequest(
    string OperationId,
    string? Reason = null);

public sealed record CancelResult(
    string OperationId,
    CancelDisposition Disposition,
    long LastSequence);

public sealed record OperationState(
    string OperationId,
    string RequestId,
    OperationKind Kind,
    OperationStatus Status,
    long LastSequence,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string TraceId,
    WorkerError? Error);

public sealed record OperationEvent(
    string OperationId,
    long Sequence,
    DateTimeOffset TimestampUtc,
    string TraceId,
    OperationEventPayload Payload);

[JsonConverter(typeof(OperationEventPayloadJsonConverter))]
public record OperationEventPayload
{
    [JsonIgnore]
    public virtual bool IsTerminal => false;
}

public sealed record AcceptedOperationEventPayload(
    string RequestId,
    OperationKind OperationKind) : OperationEventPayload;

public sealed record ProgressOperationEventPayload(
    string Stage,
    string? Message,
    double? Fraction) : OperationEventPayload;

public sealed record DiagnosticOperationEventPayload(
    Diagnostic Diagnostic) : OperationEventPayload;

public sealed record OutputChunkOperationEventPayload(
    OutputChunk Chunk) : OperationEventPayload;

public sealed record ArtifactProducedOperationEventPayload(
    ArtifactRef ArtifactRef,
    string ArtifactFormat,
    string Role) : OperationEventPayload;

public sealed record ContentProducedOperationEventPayload(
    ContentRef ContentRef,
    string MediaType,
    long Size) : OperationEventPayload;

public sealed record TypedResultOperationEventPayload(
    OperationResult Result) : OperationEventPayload;

public sealed record OutputTruncatedOperationEventPayload(
    OutputChannel Channel,
    string Reason,
    long ObservedBytes,
    long LimitBytes) : OperationEventPayload;

public sealed record CompletedOperationEventPayload(
    OperationCompletionStatus Status,
    TimeSpan Elapsed) : OperationEventPayload
{
    [JsonIgnore]
    public override bool IsTerminal => true;
}

public sealed record FailedOperationEventPayload(
    WorkerError Error) : OperationEventPayload
{
    [JsonIgnore]
    public override bool IsTerminal => true;
}

public sealed record OutputChunk(
    OutputChannel Channel,
    OutputEncoding Encoding,
    string Data,
    bool Truncated);

public sealed record WorkerError(
    string Code,
    WorkerErrorCategory Category,
    string PublicMessage,
    bool Retryable,
    bool SafeToRetry,
    string TraceId,
    string WorkerId,
    string WorkerImageId);

public static class OperationEventStreamContract
{
    public static void Validate(IEnumerable<OperationEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        string? operationId = null;
        long previousSequence = 0;
        var terminalSeen = false;

        foreach (var operationEvent in events)
        {
            ArgumentNullException.ThrowIfNull(operationEvent);

            if (terminalSeen)
                throw new InvalidOperationException("An operation cannot emit events after a terminal event.");

            operationId ??= operationEvent.OperationId;
            if (!StringComparer.Ordinal.Equals(operationId, operationEvent.OperationId))
                throw new InvalidOperationException("An event stream cannot contain multiple operation IDs.");

            if (operationEvent.Sequence <= 0 || operationEvent.Sequence <= previousSequence)
                throw new InvalidOperationException("Operation event sequence numbers must be positive and strictly increasing.");

            previousSequence = operationEvent.Sequence;
            terminalSeen = operationEvent.Payload.IsTerminal;
        }
    }
}

[JsonConverter(typeof(KebabCaseJsonStringEnumConverter<OperationKind>))]
public enum OperationKind
{
    Build,
    TransformArtifact,
    RenderArtifact,
    VerifyArtifact,
    Run,
    Jit,
    Explain
}

[JsonConverter(typeof(KebabCaseJsonStringEnumConverter<OperationStatus>))]
public enum OperationStatus
{
    Accepted,
    Running,
    Cancelling,
    Completed,
    Failed,
    Cancelled
}

[JsonConverter(typeof(KebabCaseJsonStringEnumConverter<OperationCompletionStatus>))]
public enum OperationCompletionStatus
{
    Completed,
    Cancelled
}

[JsonConverter(typeof(KebabCaseJsonStringEnumConverter<CancelDisposition>))]
public enum CancelDisposition
{
    Accepted,
    AlreadyCancelling,
    AlreadyTerminal,
    NotFound
}

[JsonConverter(typeof(KebabCaseJsonStringEnumConverter<OutputChannel>))]
public enum OutputChannel
{
    Stdout,
    Stderr,
    Inspection,
    Flow,
    Jit,
    Log
}

[JsonConverter(typeof(KebabCaseJsonStringEnumConverter<OutputEncoding>))]
public enum OutputEncoding
{
    [System.Runtime.Serialization.EnumMember(Value = "utf-8")]
    Utf8,
    Binary
}

[JsonConverter(typeof(KebabCaseJsonStringEnumConverter<WorkerErrorCategory>))]
public enum WorkerErrorCategory
{
    InvalidArgument,
    NotFound,
    UnsupportedCapability,
    StaleRevision,
    IncompatibleArtifact,
    ResourceExhausted,
    DeadlineExceeded,
    Cancelled,
    Unavailable,
    Internal
}
