using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpLabNext.Catalog;

namespace SharpLabNext.BundleBuilder;

internal sealed record WineRuntimePackageManifest
{
    public required int SchemaVersion { get; init; }

    public required string Platform { get; init; }

    public required string BaseImageId { get; init; }

    public required WineRuntimePackageComponent Component { get; init; }

    public required IReadOnlyList<WineArchiveSnapshot> ArchiveSnapshots { get; init; }

    public required IReadOnlyList<WineDirectPackage> DirectPackages { get; init; }

    public required IReadOnlyList<WineResolvedPackage> ResolvedPackages { get; init; }

    public required IReadOnlyList<WineSourcePackage> SourcePackages { get; init; }

    public required string ResolvedPackageListSha256 { get; init; }

    public required WineSourceOffer SourceOffer { get; init; }

    public required WineNoticeArchive NoticeArchive { get; init; }
}

internal sealed record WineRuntimePackageManifestSnapshot(WineRuntimePackageManifest Manifest, string ManifestSha256, ReadOnlyMemory<byte> ManifestBytes);

internal interface IWineRuntimePackageManifestSnapshotProvider
{
    Task<WineRuntimePackageManifestSnapshot> LoadValidatedAsync(string repositoryRoot, ReleaseLockDocument releaseLock, CancellationToken cancellationToken);
}

internal sealed class RepositoryWineRuntimePackageManifestSnapshotProvider : IWineRuntimePackageManifestSnapshotProvider
{
    public async Task<WineRuntimePackageManifestSnapshot> LoadValidatedAsync(string repositoryRoot, ReleaseLockDocument releaseLock, CancellationToken cancellationToken)
    {
        var snapshot = await WineRuntimePackageManifestLoader.LoadSnapshotAsync(repositoryRoot, cancellationToken);
        WineRuntimePackageManifestLoader.ValidateReleaseLock(snapshot, releaseLock);
        return snapshot;
    }
}

internal sealed record WineRuntimePackageComponent
{
    public required string Id { get; init; }

    public required string Kind { get; init; }

    public required string ResolvedVersion { get; init; }

    public required string License { get; init; }

    public required string SourceUri { get; init; }
}

internal sealed record WineDirectPackage
{
    public required string Name { get; init; }

    public required string Version { get; init; }

    public required string Architecture { get; init; }

    public required string Path { get; init; }

    public required string Sha256 { get; init; }

    public required string SourcePackage { get; init; }

    public required string License { get; init; }

    public required string SourceUri { get; init; }

    public required string SourceSha256 { get; init; }

    public required long SourceSizeBytes { get; init; }
}

internal sealed record WineArchiveSnapshot
{
    public required string Purpose { get; init; }

    public required string Id { get; init; }

    public required string Uri { get; init; }

    public required IReadOnlyList<WineArchiveSnapshotSuite> Suites { get; init; }
}

internal sealed record WineArchiveSnapshotSuite
{
    public required string Name { get; init; }

    public required string InReleaseSha256 { get; init; }

    public required long InReleaseSizeBytes { get; init; }

    public required string SigningKeyFingerprint { get; init; }

    public required IReadOnlyList<WineArchiveIndex> Indexes { get; init; }
}

internal sealed record WineArchiveIndex
{
    public required string Kind { get; init; }

    public required string Component { get; init; }

    public string? Architecture { get; init; }

    public required string Path { get; init; }

    public required string Sha256 { get; init; }

    public required long SizeBytes { get; init; }
}

internal sealed record WineResolvedPackage
{
    public required string Name { get; init; }

    public required string Version { get; init; }

    public required string Architecture { get; init; }

    public required string ArchiveSnapshotId { get; init; }

    public required string ArchiveSuite { get; init; }

    public required string ArchiveComponent { get; init; }

    public required string ArchiveIndexPath { get; init; }

    public required string Path { get; init; }

    public required string Sha256 { get; init; }

    public required long SizeBytes { get; init; }

    public required string SourcePackage { get; init; }

    public required string SourceVersion { get; init; }

    public required string CopyrightPath { get; init; }

    public required string CopyrightSha256 { get; init; }

    public required long CopyrightSizeBytes { get; init; }
}

internal sealed record WineSourcePackage
{
    public required string Name { get; init; }

    public required string Version { get; init; }

    public required string ArchiveSnapshotId { get; init; }

    public required string ArchiveSuite { get; init; }

    public required string ArchiveComponent { get; init; }

    public required string ArchiveIndexPath { get; init; }

