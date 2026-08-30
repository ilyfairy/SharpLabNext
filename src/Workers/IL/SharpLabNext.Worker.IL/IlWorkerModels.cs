using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.IL;

public sealed record IlWorkerBuildExecution(OperationResult Result, IlCompiledArtifact? Artifact);

public sealed record IlCompiledArtifact(
    ArtifactRef ArtifactRef,
    string ArtifactFormat,
    string AssemblyName,
    string ReferenceSetId,
    string TargetFramework,
    byte[] PeImage,
    ArtifactManifest Manifest,
    IReadOnlyList<ArtifactFileDescriptor> Files,
    BuildIdentity Identity);

public sealed record IlWorkerBuildHttpResponse(string RequestId, OperationResult Result, IlDevelopmentArtifactEnvelope? DevelopmentArtifact);

public sealed record IlDevelopmentArtifactEnvelope(
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
    public static IlDevelopmentArtifactEnvelope FromArtifact(IlCompiledArtifact artifact, IlDevelopmentArtifactEnvelopeOptions options)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
            throw new IlDevelopmentArtifactEnvelopeException("The development artifact envelope is disabled.");
        if (artifact.PeImage.Length > options.MaxBytes)
            throw new IlDevelopmentArtifactEnvelopeException($"The IL artifact exceeds the {options.MaxBytes} byte envelope limit.");
        return new IlDevelopmentArtifactEnvelope(artifact.ArtifactRef, artifact.ArtifactFormat, artifact.AssemblyName, artifact.ReferenceSetId, artifact.TargetFramework, Convert.ToBase64String(artifact.PeImage), null, artifact.Manifest, artifact.Files);
    }
}

public class IlWorkerException : Exception
{
    public IlWorkerException(string message) : base(message) { }
    public IlWorkerException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class IlBuildRequestValidationException(string message) : IlWorkerException(message);
public sealed class IlReferenceSetUnavailableException : IlWorkerException
{
    public IlReferenceSetUnavailableException(string message) : base(message) { }
    public IlReferenceSetUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}
public sealed class IlAssemblerUnavailableException(string message, Exception? innerException = null) : IlWorkerException(message, innerException ?? new InvalidOperationException(message));
public sealed class IlBuildOutputLimitExceededException(string message) : IlWorkerException(message);
public sealed class IlDevelopmentArtifactEnvelopeException(string message) : IlWorkerException(message);
public sealed class IlBuildDeadlineExceededException(string message, CancellationToken cancellationToken) : OperationCanceledException(message, cancellationToken);
