using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.Contracts;
using SharpLabNext.Worker.Client;

namespace SharpLabNext.Gateway;

public interface IBuildArtifactPublisher
{
    Task<PublishedBuildArtifact> PublishAsync(
        WorkerArtifactEnvelope envelope,
        CancellationToken cancellationToken);

    Task<PublishedBuildArtifact> AcceptPublishedAsync(
        ArtifactRef artifactRef,
        BuildIdentity identity,
        CancellationToken cancellationToken);
}

public sealed record PublishedBuildArtifact(
    ArtifactRef ArtifactRef,
    ArtifactManifest Manifest,
    string ArtifactFormat);

public sealed class BuildArtifactPublishingException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class BuildArtifactPublisher(
    IArtifactStoreClient artifactStore,
    BuildPipelineOptions options) : IBuildArtifactPublisher
{
    public async Task<PublishedBuildArtifact> PublishAsync(
        WorkerArtifactEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ValidateEnvelopeMetadata(envelope);

        var contents = envelope.FileContentsBase64 is null
            ? ReadLegacyManagedPeEnvelope(envelope)
            : ReadGenericEnvelope(envelope);
        if (contents.Values.Sum(static value => value.LongLength) > options.MaximumWorkerArtifactBytes)
            throw new BuildArtifactPublishingException("The worker artifact exceeds the Gateway transfer limit.");

        ArtifactManifest canonicalManifest;
        try
        {
            canonicalManifest = ArtifactIdentity.WithComputedId(envelope.Manifest);
        }
        catch (ArgumentException exception)
        {
            throw new BuildArtifactPublishingException("The worker artifact manifest is invalid.", exception);
        }
        if (canonicalManifest.ArtifactId != envelope.ArtifactRef)
            throw new BuildArtifactPublishingException("The worker artifact ID does not match its canonical manifest.");

        var uploads = canonicalManifest.Files.Select(file =>
        {
            var bytes = contents[file.Path];
            return new ArtifactFileUpload(file.Path, new MemoryStream(bytes, writable: false), bytes.LongLength);
        }).ToArray();

        PutArtifactResponse stored;
        try
        {
            stored = await artifactStore.PutArtifactAsync(
                canonicalManifest,
                uploads,
                options.ArtifactTimeToLive,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var upload in uploads)
            {
                upload.Content.Dispose();
            }
        }
        if (stored.ArtifactRef != canonicalManifest.ArtifactId)
        {
            throw new BuildArtifactPublishingException("Artifact Store returned a different content address.");
        }

        return new PublishedBuildArtifact(stored.ArtifactRef, canonicalManifest, envelope.ArtifactFormat);
    }

    public async Task<PublishedBuildArtifact> AcceptPublishedAsync(
        ArtifactRef artifactRef,
        BuildIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        try
        {
            _ = ArtifactStoreProtocol.ParseArtifactRef(artifactRef.Value);
        }
        catch (ArgumentException exception)
        {
            throw new BuildArtifactPublishingException("The worker artifact reference is invalid.", exception);
        }

        var bundle = await artifactStore.GetArtifactAsync(artifactRef, cancellationToken).ConfigureAwait(false)
            ?? throw new BuildArtifactPublishingException(
                "The worker returned an artifact reference that is not present in the Artifact Store.");
        ValidatePublishedBundle(artifactRef, identity, bundle);
        return new PublishedBuildArtifact(
            artifactRef,
            bundle.Manifest,
            bundle.Manifest.ArtifactFormat);
    }

    private Dictionary<string, byte[]> ReadLegacyManagedPeEnvelope(WorkerArtifactEnvelope envelope)
    {
        if (envelope.PeImageBase64 is null)
            throw new BuildArtifactPublishingException("The worker PE image was empty.");
        var peImage = DecodeBase64(envelope.PeImageBase64, "PE image");
        var portablePdb = envelope.PortablePdbBase64 is null
            ? null
            : DecodeBase64(envelope.PortablePdbBase64, "portable PDB");
        var primaryAssembly = SingleFileByRole(envelope.Manifest.Files, "primary-assembly", required: true)!;
        var portablePdbFile = SingleFileByRole(envelope.Manifest.Files, "portable-pdb", required: false);
        if (!string.Equals(primaryAssembly.Path, envelope.Manifest.EntryAssembly, StringComparison.Ordinal))
            throw new BuildArtifactPublishingException("The worker entry assembly does not match the primary assembly.");
        if (envelope.Manifest.Files.Count != (portablePdbFile is null ? 1 : 2))
            throw new BuildArtifactPublishingException("The legacy worker envelope contains unsupported file roles.");
        ValidateFile(primaryAssembly, peImage);
        if ((portablePdbFile is null) != (portablePdb is null))
            throw new BuildArtifactPublishingException("The worker portable PDB bytes do not match the manifest.");
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [primaryAssembly.Path] = peImage
        };
        if (portablePdbFile is not null)
        {
            ValidateFile(portablePdbFile, portablePdb!);
            result.Add(portablePdbFile.Path, portablePdb!);
        }
        return result;
    }

    private Dictionary<string, byte[]> ReadGenericEnvelope(WorkerArtifactEnvelope envelope)
    {
        if (envelope.PeImageBase64 is not null || envelope.PortablePdbBase64 is not null)
            throw new BuildArtifactPublishingException("A generic worker envelope cannot mix legacy PE fields.");
        var encoded = envelope.FileContentsBase64!;
        var expectedPaths = envelope.Manifest.Files.Select(static file => file.Path).Order(StringComparer.Ordinal).ToArray();
        var actualPaths = encoded.Keys.Select(ArtifactPath.Normalize).Order(StringComparer.Ordinal).ToArray();
        if (!expectedPaths.SequenceEqual(actualPaths, StringComparer.Ordinal))
            throw new BuildArtifactPublishingException("The generic worker envelope does not contain every manifest file exactly once.");
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in envelope.Manifest.Files)
        {
            var bytes = DecodeBase64(encoded[file.Path], $"artifact file '{file.Path}'");
            ValidateFile(file, bytes);
            result.Add(file.Path, bytes);
        }
        return result;
    }

    private byte[] DecodeBase64(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new BuildArtifactPublishingException($"The worker {description} was empty.");
        }

        var maximumEncodedLength = checked(((options.MaximumWorkerArtifactBytes + 2) / 3) * 4 + 4);
        if (value.Length > maximumEncodedLength)
        {
            throw new BuildArtifactPublishingException($"The worker {description} exceeds the transfer limit.");
        }

        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException exception)
        {
            throw new BuildArtifactPublishingException($"The worker {description} was not valid base64.", exception);
        }
    }

    private static void ValidateEnvelopeMetadata(WorkerArtifactEnvelope envelope)
    {
        if (!string.Equals(envelope.ArtifactFormat, envelope.Manifest.ArtifactFormat, StringComparison.Ordinal)
            || !string.Equals(envelope.ReferenceSetId, envelope.Manifest.ReferenceSetId, StringComparison.Ordinal)
            || !string.Equals(envelope.TargetFramework, envelope.Manifest.TargetFramework, StringComparison.Ordinal))
        {
            throw new BuildArtifactPublishingException("The worker artifact envelope conflicts with its manifest.");
        }

        if (envelope.Files.Count != envelope.Manifest.Files.Count)
        {
            throw new BuildArtifactPublishingException("The worker artifact file list conflicts with its manifest.");
        }

        for (var index = 0; index < envelope.Files.Count; index++)
        {
            if (envelope.Files[index] != envelope.Manifest.Files[index])
            {
                throw new BuildArtifactPublishingException("The worker artifact file list conflicts with its manifest.");
            }
        }
    }

    private static void ValidatePublishedBundle(
        ArtifactRef requestedRef,
        BuildIdentity identity,
        ArtifactBundleDescriptor bundle)
    {
        try
        {
            ArtifactIdentity.Validate(bundle.Manifest);
        }
        catch (ArgumentException exception)
        {
            throw new BuildArtifactPublishingException(
                "The published artifact manifest is invalid.",
                exception);
        }
        if (bundle.Manifest.ArtifactId != requestedRef ||
            ArtifactIdentity.Compute(bundle.Manifest) != requestedRef)
        {
            throw new BuildArtifactPublishingException(
                "The published artifact identity does not match the worker result.");
        }

        var producer = bundle.Manifest.Producer;
        if (!string.Equals(producer.ReleaseId, identity.ReleaseId, StringComparison.Ordinal) ||
            !string.Equals(producer.LanguageId, identity.LanguageId, StringComparison.Ordinal) ||
            !string.Equals(producer.ToolchainId, identity.ToolchainId, StringComparison.Ordinal) ||
            !string.Equals(producer.CompilerVersion, identity.CompilerVersion, StringComparison.Ordinal) ||
            !string.Equals(producer.CompilerCommit, identity.CompilerCommit, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(producer.WorkerImageId, identity.WorkerImageId, StringComparison.Ordinal) ||
            !string.Equals(bundle.Manifest.ReferenceSetId, identity.ReferenceSetId, StringComparison.Ordinal))
        {
            throw new BuildArtifactPublishingException(
                "The published artifact producer identity does not match the build result.");
        }

        if (bundle.Entries.Count != bundle.Manifest.Files.Count)
            throw new BuildArtifactPublishingException("The published artifact file set is incomplete.");
        Dictionary<string, ArtifactBundleEntry> entries;
        try
        {
            entries = bundle.Entries.ToDictionary(
                static entry => ArtifactPath.Normalize(entry.Path),
                StringComparer.Ordinal);
        }
        catch (ArgumentException exception)
        {
            throw new BuildArtifactPublishingException(
                "The published artifact contains invalid or duplicate paths.",
                exception);
        }
        foreach (var file in bundle.Manifest.Files)
        {
            var path = ArtifactPath.Normalize(file.Path);
            if (!entries.TryGetValue(path, out var entry) ||
                entry.Size != file.Size ||
                !string.Equals(entry.Digest, file.Digest, StringComparison.Ordinal) ||
                !string.Equals(entry.Role, file.Role, StringComparison.Ordinal) ||
                !string.Equals(entry.ContentRef.Value, file.Digest, StringComparison.Ordinal))
            {
                throw new BuildArtifactPublishingException(
                    $"The published artifact file '{path}' conflicts with its manifest.");
            }
            try
            {
                _ = ArtifactStoreProtocol.ParseContentRef(entry.ContentRef.Value);
            }
            catch (ArgumentException exception)
            {
                throw new BuildArtifactPublishingException(
                    $"The published artifact file '{path}' has an invalid content reference.",
                    exception);
            }
        }
    }

    private static ArtifactFileDescriptor? SingleFileByRole(
        IReadOnlyList<ArtifactFileDescriptor> files,
        string role,
        bool required)
    {
        var matches = files.Where(file => string.Equals(file.Role, role, StringComparison.Ordinal)).ToArray();
        if (matches.Length > 1 || (required && matches.Length == 0))
        {
            throw new BuildArtifactPublishingException($"The worker artifact must contain exactly one '{role}' file.");
        }

        return matches.SingleOrDefault();
    }

    private static void ValidateFile(ArtifactFileDescriptor descriptor, byte[] content)
    {
        if (descriptor.Size != content.LongLength
            || !string.Equals(descriptor.Digest, ContentIdentity.Compute(content).Value, StringComparison.Ordinal))
        {
            throw new BuildArtifactPublishingException(
                $"The worker artifact file '{descriptor.Path}' failed size or checksum verification.");
        }
    }
}
