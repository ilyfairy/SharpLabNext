using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.Roslyn;

public sealed record WorkerBuildExecution(
    OperationResult Result,
    CompiledArtifact? Artifact);

public sealed record CompiledArtifact(
    ArtifactRef ArtifactRef,
    string ArtifactFormat,
    string AssemblyName,
    string ReferenceSetId,
    string TargetFramework,
    byte[] PeImage,
    byte[] PortablePdb,
    ArtifactManifest Manifest,
    IReadOnlyList<ArtifactFileDescriptor> Files,
    BuildIdentity Identity);

public sealed record WorkerBuildHttpResponse(
    string RequestId,
    OperationResult Result,
    DevelopmentArtifactEnvelope? DevelopmentArtifact);

public sealed record WorkerExplainHttpResponse(
    string RequestId,
    ExplainResult Result);

public sealed record DevelopmentArtifactEnvelope(
    ArtifactRef ArtifactRef,
    string ArtifactFormat,
    string AssemblyName,
    string ReferenceSetId,
    string TargetFramework,
    string PeImageBase64,
    string? PortablePdbBase64,
    ArtifactManifest Manifest,
    IReadOnlyList<ArtifactFileDescriptor> Files)
{
    public static DevelopmentArtifactEnvelope FromArtifact(
        CompiledArtifact artifact,
        DevelopmentArtifactEnvelopeOptions options)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(options);

        var totalBytes = checked(artifact.PeImage.Length + artifact.PortablePdb.Length);
        if (!options.Enabled)
        {
            throw new DevelopmentArtifactEnvelopeException(
                "The development artifact envelope is disabled. Configure Artifact Store integration for production builds.");
        }

        if (totalBytes > options.MaxBytes)
        {
            throw new DevelopmentArtifactEnvelopeException(
                $"The compiled artifact exceeds the {options.MaxBytes} byte development envelope limit.");
        }

        return new DevelopmentArtifactEnvelope(
            artifact.ArtifactRef,
            artifact.ArtifactFormat,
            artifact.AssemblyName,
            artifact.ReferenceSetId,
            artifact.TargetFramework,
            Convert.ToBase64String(artifact.PeImage),
            artifact.PortablePdb.Length == 0 ? null : Convert.ToBase64String(artifact.PortablePdb),
            artifact.Manifest,
            artifact.Files);
    }
}

public class RoslynWorkerException : Exception
{
    public RoslynWorkerException(string message)
        : base(message)
    {
    }

    public RoslynWorkerException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

public sealed class BuildRequestValidationException(string message) : RoslynWorkerException(message);

public sealed class ReferenceSetUnavailableException : RoslynWorkerException
{
    public ReferenceSetUnavailableException(string message)
        : base(message)
    {
    }

    public ReferenceSetUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class CompilerIdentityMismatchException(string message) : RoslynWorkerException(message);

public sealed class BuildOutputLimitExceededException(string message) : RoslynWorkerException(message);

public sealed class DevelopmentArtifactEnvelopeException(string message) : RoslynWorkerException(message);

public sealed class BuildDeadlineExceededException(string message, CancellationToken cancellationToken)
    : OperationCanceledException(message, cancellationToken);
