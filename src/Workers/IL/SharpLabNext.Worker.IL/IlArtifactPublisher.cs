using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.IL;

public sealed class IlArtifactPublisher(IArtifactStoreClient artifactStore, ArtifactBundlePublishingOptions options)
{
    public async Task<ArtifactRef> PublishAsync(IlCompiledArtifact artifact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.ArtifactRef != artifact.Manifest.ArtifactId || !string.Equals(artifact.ArtifactFormat, artifact.Manifest.ArtifactFormat, StringComparison.Ordinal) || !string.Equals(artifact.ReferenceSetId, artifact.Manifest.ReferenceSetId, StringComparison.Ordinal) || !string.Equals(artifact.TargetFramework, artifact.Manifest.TargetFramework, StringComparison.Ordinal) || !artifact.Files.SequenceEqual(artifact.Manifest.Files))
        {
            throw new InvalidOperationException("The compiled IL artifact conflicts with its manifest.");
        }

        var stored = await new ArtifactBundlePublisher(artifactStore).PublishAsync(artifact.Manifest, [new ArtifactBundleContent($"{artifact.AssemblyName}.dll", artifact.PeImage)], options.TimeToLive, cancellationToken).ConfigureAwait(false);
        return stored.ArtifactRef;
    }
}
