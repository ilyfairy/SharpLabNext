using System.Text.Json.Serialization;
using SharpLabNext.Contracts;

namespace SharpLabNext.Catalog;

public sealed record CatalogDocument
{
    public required int SchemaVersion { get; init; }
    public required string Revision { get; init; }
    public required string ReleaseId { get; init; }
    public required IReadOnlyList<LanguageManifest> Languages { get; init; }
    public required IReadOnlyList<ToolchainManifest> Toolchains { get; init; }
    public required IReadOnlyList<ReferenceSetManifest> ReferenceSets { get; init; }
    public required IReadOnlyList<RuntimeManifest> Runtimes { get; init; }
    public required IReadOnlyList<ArtifactProcessorManifest> ArtifactProcessors { get; init; }
    public required IReadOnlyList<OutputManifest> Outputs { get; init; }
    public required IReadOnlyList<CompatibilityRule> Compatibility { get; init; }
    public required IReadOnlyList<ProfilePreset> Presets { get; init; }
}

public sealed record LanguageManifest
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string MonacoLanguageId { get; init; }
    public required IReadOnlyList<string> Extensions { get; init; }
    public required string DefaultFileName { get; init; }
    public required string DefaultSource { get; init; }
    public required string DefaultToolchainId { get; init; }
    public required IReadOnlyList<string> Capabilities { get; init; }
    public IReadOnlyList<string> LegacyAliases { get; init; } = [];
}

public sealed record ToolchainManifest
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string WorkerId { get; init; }
    public required string ReleaseTrack { get; init; }
    public required string ResolvedVersion { get; init; }
    public required string DefaultReferenceSetId { get; init; }
    public required IReadOnlyList<string> SupportedLanguageIds { get; init; }
    public required IReadOnlyList<string> AllowedReferenceSetIds { get; init; }
    public required IReadOnlyList<string> ProducesArtifactFormats { get; init; }
    public required IReadOnlyList<string> Capabilities { get; init; }
    public IReadOnlyList<string> MetadataFeatureTags { get; init; } = [];
    public IReadOnlyList<string> LegacyAliases { get; init; } = [];
    public required ComponentAvailability Availability { get; init; }
}

public sealed record ReferenceSetManifest
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string TargetFramework { get; init; }
    public required string Digest { get; init; }
    public required string RuntimeFamily { get; init; }
    public IReadOnlyList<string> RequiredRuntimeFeatureTags { get; init; } = [];
    public IReadOnlyList<string> MetadataFeatureTags { get; init; } = [];
    public string SupportStatus { get; init; } = "active";
    public DateOnly? SupportEndDate { get; init; }
    public string Visibility { get; init; } = "visible";
    public string? ReplacementReferenceSetId { get; init; }
    public required ComponentAvailability Availability { get; init; }
}

public sealed record RuntimeManifest
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Family { get; init; }
    public required string ResolvedVersion { get; init; }
    public required string Rid { get; init; }
    public required string Architecture { get; init; }
    public required IReadOnlyList<string> AcceptedArtifactFormats { get; init; }
    public required IReadOnlyList<string> Capabilities { get; init; }
    // These fields bind the catalog to the exact profile loaded by Runtime
    // Supervisor. They are optional for development catalogs produced before
    // the runtime identity contract was introduced.
    public string? RuntimeCommit { get; init; }
    public string? JitVersion { get; init; }
    public string? JitCommit { get; init; }
    public string? RuntimeImageId { get; init; }
    public IReadOnlyList<string> AcceptedRuntimeFamilies { get; init; } = [];
    public IReadOnlyList<RuntimeFrameworkManifest> AcceptedFrameworks { get; init; } = [];
    public string? ContainerIsolationKind { get; init; }
    public string? ContainerEnvironmentKind { get; init; }
    public string? JitSourceMappingKind { get; init; }
    public IReadOnlyList<string> ProvidedRuntimeFeatureTags { get; init; } = [];
    public IReadOnlyList<string> ProvidedMetadataFeatureTags { get; init; } = [];
    public IReadOnlyList<string> LegacyAliases { get; init; } = [];
    public string SupportStatus { get; init; } = "active";
    public DateOnly? SupportEndDate { get; init; }
    public string Visibility { get; init; } = "visible";
    public required ComponentAvailability Availability { get; init; }
}

