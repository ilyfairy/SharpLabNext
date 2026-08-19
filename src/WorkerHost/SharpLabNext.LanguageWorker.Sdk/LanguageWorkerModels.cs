using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.Contracts;
using Microsoft.AspNetCore.Http;

namespace SharpLabNext.LanguageWorker.Sdk;

public sealed record LanguageWorkerBuildExecution(
    OperationResult Result,
    LanguageWorkerArtifactEnvelope? Artifact = null);

public sealed record LanguageWorkerBuildHttpResponse(
    string RequestId,
    OperationResult Result,
    LanguageWorkerArtifactEnvelope? DevelopmentArtifact);

public sealed record LanguageWorkerArtifactEnvelope(
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

public sealed record LanguageArtifactFile(
    string Role,
    string Path,
    ReadOnlyMemory<byte> Content);

public sealed record LanguageArtifactDefinition(
    string ArtifactFormat,
    string DisplayName,
    string ReferenceSetId,
    string TargetFramework,
    ArtifactRuntimeRequirement RuntimeRequirement,
    IReadOnlyList<string> MetadataFeatureTags,
    BuildOutputKind OutputKind,
    string EntryFile,
    string? EntryPoint,
    IReadOnlyList<LanguageArtifactFile> Files,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record LanguageWorkerCapabilityManifest
{
    public required int SchemaVersion { get; init; }

    public required string WorkerId { get; init; }

    public required string LanguageId { get; init; }

    public required IReadOnlyList<string> ToolchainIds { get; init; }

    public required string ProtocolVersion { get; init; }

    public required IReadOnlyList<string> Capabilities { get; init; }

    public required IReadOnlyList<string> ProducedArtifactFormats { get; init; }

    public required IReadOnlyList<string> SupportedReferenceSetIds { get; init; }

    public required LanguageWorkerLimits Limits { get; init; }
}

public sealed record LanguageWorkerLimits(
    int MaximumFiles,
    int MaximumSourceUtf8Bytes,
    int MaximumArtifactBytes,
    int MaximumConcurrentBuilds,
    int MaximumBuildMilliseconds,
    int MaximumLspMessageBytes);

public sealed record LanguageWorkerHostMetadata(
    string WorkerImageId,
    string InstanceId,
    DateTimeOffset StartedAtUtc,
    IReadOnlyList<ReferenceSetAttestation>? ReferenceSets = null)
{
    public static LanguageWorkerHostMetadata Create(
        string workerId,
        string workerImageId,
        IReadOnlyList<ReferenceSetAttestation>? referenceSets = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerImageId);
        return new LanguageWorkerHostMetadata(
            workerImageId,
            $"{workerId}-{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow,
            referenceSets);
    }
}

public sealed class LanguageWorkerRequestException(
    string code,
    string publicMessage,
    int statusCode = StatusCodes.Status400BadRequest,
    Exception? innerException = null) : Exception(publicMessage, innerException)
{
    public string Code { get; } = code;

    public string PublicMessage { get; } = publicMessage;

    public int StatusCode { get; } = statusCode;
}
