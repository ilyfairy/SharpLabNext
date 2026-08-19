using System.Security.Cryptography;
using System.Text.Json;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.Contracts;

namespace SharpLabNext.ArtifactStore.Client;

public static class ArtifactStoreProtocol
{
    public const string ApiPrefix = "/internal/v1";
    public const string ManifestPartName = "Manifest";
    public const string FilesPartName = "Files";
    public const string DigestAlgorithm = "sha256";
    public const int Sha256HexLength = 64;
    public const int ArtifactManifestVersion = 1;

    public static string GetDigest(ArtifactRef reference) => GetDigest(reference.Value, nameof(reference));

    public static string GetDigest(ContentRef reference) => GetDigest(reference.Value, nameof(reference));

    public static ArtifactRef ParseArtifactRef(string value)
    {
        _ = GetDigest(value, nameof(value));
        return new ArtifactRef(value);
    }

    public static ContentRef ParseContentRef(string value)
    {
        _ = GetDigest(value, nameof(value));
        return new ContentRef(value);
    }

    public static ArtifactRef ArtifactRefFromDigest(string digest)
    {
        ValidateDigest(digest, nameof(digest));
        return new ArtifactRef($"{DigestAlgorithm}:{digest}");
    }

    public static ContentRef ContentRefFromDigest(string digest)
    {
        ValidateDigest(digest, nameof(digest));
        return new ContentRef($"{DigestAlgorithm}:{digest}");
    }

    private static string GetDigest(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var separatorIndex = value.IndexOf(':', StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex != value.LastIndexOf(':'))
        {
            throw new ArgumentException("A reference must use the 'sha256:<lowercase-hex>' format.", parameterName);
        }

        if (!value.AsSpan(0, separatorIndex).SequenceEqual(DigestAlgorithm))
        {
            throw new ArgumentException("Only sha256 references are supported.", parameterName);
        }

        var digest = value[(separatorIndex + 1)..];
        ValidateDigest(digest, parameterName);
        return digest;
    }

    private static void ValidateDigest(string digest, string parameterName)
    {
        if (digest.Length != Sha256HexLength)
        {
            throw new ArgumentException("A SHA-256 digest must contain exactly 64 lowercase hexadecimal characters.", parameterName);
        }

        foreach (var character in digest)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                throw new ArgumentException("A SHA-256 digest must contain exactly 64 lowercase hexadecimal characters.", parameterName);
            }
        }
    }
}

public static class ArtifactPath
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Contains('\0'))
        {
            throw new ArgumentException("Artifact paths cannot contain NUL characters.", nameof(path));
        }

        if (path.Contains('\\'))
        {
            throw new ArgumentException("Artifact paths must use '/' separators.", nameof(path));
        }

        if (path[0] == '/' || Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Artifact paths must be relative.", nameof(path));
        }

        var segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new ArgumentException("Artifact paths cannot contain empty, '.' or '..' segments.", nameof(path));
        }

        return string.Join('/', segments);
    }

    public static IReadOnlyList<string> NormalizeDistinct(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var normalized = Normalize(path);
            if (!seen.Add(normalized))
            {
                throw new ArgumentException($"Artifact path '{normalized}' is duplicated.", nameof(paths));
            }

            result.Add(normalized);
        }

        return result;
    }
}

public static class ContentIdentity
{
    public static ContentRef Compute(ReadOnlySpan<byte> content) =>
        ArtifactStoreProtocol.ContentRefFromDigest(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());

    public static async Task<ContentRef> ComputeAsync(Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var digest = await SHA256.HashDataAsync(content, cancellationToken).ConfigureAwait(false);
        return ArtifactStoreProtocol.ContentRefFromDigest(Convert.ToHexString(digest).ToLowerInvariant());
    }
}

public static class ArtifactIdentity
{
    private static readonly JsonSerializerOptions JsonOptions = ContractJson.CreateCanonicalSerializerOptions();

