using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.Contracts;

namespace SharpLabNext.ArtifactWorker;

internal sealed record ArtifactJobExecution(OperationResult Result, ProducedContent? Content = null, ProducedArtifact? Artifact = null);

internal sealed record ProducedContent(ContentRef ContentRef, string MediaType, long Size);

internal sealed record ProducedArtifact(ArtifactRef ArtifactRef, string ArtifactFormat, string Role);

internal interface IArtifactJobExecutor
{
    Task<ArtifactJobExecution> TransformAsync(TransformArtifactRequest request, string operationId, CancellationToken cancellationToken);

    Task<ArtifactJobExecution> RenderAsync(RenderArtifactRequest request, string operationId, CancellationToken cancellationToken);

    Task<ArtifactJobExecution> VerifyAsync(VerifyArtifactRequest request, string operationId, CancellationToken cancellationToken);
}

internal class ArtifactWorkerException : Exception
{
    public ArtifactWorkerException(string message) : base(message) { }

    public ArtifactWorkerException(string message, Exception innerException) : base(message, innerException) { }
}

internal sealed class ArtifactRequestValidationException(string message) : ArtifactWorkerException(message);

internal sealed class ArtifactStoreUnavailableException(string message, Exception innerException) : ArtifactWorkerException(message, innerException);

internal sealed class ArtifactNotFoundException(string message) : ArtifactWorkerException(message);

internal sealed class ArtifactProcessorCrashedException(string message) : ArtifactWorkerException(message);
