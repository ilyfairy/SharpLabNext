using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.Client;

public sealed record ToolchainWorkerClientSettings(
    string WorkerId,
    string ExpectedReleaseId,
    string? ExpectedWorkerImageId = null,
    IReadOnlyDictionary<string, string>? ExpectedReferenceSetDigests = null);

public sealed record ToolchainBuildResponse(
    string RequestId,
    OperationResult Result,
    WorkerArtifactEnvelope? DevelopmentArtifact);

public sealed record ToolchainExplainResponse(
    string RequestId,
    ExplainResult Result);

public sealed record WorkerArtifactEnvelope(
    ArtifactRef ArtifactRef,
    string ArtifactFormat,
    string AssemblyName,
    string ReferenceSetId,
    string TargetFramework,
    string? PeImageBase64,
    string? PortablePdbBase64,
    ArtifactManifest Manifest,
    IReadOnlyList<ArtifactFileDescriptor> Files,
    IReadOnlyDictionary<string, string>? FileContentsBase64 = null);

public sealed class ToolchainWorkerException : Exception
{
    public ToolchainWorkerException(WorkerError error, int? statusCode = null, Exception? innerException = null)
        : base(error.PublicMessage, innerException)
    {
        Error = error;
        StatusCode = statusCode;
    }

    public WorkerError Error { get; }

    public int? StatusCode { get; }
}
