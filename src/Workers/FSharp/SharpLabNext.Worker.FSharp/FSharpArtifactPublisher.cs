using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.FSharp;

public sealed class FSharpArtifactPublisher(IArtifactStoreClient artifactStore, ArtifactBundlePublishingOptions options)
{
    public async Task<ArtifactRef> PublishAsync(FSharpCompiledArtifact artifact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ValidateArtifact(artifact);

        var contents = new List<ArtifactBundleContent> { new($"{artifact.AssemblyName}.dll", artifact.PeImage), new("FSharp.Core.dll", artifact.FSharpCoreImage) };
        if (artifact.PortablePdb.Length > 0)
            contents.Add(new ArtifactBundleContent($"{artifact.AssemblyName}.pdb", artifact.PortablePdb));

        var stored = await new ArtifactBundlePublisher(artifactStore).PublishAsync(artifact.Manifest, contents, options.TimeToLive, cancellationToken).ConfigureAwait(false);
        return stored.ArtifactRef;
    }

    internal static void ValidateArtifact(FSharpCompiledArtifact artifact)
    {
        ArtifactIdentity.Validate(artifact.Manifest);
        if (artifact.ArtifactRef != artifact.Manifest.ArtifactId || artifact.ArtifactRef != ArtifactIdentity.Compute(artifact.Manifest) || !string.Equals(artifact.ArtifactFormat, artifact.Manifest.ArtifactFormat, StringComparison.Ordinal) || !string.Equals(artifact.ReferenceSetId, artifact.Manifest.ReferenceSetId, StringComparison.Ordinal) || !string.Equals(artifact.TargetFramework, artifact.Manifest.TargetFramework, StringComparison.Ordinal) || !artifact.Files.SequenceEqual(artifact.Manifest.Files))
        {
            throw new InvalidOperationException("The compiled F# artifact conflicts with its manifest.");
        }

        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [$"{artifact.AssemblyName}.dll"] = artifact.PeImage,
            ["FSharp.Core.dll"] = artifact.FSharpCoreImage
        };
        if (artifact.PortablePdb.Length > 0)
            contents.Add($"{artifact.AssemblyName}.pdb", artifact.PortablePdb);
        if (artifact.Manifest.Files.Count != contents.Count || !StringComparer.Ordinal.Equals(artifact.Manifest.EntryAssembly, $"{artifact.AssemblyName}.dll"))
        {
            throw new InvalidOperationException("The compiled F# artifact contains an unsupported file layout.");
        }
        foreach (var file in artifact.Manifest.Files)
        {
            if (!contents.TryGetValue(file.Path, out var content) || file.Size != content.LongLength || !StringComparer.Ordinal.Equals(file.Digest, ContentIdentity.Compute(content).Value))
            {
                throw new InvalidOperationException($"The compiled F# artifact file '{file.Path}' failed size or checksum validation.");
            }
        }
    }
}
