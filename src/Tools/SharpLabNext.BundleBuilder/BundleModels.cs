using System.Text.Json.Serialization;

namespace SharpLabNext.BundleBuilder;

public sealed record DeploymentImageManifest
{
    public required int SchemaVersion { get; init; }

    public required IReadOnlyList<DeploymentImageDefinition> Images { get; init; }
}

public sealed record BaseImageManifest
{
    public required int SchemaVersion { get; init; }

    public required IReadOnlyList<BaseImageDefinition> Images { get; init; }
}

public sealed record BaseImageDefinition
{
    public required string Id { get; init; }

    public required string BakeVariable { get; init; }

    public required string Reference { get; init; }
}

public sealed record DeploymentImageDefinition
{
    public required string Id { get; init; }

    public required string Repository { get; init; }

    public string? ImmutableReference { get; init; }

    public bool Always { get; init; }

    public string? ComposeService { get; init; }

    public string? ToolchainId { get; init; }

    public string? RuntimeId { get; init; }

    public string? ArtifactProcessorId { get; init; }

    public string? LockComponentId { get; init; }

    public IReadOnlyList<string> LockComponentIds { get; init; } = [];

    public string? ReleaseIdEnvironment { get; init; }

    public string? ImageIdEnvironment { get; init; }
}

public sealed record ReleaseImagePlan
{
    public required int SchemaVersion { get; init; }

    public required string ReleaseId { get; init; }

    public required string ImagePrefix { get; init; }

    public required IReadOnlyList<ReleaseImagePlanEntry> Images { get; init; }
}

public sealed record ReleaseImagePlanEntry
{
    public required string Id { get; init; }

    public required string Reference { get; init; }

    public required ReleaseImageProducer Producer { get; init; }

    public string? RuntimeId { get; init; }
}

public sealed record ReleaseImageProducer
{
    public required string Kind { get; init; }

    public required string Id { get; init; }
}

public sealed record InspectedImage(
    string Id,
    string SourceReference,
    string ImageId,
    string OperatingSystem,
    string Architecture,
    long SizeBytes,
    IReadOnlyList<string> RepoDigests,
    IReadOnlyDictionary<string, string> Labels,
    string? ComposeService,
    string? ToolchainId,
    string? RuntimeId,
    string? ArtifactProcessorId,
    string LockComponentId,
    string? ReleaseIdEnvironment,
    string? ImageIdEnvironment);

public sealed record ReleaseBundleDocument
{
    public required int SchemaVersion { get; init; }

    public required string ReleaseId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required string Platform { get; init; }

    public required bool ContainsImages { get; init; }

    public required bool HasSignature { get; init; }

    public required BundleSourceDocument Source { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SignatureAlgorithm { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SignatureKeyId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SigningPublicKeySha256 { get; init; }

    public required IReadOnlyList<BundleImageDocument> Images { get; init; }
}

public sealed record BundleSourceDocument
{
    public required string Revision { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HeadRevision { get; init; }

    public required bool Dirty { get; init; }

    public required bool Verified { get; init; }

    public required bool DevelopmentOverrideUsed { get; init; }

    public required bool DevelopmentImageInputsUsed { get; init; }
}

public sealed record BundleImageDocument
{
    public required string Id { get; init; }

    public required string SourceReference { get; init; }

    public required string ImageId { get; init; }

    public required string OperatingSystem { get; init; }

    public required string Architecture { get; init; }

    public required IReadOnlyList<string> RepoDigests { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ComposeService { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RuntimeId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RuntimeCommit { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? JitCommit { get; init; }
}

public sealed record BundleBuildResult(
    string OutputDirectory,
    string ReleaseId,
    IReadOnlyList<InspectedImage> Images,
    bool ContainsImages,
    bool HasSignature);

public sealed record DependencyComponent(
    string PackageManager,
    string Name,
    string Version,
    string? Integrity,
    string License,
    string? SourceUri,
    bool Direct,
    bool Optional);

public sealed record DependencyInventoryDocument(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<DependencyComponent> Components);

public sealed record SourceMaterialDocument(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<SourceMaterialComponent> Components);

public sealed record SourceMaterialComponent(
    string PackageManager,
    string Name,
    string Version,
    string License,
    string? SourceUri,
    string MaterialPath);

public sealed record LicensePolicy
{
    public required int SchemaVersion { get; init; }

    public required IReadOnlyList<string> AllowedLicenses { get; init; }

    public required IReadOnlyDictionary<string, string> LicenseAliases { get; init; }

    public required IReadOnlyDictionary<string, string> Overrides { get; init; }

    public required IReadOnlyList<string> DeniedPrefixes { get; init; }
}

public sealed class BundleBuilderUsageException(string message) : Exception(message);

public sealed class BundleValidationException(string message) : Exception(message);