    public required IReadOnlyList<WineSourcePackageFile> Files { get; init; }
}

internal sealed record WineSourcePackageFile
{
    public required string Path { get; init; }

    public required string Sha256 { get; init; }

    public required long SizeBytes { get; init; }
}

internal sealed record WineSourceOffer
{
    public required string BaseUri { get; init; }

    public required string Package { get; init; }

    public required string Version { get; init; }

    public required string License { get; init; }

    public required IReadOnlyList<WineSourceOfferFile> Files { get; init; }
}

internal sealed record WineSourceOfferFile
{
    public required string Path { get; init; }

    public required string Sha256 { get; init; }

    public required long SizeBytes { get; init; }
}

internal sealed record WineNoticeArchive
{
    public required string ImagePath { get; init; }

    public required string Sha256 { get; init; }

    public required long SizeBytes { get; init; }

    public required int EntryCount { get; init; }
}

internal static class WineRuntimePackageManifestLoader
{
    internal const string ManifestRelativePath = "profiles/runtime-wine-packages.json";
    internal const long MaximumSourceFileBytes = 96L * 1024 * 1024;
    internal const long MaximumSourceTotalBytes = 128L * 1024 * 1024;
    internal const int RequiredWineSourceOfferFileCount = 3;
    internal const int RequiredResolvedPackageCount = 228;
    internal const int RequiredSourcePackageCount = 162;
    internal const int RequiredSourceFileCount = 526;
    internal const long RequiredSourceTotalBytes = 840_446_201;
    internal const long MaximumClosureSourceFileBytes = 256L * 1024 * 1024;
    internal const long MaximumClosureSourceTotalBytes = 1024L * 1024 * 1024;
    internal const long MaximumNoticeArchiveBytes = 64L * 1024 * 1024;
    internal const string RequiredNoticeArchiveImagePath = "/usr/local/share/sharplabnext/wine-coreclr-copyright-notices.tar";
    private const string RequiredBaseImageId = "dotnet-runtime-deps";
    private const string RequiredComponentId = "wine-coreclr-userspace";
    private const string RequiredComponentVersion = "wine-9.0~repack-4build3+xvfb-2:21.1.12-1ubuntu1.6";
    private const string RequiredComponentSourceUri = "https://snapshot.ubuntu.com/ubuntu/20260810T000000Z/";
    private const string RequiredWineVersion = "9.0~repack-4build3";
    private const string RequiredWineSourceBaseUri = "https://snapshot.ubuntu.com/ubuntu/20260810T000000Z/pool/universe/w/wine/";
    private const string RequiredResolvedPackageListSha256 = "sha256:fa83c245764fc09102029b249f5149a48baeda53a40c0432de973ebe09e39dee";
    private const string RequiredUbuntuArchiveSigningKeyFingerprint = "F6ECB3762474EDA9D21B7022871920D1991BC93C";

    private static readonly Dictionary<string, ReviewedDirectPackage> RequiredDirectPackages =
        new Dictionary<string, ReviewedDirectPackage>(StringComparer.Ordinal)
        {
            ["wine"] = new(RequiredWineVersion, "all", "pool/universe/w/wine/wine_9.0~repack-4build3_all.deb", "5606f0f5677ac0fb882e3fdc8ce03a60232cdf2930e912ed3e42db2b9e0d5f7f", "wine", "LGPL-2.1+", RequiredWineSourceBaseUri + "wine_9.0~repack-4build3.dsc", "5d720edb86a3069749efe89c3a9d886c7faa19aa3f55f1e9c4a8e0abda8bda85", 3826),
            ["wine64"] = new(RequiredWineVersion, "amd64", "pool/universe/w/wine/wine64_9.0~repack-4build3_amd64.deb", "7f4f912b21917f28c281c99e3244ee801be2e20f0a23cd9f6d37acc97f5041ff", "wine", "LGPL-2.1+", RequiredWineSourceBaseUri + "wine_9.0~repack-4build3.dsc", "5d720edb86a3069749efe89c3a9d886c7faa19aa3f55f1e9c4a8e0abda8bda85", 3826),
            ["fonts-wine"] = new(RequiredWineVersion, "all", "pool/universe/w/wine/fonts-wine_9.0~repack-4build3_all.deb", "7dcb227d236eed96c707f532dd498ef227da27b2950ee49bbea37efc309e2bcc", "wine", "LGPL-2.1+", RequiredWineSourceBaseUri + "wine_9.0~repack-4build3.dsc", "5d720edb86a3069749efe89c3a9d886c7faa19aa3f55f1e9c4a8e0abda8bda85", 3826),
            ["xvfb"] = new("2:21.1.12-1ubuntu1.6", "amd64", "pool/universe/x/xorg-server/xvfb_21.1.12-1ubuntu1.6_amd64.deb", "9188c2ab6394dfe6aa53f782f6bfa22b7eb6febfe51a48e0e36af35cd2f64307", "xorg-server", "MIT", "https://snapshot.ubuntu.com/ubuntu/20260810T000000Z/pool/main/x/xorg-server/xorg-server_21.1.12-1ubuntu1.6.dsc", "a863f83234ff1bb20147b774820ffac461b01dc3747de8e4275f7f76b3004094", 4381)
        };

