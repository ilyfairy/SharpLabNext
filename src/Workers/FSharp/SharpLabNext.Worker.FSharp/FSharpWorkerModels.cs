using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.FSharp;

public sealed record FSharpWorkerBuildExecution(OperationResult Result, FSharpCompiledArtifact? Artifact);

public sealed record FSharpCompiledArtifact(
    ArtifactRef ArtifactRef,
    string ArtifactFormat,
    string AssemblyName,
    string ReferenceSetId,
    string TargetFramework,
    byte[] PeImage,
    byte[] PortablePdb,
    byte[] FSharpCoreImage,
    ArtifactManifest Manifest,
    IReadOnlyList<ArtifactFileDescriptor> Files,
    BuildIdentity Identity);

public sealed record FSharpWorkerBuildHttpResponse(
    string RequestId,
    OperationResult Result,
    FSharpDevelopmentArtifactEnvelope? DevelopmentArtifact);

public sealed record FSharpDevelopmentArtifactEnvelope(
    ArtifactRef ArtifactRef,
    string ArtifactFormat,
    string AssemblyName,
    string ReferenceSetId,
    string TargetFramework,
    string? PeImageBase64,
    string? PortablePdbBase64,
    ArtifactManifest Manifest,
    IReadOnlyList<ArtifactFileDescriptor> Files,
    IReadOnlyDictionary<string, string>? FileContentsBase64 = null)
{
    public static FSharpDevelopmentArtifactEnvelope FromArtifact(
        FSharpCompiledArtifact artifact,
        FSharpDevelopmentArtifactEnvelopeOptions options)
    {
        var totalBytes = checked(
            artifact.PeImage.Length +
            artifact.PortablePdb.Length +
            artifact.FSharpCoreImage.Length);
        if (!options.Enabled)
            throw new FSharpDevelopmentArtifactEnvelopeException("The development artifact envelope is disabled.");
        if (totalBytes > options.MaxBytes)
            throw new FSharpDevelopmentArtifactEnvelopeException($"The artifact exceeds the {options.MaxBytes} byte envelope limit.");
        return new FSharpDevelopmentArtifactEnvelope(
            artifact.ArtifactRef,
            artifact.ArtifactFormat,
            artifact.AssemblyName,
            artifact.ReferenceSetId,
            artifact.TargetFramework,
            null,
            null,
            artifact.Manifest,
            artifact.Files,
            CreateFileContents(artifact));
    }

    private static Dictionary<string, string> CreateFileContents(FSharpCompiledArtifact artifact)
    {
        var contents = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"{artifact.AssemblyName}.dll"] = Convert.ToBase64String(artifact.PeImage),
            ["FSharp.Core.dll"] = Convert.ToBase64String(artifact.FSharpCoreImage)
        };
        if (artifact.PortablePdb.Length > 0)
            contents.Add($"{artifact.AssemblyName}.pdb", Convert.ToBase64String(artifact.PortablePdb));
        return contents;
    }
}

public class FSharpWorkerException : Exception
{
    public FSharpWorkerException(string message) : base(message) { }
    public FSharpWorkerException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class FSharpBuildRequestValidationException(string message) : FSharpWorkerException(message);

public sealed class FSharpReferenceSetUnavailableException : FSharpWorkerException
{
    public FSharpReferenceSetUnavailableException(string message) : base(message) { }
    public FSharpReferenceSetUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class FSharpBuildOutputLimitExceededException(string message) : FSharpWorkerException(message);
public sealed class FSharpDevelopmentArtifactEnvelopeException(string message) : FSharpWorkerException(message);
public sealed class FSharpCompilerFailureException(string message) : FSharpWorkerException(message);
public sealed class FSharpBuildDeadlineExceededException(string message, CancellationToken cancellationToken)
    : OperationCanceledException(message, cancellationToken);
