using SharpLabNext.Contracts;

namespace SharpLabNext.Artifacts.Contracts;

public sealed record ArtifactManifest(
    int ManifestVersion,
    ArtifactRef ArtifactId,
    ArtifactProducer Producer,
    string ReferenceSetId,
    string TargetFramework,
    string ArtifactFormat,
    ArtifactRuntimeRequirement RuntimeRequirement,
    IReadOnlyList<string> MetadataFeatureTags,
    BuildOutputKind OutputKind,
    string EntryAssembly,
    string? EntryPoint,
    IReadOnlyList<ArtifactFileDescriptor> Files,
    ArtifactDerivation? Derivation = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record ArtifactProducer(string ReleaseId, string LanguageId, string ToolchainId, string CompilerVersion, string? CompilerCommit, string WorkerImageId);

public sealed record ArtifactRuntimeRequirement(string Family, IReadOnlyList<FrameworkRequirement> Frameworks, string Architecture, IReadOnlyList<string> RequiredRuntimeFeatureTags);

public sealed record FrameworkRequirement(string Name, string MinimumVersion);

public sealed record ArtifactFileDescriptor(string Role, string Path, long Size, string Digest);

public sealed record ArtifactDerivation(ArtifactRef ParentArtifactId, string ProcessorId, string ProcessorVersion, string OptionsDigest);
