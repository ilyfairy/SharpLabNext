using Microsoft.AspNetCore.Http;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.Contracts;

namespace SharpLabNext.ArtifactWorker.Sdk;

public sealed record ArtifactWorkerJobExecution(
    OperationResult Result,
    ArtifactWorkerProducedContent? Content = null,
    ArtifactWorkerProducedArtifact? Artifact = null);

public sealed record ArtifactWorkerProducedContent(
    ContentRef ContentRef,
    string MediaType,
    long Size);

public sealed record ArtifactWorkerProducedArtifact(
    ArtifactRef ArtifactRef,
    string ArtifactFormat,
    string Role);

public interface IArtifactTransformHandler
{
    string TransformId { get; }

    Task<ArtifactWorkerJobExecution> TransformAsync(
        TransformArtifactRequest request,
        string operationId,
        CancellationToken cancellationToken);
}

public interface IArtifactRenderHandler
{
    string OutputId { get; }

    Task<ArtifactWorkerJobExecution> RenderAsync(
        RenderArtifactRequest request,
        string operationId,
        CancellationToken cancellationToken);
}

public interface IArtifactVerificationHandler
{
    string VerificationProfileId { get; }

    Task<ArtifactWorkerJobExecution> VerifyAsync(
        VerifyArtifactRequest request,
        string operationId,
        CancellationToken cancellationToken);
}

public interface IArtifactWorkerReadinessCheck
{
    string Name { get; }

    Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken);
}

public sealed record ArtifactWorkerCapabilityManifest
{
    public required int SchemaVersion { get; init; }

    public required string WorkerId { get; init; }

    public required string ProtocolVersion { get; init; }

    public required IReadOnlyList<string> Capabilities { get; init; }

    public required IReadOnlyList<string> AcceptedArtifactFormats { get; init; }

    public required IReadOnlyList<string> ProducedArtifactFormats { get; init; }

    public required IReadOnlyList<string> TransformIds { get; init; }

    public required IReadOnlyList<string> RenderOutputIds { get; init; }

    public required IReadOnlyList<string> VerificationProfileIds { get; init; }

    public required ArtifactWorkerLimits Limits { get; init; }
}

public sealed record ArtifactWorkerLimits(
    int MaximumInputArtifactBytes,
    int MaximumOutputArtifactBytes,
    int MaximumConcurrentOperations,
    int MaximumOperationMilliseconds,
    int MaximumRetainedOperations,
    int MaximumEventsPerOperation);

public sealed record ArtifactWorkerHostIdentity(string WorkerImageId);

public class ArtifactWorkerException : Exception
{
    public ArtifactWorkerException(
        string code,
        WorkerErrorCategory category,
        string publicMessage,
        bool retryable,
        bool safeToRetry,
        Exception? innerException = null)
        : base(publicMessage, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicMessage);
        Code = code;
        Category = category;
        PublicMessage = publicMessage;
        Retryable = retryable;
        SafeToRetry = safeToRetry;
    }

    public string Code { get; }

    public WorkerErrorCategory Category { get; }

    public string PublicMessage { get; }

    public bool Retryable { get; }

    public bool SafeToRetry { get; }
}

public sealed class ArtifactWorkerRequestException : ArtifactWorkerException
{
    public ArtifactWorkerRequestException(
        string code,
        string publicMessage,
        int statusCode = StatusCodes.Status400BadRequest,
        WorkerErrorCategory category = WorkerErrorCategory.InvalidArgument,
        Exception? innerException = null)
        : base(code, category, publicMessage, false, false, innerException)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}

public sealed class ArtifactWorkerArtifactNotFoundException(
    string publicMessage,
    Exception? innerException = null)
    : ArtifactWorkerException(
        "artifact-not-found",
        WorkerErrorCategory.NotFound,
        publicMessage,
        false,
        false,
        innerException);

public sealed class ArtifactWorkerIncompatibleArtifactException(
    string publicMessage,
    Exception? innerException = null)
    : ArtifactWorkerException(
        "incompatible-artifact",
        WorkerErrorCategory.IncompatibleArtifact,
        publicMessage,
        false,
        false,
        innerException);

public sealed class ArtifactWorkerLimitExceededException(
    string publicMessage,
    Exception? innerException = null)
    : ArtifactWorkerException(
        "artifact-limit-exceeded",
        WorkerErrorCategory.ResourceExhausted,
        publicMessage,
        false,
        false,
        innerException);

public sealed class ArtifactWorkerDependencyUnavailableException(
    string publicMessage,
    Exception? innerException = null)
    : ArtifactWorkerException(
        "artifact-dependency-unavailable",
        WorkerErrorCategory.Unavailable,
        publicMessage,
        true,
        true,
        innerException);

public sealed class ArtifactWorkerProcessorException(
    string publicMessage,
    Exception? innerException = null)
    : ArtifactWorkerException(
        "artifact-processor-failed",
        WorkerErrorCategory.Internal,
        publicMessage,
        true,
        true,
        innerException);

public sealed class ArtifactWorkerDeadlineExceededException(
    string publicMessage,
    Exception? innerException = null)
    : ArtifactWorkerException(
        "deadline-exceeded",
        WorkerErrorCategory.DeadlineExceeded,
        publicMessage,
        true,
        true,
        innerException);

public static class ArtifactWorkerErrorMapper
{
    public static WorkerError Map(
        Exception exception,
        string traceId,
        string workerId,
        string workerImageId)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(traceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerImageId);

        var mapped = exception as ArtifactWorkerException;
        return new WorkerError(
            mapped?.Code ?? "artifact-worker-internal",
            mapped?.Category ?? WorkerErrorCategory.Internal,
            mapped?.PublicMessage ?? "The artifact worker failed.",
            mapped?.Retryable ?? true,
            mapped?.SafeToRetry ?? true,
            traceId,
            workerId,
            workerImageId);
    }
}
