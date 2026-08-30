using System.Security.Cryptography;
using System.Text.Json;
using SharpLabNext.Catalog;

namespace SharpLabNext.BundleBuilder;

internal sealed record MaintainedProvenanceInput(
    string RelativePath,
    string FullPath,
    string ComponentId,
    string SourceComponentId,
    string License,
    string BuilderImageId,
    string? PatchSeriesDigest,
    IReadOnlyList<string> PatchPaths,
    IReadOnlyList<string> ReferencedComponentIds);

internal static class MaintainedProvenanceLoader
{
    private static readonly HashSet<string> CopiedIdentityFields =
    [
        "archiveSha256",
        "archiveUrl",
        "commit",
        "compilerVersion",
        "metadataRuntimeCommit",
        "repository",
        "runtimeVersion",
        "source"
    ];

    public static async Task<IReadOnlyList<MaintainedProvenanceInput>> LoadAsync(string repositoryRoot, ReleaseLockDocument releaseLock, BaseImageManifest baseImages, CancellationToken cancellationToken)
    {
        var provenanceRoot = Path.Combine(repositoryRoot, "profiles", "provenance");
        if (!Directory.Exists(provenanceRoot))
            throw new BundleValidationException($"Maintained provenance directory '{provenanceRoot}' is missing.");

        var baseImageIds = baseImages.Images.Select(static image => image.Id).ToHashSet(StringComparer.Ordinal);
        var documents = new List<MaintainedProvenanceInput>();
        var componentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(provenanceRoot, "*.json").Order(StringComparer.Ordinal))
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            RejectCopiedIdentities(root, Path.GetFileName(path));

            if (root.GetProperty("schemaVersion").GetInt32() != 1 || !string.Equals(root.GetProperty("status").GetString(), "source-inputs-recorded", StringComparison.Ordinal))
            {
                throw new BundleValidationException($"Maintained provenance '{path}' has an unsupported schema or status.");
            }

            var componentId = RequiredString(root, "componentId", path);
            var sourceComponentId = RequiredString(root, "sourceComponentId", path);
            var license = RequiredString(root, "license", path);
            if (!componentIds.Add(componentId))
                throw new BundleValidationException($"Maintained provenance repeats component '{componentId}'.");

            var component = RequiredComponent(releaseLock, componentId, path);
            if (string.IsNullOrWhiteSpace(component.ResolvedVersion))
                throw new BundleValidationException($"Maintained provenance '{path}' component has no resolved version.");
            var sourceComponent = RequiredComponent(releaseLock, sourceComponentId, path);
            ValidateSourceComponent(sourceComponentId, sourceComponent, path);

            var builderImageId = RequiredString(root.GetProperty("builder"), "imageId", path);
            if (!baseImageIds.Contains(builderImageId))
            {
                throw new BundleValidationException($"Maintained provenance '{path}' references unknown base image '{builderImageId}'.");
            }

            var referenced = new HashSet<string>(StringComparer.Ordinal) { componentId, sourceComponentId };
            ValidateAndCollectReferences(root, releaseLock, referenced, path);
            var (patchPaths, patchSeriesDigest) = await ResolvePatchSeriesAsync(repositoryRoot, root, path, cancellationToken);
            documents.Add(new MaintainedProvenanceInput(Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'), path, componentId, sourceComponentId, license, builderImageId, patchSeriesDigest, patchPaths, referenced.Order(StringComparer.Ordinal).ToArray()));
        }

        if (documents.Count == 0)
            throw new BundleValidationException("No maintained provenance documents were found.");
        return documents;
    }