    private static readonly Dictionary<string, ReviewedSourceFile> RequiredWineSourceFiles =
        new Dictionary<string, ReviewedSourceFile>(StringComparer.Ordinal)
        {
            ["wine_9.0~repack-4build3.dsc"] = new("5d720edb86a3069749efe89c3a9d886c7faa19aa3f55f1e9c4a8e0abda8bda85", 3826),
            ["wine_9.0~repack.orig.tar.xz"] = new("b956a23e00a5083f46c5c5ce0fbb3428460548a55ec1414cc20c6c21c7c8d0a7", 26988196),
            ["wine_9.0~repack-4build3.debian.tar.xz"] = new("0e1ac34c2272c560df213602495e2792de8a1c31bf27a6b6fbea39289dfc145a", 58753032)
        };

    private static readonly string[] RequiredWineSourceOfferPaths =
    [
        "wine_9.0~repack-4build3.dsc",
        "wine_9.0~repack.orig.tar.xz",
        "wine_9.0~repack-4build3.debian.tar.xz"
    ];

    private static readonly IReadOnlyDictionary<string, ReviewedArchiveSnapshot> RequiredArchiveSnapshots =
        new Dictionary<string, ReviewedArchiveSnapshot>(StringComparer.Ordinal)
        {
            ["20260810T000000Z"] = new(
                "operator-installation",
                "https://snapshot.ubuntu.com/ubuntu/20260810T000000Z/",
                new Dictionary<string, ReviewedArchiveSuite>(StringComparer.Ordinal)
                {
                    ["noble"] = new("cdb2f31d809f589719a53c6ad15f255b27569c4059542ada282aaa21b8e164b0", 255_850),
                    ["noble-updates"] = new("ef81441269d3a8bdd8cdfe9095de7deb7f1af70d42191f61f1af3c8fb72cfb32", 126_125),
                    ["noble-security"] = new("3cfb1c8d7499c0bac1bfbe1e32675d200f0ca74b18afc4248c45325a073d0fd0", 126_127)
                }),
            ["20260610T000000Z"] = new(
                "base-image-package-evidence",
                "https://snapshot.ubuntu.com/ubuntu/20260610T000000Z/",
                new Dictionary<string, ReviewedArchiveSuite>(StringComparer.Ordinal)
                {
                    ["noble-updates"] = new("f51355c88d0b337b45cede930d215a56f806b7c9339e95487b6600ea02c728ce", 126_125)
                })
        };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static async Task<WineRuntimePackageManifest> LoadAsync(string repositoryRoot, CancellationToken cancellationToken) =>
        (await LoadSnapshotAsync(repositoryRoot, cancellationToken)).Manifest;