public sealed record RuntimeFrameworkManifest
{
    public required string Name { get; init; }
    public string? MinimumVersion { get; init; }
    public string? MaximumVersion { get; init; }
    public string? ExactVersion { get; init; }
}

public sealed record ArtifactProcessorManifest
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string ResolvedVersion { get; init; }
    public required string WorkerId { get; init; }
    public required IReadOnlyList<string> AcceptsArtifactFormats { get; init; }
    public required IReadOnlyList<string> ProducesArtifactFormats { get; init; }
    public required IReadOnlyList<string> Capabilities { get; init; }
    public IReadOnlyList<ArtifactTransformationManifest> Transformations { get; init; } = [];
    public IReadOnlyList<string> AcceptedMetadataFeatureTags { get; init; } = [];
    public required ComponentAvailability Availability { get; init; }
}

public sealed record ArtifactTransformationManifest
{
    public required string Id { get; init; }
    public required string InputArtifactFormat { get; init; }
    public required string OutputArtifactFormat { get; init; }
}

public sealed record OutputManifest
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Renderer { get; init; }
    public required bool RequiresRuntime { get; init; }
    public required IReadOnlyList<string> RequiredCapabilities { get; init; }
    public IReadOnlyList<string> AcceptedArtifactFormats { get; init; } = [];
    public string? OutputArtifactFormat { get; init; }
}

[JsonConverter(typeof(KebabCaseJsonStringEnumConverter<CompatibilityRuleKind>))]
public enum CompatibilityRuleKind
{
    ToolchainReferenceSet,
    ArtifactProcessor,
    ArtifactRuntime
}

public sealed record CompatibilityRule
{
    public required string Id { get; init; }
    public required CompatibilityRuleKind Kind { get; init; }
    public required string FromId { get; init; }
    public required string ToId { get; init; }
    public required bool Allowed { get; init; }
    public string? Reason { get; init; }
    public IReadOnlyList<string> RequiredMetadataFeatureTags { get; init; } = [];
}

public sealed record ProfilePreset
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string LanguageId { get; init; }
    public required string ToolchainId { get; init; }
    public required string ReferenceSetId { get; init; }
    public required string DefaultOutputId { get; init; }
    public string? DefaultRuntimeId { get; init; }
    public IReadOnlyList<string> LegacyAliases { get; init; } = [];
    public string SupportStatus { get; init; } = "active";
    public DateOnly? SupportEndDate { get; init; }
    public string Visibility { get; init; } = "visible";
    public required ComponentAvailability Availability { get; init; }
}

public sealed record ComponentAvailability
{
    public required bool Installed { get; init; }
    public required string Health { get; init; }
    public string? Reason { get; init; }

    public bool IsSelectable => Installed && string.Equals(Health, "healthy", StringComparison.Ordinal);
}

public sealed record ReleaseLockDocument
{
    public required int SchemaVersion { get; init; }
    public required string ReleaseId { get; init; }
    public required DateTimeOffset ResolvedAt { get; init; }
    public required IReadOnlyDictionary<string, LockedComponent> Components { get; init; }
}

public sealed record LockedComponent
{
    public required string Kind { get; init; }
    public required string ResolvedVersion { get; init; }
    public string? Commit { get; init; }
    public string? JitCommit { get; init; }
    public string? Digest { get; init; }
    public string? PatchDigest { get; init; }
    public string? ImageId { get; init; }
    public string? SourceUri { get; init; }
    public string? Sha512 { get; init; }
    public string? Package { get; init; }
    public string? PackageContentHash { get; init; }
    public DateOnly? ReleaseDate { get; init; }
}
