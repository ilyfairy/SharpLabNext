using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.Contracts;

namespace SharpLabNext.LanguageWorker.Sdk;

public static class LanguageArtifactBuilder
{
    public static LanguageWorkerArtifactEnvelope CreateGenericEnvelope(LanguageArtifactDefinition definition, BuildIdentity identity, int maximumArtifactBytes)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(identity);
        if (definition.Files.Count == 0)
            throw new ArgumentException("An artifact must contain at least one file.", nameof(definition));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumArtifactBytes);

        var paths = ArtifactPath.NormalizeDistinct(definition.Files.Select(static file => file.Path));
        var entryFile = ArtifactPath.Normalize(definition.EntryFile);
        if (!paths.Contains(entryFile, StringComparer.Ordinal))
            throw new ArgumentException("EntryFile must identify an artifact file.", nameof(definition));
        var totalBytes = definition.Files.Sum(static file => (long)file.Content.Length);
        if (totalBytes > maximumArtifactBytes)
            throw new LanguageWorkerRequestException("artifact-too-large", "The compiler output exceeds the configured artifact limit.", StatusCodes.Status413PayloadTooLarge);

        var descriptors = definition.Files.Select(static file =>
        {
            var bytes = file.Content.Span;
            return new ArtifactFileDescriptor(file.Role, ArtifactPath.Normalize(file.Path), bytes.Length, $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}");
        }).ToArray();
        var placeholder = new ArtifactManifest(
            ContractSchemaVersions.ArtifactManifest,
            new ArtifactRef($"sha256:{new string('0', 64)}"),
            new ArtifactProducer(identity.ReleaseId, identity.LanguageId, identity.ToolchainId, identity.CompilerVersion, identity.CompilerCommit, identity.WorkerImageId),
            definition.ReferenceSetId,
            definition.TargetFramework,
            definition.ArtifactFormat,
            definition.RuntimeRequirement,
            definition.MetadataFeatureTags,
            definition.OutputKind,
            entryFile,
            definition.EntryPoint,
            descriptors,
            Metadata: definition.Metadata);
        var manifest = ArtifactIdentity.WithComputedId(placeholder);
        var contents = definition.Files.ToDictionary(static file => ArtifactPath.Normalize(file.Path), static file => Convert.ToBase64String(file.Content.Span), StringComparer.Ordinal);
        return new LanguageWorkerArtifactEnvelope(manifest.ArtifactId, definition.ArtifactFormat, definition.DisplayName, definition.ReferenceSetId, definition.TargetFramework, null, null, manifest, descriptors, contents);
    }
}
