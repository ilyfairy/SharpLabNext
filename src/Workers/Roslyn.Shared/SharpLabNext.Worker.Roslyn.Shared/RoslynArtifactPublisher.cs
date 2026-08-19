using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.Roslyn;

public sealed class RoslynArtifactPublisher(
    IArtifactStoreClient artifactStore,
    ArtifactBundlePublishingOptions options)
{
    public async Task<ArtifactRef> PublishAsync(
        CompiledArtifact artifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ValidateArtifact(artifact);

        var fileContents = GetFileContents(artifact)
            .Select(static pair => new ArtifactBundleContent(pair.Key, pair.Value))
            .ToArray();
        var stored = await new ArtifactBundlePublisher(artifactStore).PublishAsync(
            artifact.Manifest,
            fileContents,
            options.TimeToLive,
            cancellationToken).ConfigureAwait(false);
        return stored.ArtifactRef;
    }

    internal static void ValidateArtifact(CompiledArtifact artifact)
    {
        ArtifactIdentity.Validate(artifact.Manifest);
        if (artifact.ArtifactRef != artifact.Manifest.ArtifactId ||
            artifact.ArtifactRef != ArtifactIdentity.Compute(artifact.Manifest))
        {
            throw new InvalidOperationException(
                "The compiled artifact identity does not match its canonical manifest.");
        }
        if (!string.Equals(
                artifact.ArtifactFormat,
                artifact.Manifest.ArtifactFormat,
                StringComparison.Ordinal) ||
            !string.Equals(
                artifact.ReferenceSetId,
                artifact.Manifest.ReferenceSetId,
                StringComparison.Ordinal) ||
            !string.Equals(
                artifact.TargetFramework,
                artifact.Manifest.TargetFramework,
                StringComparison.Ordinal) ||
            !artifact.Files.SequenceEqual(artifact.Manifest.Files))
        {
            throw new InvalidOperationException(
                "The compiled artifact metadata conflicts with its manifest.");
        }
    }

    private static Dictionary<string, byte[]> GetFileContents(CompiledArtifact artifact)
    {
        var primary = SingleFile(artifact.Manifest.Files, "primary-assembly", required: true)!;
        var portablePdb = SingleFile(artifact.Manifest.Files, "portable-pdb", required: false);
        if (!string.Equals(primary.Path, artifact.Manifest.EntryAssembly, StringComparison.Ordinal) ||
            artifact.Manifest.Files.Count != (portablePdb is null ? 1 : 2))
        {
            throw new InvalidOperationException(
                "The compiled Roslyn artifact contains an unsupported file layout.");
        }
        if ((portablePdb is null) != (artifact.PortablePdb.Length == 0))
        {
            throw new InvalidOperationException(
                "The compiled Roslyn artifact portable PDB does not match its manifest.");
        }

        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [primary.Path] = artifact.PeImage
        };
        if (portablePdb is not null)
            contents.Add(portablePdb.Path, artifact.PortablePdb);

        foreach (var file in artifact.Manifest.Files)
        {
            var content = contents[file.Path];
            if (file.Size != content.LongLength ||
                !string.Equals(file.Digest, ContentIdentity.Compute(content).Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The compiled artifact file '{file.Path}' failed size or checksum validation.");
            }
        }

        return contents;
    }

    private static ArtifactFileDescriptor? SingleFile(
        IReadOnlyList<ArtifactFileDescriptor> files,
        string role,
        bool required)
    {
        var matches = files
            .Where(file => string.Equals(file.Role, role, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length > 1 || (required && matches.Length == 0))
            throw new InvalidOperationException($"The compiled artifact must contain exactly one '{role}' file.");

        return matches.SingleOrDefault();
    }

}