    public static ArtifactRef Compute(ArtifactManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ValidateRequiredValues(manifest);

        var normalizedEntryAssembly = ArtifactPath.Normalize(manifest.EntryAssembly);
        var normalizedFiles = manifest.Files
            .Select(CanonicalizeFile)
            .ToArray();
        _ = ArtifactPath.NormalizeDistinct(normalizedFiles.Select(file => file.Path));

        if (normalizedFiles.Any(file => file.Size < 0))
        {
            throw new ArgumentException("Artifact file sizes cannot be negative.", nameof(manifest));
        }

        if (normalizedFiles.Length > 0 && !normalizedFiles.Any(file => file.Path == normalizedEntryAssembly))
        {
            throw new ArgumentException("EntryAssembly must name one of the artifact files.", nameof(manifest));
        }

        var canonical = new CanonicalManifest(
            manifest.ManifestVersion,
            new CanonicalProducer(
                Required(manifest.Producer.ReleaseId, nameof(manifest.Producer.ReleaseId)),
                Required(manifest.Producer.LanguageId, nameof(manifest.Producer.LanguageId)),
                Required(manifest.Producer.ToolchainId, nameof(manifest.Producer.ToolchainId)),
                Required(manifest.Producer.CompilerVersion, nameof(manifest.Producer.CompilerVersion)),
                manifest.Producer.CompilerCommit,
                Required(manifest.Producer.WorkerImageId, nameof(manifest.Producer.WorkerImageId))),
            Required(manifest.ReferenceSetId, nameof(manifest.ReferenceSetId)),
            Required(manifest.TargetFramework, nameof(manifest.TargetFramework)),
            Required(manifest.ArtifactFormat, nameof(manifest.ArtifactFormat)),
            new CanonicalRuntimeRequirement(
                Required(manifest.RuntimeRequirement.Family, nameof(manifest.RuntimeRequirement.Family)),
                manifest.RuntimeRequirement.Frameworks
                    .Select(CanonicalizeFramework)
                    .ToArray(),
                Required(manifest.RuntimeRequirement.Architecture, nameof(manifest.RuntimeRequirement.Architecture)),
                RequiredValues(manifest.RuntimeRequirement.RequiredRuntimeFeatureTags, "runtime feature tag")),
            RequiredValues(manifest.MetadataFeatureTags, "metadata feature tag"),
            manifest.OutputKind,
            normalizedEntryAssembly,
            manifest.EntryPoint,
            normalizedFiles,
            manifest.Derivation is null
                ? null
                : new CanonicalDerivation(
                    ArtifactStoreProtocol.GetDigest(manifest.Derivation.ParentArtifactId),
                    Required(manifest.Derivation.ProcessorId, nameof(manifest.Derivation.ProcessorId)),
                    Required(manifest.Derivation.ProcessorVersion, nameof(manifest.Derivation.ProcessorVersion)),
                    Required(manifest.Derivation.OptionsDigest, nameof(manifest.Derivation.OptionsDigest))),
            manifest.Metadata is null
                ? null
                : SortMetadata(manifest.Metadata));

        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, JsonOptions);
        return ArtifactStoreProtocol.ArtifactRefFromDigest(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    public static ArtifactManifest WithComputedId(ArtifactManifest manifest) => manifest with { ArtifactId = Compute(manifest) };

    public static void Validate(ArtifactManifest manifest)
    {
        var computed = Compute(manifest);
        if (computed != manifest.ArtifactId)
        {
            throw new ArgumentException($"Artifact ID mismatch. Expected '{computed}'.", nameof(manifest));
        }
    }

    private static void ValidateRequiredValues(ArtifactManifest manifest)
    {
        if (manifest.ManifestVersion <= 0)
        {
            throw new ArgumentException("ManifestVersion must be positive.", nameof(manifest));
        }

        if (manifest.OutputKind is not (
            BuildOutputKind.Console or
            BuildOutputKind.Library or
            BuildOutputKind.WindowsApplication))
        {
            throw new ArgumentException(
                "Artifact manifests must use a concrete output kind.",
                nameof(manifest));
        }

        ArgumentNullException.ThrowIfNull(manifest.Producer);
        ArgumentNullException.ThrowIfNull(manifest.RuntimeRequirement);
        ArgumentNullException.ThrowIfNull(manifest.RuntimeRequirement.Frameworks);
        ArgumentNullException.ThrowIfNull(manifest.RuntimeRequirement.RequiredRuntimeFeatureTags);
        ArgumentNullException.ThrowIfNull(manifest.MetadataFeatureTags);
        ArgumentNullException.ThrowIfNull(manifest.Files);
    }

    private static string Required(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
        {
            throw new ArgumentException($"{name} must be non-empty and cannot contain NUL characters.", name);
        }

        return value;
    }

    private static CanonicalFile CanonicalizeFile(ArtifactFileDescriptor? file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new CanonicalFile(
            Required(file.Role, nameof(file.Role)),
            ArtifactPath.Normalize(file.Path),
            file.Size,
            ArtifactStoreProtocol.GetDigest(ArtifactStoreProtocol.ParseContentRef(file.Digest)));
    }

    private static CanonicalFramework CanonicalizeFramework(FrameworkRequirement? framework)
    {
        ArgumentNullException.ThrowIfNull(framework);
        return new CanonicalFramework(
            Required(framework.Name, nameof(framework.Name)),
            Required(framework.MinimumVersion, nameof(framework.MinimumVersion)));
    }

    private static string[] RequiredValues(IEnumerable<string> values, string description) =>
        values.Select(value => Required(value, description)).ToArray();

    private static SortedDictionary<string, string> SortMetadata(IReadOnlyDictionary<string, string> metadata)
    {
        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            sorted.Add(Required(key, "metadata key"), Required(value, "metadata value"));
        }

        return sorted;
    }