    internal static async Task<WineRuntimePackageManifestSnapshot> LoadSnapshotAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var path = Path.Combine(repositoryRoot, ManifestRelativePath);
        if (!File.Exists(path))
            throw new BundleValidationException("Wine package inventory manifest is missing.");

        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var manifest = JsonSerializer.Deserialize<WineRuntimePackageManifest>(bytes, JsonOptions) ?? throw new BundleValidationException("Wine package inventory manifest is empty.");
            Validate(manifest);
            return new WineRuntimePackageManifestSnapshot(manifest, $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}", bytes);
        }
        catch (JsonException exception)
        {
            throw new BundleValidationException($"Wine package inventory manifest is invalid: {exception.Message}");
        }
    }

    internal static void ValidateBaseImage(WineRuntimePackageManifest manifest, BaseImageManifest baseImages)
    {
        if (!baseImages.Images.Any(image => string.Equals(image.Id, manifest.BaseImageId, StringComparison.Ordinal)))
        {
            throw new BundleValidationException($"Wine package inventory references unknown base image '{manifest.BaseImageId}'.");
        }
    }

    internal static void ValidateResolvedPackagesForBundle(WineRuntimePackageManifest manifest)
    {
        if (manifest.ResolvedPackages.Count != RequiredResolvedPackageCount || manifest.SourcePackages.Count != RequiredSourcePackageCount)
        {
            throw new BundleValidationException($"Wine operating-system inventory must contain exactly {RequiredResolvedPackageCount} binary packages and {RequiredSourcePackageCount} source packages.");
        }
        ValidateArchiveSnapshots(manifest.ArchiveSnapshots);
        ValidateSourcePackages(manifest.SourcePackages, manifest.ArchiveSnapshots, requireReviewedTotal: false);
        ValidateResolvedPackages(manifest.ResolvedPackages, manifest.SourcePackages, manifest.ArchiveSnapshots);
        ValidateNoticeArchive(manifest.NoticeArchive, manifest.ResolvedPackages);
    }

    internal static void ValidateReleaseLock(WineRuntimePackageManifestSnapshot snapshot, ReleaseLockDocument releaseLock)
    {
        var manifest = snapshot.Manifest;
        if (!releaseLock.Components.TryGetValue(manifest.Component.Id, out var component) || !string.Equals(component.Kind, manifest.Component.Kind, StringComparison.Ordinal) || !string.Equals(component.ResolvedVersion, manifest.Component.ResolvedVersion, StringComparison.Ordinal) || !string.Equals(component.Digest, snapshot.ManifestSha256, StringComparison.Ordinal) || !string.Equals(component.SourceUri, manifest.Component.SourceUri, StringComparison.Ordinal))
        {
            throw new BundleValidationException($"Wine package inventory '{manifest.Component.Id}' does not match its release lock identity.");
        }
    }

    internal static Uri SourceUri(WineRuntimePackageManifest manifest, WineSourceOfferFile file)
    {
        var uri = new Uri(new Uri(manifest.SourceOffer.BaseUri, UriKind.Absolute), file.Path);
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.Equals(uri.AbsolutePath, new Uri(manifest.SourceOffer.BaseUri).AbsolutePath + file.Path, StringComparison.Ordinal))
        {
            throw new BundleValidationException("Wine source offer file URL is not canonical HTTPS.");
        }

        return uri;
    }

    internal static Uri ArchiveUri(WineRuntimePackageManifest manifest, string archiveSnapshotId, string relativePath)
    {
        var snapshot = manifest.ArchiveSnapshots.SingleOrDefault(item => string.Equals(item.Id, archiveSnapshotId, StringComparison.Ordinal)) ?? throw new BundleValidationException($"Wine package evidence references unknown archive snapshot '{archiveSnapshotId}'.");
        if (!IsCanonicalPoolPath(relativePath))
            throw new BundleValidationException("Wine package evidence has an unsafe archive path.");
        var uri = new Uri(new Uri(snapshot.Uri, UriKind.Absolute), relativePath);
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new BundleValidationException("Wine package evidence URL is not canonical HTTPS.");
        }
        return uri;
    }

    private static void Validate(WineRuntimePackageManifest manifest)
    {
        if (manifest.SchemaVersion != 1 ||
            !string.Equals(manifest.Platform, "linux/amd64", StringComparison.Ordinal) ||
            !string.Equals(manifest.BaseImageId, RequiredBaseImageId, StringComparison.Ordinal) ||
            !string.Equals(manifest.Component.Id, RequiredComponentId, StringComparison.Ordinal) ||
            !string.Equals(manifest.Component.Kind, "runtime-dependency", StringComparison.Ordinal) ||
            !string.Equals(manifest.Component.ResolvedVersion, RequiredComponentVersion, StringComparison.Ordinal) ||
            !string.Equals(manifest.Component.License, "LGPL-2.1+", StringComparison.Ordinal) ||
            !string.Equals(manifest.Component.SourceUri, RequiredComponentSourceUri, StringComparison.Ordinal) ||
            manifest.ArchiveSnapshots.Count != RequiredArchiveSnapshots.Count ||
            !string.Equals(manifest.SourceOffer.Package, "wine", StringComparison.Ordinal) ||
            !string.Equals(manifest.SourceOffer.Version, RequiredWineVersion, StringComparison.Ordinal) ||
            !string.Equals(manifest.SourceOffer.License, "LGPL-2.1+", StringComparison.Ordinal) ||
            !string.Equals(manifest.SourceOffer.BaseUri, RequiredWineSourceBaseUri, StringComparison.Ordinal) ||
            manifest.DirectPackages.Count != RequiredDirectPackages.Count ||
            manifest.ResolvedPackages.Count != RequiredResolvedPackageCount ||
            manifest.SourcePackages.Count != RequiredSourcePackageCount ||
            !string.Equals(manifest.ResolvedPackageListSha256, RequiredResolvedPackageListSha256, StringComparison.Ordinal) ||
            manifest.SourceOffer.Files.Count != RequiredWineSourceOfferFileCount)
        {
            throw new BundleValidationException("Wine package inventory manifest has an invalid reviewed identity.");
        }

        ValidateArchiveSnapshots(manifest.ArchiveSnapshots);
        ValidateDirectPackages(manifest.DirectPackages);
        ValidateSourcePackages(manifest.SourcePackages, manifest.ArchiveSnapshots, requireReviewedTotal: true);
        ValidateResolvedPackages(manifest.ResolvedPackages, manifest.SourcePackages, manifest.ArchiveSnapshots);
        ValidateDirectPackagesAreResolved(manifest.DirectPackages, manifest.ResolvedPackages);
        ValidateResolvedPackageDigest(manifest.ResolvedPackages, manifest.ResolvedPackageListSha256);
        ValidateSourceOffer(manifest.SourceOffer, manifest.SourcePackages);
        ValidateNoticeArchive(manifest.NoticeArchive, manifest.ResolvedPackages);
    }

    private static void ValidateArchiveSnapshots(IReadOnlyList<WineArchiveSnapshot> snapshots)
    {
        var actual = new Dictionary<string, WineArchiveSnapshot>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            if (!actual.TryAdd(snapshot.Id, snapshot) || !RequiredArchiveSnapshots.TryGetValue(snapshot.Id, out var expected) || !string.Equals(snapshot.Purpose, expected.Purpose, StringComparison.Ordinal) || !string.Equals(snapshot.Uri, expected.Uri, StringComparison.Ordinal) || snapshot.Suites.Count != expected.Suites.Count)
            {
                throw new BundleValidationException("Wine archive snapshot inventory has an invalid reviewed identity.");
            }

            var suites = snapshot.Suites.ToDictionary(static suite => suite.Name, StringComparer.Ordinal);
            if (suites.Count != snapshot.Suites.Count || expected.Suites.Any(pair => !suites.TryGetValue(pair.Key, out var suite) || !string.Equals(suite.InReleaseSha256, pair.Value.Sha256, StringComparison.Ordinal) || suite.InReleaseSizeBytes != pair.Value.SizeBytes || !string.Equals(suite.SigningKeyFingerprint, RequiredUbuntuArchiveSigningKeyFingerprint, StringComparison.Ordinal)))
            {
                throw new BundleValidationException($"Wine archive snapshot '{snapshot.Id}' does not match its reviewed signed index identity.");
            }
            foreach (var suite in snapshot.Suites)
                ValidateArchiveIndexes(snapshot.Id, suite);
        }

        if (!actual.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(RequiredArchiveSnapshots.Keys))
        {
            throw new BundleValidationException("Wine archive snapshot inventory is incomplete.");
        }
    }

    private static void ValidateArchiveIndexes(string snapshotId, WineArchiveSnapshotSuite suite)
    {
        if (suite.Indexes.Count == 0)
            throw new BundleValidationException($"Wine archive suite '{snapshotId}/{suite.Name}' has no signed indexes.");

        var identities = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var index in suite.Indexes)
        {
            var binary = string.Equals(index.Kind, "binary", StringComparison.Ordinal);
            var source = string.Equals(index.Kind, "source", StringComparison.Ordinal);
            var architectureValid = binary
                ? string.Equals(index.Architecture, "amd64", StringComparison.Ordinal) : index.Architecture is null;
            var expectedPrefix = source
                ? $"{index.Component}/source/Sources." : $"{index.Component}/binary-{index.Architecture}/Packages.";
            if ((!binary && !source) || !IsArchiveComponent(index.Component) || !architectureValid || !IsCanonicalRelativePath(index.Path) || !index.Path.StartsWith(expectedPrefix, StringComparison.Ordinal) || !(index.Path.EndsWith(".gz", StringComparison.Ordinal) || index.Path.EndsWith(".xz", StringComparison.Ordinal)) || !IsSha256(index.Sha256) || index.SizeBytes <= 0 || !identities.Add($"{index.Kind}\0{index.Component}\0{index.Architecture}") || !paths.Add(index.Path))
            {
                throw new BundleValidationException($"Wine archive suite '{snapshotId}/{suite.Name}' has an invalid or ambiguous signed index.");
            }
        }
    }

    private static void ValidateDirectPackages(IReadOnlyList<WineDirectPackage> packages)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in packages)
        {
            if (!IsStableId(package.Name) || string.IsNullOrWhiteSpace(package.Version) ||
                !IsStableId(package.Architecture) || !IsCanonicalRelativePath(package.Path) ||
                !package.Path.EndsWith(".deb", StringComparison.Ordinal) || !IsSha256(package.Sha256) ||
                !IsStableId(package.SourcePackage) || string.IsNullOrWhiteSpace(package.License) ||
                !IsHttpsFileUri(package.SourceUri) || !IsSha256(package.SourceSha256) ||
                package.SourceSizeBytes <= 0 || package.SourceSizeBytes > MaximumSourceFileBytes ||
                !identities.Add($"{package.Name}\0{package.Version}\0{package.Architecture}"))
            {
                throw new BundleValidationException("Wine package inventory has an invalid direct package identity.");
            }

            if (!RequiredDirectPackages.TryGetValue(package.Name, out var expected) ||
                !string.Equals(package.Version, expected.Version, StringComparison.Ordinal) ||
                !string.Equals(package.Architecture, expected.Architecture, StringComparison.Ordinal) ||
                !string.Equals(package.Path, expected.Path, StringComparison.Ordinal) ||
                !string.Equals(package.Sha256, expected.Sha256, StringComparison.Ordinal) ||
                !string.Equals(package.SourcePackage, expected.SourcePackage, StringComparison.Ordinal) ||
                !string.Equals(package.License, expected.License, StringComparison.Ordinal) ||
                !string.Equals(package.SourceUri, expected.SourceUri, StringComparison.Ordinal) ||
                !string.Equals(package.SourceSha256, expected.SourceSha256, StringComparison.Ordinal) ||
                package.SourceSizeBytes != expected.SourceSizeBytes)
            {
                throw new BundleValidationException($"Wine direct package '{package.Name}' does not match its reviewed public identity.");
            }
        }
    }

    private static void ValidateSourcePackages(IReadOnlyList<WineSourcePackage> packages, IReadOnlyList<WineArchiveSnapshot> snapshots, bool requireReviewedTotal)
    {
        var previous = string.Empty;
        var filePaths = new HashSet<string>(StringComparer.Ordinal);
        var fileCount = 0;
        long totalBytes = 0;
        foreach (var package in packages)
        {
            var identity = $"{package.Name}\0{package.Version}";
            if (!IsStableId(package.Name) || string.IsNullOrWhiteSpace(package.Version) || StringComparer.Ordinal.Compare(previous, identity) >= 0 || package.Files.Count == 0 || !HasSnapshotIndex(snapshots, package.ArchiveSnapshotId, package.ArchiveSuite, package.ArchiveComponent, package.ArchiveIndexPath, "source", null))
            {
                throw new BundleValidationException("Wine source package inventory must be complete, ordinally sorted, and snapshot bound.");
            }

            var hasDescriptor = false;
            string? sourceDirectory = null;
            foreach (var file in package.Files)
            {
                var separator = file.Path.LastIndexOf('/');
                var directory = separator > 0 ? file.Path[..separator] : string.Empty;
                if (!IsCanonicalPoolPath(file.Path) || !IsSha256(file.Sha256) || file.SizeBytes <= 0 || file.SizeBytes > MaximumClosureSourceFileBytes || !filePaths.Add(file.Path) || sourceDirectory is not null && !string.Equals(sourceDirectory, directory, StringComparison.Ordinal))
                {
                    throw new BundleValidationException($"Wine source package '{package.Name}@{package.Version}' has invalid or duplicate source material.");
                }
                sourceDirectory ??= directory;
                hasDescriptor |= file.Path.EndsWith(".dsc", StringComparison.Ordinal);
                fileCount = checked(fileCount + 1);
                totalBytes = checked(totalBytes + file.SizeBytes);
                if (totalBytes > MaximumClosureSourceTotalBytes)
                    throw new BundleValidationException("Wine source closure exceeds its one-GiB review limit.");
            }
            if (!hasDescriptor)
            {
                throw new BundleValidationException($"Wine source package '{package.Name}@{package.Version}' has no source descriptor.");
            }
            previous = identity;
        }

        if (fileCount != RequiredSourceFileCount || requireReviewedTotal && totalBytes != RequiredSourceTotalBytes)
        {
            throw new BundleValidationException($"Wine source closure must contain exactly {RequiredSourcePackageCount} sources, {RequiredSourceFileCount} files, and the reviewed total byte count.");
        }
    }

    private static void ValidateResolvedPackages(IReadOnlyList<WineResolvedPackage> packages, IReadOnlyList<WineSourcePackage> sourcePackages, IReadOnlyList<WineArchiveSnapshot> snapshots)
    {
        var sources = sourcePackages.ToDictionary(static package => $"{package.Name}\0{package.Version}", StringComparer.Ordinal);
        var previous = string.Empty;
        var binaryPaths = new HashSet<string>(StringComparer.Ordinal);
        var copyrights = new Dictionary<string, (string Sha256, long SizeBytes)>(StringComparer.Ordinal);
        foreach (var package in packages)
        {
            if (string.IsNullOrWhiteSpace(package.Name) || string.IsNullOrWhiteSpace(package.Version) ||
                package.Name.EndsWith(":i386", StringComparison.Ordinal) ||
                StringComparer.Ordinal.Compare(previous, package.Name) >= 0 ||
                package.Architecture is not ("all" or "amd64") ||
                !HasSnapshotIndex(snapshots, package.ArchiveSnapshotId, package.ArchiveSuite, package.ArchiveComponent, package.ArchiveIndexPath, "binary", "amd64") ||
                !IsCanonicalPoolPath(package.Path) || !package.Path.EndsWith(".deb", StringComparison.Ordinal) ||
                !binaryPaths.Add(package.Path) || !IsSha256(package.Sha256) || package.SizeBytes <= 0 ||
                !IsStableId(package.SourcePackage) || string.IsNullOrWhiteSpace(package.SourceVersion) ||
                !sources.ContainsKey($"{package.SourcePackage}\0{package.SourceVersion}") ||
                !IsCanonicalCopyrightPath(package.CopyrightPath) ||
                !IsSha256(package.CopyrightSha256) || package.CopyrightSizeBytes <= 0)
            {
                throw new BundleValidationException("Wine resolved package inventory must contain complete binary, source, snapshot, and copyright evidence.");
            }
            if (copyrights.TryGetValue(package.CopyrightPath, out var existing) && (existing.SizeBytes != package.CopyrightSizeBytes || !string.Equals(existing.Sha256, package.CopyrightSha256, StringComparison.Ordinal)))
            {
                throw new BundleValidationException($"Wine copyright path '{package.CopyrightPath}' has conflicting identities.");
            }
            copyrights[package.CopyrightPath] = (package.CopyrightSha256, package.CopyrightSizeBytes);
            previous = package.Name;
        }
    }

    private static void ValidateNoticeArchive(WineNoticeArchive archive, IReadOnlyList<WineResolvedPackage> packages)
    {
        var entries = packages.Select(static package => package.CopyrightPath).Distinct(StringComparer.Ordinal).Count();
        if (!string.Equals(archive.ImagePath, RequiredNoticeArchiveImagePath, StringComparison.Ordinal) || !IsSha256(archive.Sha256) || archive.SizeBytes <= 0 || archive.SizeBytes > MaximumNoticeArchiveBytes || archive.EntryCount != entries)
        {
            throw new BundleValidationException("Wine notice archive does not bind the exact deduplicated copyright inventory.");
        }
    }

    private static bool HasSnapshotIndex(IReadOnlyList<WineArchiveSnapshot> snapshots, string snapshotId, string suite, string component, string indexPath, string kind, string? architecture) =>
        snapshots.Any(snapshot => string.Equals(snapshot.Id, snapshotId, StringComparison.Ordinal) && snapshot.Suites.Any(item => string.Equals(item.Name, suite, StringComparison.Ordinal) && item.Indexes.Any(index => string.Equals(index.Kind, kind, StringComparison.Ordinal) && string.Equals(index.Component, component, StringComparison.Ordinal) && string.Equals(index.Architecture, architecture, StringComparison.Ordinal) && string.Equals(index.Path, indexPath, StringComparison.Ordinal))));

    private static void ValidateDirectPackagesAreResolved(IReadOnlyList<WineDirectPackage> directPackages, IReadOnlyList<WineResolvedPackage> resolvedPackages)
    {
        var resolved = resolvedPackages.Select(static package => $"{package.Name}\0{package.Version}").ToHashSet(StringComparer.Ordinal);
        if (directPackages.Any(package => !resolved.Contains($"{package.Name}\0{package.Version}")))
        {
            throw new BundleValidationException("Wine direct package inventory is not included in its resolved package closure.");
        }
    }

    private static void ValidateSourceOffer(WineSourceOffer offer, IReadOnlyList<WineSourcePackage> sourcePackages)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var file in offer.Files)
        {
            if (!IsCanonicalFileName(file.Path) || !IsSha256(file.Sha256) || file.SizeBytes <= 0 || file.SizeBytes > MaximumSourceFileBytes || !names.Add(file.Path))
            {
                throw new BundleValidationException("Wine source offer has an invalid file identity.");
            }
            if (!RequiredWineSourceFiles.TryGetValue(file.Path, out var expected) || !string.Equals(file.Sha256, expected.Sha256, StringComparison.Ordinal) || file.SizeBytes != expected.SizeBytes)
            {
                throw new BundleValidationException($"Wine source material '{file.Path}' does not match its reviewed public identity.");
            }
            total = checked(total + file.SizeBytes);
        }

        if (total > MaximumSourceTotalBytes || !names.SetEquals(RequiredWineSourceOfferPaths) || !names.SetEquals(RequiredWineSourceFiles.Keys))
        {
            throw new BundleValidationException("Wine source offer must contain exactly the reviewed three-file source closure.");
        }

        var wineSource = sourcePackages.SingleOrDefault(source => string.Equals(source.Name, offer.Package, StringComparison.Ordinal) && string.Equals(source.Version, offer.Version, StringComparison.Ordinal));
        if (wineSource is null || wineSource.Files.Count != offer.Files.Count || offer.Files.Any(offerFile => !wineSource.Files.Any(sourceFile => sourceFile.Path.EndsWith('/' + offerFile.Path, StringComparison.Ordinal) && string.Equals(sourceFile.Sha256, offerFile.Sha256, StringComparison.Ordinal) && sourceFile.SizeBytes == offerFile.SizeBytes)))
        {
            throw new BundleValidationException("Wine source offer is not the same material as the complete operating-system source closure.");
        }
    }

    private static void ValidateResolvedPackageDigest(IReadOnlyList<WineResolvedPackage> packages, string expectedDigest)
    {
        var text = string.Join('\n', packages.Select(static package => $"{package.Name}={package.Version}")) + "\n";
        var actualDigest = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))}";
        if (!string.Equals(actualDigest, expectedDigest, StringComparison.Ordinal))
        {
            throw new BundleValidationException("Wine resolved package inventory does not match its reviewed SHA-256 digest.");
        }
    }

    private static bool IsStableId(string value) =>
        value.Length is > 0 and <= 128 && char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static bool IsSha256(string value) => value.Length == 64 && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSha256Digest(string value) => value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) && IsSha256(value[7..]);

    private static bool IsCanonicalRelativePath(string value) =>
        value.Length is > 0 and <= 512 && !value.Contains('\\') && !Path.IsPathRooted(value) &&
        value.Split('/').All(static segment => segment is not "" and not "." and not ".." && segment.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or '~' or '+'));

    private static bool IsCanonicalFileName(string value) => !value.Contains('/') && IsCanonicalRelativePath(value);

    private static bool IsCanonicalPoolPath(string value) => value.StartsWith("pool/", StringComparison.Ordinal) && IsCanonicalRelativePath(value);

    private static bool IsArchiveComponent(string value) => value is "main" or "universe" or "restricted" or "multiverse";

    private static bool IsCanonicalCopyrightPath(string value) =>
        value.StartsWith("/usr/share/doc/", StringComparison.Ordinal) &&
        value.EndsWith("/copyright", StringComparison.Ordinal) &&
        IsCanonicalRelativePath(value[1..]);

    private static bool IsHttpsFileUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) &&
        !uri.AbsolutePath.EndsWith('/');

    private sealed record ReviewedDirectPackage(string Version, string Architecture, string Path, string Sha256, string SourcePackage, string License, string SourceUri, string SourceSha256, long SourceSizeBytes);

    private sealed record ReviewedSourceFile(string Sha256, long SizeBytes);

    private sealed record ReviewedArchiveSnapshot(string Purpose, string Uri, IReadOnlyDictionary<string, ReviewedArchiveSuite> Suites);

    private sealed record ReviewedArchiveSuite(string Sha256, long SizeBytes);
}
