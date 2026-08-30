using SharpLabNext.Contracts;

namespace SharpLabNext.Artifacts.Contracts;

public sealed record ArtifactBundleDescriptor(ArtifactManifest Manifest, IReadOnlyList<ArtifactBundleEntry> Entries);

public sealed record ArtifactBundleEntry(string Path, long Size, string Digest, string Role, ContentRef ContentRef);
