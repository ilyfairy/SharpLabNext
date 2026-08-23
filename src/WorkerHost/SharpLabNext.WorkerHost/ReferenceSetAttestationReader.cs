using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpLabNext.Contracts;

namespace SharpLabNext.WorkerHost;

public static partial class ReferenceSetAttestationReader
{
    public const string ManifestFileName = "reference-set.attestation.json";
    private const int MaximumAttestedFiles = 2048;
    private const string NuGetPackageCompositionKind = "nuget-package-composition";
    private const string NetFx30ReferenceSetId = "netfx30-managed-ref";
    private const string NetFx30TargetFramework = "net30";
    private const string NetFx30ResolvedVersion = "net30-union-v1";
    public static ReferenceSetAttestation LoadAndVerify(
        string rootPath,
        string referenceSetId,
        string targetFramework,
        string resolvedVersion,
        string? expectedDigest,
        bool requireManifest,
        string? manifestPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceSetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFramework);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedVersion);

        var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(rootPath));
        if (!Directory.Exists(root))
            throw new InvalidDataException($"Reference set '{referenceSetId}' directory does not exist.");
        var path = string.IsNullOrWhiteSpace(manifestPath)
            ? Path.Combine(root, ManifestFileName)
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(manifestPath));
        if (!File.Exists(path))
        {
            if (requireManifest)
                throw new InvalidDataException($"Reference set '{referenceSetId}' attestation manifest is missing.");
            return CreateDevelopmentAttestation(
                root,
                referenceSetId,
                targetFramework,
                resolvedVersion,
                expectedDigest);
        }

        ReferenceSetAttestationDocument document;
        try
        {
            using var stream = File.OpenRead(path);
            document = JsonSerializer.Deserialize(
                    stream,
                    ReferenceSetAttestationJsonContext.Default.ReferenceSetAttestationDocument)
                ?? throw new InvalidDataException("The reference-set attestation manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Reference set '{referenceSetId}' attestation manifest is invalid.",
                exception);
        }

        if (document.SchemaVersion != 1 || document.ReferenceSet is null || document.Files is null)
            throw new InvalidDataException($"Reference set '{referenceSetId}' attestation schema is unsupported.");
        ValidateIdentity(
            document.ReferenceSet,
            referenceSetId,
            targetFramework,
            resolvedVersion,
            expectedDigest,
            requireManifest);
        var actualFiles = ReadActualFiles(root);
        ValidateFiles(referenceSetId, document.Files, actualFiles);
        var contentDigest = ComputeContentDigest(actualFiles);
        if (!string.Equals(document.ReferenceSet.ContentDigest, contentDigest, StringComparison.Ordinal))
            throw new InvalidDataException($"Reference set '{referenceSetId}' content digest does not match its attestation.");
        return document.ReferenceSet;
    }

    private static ReferenceSetAttestation CreateDevelopmentAttestation(
        string root,
        string referenceSetId,
        string targetFramework,
        string resolvedVersion,
        string? developmentDigest)
    {
        var files = ReadActualFiles(root);
        if (files.Count == 0)
            throw new InvalidDataException($"Reference set '{referenceSetId}' contains no assemblies.");
        return new ReferenceSetAttestation(
            referenceSetId,
            targetFramework,
            string.IsNullOrWhiteSpace(developmentDigest)
                ? $"development-{referenceSetId}"
                : developmentDigest,
            ComputeContentDigest(files),
            new ReferenceSetProvenance("development-directory", resolvedVersion));
    }

    private static void ValidateIdentity(
        ReferenceSetAttestation attestation,
        string expectedId,
        string expectedTargetFramework,
        string expectedResolvedVersion,
        string? expectedDigest,
        bool requireExpectedDigest)
    {
        if (!string.Equals(attestation.Id, expectedId, StringComparison.Ordinal) ||
            !string.Equals(attestation.TargetFramework, expectedTargetFramework, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(attestation.Digest) ||
            !IsSha256(attestation.ContentDigest) ||
            attestation.Provenance is null ||
            string.IsNullOrWhiteSpace(attestation.Provenance.Kind) ||
            string.IsNullOrWhiteSpace(attestation.Provenance.ResolvedVersion))
        {
            throw new InvalidDataException($"Reference set '{expectedId}' attestation identity is invalid.");
        }
        if (!string.Equals(
                attestation.Provenance.ResolvedVersion,
                expectedResolvedVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Reference set '{expectedId}' attestation resolved version does not match its configuration.");
        }
        if (requireExpectedDigest && string.IsNullOrWhiteSpace(expectedDigest))
        {
            throw new InvalidDataException(
                $"Reference set '{expectedId}' configured digest is missing.");
        }
        if (!string.IsNullOrWhiteSpace(expectedDigest) &&
            !string.Equals(attestation.Digest, expectedDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Reference set '{expectedId}' attestation digest does not match its configuration.");
        }

        ValidateProvenance(attestation);
    }

    private static void ValidateProvenance(ReferenceSetAttestation attestation)
    {
        var provenance = attestation.Provenance;
        if (!string.Equals(provenance.Kind, NuGetPackageCompositionKind, StringComparison.Ordinal))
        {
            if (provenance.Sources is not null)
            {
                throw new InvalidDataException(
                    $"Reference set '{attestation.Id}' has source provenance that is not valid for its kind.");
            }
            return;
        }

        if (!string.Equals(attestation.Id, NetFx30ReferenceSetId, StringComparison.Ordinal) ||
            !string.Equals(attestation.TargetFramework, NetFx30TargetFramework, StringComparison.Ordinal) ||
            !string.Equals(provenance.ResolvedVersion, NetFx30ResolvedVersion, StringComparison.Ordinal) ||
            provenance.Package is not null ||
            provenance.SourceUri is not null ||
            provenance.Commit is not null ||
            provenance.SourceArchiveDigest is not null)
        {
            throw new InvalidDataException(
                $"Reference set '{attestation.Id}' composite provenance identity is invalid.");
        }

        var sources = provenance.Sources;
        if (sources is null || sources.Count != 2)
        {
            throw new InvalidDataException(
                $"Reference set '{attestation.Id}' composite provenance must contain exactly two ordered sources.");
        }

        ValidateCompositeSource(attestation.Id, sources[0], "base", "all");
        ValidateCompositeSource(
            attestation.Id,
            sources[1],
            "extension",
            "assembly-version:3.0.0.0");

        var sourceIdentityDigest = ComputeCompositeSourceIdentityDigest(attestation, sources);
        if (!string.Equals(attestation.Digest, sourceIdentityDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Reference set '{attestation.Id}' composite source identity digest does not match its provenance.");
        }
    }

    private static void ValidateCompositeSource(
        string referenceSetId,
        ReferenceSetProvenanceSource? source,
        string expectedRole,
        string expectedSelection)
    {
        if (source is null ||
            !string.Equals(source.Role, expectedRole, StringComparison.Ordinal) ||
            !string.Equals(source.Selection, expectedSelection, StringComparison.Ordinal) ||
            HasCanonicalDelimiter(source.Package) ||
            HasCanonicalDelimiter(source.ResolvedVersion) ||
            HasCanonicalDelimiter(source.SourceUri) ||
            HasCanonicalDelimiter(source.SourceArchiveDigest) ||
            HasCanonicalDelimiter(source.PackageContentHash) ||
            !IsAbsoluteHttpsUri(source.SourceUri) ||
            !IsSha512(source.SourceArchiveDigest) ||
            !IsNuGetPackageContentHash(source.PackageContentHash))
        {
            throw new InvalidDataException(
                $"Reference set '{referenceSetId}' composite source provenance is invalid.");
        }
    }

    private static string ComputeCompositeSourceIdentityDigest(
        ReferenceSetAttestation attestation,
        IReadOnlyList<ReferenceSetProvenanceSource> sources)
    {
        var canonical = new StringBuilder()
            .Append("referenceSet=").Append(attestation.Id).Append('\n')
            .Append("targetFramework=").Append(attestation.TargetFramework).Append('\n')
            .Append("kind=").Append(attestation.Provenance.Kind).Append('\n')
            .Append("resolvedVersion=").Append(attestation.Provenance.ResolvedVersion).Append('\n');
        foreach (var source in sources)
        {
            canonical.Append("source=")
                .Append(source.Role).Append('\t')
                .Append(source.Selection).Append('\t')
                .Append(source.Package).Append('\t')
                .Append(source.ResolvedVersion).Append('\t')
                .Append(source.SourceUri).Append('\t')
                .Append(source.SourceArchiveDigest).Append('\t')
                .Append(source.PackageContentHash).Append('\n');
        }

        return $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant()}";
    }

    private static bool HasCanonicalDelimiter(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['\t', '\r', '\n']) >= 0;

    private static bool IsAbsoluteHttpsUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment);

    private static bool IsSha512(string? value)
    {
        if (value is not { Length: 135 } || !value.StartsWith("sha512:", StringComparison.Ordinal))
            return false;
        foreach (var character in value.AsSpan(7))
        {
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
        }
        return true;
    }

    private static bool IsNuGetPackageContentHash(string? value)
    {
        if (value is not { Length: > 7 } || !value.StartsWith("sha512-", StringComparison.Ordinal))
            return false;
        try
        {
            var bytes = Convert.FromBase64String(value[7..]);
            return bytes.Length == 64 &&
                   string.Equals(value[7..], Convert.ToBase64String(bytes), StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static List<ReferenceSetAttestedFile> ReadActualFiles(string root)
    {
        var paths = Directory.EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToArray();
        if (paths.Length is 0 or > MaximumAttestedFiles)
            throw new InvalidDataException("The reference set has an invalid assembly count.");

        var files = new List<ReferenceSetAttestedFile>(paths.Length);
        foreach (var path in paths)
        {
            using var stream = File.OpenRead(path);
            files.Add(new ReferenceSetAttestedFile(
                Path.GetFileName(path),
                stream.Length,
                $"sha256:{Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()}"));
        }
        return files;
    }

    private static void ValidateFiles(
        string referenceSetId,
        IReadOnlyList<ReferenceSetAttestedFile> expected,
        IReadOnlyList<ReferenceSetAttestedFile> actual)
    {
        if (expected.Count == 0 || expected.Count > MaximumAttestedFiles || expected.Count != actual.Count)
            throw new InvalidDataException($"Reference set '{referenceSetId}' attested file set does not match the loaded directory.");
        for (var index = 0; index < expected.Count; index++)
        {
            var file = expected[index];
            if (string.IsNullOrWhiteSpace(file.Path) ||
                file.Path != Path.GetFileName(file.Path) ||
                file.Size < 0 ||
                !IsSha256(file.Digest) ||
                file != actual[index])
            {
                throw new InvalidDataException($"Reference set '{referenceSetId}' contains an unattested or modified assembly.");
            }
        }
    }

    private static string ComputeContentDigest(IReadOnlyList<ReferenceSetAttestedFile> files)
    {
        var canonical = new StringBuilder();
        foreach (var file in files)
        {
            canonical.Append(file.Digest)
                .Append("  ")
                .Append(file.Size)
                .Append("  ")
                .Append(file.Path)
                .Append('\n');
        }
        return $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant()}";
    }

    private static bool IsSha256(string? value)
    {
        if (value is not { Length: 71 } || !value.StartsWith("sha256:", StringComparison.Ordinal))
            return false;
        foreach (var character in value.AsSpan(7))
        {
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
        }
        return true;
    }

    private sealed record ReferenceSetAttestationDocument(
        int SchemaVersion,
        ReferenceSetAttestation? ReferenceSet,
        IReadOnlyList<ReferenceSetAttestedFile>? Files);

    private sealed record ReferenceSetAttestedFile(
        string Path,
        long Size,
        string Digest);

    [JsonSourceGenerationOptions(
        JsonSerializerDefaults.Web,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
    [JsonSerializable(typeof(ReferenceSetAttestationDocument))]
    private sealed partial class ReferenceSetAttestationJsonContext : JsonSerializerContext;
}