    private sealed record CanonicalManifest(
        int ManifestVersion,
        CanonicalProducer Producer,
        string ReferenceSetId,
        string TargetFramework,
        string ArtifactFormat,
        CanonicalRuntimeRequirement RuntimeRequirement,
        IReadOnlyList<string> MetadataFeatureTags,
        BuildOutputKind OutputKind,
        string EntryAssembly,
        string? EntryPoint,
        IReadOnlyList<CanonicalFile> Files,
        CanonicalDerivation? Derivation,
        IReadOnlyDictionary<string, string>? Metadata);

    private sealed record CanonicalProducer(
        string ReleaseId,
        string LanguageId,
        string ToolchainId,
        string CompilerVersion,
        string? CompilerCommit,
        string WorkerImageId);

    private sealed record CanonicalRuntimeRequirement(
        string Family,
        IReadOnlyList<CanonicalFramework> Frameworks,
        string Architecture,
        IReadOnlyList<string> RequiredRuntimeFeatureTags);

    private sealed record CanonicalFramework(string Name, string MinimumVersion);

    private sealed record CanonicalFile(string Role, string Path, long Size, string Digest);

    private sealed record CanonicalDerivation(
        string ParentArtifactDigest,
        string ProcessorId,
        string ProcessorVersion,
        string OptionsDigest);
}

public sealed record PutContentResponse(
    ContentRef ContentRef,
    long Size,
    DateTimeOffset ExpiresAt,
    bool AlreadyExisted);

public sealed record PutArtifactResponse(
    ArtifactRef ArtifactRef,
    long TotalSize,
    DateTimeOffset ExpiresAt,
    bool AlreadyExisted);

public sealed record ArtifactLeaseRequest(string Owner, int DurationSeconds);

public sealed record ArtifactLeaseRenewalRequest(int DurationSeconds);

public sealed record ArtifactLeaseResponse(
    string LeaseToken,
    ArtifactRef ArtifactRef,
    string Owner,
    DateTimeOffset ExpiresAt);

public sealed record GarbageCollectionRequest(int MaxArtifacts = 1000, int MaxContents = 5000);

public sealed record GarbageCollectionResponse(
    int ExpiredLeasesDeleted,
    int ArtifactsDeleted,
    int ContentsDeleted,
    long ContentBytesReclaimed);