    private static void RejectCopiedIdentities(JsonElement value, string documentName)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                RejectCopiedIdentities(item, documentName);
            return;
        }
        if (value.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in value.EnumerateObject())
        {
            if (CopiedIdentityFields.Contains(property.Name))
            {
                throw new BundleValidationException($"Maintained provenance '{documentName}' copies release identity field '{property.Name}'.");
            }
            RejectCopiedIdentities(property.Value, documentName);
        }
    }

    private static void ValidateAndCollectReferences(JsonElement root, ReleaseLockDocument releaseLock, HashSet<string> referenced, string path)
    {
        var build = root.GetProperty("build");
        if (build.TryGetProperty("referenceSet", out var referenceSet))
            AddReference(referenceSet.GetProperty("componentId").GetString(), "reference-set", releaseLock, referenced, path);
        if (build.TryGetProperty("referenceSetId", out var referenceSetId))
            AddReference(referenceSetId.GetString(), "reference-set", releaseLock, referenced, path);
        if (build.TryGetProperty("metadataRuntimeSourceComponentId", out var metadataSource))
            AddReference(metadataSource.GetString(), "source", releaseLock, referenced, path);
        if (build.TryGetProperty("sourceInputComponentIds", out var sourceInputs))
        {
            foreach (var sourceInput in sourceInputs.EnumerateArray())
                AddReference(sourceInput.GetString(), null, releaseLock, referenced, path);
        }
        if (build.TryGetProperty("runtimeComponentId", out var runtimeComponent))
            AddReference(runtimeComponent.GetString(), "runtime", releaseLock, referenced, path);
        if (build.TryGetProperty("bootstrapDependencyOverrides", out var bootstrapOverrides))
        {
            foreach (var dependency in bootstrapOverrides.EnumerateArray())
                AddReference(dependency.GetProperty("componentId").GetString(), null, releaseLock, referenced, path);
        }

        if (root.TryGetProperty("runtimeDependency", out var runtimeDependency))
        {
            AddReference(runtimeDependency.GetProperty("sourceComponentId").GetString(), "source", releaseLock, referenced, path);
            AddReference(runtimeDependency.GetProperty("runtimeComponentId").GetString(), "runtime", releaseLock, referenced, path);
        }

        if (root.TryGetProperty("artifactContract", out var contract))
        {
            if (contract.TryGetProperty("toolchainId", out var toolchainId))
                AddReference(toolchainId.GetString(), "toolchain", releaseLock, referenced, path);
            if (contract.TryGetProperty("referenceSetId", out var contractReferenceSetId))
                AddReference(contractReferenceSetId.GetString(), "reference-set", releaseLock, referenced, path);
        }
    }

    private static async Task<(IReadOnlyList<string> Paths, string? Digest)> ResolvePatchSeriesAsync(string repositoryRoot, JsonElement root, string provenancePath, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("patchSeries", out var patchSeries))
            return ([], null);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var paths = new List<string>();
        var repositoryPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot)) +
            Path.DirectorySeparatorChar;
        foreach (var patch in patchSeries.EnumerateArray())
        {
            var relativePath = RequiredString(patch, "path", provenancePath);
            var fullPath = Path.GetFullPath(Path.Combine(repositoryRoot, relativePath));
            if (!fullPath.StartsWith(repositoryPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            {
                throw new BundleValidationException($"Maintained provenance '{provenancePath}' references invalid patch '{relativePath}'.");
            }
            var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
            hash.AppendData(bytes);
            paths.Add(relativePath.Replace('\\', '/'));
        }
        return (paths, $"sha256:{Convert.ToHexStringLower(hash.GetHashAndReset())}");
    }

    private static void AddReference(string? componentId, string? expectedKind, ReleaseLockDocument releaseLock, HashSet<string> referenced, string path)
    {
        if (string.IsNullOrWhiteSpace(componentId))
            throw new BundleValidationException($"Maintained provenance '{path}' has an empty component reference.");
        var component = RequiredComponent(releaseLock, componentId, path);
        if (expectedKind is not null && !string.Equals(component.Kind, expectedKind, StringComparison.Ordinal))
        {
            throw new BundleValidationException($"Maintained provenance '{path}' references '{componentId}' as {expectedKind}, but lock kind is '{component.Kind}'.");
        }
        if (string.Equals(expectedKind, "source", StringComparison.Ordinal))
            ValidateSourceComponent(componentId, component, path);
        referenced.Add(componentId);
    }

    private static LockedComponent RequiredComponent(ReleaseLockDocument releaseLock, string componentId, string path) =>
        releaseLock.Components.TryGetValue(componentId, out var component)
            ? component : throw new BundleValidationException($"Maintained provenance '{path}' references missing lock component '{componentId}'.");

    private static void ValidateSourceComponent(string id, LockedComponent component, string path)
    {
        if (!string.Equals(component.Kind, "source", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(component.ResolvedVersion) || !IsCommit(component.Commit) || !IsSha256(component.Digest) || !Uri.TryCreate(component.SourceUri, UriKind.Absolute, out _))
        {
            throw new BundleValidationException($"Maintained provenance '{path}' source component '{id}' has incomplete lock identity.");
        }
    }

    private static string RequiredString(JsonElement element, string propertyName, string path)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new BundleValidationException($"Maintained provenance '{path}' requires string property '{propertyName}'.");
        }
        return property.GetString()!;
    }

    private static bool IsCommit(string? value) =>
        value is { Length: 40 or 64 } &&
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSha256(string? value)
    {
        if (value is not { Length: 71 } || !value.StartsWith("sha256:", StringComparison.Ordinal))
            return false;
        foreach (var character in value.AsSpan(7))
        {
            if (!(character is >= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
        }
        return true;
    }
}
