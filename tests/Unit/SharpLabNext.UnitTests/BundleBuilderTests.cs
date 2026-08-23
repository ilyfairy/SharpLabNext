using System.Diagnostics;
using System.ComponentModel;
using System.Formats.Tar;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Microsoft.Extensions.Configuration;
using SharpLabNext.BundleBuilder;
using SharpLabNext.Catalog;
using SharpLabNext.Contracts;
using SharpLabNext.RuntimeProfile.Sdk;
using SharpLabNext.RuntimeSupervisor;

namespace SharpLabNext.UnitTests;

public sealed class BundleBuilderTests
{
    private const string TestSourceRevision = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly string[] DependencyInventoryAllowedLicenses = ["Apache-2.0", "LGPL-2.1+", "MIT"];
    private static readonly string[] DependencyInventoryDeniedPrefixes = ["GPL-"];
    private static readonly string[] MaintainedIdentityProperties =
        ["kind", "resolvedVersion", "commit", "digest", "sourceUri"];
    private static readonly JsonSerializerOptions RuntimeProfileFixtureJsonOptions =
        new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyDictionary<string, byte[]> TestWineSourceFiles =
        new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["wine_9.0~repack-4build3.dsc"] = Encoding.UTF8.GetBytes("test wine dsc"),
            ["wine_9.0~repack.orig.tar.xz"] = Encoding.UTF8.GetBytes("test wine orig"),
            ["wine_9.0~repack-4build3.debian.tar.xz"] = Encoding.UTF8.GetBytes("test wine debian")
        };
    private static readonly Dictionary<string, byte[]> TestOperatingSystemSourceFiles =
        new(StringComparer.Ordinal);
    private static readonly byte[] TestWineNoticeArchive = CreateTestWineNoticeArchive();
    private static readonly WineRuntimePackageManifest TestWineManifest = CreateTestWineManifest();

    [Fact]
    public async Task BuilderCreatesPinnedOfflineBundleFromSelectableCatalogComponents()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var catalogDocument = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "profiles", "catalog", "catalog.json"),
            TestContext.Current.CancellationToken));
        var activeReleaseId = catalogDocument.RootElement.GetProperty("releaseId").GetString()
            ?? throw new InvalidOperationException("The test Catalog does not declare a release ID.");
        var output = Path.Combine(Path.GetTempPath(), $"sharplabnext-bundle-{Guid.NewGuid():N}");
        var statusPath = Path.Combine(Path.GetTempPath(), $"sharplabnext-profile-status-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(
                statusPath,
                $$"""
                {
                  "schemaVersion": 1,
                  "status": "candidate-failed",
                  "checked": true,
                  "active": {
                    "releaseId": "{{activeReleaseId}}",
                    "lockDigest": "sha256:{{new string('a', 64)}}"
                  },
                  "candidate": {
                    "releaseId": "candidate",
                    "lockDigest": "sha256:{{new string('b', 64)}}"
                  },
                  "updateAvailable": true,
                  "updatedAt": "2026-07-11T01:00:00Z",
                  "candidatePath": "C:\\private\\candidate",
                  "lastStage": {
                    "stage": "build",
                    "outcome": "failed",
                    "commands": ["dotnet build C:\\private\\source"],
                    "error": {
                      "code": "profile-update.build-failed",
                      "message": "failed at C:\\private\\source\\Program.cs"
                    }
                  }
                }
                """,
                TestContext.Current.CancellationToken);
            var command = new BundleBuilderCommand(
                repositoryRoot,
                Path.Combine(repositoryRoot, "profiles", "catalog", "catalog.json"),
                Path.Combine(repositoryRoot, "profiles", "lock.json"),
                Path.Combine(repositoryRoot, "deploy", "images.json"),
                Path.Combine(repositoryRoot, "profiles", "license-policy.json"),
                Path.Combine(repositoryRoot, "deploy", "compose.prod.yaml"),
                Path.Combine(repositoryRoot, "THIRD-PARTY-NOTICES.md"),
                output,
                "docker",
                "openssl",
                null,
                null,
                null,
                null,
                MetadataOnly: false,
                new Dictionary<string, string>(),
                ProfileUpdateStatusPath: statusPath);
            var docker = new FakeDockerCli();

            var result = await CreateBuilder(docker).BuildAsync(
                command,
                TestContext.Current.CancellationToken);

            Assert.True(result.ContainsImages);
            Assert.Contains(result.Images, static image => image.Id == "worker-roslyn-stable");
            Assert.Contains(result.Images, static image => image.Id == "worker-roslyn-netfx48");
            Assert.Contains(result.Images, static image => image.Id == "worker-fsharp");
            Assert.Contains(result.Images, static image => image.Id == "worker-gsharp");
            Assert.Contains(result.Images, static image => image.Id == "worker-peachpie");
            Assert.Contains(result.Images, static image => image.Id == "worker-cppcli");
            Assert.Contains(result.Images, static image => image.Id == "worker-jsharp");
            Assert.Contains(result.Images, static image => image.Id == "dotnet-11-preview-linux-x64");
            Assert.Contains(result.Images, static image => image.Id == "worker-roslyn-const-generics");
            Assert.Contains(result.Images, static image => image.Id == "worker-artifacts-jsil");
            Assert.Contains(result.Images, static image => image.Id == "worker-artifacts-const-generics");
            Assert.Contains(result.Images, static image => image.Id == "const-generics-linux-x64");
            Assert.Contains(result.Images, static image => image.Id == "wine-netfx48-linux-x64");
            Assert.Contains(result.Images, static image => image.Id == "wine-jsharp20-linux-x64");
            Assert.Equal(54, result.Images.Count);
            Assert.True(File.Exists(Path.Combine(output, "images.tar")));
            Assert.True(File.Exists(Path.Combine(output, "checksums.sha256")));
            Assert.True(File.Exists(Path.Combine(output, "sbom", "release.spdx.json")));
            Assert.True(File.Exists(Path.Combine(output, "sbom", "release.cdx.json")));
            Assert.True(File.Exists(Path.Combine(output, "sbom", "dependencies.json")));
            Assert.Equal(
                JsonSerializer.SerializeToUtf8Bytes(TestWineManifest, RuntimeProfileFixtureJsonOptions),
                await File.ReadAllBytesAsync(
                    Path.Combine(output, "sbom", "runtime-wine-packages.json"),
                    TestContext.Current.CancellationToken));
            var wineSourceRoot = Path.Combine(
                output,
                "sources",
                "ubuntu",
                "20260810T000000Z",
                "pool",
                "universe",
                "w",
                "wine");
            foreach (var (fileName, expectedBytes) in TestWineSourceFiles)
            {
                Assert.Equal(
                    expectedBytes,
                    await File.ReadAllBytesAsync(
                        Path.Combine(wineSourceRoot, fileName),
                        TestContext.Current.CancellationToken));
            }
            using (var wineSourceManifestDocument = JsonDocument.Parse(await File.ReadAllTextAsync(
                       Path.Combine(output, "sources", "manifest.json"),
                       TestContext.Current.CancellationToken)))
            {
                var wineMaterial = Assert.Single(
                    wineSourceManifestDocument.RootElement.GetProperty("components").EnumerateArray(),
                    static component =>
                        component.GetProperty("packageManager").GetString() == "apt-source" &&
                        component.GetProperty("name").GetString() == "wine");
                Assert.Equal(
                    "sources/ubuntu/20260810T000000Z/pool/universe/w/wine",
                    wineMaterial.GetProperty("materialPath").GetString());
            }
            using (var wineDependencyDocument = JsonDocument.Parse(await File.ReadAllTextAsync(
                       Path.Combine(output, "sbom", "dependencies.json"),
                       TestContext.Current.CancellationToken)))
            {
                Assert.DoesNotContain(
                    wineDependencyDocument.RootElement.GetProperty("components").EnumerateArray(),
                    static component =>
                        component.GetProperty("packageManager").GetString() == "apt-source");
            }
            using (var spdx = JsonDocument.Parse(await File.ReadAllTextAsync(
                       Path.Combine(output, "sbom", "release.spdx.json"),
                       TestContext.Current.CancellationToken)))
            {
                var osPackages = spdx.RootElement.GetProperty("packages").EnumerateArray()
                    .Where(static package => package.TryGetProperty("SPDXID", out var spdxId) &&
                        spdxId.GetString() is { } value &&
                        value.StartsWith("SPDXRef-OS-apt-", StringComparison.Ordinal))
                    .ToArray();
                Assert.Equal(228, osPackages.Length);
                Assert.All(osPackages, static package =>
                {
                    Assert.Equal("NOASSERTION", package.GetProperty("licenseDeclared").GetString());
                    Assert.Equal("NOASSERTION", package.GetProperty("licenseConcluded").GetString());
                });
            }
            using (var cycloneDx = JsonDocument.Parse(await File.ReadAllTextAsync(
                       Path.Combine(output, "sbom", "release.cdx.json"),
                       TestContext.Current.CancellationToken)))
            {
                var osPackages = cycloneDx.RootElement.GetProperty("components").EnumerateArray()
                    .Where(static component => component.TryGetProperty("properties", out var properties) &&
                        properties.EnumerateArray()
                        .Any(static property =>
                            property.TryGetProperty("name", out var name) &&
                            property.TryGetProperty("value", out var value) &&
                            name.GetString() == "sharplabnext:scope" &&
                            value.GetString() == "os-package"))
                    .ToArray();
                Assert.Equal(228, osPackages.Length);
                Assert.All(osPackages, static package => Assert.False(package.TryGetProperty("licenses", out _)));
            }
            using (var wineProvenance = JsonDocument.Parse(await File.ReadAllTextAsync(
                       Path.Combine(output, "provenance", "release.slsa.json"),
                       TestContext.Current.CancellationToken)))
            {
                var resolvedMaterials = wineProvenance.RootElement
                    .GetProperty("predicate")
                    .GetProperty("buildDefinition")
                    .GetProperty("resolvedDependencies")
                    .EnumerateArray()
                    .ToArray();
                Assert.Equal(
                    228,
                    resolvedMaterials.Count(static dependency => dependency.GetProperty("uri").GetString()!
                        .StartsWith("pkg:deb/ubuntu/", StringComparison.Ordinal)));
                Assert.Contains(resolvedMaterials, static dependency => dependency.GetProperty("uri").GetString() ==
                    $"https://github.com/sharplabnext/SharpLabNext/blob/{TestSourceRevision}/profiles/runtime-wine-packages.json");
            }
            Assert.True(File.Exists(Path.Combine(output, "provenance", "release.slsa.json")));
            Assert.True(File.Exists(Path.Combine(
                output,
                "provenance",
                "maintained",
                "const-generics-runtime.json")));
            Assert.True(File.Exists(Path.Combine(
                output,
                "provenance",
                "maintained",
                "gsharp.json")));
            Assert.True(File.Exists(Path.Combine(
                output,
                "provenance",
                "maintained",
                "cppcli.json")));
            Assert.True(File.Exists(Path.Combine(
                output,
                "provenance",
                "maintained",
                "jsharp.json")));
            Assert.True(File.Exists(Path.Combine(
                output,
                "provenance",
                "maintained",
                "jsil.json")));
            Assert.True(File.Exists(Path.Combine(output, "base-images.json")));
            Assert.True(File.Exists(Path.Combine(output, "install.ps1")));
            Assert.True(File.Exists(Path.Combine(output, "install.sh")));
            Assert.True(File.Exists(Path.Combine(output, "rollback.ps1")));
            Assert.True(File.Exists(Path.Combine(output, "rollback.sh")));
            Assert.True(File.Exists(Path.Combine(output, "smoke.ps1")));
            Assert.True(File.Exists(Path.Combine(output, "smoke.sh")));
            Assert.True(File.Exists(Path.Combine(output, "deployment-common.ps1")));
            Assert.True(File.Exists(Path.Combine(output, "deployment-common.sh")));
            Assert.True(File.Exists(Path.Combine(output, "deploy.sh")));
            Assert.True(File.Exists(Path.Combine(output, "profile-update-status.json")));
            foreach (var jsonPath in Directory.EnumerateFiles(output, "*.json", SearchOption.AllDirectories))
            {
                var jsonBytes = await File.ReadAllBytesAsync(
                    jsonPath,
                    TestContext.Current.CancellationToken);
                if (jsonPath.StartsWith(
                        Path.Combine(output, ReleaseBundleBuilder.PromotionEvidenceDirectoryName, "source") +
                        Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    // These are intentionally exact retained source bytes, not normalized generated JSON.
                    continue;
                }
                Assert.False(jsonBytes.AsSpan().StartsWith("\uFEFF"u8));
                Assert.DoesNotContain((byte)'\r', jsonBytes);
            }
            var promotionManifestPath = Path.Combine(
                output,
                ReleaseBundleBuilder.PromotionEvidenceDirectoryName,
                "manifest.json");
            var promotionManifestBytes = await File.ReadAllBytesAsync(
                promotionManifestPath,
                TestContext.Current.CancellationToken);
            Assert.False(promotionManifestBytes.AsSpan().StartsWith("\uFEFF"u8));
            Assert.DoesNotContain((byte)'\r', promotionManifestBytes);
            using var promotionManifest = JsonDocument.Parse(promotionManifestBytes);
            var promotedRuntimeIds = promotionManifest.RootElement
                .GetProperty("promotedRuntimeIds")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray();
            var expectedPromotedRuntimeCount = PromotionFixtures.Value.ImagesByReference.Count;
            Assert.Equal(expectedPromotedRuntimeCount, promotedRuntimeIds.Length);
            Assert.Equal(expectedPromotedRuntimeCount, promotedRuntimeIds.Distinct(StringComparer.Ordinal).Count());
            var promotionEntries = promotionManifest.RootElement
                .GetProperty("entries")
                .EnumerateArray()
                .ToArray();
            foreach (var runtimeId in promotedRuntimeIds)
            {
                Assert.NotNull(runtimeId);
                var kinds = promotionEntries
                    .Where(entry => entry.GetProperty("runtimeIds")
                        .EnumerateArray()
                        .Select(static value => value.GetString())
                        .Contains(runtimeId, StringComparer.Ordinal))
                    .Select(entry => entry.GetProperty("kind").GetString())
                    .ToHashSet(StringComparer.Ordinal);
                Assert.Contains("plan", kinds);
                Assert.Contains("preflight-profile", kinds);
                Assert.Contains("receipt", kinds);
                Assert.Contains("capability-evidence", kinds);
                Assert.Contains("performance-evidence", kinds);
            }
            foreach (var entry in promotionEntries)
            {
                var bundlePath = entry.GetProperty("bundlePath").GetString();
                Assert.NotNull(bundlePath);
                var copiedBytes = await File.ReadAllBytesAsync(
                    Path.Combine(
                        output,
                        ReleaseBundleBuilder.PromotionEvidenceDirectoryName,
                        bundlePath!.Replace('/', Path.DirectorySeparatorChar)),
                    TestContext.Current.CancellationToken);
                Assert.Equal(entry.GetProperty("sizeBytes").GetInt64(), copiedBytes.LongLength);
                Assert.Equal(
                    entry.GetProperty("sha256").GetString(),
                    "sha256:" + Convert.ToHexStringLower(SHA256.HashData(copiedBytes)));
            }
            const string promotedFixtureRuntimeId = "dotnet-10-linux-x64";
            var candidateBinding = Assert.Single(
                promotionEntries,
                static entry =>
                    entry.GetProperty("kind").GetString() == "candidate-profile" &&
                    entry.GetProperty("runtimeIds").EnumerateArray()
                        .Any(static runtimeId => runtimeId.GetString() == promotedFixtureRuntimeId));
            var planBinding = Assert.Single(
                promotionEntries,
                static entry =>
                    entry.GetProperty("kind").GetString() == "plan" &&
                    entry.GetProperty("runtimeIds").EnumerateArray()
                        .Any(static runtimeId => runtimeId.GetString() == promotedFixtureRuntimeId));
            using (var signedPlan = JsonDocument.Parse(await File.ReadAllBytesAsync(
                       Path.Combine(
                           output,
                           ReleaseBundleBuilder.PromotionEvidenceDirectoryName,
                           planBinding.GetProperty("bundlePath").GetString()!
                               .Replace('/', Path.DirectorySeparatorChar)),
                       TestContext.Current.CancellationToken)))
            {
                Assert.Equal(
                    signedPlan.RootElement.GetProperty("profileSha256").GetString(),
                    candidateBinding.GetProperty("sha256").GetString());
            }
            var promotionVerificationManifest = await File.ReadAllBytesAsync(
                Path.Combine(output, ReleaseBundleBuilder.PromotionEvidenceDirectoryName, "manifest.tsv"),
                TestContext.Current.CancellationToken);
            Assert.False(promotionVerificationManifest.AsSpan().StartsWith("\uFEFF"u8));
            Assert.DoesNotContain((byte)'\r', promotionVerificationManifest);
            var bundledNotices = await File.ReadAllTextAsync(
                Path.Combine(output, "THIRD-PARTY-NOTICES.md"),
                TestContext.Current.CancellationToken);
            Assert.Contains(
                "Microsoft Visual J# 2.0 Second Edition and .NET Framework CLR 2.0 binaries",
                bundledNotices,
                StringComparison.Ordinal);
            Assert.Contains("x64-only J#", bundledNotices, StringComparison.Ordinal);
            Assert.Contains("not BSD-licensed project content", bundledNotices, StringComparison.Ordinal);
            var appArmorPath = Path.Combine(output, "security", "sharplabnext-runtime-job-v1.apparmor");
            Assert.True(File.Exists(appArmorPath));
            var appArmor = await File.ReadAllTextAsync(
                appArmorPath,
                TestContext.Current.CancellationToken);
            Assert.Contains("network unix,", appArmor, StringComparison.Ordinal);
            Assert.Contains("deny network inet,", appArmor, StringComparison.Ordinal);
            Assert.Contains("deny network inet6,", appArmor, StringComparison.Ordinal);
            Assert.DoesNotContain("  deny network,", appArmor, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(output, "security", "inventory.json")));
            Assert.True(File.Exists(Path.Combine(output, "security", "THIRD-PARTY-NOTICES.md")));
            var expectedImages = await File.ReadAllTextAsync(
                Path.Combine(output, "images.expected"),
                TestContext.Current.CancellationToken);
            Assert.DoesNotContain("\r", expectedImages, StringComparison.Ordinal);
            Assert.EndsWith("\n", expectedImages, StringComparison.Ordinal);
            var mobyLicensePath = Path.Combine(
                output,
                "security",
                "licenses",
                "moby-profiles-Apache-2.0.txt");
            Assert.True(File.Exists(mobyLicensePath));
            var mobyLicense = await File.ReadAllTextAsync(
                mobyLicensePath,
                TestContext.Current.CancellationToken);
            Assert.Contains("Apache License", mobyLicense, StringComparison.Ordinal);
            Assert.Contains("END OF TERMS AND CONDITIONS", mobyLicense, StringComparison.Ordinal);
            Assert.True(mobyLicense.Length >= 10_000);
            var creativeCommonsLicensePath = Path.Combine(
                output,
                "security",
                "licenses",
                "CC-BY-4.0.txt");
            Assert.True(File.Exists(creativeCommonsLicensePath));
            var creativeCommonsLicense = await File.ReadAllTextAsync(
                creativeCommonsLicensePath,
                TestContext.Current.CancellationToken);
            Assert.Contains(
                "Creative Commons Attribution 4.0 International Public License",
                creativeCommonsLicense,
                StringComparison.Ordinal);
            Assert.Contains("Section 8 -- Interpretation.", creativeCommonsLicense, StringComparison.Ordinal);
            Assert.True(creativeCommonsLicense.Length >= 18_000);

            var checksums = await File.ReadAllLinesAsync(
                Path.Combine(output, "checksums.sha256"),
                TestContext.Current.CancellationToken);
            Assert.Contains(checksums, static line =>
                line.EndsWith("  security/sharplabnext-runtime-job-v1.apparmor", StringComparison.Ordinal));
            Assert.Contains(checksums, static line =>
                line.EndsWith("  security/inventory.json", StringComparison.Ordinal));
            Assert.Contains(checksums, static line =>
                line.EndsWith("  security/licenses/moby-profiles-Apache-2.0.txt", StringComparison.Ordinal));
            Assert.Contains(checksums, static line =>
                line.EndsWith("  security/licenses/CC-BY-4.0.txt", StringComparison.Ordinal));

            var statusJson = await File.ReadAllTextAsync(
                Path.Combine(output, "profile-update-status.json"),
                TestContext.Current.CancellationToken);
            Assert.DoesNotContain("candidatePath", statusJson, StringComparison.Ordinal);
            Assert.DoesNotContain("commands", statusJson, StringComparison.Ordinal);
            Assert.DoesNotContain("private", statusJson, StringComparison.OrdinalIgnoreCase);
            var publicStatus = JsonSerializer.Deserialize<ProfileUpdateStatusDocument>(
                statusJson,
                ContractJson.CreateCanonicalSerializerOptions());
            Assert.NotNull(publicStatus);
            Assert.Equal(ProfileUpdateStatusKind.CandidateFailed, publicStatus.Status);
            Assert.Equal("candidate", publicStatus.Candidate?.ReleaseId);
            Assert.Equal(
                "Profile candidate build failed; the approved release remains active.",
                publicStatus.LastStage.Error?.Message);

            using var dependencies = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(output, "sbom", "dependencies.json"),
                TestContext.Current.CancellationToken));
            Assert.Contains(
                dependencies.RootElement.GetProperty("components").EnumerateArray(),
                static component =>
                    component.GetProperty("name").GetString() == "lightningcss" &&
                    component.GetProperty("license").GetString() == "MPL-2.0");
            Assert.Contains(
                dependencies.RootElement.GetProperty("components").EnumerateArray(),
                static component =>
                    component.GetProperty("packageManager").GetString() == "nuget" &&
                    component.GetProperty("name").GetString() ==
                        "Microsoft.NETFramework.ReferenceAssemblies" &&
                    component.GetProperty("version").GetString() == "1.0.3" &&
                    component.GetProperty("license").GetString() == "MIT");
            Assert.Contains(
                dependencies.RootElement.GetProperty("components").EnumerateArray(),
                static component =>
                    component.GetProperty("packageManager").GetString() == "github" &&
                    component.GetProperty("name").GetString() == "moby/profiles" &&
                    component.GetProperty("version").GetString() == "seccomp/v0.1.0" &&
                    component.GetProperty("license").GetString() == "Apache-2.0" &&
                    component.GetProperty("integrity").GetString() ==
                        "sha256-AVNvHR35OK5hHrog1jSeDeepm27N7hVJQnoLAbgwHig=");

            using var baseImageProvenance = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(output, "provenance", "release.slsa.json"),
                TestContext.Current.CancellationToken));
            var buildDefinition = baseImageProvenance.RootElement.GetProperty("predicate").GetProperty("buildDefinition");
            Assert.Equal(
                "profiles/base-images.json",
                buildDefinition.GetProperty("externalParameters").GetProperty("baseImageManifest").GetString());
            Assert.Contains(
                buildDefinition.GetProperty("resolvedDependencies").EnumerateArray(),
                static dependency =>
                    dependency.GetProperty("uri").GetString()!.StartsWith("pkg:docker/node%3A24.18.0-bookworm-slim", StringComparison.Ordinal) &&
                    dependency.GetProperty("digest").GetProperty("sha256").GetString() ==
                        "0778d035a13f3f3833b7f2cb750e0df6cbce45583e84fd822f499f0c902a6c74");
            using var sourceLock = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(repositoryRoot, "profiles", "lock.json"),
                TestContext.Current.CancellationToken));
            var lockComponents = sourceLock.RootElement.GetProperty("components");
            var maintained = buildDefinition
                .GetProperty("externalParameters")
                .GetProperty("maintainedProvenance");
            AssertMaintainedIdentityMatchesLock(
                maintained,
                lockComponents,
                "const-generics-linux-x64",
                "const-generics-runtime-source");
            AssertMaintainedIdentityMatchesLock(
                maintained,
                lockComponents,
                "roslyn-const-generics",
                "const-generics-roslyn-source");
            AssertMaintainedIdentityMatchesLock(
                maintained,
                lockComponents,
                "artifacts-const-generics",
                "const-generics-ilspy-source");
            AssertMaintainedIdentityMatchesLock(
                maintained,
                lockComponents,
                "gsharp-stable",
                "gsharp-source");
            AssertMaintainedIdentityMatchesLock(
                maintained,
                lockComponents,
                "msvc-cppcli-netfx48",
                "msvc-wine-source");
            AssertMaintainedIdentityMatchesLock(
                maintained,
                lockComponents,
                "vjc-jsharp20",
                "msvc-wine-source");
            AssertMaintainedIdentityMatchesLock(
                maintained,
                lockComponents,
                "artifacts-jsil",
                "jsil-source");
            var roslynMaintained = maintained.EnumerateArray().Single(static item =>
                item.GetProperty("componentId").GetString() == "roslyn-const-generics");
            Assert.Matches("^sha256:[0-9a-f]{64}$", roslynMaintained.GetProperty("patchSeriesDigest").GetString());

            using var cyclone = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(output, "sbom", "release.cdx.json"),
                TestContext.Current.CancellationToken));
            Assert.Contains(
                cyclone.RootElement.GetProperty("components").EnumerateArray(),
                static component =>
                    component.GetProperty("name").GetString() == "moby/profiles" &&
                    component.GetProperty("purl").GetString() ==
                        "pkg:github/moby/profiles@seccomp%2Fv0.1.0");

            using var sourceMaterials = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(output, "sources", "manifest.json"),
                TestContext.Current.CancellationToken));
            var sourceComponents = sourceMaterials.RootElement.GetProperty("components").EnumerateArray().ToArray();
            Assert.Contains(sourceComponents, static component =>
                component.GetProperty("name").GetString() == "lightningcss" &&
                component.GetProperty("license").GetString() == "MPL-2.0");
            Assert.DoesNotContain(sourceComponents, static component =>
                component.GetProperty("name").GetString() == "lightningcss-android-arm64");
            foreach (var component in sourceComponents)
            {
                var materialPath = component.GetProperty("materialPath").GetString();
                Assert.False(string.IsNullOrWhiteSpace(materialPath));
                Assert.True(Directory.Exists(Path.Combine(output, materialPath!.Replace('/', Path.DirectorySeparatorChar))));
            }

            var installShell = await File.ReadAllTextAsync(
                Path.Combine(output, "install.sh"),
                TestContext.Current.CancellationToken);
            var deployShell = await File.ReadAllTextAsync(
                Path.Combine(output, "deploy.sh"),
                TestContext.Current.CancellationToken);
            var installPowerShell = await File.ReadAllTextAsync(
                Path.Combine(output, "install.ps1"),
                TestContext.Current.CancellationToken);
            var commonShell = await File.ReadAllTextAsync(
                Path.Combine(output, "deployment-common.sh"),
                TestContext.Current.CancellationToken);
            var rollbackPowerShell = await File.ReadAllTextAsync(
                Path.Combine(output, "rollback.ps1"),
                TestContext.Current.CancellationToken);
            var commonPowerShell = await File.ReadAllTextAsync(
                Path.Combine(output, "deployment-common.ps1"),
                TestContext.Current.CancellationToken);
            var verifyPowerShell = await File.ReadAllTextAsync(
                Path.Combine(output, "verify.ps1"),
                TestContext.Current.CancellationToken);
            Assert.Contains("--pull never --no-build", commonShell, StringComparison.Ordinal);
            Assert.Contains("${HOME:?HOME is required}/sharplabnext", deployShell, StringComparison.Ordinal);
            Assert.Contains("$install_root/secrets/internal-service-token", deployShell, StringComparison.Ordinal);
            Assert.Contains("openssl rand -base64 48", deployShell, StringComparison.Ordinal);
            Assert.Contains("DOCKER_GID=$(stat -c '%g' /var/run/docker.sock)", deployShell, StringComparison.Ordinal);
            Assert.Contains("--skip-artifact-backup", deployShell, StringComparison.Ordinal);
            Assert.Contains("--current-only", deployShell, StringComparison.Ordinal);
            Assert.Contains("$install_root/$deploy_pointer_name", deployShell, StringComparison.Ordinal);
            Assert.Contains("docker ps -aq --filter", deployShell, StringComparison.Ordinal);
            Assert.Contains("docker image rm", deployShell, StringComparison.Ordinal);
            Assert.DoesNotContain("docker system prune", deployShell, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ssh", deployShell, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("scp", deployShell, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("readiness checks", installShell, StringComparison.Ordinal);
            Assert.Contains("install_release_assets", installShell, StringComparison.Ordinal);
            Assert.Contains("validate_container_secret_file", installShell, StringComparison.Ordinal);
            Assert.Contains("SHARPLABNEXT_INTERNAL_SERVICE_TOKEN_FILE", installShell, StringComparison.Ordinal);
            Assert.Contains("Resolve-ContainerSecretFile", installPowerShell, StringComparison.Ordinal);
            Assert.Contains("SHARPLABNEXT_INTERNAL_SERVICE_TOKEN_FILE", installPowerShell, StringComparison.Ordinal);
            Assert.Contains("Internal service token", installPowerShell, StringComparison.Ordinal);
            Assert.Contains("GitHub OAuth client secret", installPowerShell, StringComparison.Ordinal);
            Assert.Contains("must be readable by container UID/GID 1654", commonShell, StringComparison.Ordinal);
            Assert.Contains("must not be readable by other host users", commonShell, StringComparison.Ordinal);
            Assert.Contains("does not exist", commonPowerShell, StringComparison.Ordinal);
            Assert.Contains("is not readable", commonPowerShell, StringComparison.Ordinal);
            Assert.Contains("deployment.sha256", commonPowerShell, StringComparison.Ordinal);
            Assert.Contains("Restore-InstalledRelease", rollbackPowerShell, StringComparison.Ordinal);
            Assert.Contains("TrustedPublicKeySha256", verifyPowerShell, StringComparison.Ordinal);
            Assert.Contains("ExpectedSigningKeyId", verifyPowerShell, StringComparison.Ordinal);
            Assert.Contains("Backup-ArtifactStore", commonPowerShell, StringComparison.Ordinal);
            Assert.Contains("Restore-ArtifactStoreBackup", commonPowerShell, StringComparison.Ordinal);
            Assert.Contains("--network none", commonPowerShell, StringComparison.Ordinal);
            Assert.Contains("images.expected", commonPowerShell, StringComparison.Ordinal);
            Assert.Contains("profile-update-status.json", commonShell, StringComparison.Ordinal);
            Assert.Contains("profile-update-status.json", commonPowerShell, StringComparison.Ordinal);
            Assert.Contains(ReleaseBundleBuilder.DisabledGitHubOAuthSecretFileName, commonShell, StringComparison.Ordinal);
            Assert.Contains(ReleaseBundleBuilder.DisabledGitHubOAuthSecretFileName, commonPowerShell, StringComparison.Ordinal);
            Assert.Contains("security/sharplabnext-runtime-job-v1.apparmor", commonShell, StringComparison.Ordinal);
            Assert.Contains("security/sharplabnext-runtime-job-v1.apparmor", commonPowerShell, StringComparison.Ordinal);
            Assert.Contains("security/licenses/moby-profiles-Apache-2.0.txt", commonShell, StringComparison.Ordinal);
            Assert.Contains("security/licenses/moby-profiles-Apache-2.0.txt", commonPowerShell, StringComparison.Ordinal);

            var productionCompose = await File.ReadAllTextAsync(
                Path.Combine(output, "compose.prod.yaml"),
                TestContext.Current.CancellationToken);
            Assert.Contains("source: ./profile-update-status.json", productionCompose, StringComparison.Ordinal);
            Assert.Contains("target: /app/config/profile-update-status.json", productionCompose, StringComparison.Ordinal);
            Assert.Contains("create_host_path: false", productionCompose, StringComparison.Ordinal);
            Assert.Contains("InternalServiceAuth__Required: \"true\"", productionCompose, StringComparison.Ordinal);
            Assert.Contains(
                """
                  logging:
                    driver: local
                    options:
                      max-size: "10m"
                      max-file: "3"
                """,
                productionCompose,
                StringComparison.Ordinal);
            Assert.Contains(
                "InternalServiceAuth__TokenFile: /run/secrets/sharplabnext-internal-service-token",
                productionCompose,
                StringComparison.Ordinal);
            Assert.Contains("SHARPLABNEXT_INTERNAL_SERVICE_TOKEN_FILE", productionCompose, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "sharplabnext-development-internal-token-only-2026",
                productionCompose,
                StringComparison.Ordinal);
            Assert.Contains("GitHub__OAuth__Enabled:", productionCompose, StringComparison.Ordinal);
            Assert.Contains("SHARPLABNEXT_GITHUB_OAUTH_CLIENT_ID", productionCompose, StringComparison.Ordinal);
            Assert.Contains("SHARPLABNEXT_GITHUB_OAUTH_CALLBACK_URI", productionCompose, StringComparison.Ordinal);
            Assert.Contains("SHARPLABNEXT_GITHUB_OAUTH_CLIENT_SECRET_FILE", productionCompose, StringComparison.Ordinal);
            Assert.Contains(
                "GitHub__OAuth__ClientSecretFile: /run/secrets/sharplabnext-github-oauth-client-secret",
                productionCompose,
                StringComparison.Ordinal);
            Assert.DoesNotContain("GitHub__OAuth__ClientSecret:", productionCompose, StringComparison.Ordinal);
            Assert.Contains(
                "RuntimeSupervisor__Sandbox__AppArmorProfile: \"${SHARPLABNEXT_RUNTIME_APPARMOR_PROFILE:-}\"",
                productionCompose,
                StringComparison.Ordinal);
            var disabledOAuthSecret = Path.Combine(
                output,
                ReleaseBundleBuilder.DisabledGitHubOAuthSecretFileName);
            Assert.True(File.Exists(disabledOAuthSecret));
            Assert.Equal(0, new FileInfo(disabledOAuthSecret).Length);

            var compose = await File.ReadAllTextAsync(
                Path.Combine(output, "compose.generated.yaml"),
                TestContext.Current.CancellationToken);
            Assert.Contains("worker-fsharp:", compose, StringComparison.Ordinal);
            AssertToolchainWorkerIdentityOverlay(
                compose,
                activeReleaseId,
                "worker-roslyn-stable",
                "RoslynWorker__ReleaseId",
                "RoslynWorker__WorkerImageId",
                "roslyn-stable",
                FakeDockerCli.RoslynStableImageId,
                FakeDockerCli.RoslynNetFx48ImageId,
                FakeDockerCli.RoslynMainImageId);
            AssertToolchainWorkerIdentityOverlay(
                compose,
                activeReleaseId,
                "worker-roslyn-netfx48",
                "RoslynWorker__ReleaseId",
                "RoslynWorker__WorkerImageId",
                "roslyn-stable-netfx48",
                FakeDockerCli.RoslynNetFx48ImageId,
                FakeDockerCli.RoslynStableImageId,
                FakeDockerCli.RoslynMainImageId);
            AssertToolchainWorkerIdentityOverlay(
                compose,
                activeReleaseId,
                "worker-roslyn-main",
                "RoslynWorker__ReleaseId",
                "RoslynWorker__WorkerImageId",
                "roslyn-main",
                FakeDockerCli.RoslynMainImageId,
                FakeDockerCli.RoslynStableImageId);
            AssertToolchainWorkerIdentityOverlay(
                compose,
                activeReleaseId,
                "worker-roslyn-const-generics",
                "RoslynWorker__ReleaseId",
                "RoslynWorker__WorkerImageId",
                "roslyn-const-generics",
                FakeDockerCli.RoslynConstGenericsImageId,
                FakeDockerCli.RoslynMainImageId);
            AssertToolchainWorkerIdentityOverlay(
                compose,
                activeReleaseId,
                "worker-fsharp",
                "FSharpWorker__ReleaseId",
                "FSharpWorker__WorkerImageId",
                "fsharp-stable",
                FakeDockerCli.FSharpImageId,
                FakeDockerCli.IlImageId);
            AssertToolchainWorkerIdentityOverlay(
                compose,
                activeReleaseId,
                "worker-gsharp",
                "GSharp__ReleaseId",
                "GSharp__WorkerImageId",
                "gsharp-stable",
                FakeDockerCli.GSharpImageId,
                FakeDockerCli.FSharpImageId);
            AssertToolchainWorkerIdentityOverlay(
                compose,
                activeReleaseId,
                "worker-peachpie",
                "PeachPie__ReleaseId",
                "PeachPie__WorkerImageId",
                "peachpie-stable",
                FakeDockerCli.PeachPieImageId,
                FakeDockerCli.GSharpImageId);
            AssertToolchainWorkerIdentityOverlay(
                compose,
                activeReleaseId,
                "worker-cppcli",
                "CppCli__ReleaseId",
                "CppCli__WorkerImageId",
                "msvc-cppcli-netfx48",
                FakeDockerCli.CppCliImageId,
                FakeDockerCli.PeachPieImageId);
            AssertToolchainWorkerIdentityOverlay(
                compose,
                activeReleaseId,
                "worker-jsharp",
                "JSharp__ReleaseId",
                "JSharp__WorkerImageId",
                "vjc-jsharp20",
                FakeDockerCli.JSharpImageId,
                FakeDockerCli.CppCliImageId);
            AssertToolchainWorkerIdentityOverlay(
                compose,
                activeReleaseId,
                "worker-il",
                "IlWorker__ReleaseId",
                "IlWorker__WorkerImageId",
                "mobius-ilasm-stable",
                FakeDockerCli.IlImageId,
                FakeDockerCli.FSharpImageId);
            AssertToolchainWorkerIdentityOverlay(
                compose,
                activeReleaseId,
                "worker-minilang",
                "MINILANG_RELEASE_ID",
                "MINILANG_WORKER_IMAGE_ID",
                "minilang-stable",
                FakeDockerCli.MinilangImageId,
                FakeDockerCli.IlImageId);
            AssertArtifactWorkerIdentityOverlay(
                compose,
                activeReleaseId,
                "worker-artifacts-default",
                "ArtifactWorker__ReleaseId",
                "ArtifactWorker__WorkerImageId",
                "artifacts-default",
                FakeDockerCli.ArtifactsDefaultImageId,
                FakeDockerCli.ArtifactsConstGenericsImageId,
                FakeDockerCli.ArtifactsIlAssemblerImageId);
            AssertArtifactWorkerIdentityOverlay(
                compose,
                activeReleaseId,
                "worker-artifacts-jsil",
                "Jsil__ReleaseId",
                "Jsil__WorkerImageId",
                "artifacts-jsil",
                FakeDockerCli.ArtifactsJsilImageId,
                FakeDockerCli.ArtifactsDefaultImageId,
                FakeDockerCli.ArtifactsConstGenericsImageId);
            AssertArtifactWorkerIdentityOverlay(
                compose,
                activeReleaseId,
                "worker-artifacts-const-generics",
                "ConstGenericsArtifactWorker__ReleaseId",
                "ConstGenericsArtifactWorker__WorkerImageId",
                "artifacts-const-generics",
                FakeDockerCli.ArtifactsConstGenericsImageId,
                FakeDockerCli.ArtifactsDefaultImageId,
                FakeDockerCli.ArtifactsIlAssemblerImageId);
            AssertArtifactWorkerIdentityOverlay(
                compose,
                activeReleaseId,
                "worker-artifacts-il-assembler",
                "ArtifactAssembler__ReleaseId",
                "ArtifactAssembler__WorkerImageId",
                "il-assembler",
                FakeDockerCli.ArtifactsIlAssemblerImageId,
                FakeDockerCli.ArtifactsDefaultImageId,
                FakeDockerCli.ArtifactsConstGenericsImageId);
            Assert.Contains("RuntimeSupervisorProfileOverlay__Enabled: \"true\"", compose, StringComparison.Ordinal);
            Assert.Contains("RuntimeSupervisorProfileOverlay__SecurityPolicies__0__Id: \"runtime-job-default\"", compose, StringComparison.Ordinal);
            Assert.Contains("RuntimeSupervisorProfileOverlay__SecurityPolicies__1__Id: \"runtime-job-wine-jsharp20\"", compose, StringComparison.Ordinal);
            Assert.Contains("RuntimeSupervisorProfileOverlay__SecurityPolicies__2__Id: \"runtime-job-wine-netfx\"", compose, StringComparison.Ordinal);
            Assert.DoesNotContain("RuntimeSupervisor__Profiles__", compose, StringComparison.Ordinal);
            var generatedConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(ReadComposeEnvironment(compose, "runtime-supervisor"))
                .Build();
            var generatedProfileOverlay = new RuntimeSupervisorProfileOverlayOptions();
            generatedConfiguration
                .GetSection(RuntimeSupervisorProfileOverlayOptions.SectionName)
                .Bind(generatedProfileOverlay);
            Assert.True(generatedProfileOverlay.Enabled);
            Assert.Equal(36, generatedProfileOverlay.Profiles.Count);
            Assert.Equal(3, generatedProfileOverlay.SecurityPolicies.Count);
            var dotnet10Profile = generatedProfileOverlay.Profiles.Single(static profile =>
                profile.Id == "dotnet-10-linux-x64");
            var dotnet10Promotion = PromotionFixtures.Value.ImagesByReference.Values.Single(static image =>
                image.RuntimeId == "dotnet-10-linux-x64");
            Assert.Equal(dotnet10Promotion.ImageId, dotnet10Profile.Image);
            Assert.Equal(dotnet10Promotion.ImageId, dotnet10Profile.RuntimeImageId);
            Assert.Contains("execution-flow", dotnet10Profile.Capabilities, StringComparer.Ordinal);
            Assert.Equal("sharplabnext-runner-v1", dotnet10Profile.Operations?.Run?.ImplementationId);
            Assert.Equal("linux-profiler", dotnet10Profile.Operations?.Jit?.SourceMappingKind);
            var wineProfile = generatedProfileOverlay.Profiles.Single(static profile =>
                profile.Id == "wine-netfx48-linux-x64");
            Assert.Equal("not-applicable", wineProfile.RuntimeCommit);
            Assert.Equal("not-applicable", wineProfile.JitVersion);
            Assert.Equal("not-applicable", wineProfile.JitCommit);
            using var sourceWineProfile = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(repositoryRoot, "profiles", "runtimes", "wine-netfx48-linux-x64.json"),
                TestContext.Current.CancellationToken));
            Assert.Equal(
                sourceWineProfile.RootElement.GetProperty("container").GetProperty("winePrefixPath").GetString(),
                wineProfile.Container.WinePrefixPath);
            var sourceWineRun = sourceWineProfile.RootElement.GetProperty("operations").GetProperty("run");
            Assert.Equal(
                sourceWineRun.GetProperty("implementationId").GetString(),
                wineProfile.Operations?.Run?.ImplementationId);
            Assert.Equal(
                sourceWineRun.GetProperty("pathStyle").GetString(),
                wineProfile.Operations?.Run?.PathStyle);
            var jsharpProfile = generatedProfileOverlay.Profiles.Single(static profile =>
                profile.Id == "wine-jsharp20-linux-x64");
            Assert.Equal("x64", jsharpProfile.Architecture);
            Assert.Equal("not-applicable", jsharpProfile.RuntimeCommit);
            Assert.Equal("not-applicable", jsharpProfile.JitVersion);
            Assert.Equal("not-applicable", jsharpProfile.JitCommit);
            var configuredSupervisor = new RuntimeSupervisorOptions
            {
                RequireDigestPinnedImages = true,
                Profiles = Enumerable.Range(0, 7)
                    .Select(index => new RuntimeProfileOptions { Id = $"stale-{index}" })
                    .ToList(),
                SecurityPolicies = Enumerable.Range(0, 5)
                    .Select(index => new RuntimeSecurityPolicyOptions { Id = $"stale-{index}" })
                    .ToList()
            };
            generatedProfileOverlay.ApplyTo(configuredSupervisor);
            var supervisorValidation = new RuntimeSupervisorOptionsValidator().Validate(null, configuredSupervisor);
            Assert.True(
                supervisorValidation.Succeeded,
                string.Join(Environment.NewLine, supervisorValidation.Failures ?? []));
            Assert.DoesNotContain(
                configuredSupervisor.Profiles,
                static profile => profile.Id.StartsWith("stale-", StringComparison.Ordinal));
            Assert.DoesNotContain(":latest", compose, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, compose.Split("  runtime-supervisor:", StringSplitOptions.None).Length - 1);

            using var lockDocument = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(output, "lock.json"),
                TestContext.Current.CancellationToken));
            var lockedComponents = lockDocument.RootElement.GetProperty("components");
            Assert.Equal(
                PromotionFixtures.Value.ImagesByReference.Values.Single(static image =>
                    image.RuntimeId == "dotnet-10-linux-x64").ImageId,
                lockedComponents.GetProperty("dotnet-10-linux-x64")
                    .GetProperty("imageId")
                    .GetString());
            Assert.Equal(
                FakeDockerCli.ImageId,
                lockedComponents.GetProperty("const-generics-linux-x64")
                    .GetProperty("imageId")
                    .GetString());
            Assert.Equal(
                FakeDockerCli.RoslynConstGenericsImageId,
                lockedComponents.GetProperty("roslyn-const-generics")
                    .GetProperty("imageId")
                    .GetString());
            Assert.Equal(
                FakeDockerCli.RoslynStableImageId,
                lockedComponents.GetProperty("roslyn-stable")
                    .GetProperty("imageId")
                    .GetString());
            Assert.Equal(
                FakeDockerCli.RoslynNetFx48ImageId,
                lockedComponents.GetProperty("roslyn-stable-netfx48")
                    .GetProperty("imageId")
                    .GetString());
            Assert.Equal(
                FakeDockerCli.ArtifactsConstGenericsImageId,
                lockedComponents.GetProperty("artifacts-const-generics")
                    .GetProperty("imageId")
                    .GetString());
            Assert.Equal(
                FakeDockerCli.GSharpImageId,
                lockedComponents.GetProperty("gsharp-stable")
                    .GetProperty("imageId")
                    .GetString());
            Assert.Equal(
                FakeDockerCli.PeachPieImageId,
                lockedComponents.GetProperty("peachpie-stable")
                    .GetProperty("imageId")
                    .GetString());
            Assert.Equal(
                FakeDockerCli.CppCliImageId,
                lockedComponents.GetProperty("msvc-cppcli-netfx48")
                    .GetProperty("imageId")
                    .GetString());
            Assert.Equal(
                FakeDockerCli.JSharpImageId,
                lockedComponents.GetProperty("vjc-jsharp20")
                    .GetProperty("imageId")
                    .GetString());
            Assert.Equal(
                FakeDockerCli.WineNetFxRuntimeImageId,
                lockedComponents.GetProperty("wine-netfx48-linux-x64")
                    .GetProperty("imageId")
                    .GetString());
            Assert.Equal(
                FakeDockerCli.WineJSharpRuntimeImageId,
                lockedComponents.GetProperty("wine-jsharp20-linux-x64")
                    .GetProperty("imageId")
                    .GetString());
            Assert.Equal(result.Images.Count, docker.SavedReferences.Count);
            Assert.Equal(
                result.Images.Select(static image => image.ImageId).Order(StringComparer.Ordinal),
                docker.SavedReferences.Order(StringComparer.Ordinal));
            using var bundle = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(output, "bundle.json"),
                TestContext.Current.CancellationToken));
            var source = bundle.RootElement.GetProperty("source");
            Assert.Equal(TestSourceRevision, source.GetProperty("revision").GetString());
            Assert.True(source.GetProperty("verified").GetBoolean());
            Assert.False(source.GetProperty("dirty").GetBoolean());
            Assert.False(source.GetProperty("developmentOverrideUsed").GetBoolean());
            var runtimeImage = bundle.RootElement.GetProperty("images").EnumerateArray().Single(static image =>
                image.GetProperty("id").GetString() == "dotnet-10-linux-x64");
            Assert.Equal(
                dotnet10Promotion.RuntimeCommit,
                runtimeImage.GetProperty("runtimeCommit").GetString());
            Assert.Equal(
                dotnet10Promotion.JitCommit,
                runtimeImage.GetProperty("jitCommit").GetString());
            var netFxWorkerImage = bundle.RootElement.GetProperty("images").EnumerateArray().Single(static image =>
                image.GetProperty("id").GetString() == "worker-roslyn-netfx48");
            Assert.Equal(FakeDockerCli.RoslynNetFx48ImageId, netFxWorkerImage.GetProperty("imageId").GetString());
            var wineRuntimeImage = bundle.RootElement.GetProperty("images").EnumerateArray().Single(static image =>
                image.GetProperty("id").GetString() == "wine-netfx48-linux-x64");
            Assert.Equal("wine-netfx48-linux-x64", wineRuntimeImage.GetProperty("runtimeId").GetString());
            Assert.False(wineRuntimeImage.TryGetProperty("runtimeCommit", out _));
            Assert.False(wineRuntimeImage.TryGetProperty("jitCommit", out _));
            var jsharpRuntimeImage = bundle.RootElement.GetProperty("images").EnumerateArray().Single(static image =>
                image.GetProperty("id").GetString() == "wine-jsharp20-linux-x64");
            Assert.Equal("wine-jsharp20-linux-x64", jsharpRuntimeImage.GetProperty("runtimeId").GetString());
            Assert.Equal("amd64", jsharpRuntimeImage.GetProperty("architecture").GetString());
            Assert.False(jsharpRuntimeImage.TryGetProperty("runtimeCommit", out _));
            Assert.False(jsharpRuntimeImage.TryGetProperty("jitCommit", out _));
            using var provenance = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(output, "provenance", "release.slsa.json"),
                TestContext.Current.CancellationToken));
            var externalParameters = provenance.RootElement
                .GetProperty("predicate")
                .GetProperty("buildDefinition")
                .GetProperty("externalParameters");
            Assert.Equal(TestSourceRevision, externalParameters.GetProperty("sourceRevision").GetString());
            Assert.True(externalParameters.GetProperty("sourceVerified").GetBoolean());
            Assert.False(externalParameters.GetProperty("developmentSourceOverride").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
            File.Delete(statusPath);
        }
    }

    [Fact]
    public async Task MetadataOnlyBundleRetainsDeploymentMetadataWithoutSavingImages()
    {
        var repositoryRoot = FindRepositoryRoot();
        var output = Path.Combine(Path.GetTempPath(), $"sharplabnext-metadata-{Guid.NewGuid():N}");
        try
        {
            var command = new BundleBuilderCommand(
                repositoryRoot,
                Path.Combine(repositoryRoot, "profiles", "catalog", "catalog.json"),
                Path.Combine(repositoryRoot, "profiles", "lock.json"),
                Path.Combine(repositoryRoot, "deploy", "images.json"),
                Path.Combine(repositoryRoot, "profiles", "license-policy.json"),
                Path.Combine(repositoryRoot, "deploy", "compose.prod.yaml"),
                Path.Combine(repositoryRoot, "THIRD-PARTY-NOTICES.md"),
                output,
                "docker",
                "openssl",
                null,
                null,
                null,
                "registry.example.test/private",
                MetadataOnly: true,
                new Dictionary<string, string>());
            var docker = new FakeDockerCli();

            var result = await CreateBuilder(docker).BuildAsync(
                command,
                TestContext.Current.CancellationToken);

            Assert.False(result.ContainsImages);
            Assert.False(File.Exists(Path.Combine(output, "images.tar")));
            Assert.Empty(docker.SavedReferences);
            Assert.All(
                result.Images.Where(static image => !image.SourceReference.Contains('@')),
                image => Assert.StartsWith(
                    "registry.example.test/private/",
                    image.SourceReference,
                    StringComparison.Ordinal));
            Assert.All(
                result.Images.Where(static image => image.SourceReference.Contains('@')),
                image => Assert.Equal(
                    PromotionFixtures.Value.ImagesByReference[image.SourceReference].Reference,
                    image.SourceReference));
            using var bundle = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(output, "bundle.json"),
                TestContext.Current.CancellationToken));
            Assert.False(bundle.RootElement.GetProperty("containsImages").GetBoolean());
            Assert.Equal(54, bundle.RootElement.GetProperty("images").GetArrayLength());
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PowerShellVerifierRejectsMissingAndUnchecksummedPromotionEvidence()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(Path.GetTempPath(), $"sharplabnext-promotion-evidence-verify-{Guid.NewGuid():N}");
        var bundle = Path.Combine(testRoot, "bundle");
        var fakeBin = Path.Combine(testRoot, "bin");
        Directory.CreateDirectory(fakeBin);
        try
        {
            await BuildBundleForReleaseAsync(repositoryRoot, testRoot, bundle, "promotion-evidence");
            await File.WriteAllTextAsync(
                Path.Combine(fakeBin, "docker.cmd"),
                "@echo off\r\n" +
                "setlocal EnableDelayedExpansion\r\n" +
                "set last=\r\n" +
                "for %%a in (%*) do set last=%%~a\r\n" +
                "echo %* | %SystemRoot%\\System32\\findstr.exe /C:\"image inspect\" >nul && (echo !last!& exit /b 0)\r\n" +
                "echo %* | %SystemRoot%\\System32\\findstr.exe /C:\"compose\" >nul && exit /b 0\r\n" +
                "exit /b 0\r\n",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(fakeBin, "openssl.cmd"),
                "@echo off\r\nexit /b 0\r\n",
                TestContext.Current.CancellationToken);
            var environment = new Dictionary<string, string>
            {
                ["PATH"] = string.Concat(fakeBin, ";", Environment.GetEnvironmentVariable("PATH"))
            };
            var verify = Path.Combine(bundle, "verify.ps1");
            var initial = await RunAsync(
                "pwsh",
                ["-NoProfile", "-File", verify, "-AllowUnsigned"],
                environment);
            Assert.True(
                initial.ExitCode == 0,
                $"Initial offline verification failed. stdout: {initial.StandardOutput}{Environment.NewLine}stderr: {initial.StandardError}");

            var materializeScript = Path.Combine(testRoot, "materialize-installed.ps1");
            await File.WriteAllTextAsync(
                materializeScript,
                "param([string]$Bundle, [string]$InstallRoot, [string]$ReleaseId)\n" +
                ". (Join-Path $Bundle 'deployment-common.ps1')\n" +
                "Install-ReleaseAssets $Bundle $InstallRoot $ReleaseId | Out-Null\n",
                TestContext.Current.CancellationToken);
            var installedRoot = Path.Combine(testRoot, "installed", "releases", "promotion-evidence");
            var materializeInstalled = await RunAsync(
                "pwsh",
                [
                    "-NoProfile",
                    "-File",
                    materializeScript,
                    bundle,
                    Path.Combine(testRoot, "installed"),
                    "promotion-evidence"
                ],
                environment);
            Assert.True(
                materializeInstalled.ExitCode == 0,
                $"Installed-copy materialization failed. stdout: {materializeInstalled.StandardOutput}{Environment.NewLine}stderr: {materializeInstalled.StandardError}");
            var installedUnsigned = await RunAsync(
                "pwsh",
                ["-NoProfile", "-File", Path.Combine(installedRoot, "verify.ps1"), "-AllowUnsigned", "-InstalledCopy"],
                environment);
            Assert.True(
                installedUnsigned.ExitCode == 0,
                $"Unsigned installed-copy verification failed. stdout: {installedUnsigned.StandardOutput}{Environment.NewLine}stderr: {installedUnsigned.StandardError}");

            var deploymentChecksumPath = Path.Combine(installedRoot, "deployment.sha256");
            var deploymentChecksumBytes = await File.ReadAllBytesAsync(
                deploymentChecksumPath,
                TestContext.Current.CancellationToken);
            await File.AppendAllTextAsync(
                deploymentChecksumPath,
                "invalid installed deployment line\n",
                TestContext.Current.CancellationToken);
            var tamperedDeploymentManifest = await RunAsync(
                "pwsh",
                ["-NoProfile", "-File", Path.Combine(installedRoot, "verify.ps1"), "-AllowUnsigned", "-InstalledCopy"],
                environment);
            Assert.NotEqual(0, tamperedDeploymentManifest.ExitCode);
            Assert.Contains(
                "deployment checksum manifest",
                tamperedDeploymentManifest.StandardOutput + tamperedDeploymentManifest.StandardError,
                StringComparison.OrdinalIgnoreCase);
            await File.WriteAllBytesAsync(
                deploymentChecksumPath,
                deploymentChecksumBytes,
                TestContext.Current.CancellationToken);

            var incomingDeploymentChecksum = Path.Combine(bundle, "deployment.sha256");
            File.Copy(deploymentChecksumPath, incomingDeploymentChecksum);
            var incomingWithDeploymentManifest = await RunAsync(
                "pwsh",
                ["-NoProfile", "-File", verify, "-AllowUnsigned"],
                environment);
            Assert.NotEqual(0, incomingWithDeploymentManifest.ExitCode);
            Assert.Contains(
                "unchecksummed",
                incomingWithDeploymentManifest.StandardOutput + incomingWithDeploymentManifest.StandardError,
                StringComparison.OrdinalIgnoreCase);
            File.Delete(incomingDeploymentChecksum);

            var originalBundleBytes = await File.ReadAllBytesAsync(
                Path.Combine(bundle, "bundle.json"),
                TestContext.Current.CancellationToken);
            var originalChecksumBytes = await File.ReadAllBytesAsync(
                Path.Combine(bundle, "checksums.sha256"),
                TestContext.Current.CancellationToken);
            var publicKeyBytes = "installed-copy test public key"u8.ToArray();
            await File.WriteAllBytesAsync(
                Path.Combine(bundle, "signing-public-key.pem"),
                publicKeyBytes,
                TestContext.Current.CancellationToken);
            var signedBundle = JsonNode.Parse(originalBundleBytes)
                ?? throw new InvalidOperationException("Installed-copy test bundle is empty.");
            signedBundle["hasSignature"] = true;
            signedBundle["signatureAlgorithm"] = "ed25519";
            signedBundle["signatureKeyId"] = "installed-copy-test";
            signedBundle["signingPublicKeySha256"] = Convert.ToHexStringLower(SHA256.HashData(publicKeyBytes));
            await File.WriteAllTextAsync(
                Path.Combine(bundle, "bundle.json"),
                signedBundle.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                TestContext.Current.CancellationToken);
            await ReleaseBundleBuilder.WriteChecksumsAsync(bundle, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(bundle, "checksums.sha256.sig"),
                "test signature",
                TestContext.Current.CancellationToken);
            var signedInstallBase = Path.Combine(testRoot, "installed-signed");
            var signedInstalledRoot = Path.Combine(signedInstallBase, "releases", "promotion-evidence");
            var materializeSigned = await RunAsync(
                "pwsh",
                [
                    "-NoProfile",
                    "-File",
                    materializeScript,
                    bundle,
                    signedInstallBase,
                    "promotion-evidence"
                ],
                environment);
            Assert.True(
                materializeSigned.ExitCode == 0,
                $"Signed installed-copy materialization failed. stdout: {materializeSigned.StandardOutput}{Environment.NewLine}stderr: {materializeSigned.StandardError}");
            var installedSigned = await RunAsync(
                "pwsh",
                ["-NoProfile", "-File", Path.Combine(signedInstalledRoot, "verify.ps1"), "-TrustBundledPublicKey", "-InstalledCopy"],
                environment);
            Assert.True(
                installedSigned.ExitCode == 0,
                $"Signed installed-copy verification failed. stdout: {installedSigned.StandardOutput}{Environment.NewLine}stderr: {installedSigned.StandardError}");
            File.Delete(Path.Combine(bundle, "checksums.sha256.sig"));
            File.Delete(Path.Combine(bundle, "signing-public-key.pem"));
            await File.WriteAllBytesAsync(
                Path.Combine(bundle, "bundle.json"),
                originalBundleBytes,
                TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(
                Path.Combine(bundle, "checksums.sha256"),
                originalChecksumBytes,
                TestContext.Current.CancellationToken);

            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(
                Path.Combine(bundle, ReleaseBundleBuilder.PromotionEvidenceDirectoryName, "manifest.json"),
                TestContext.Current.CancellationToken));
            var entryPath = document.RootElement.GetProperty("entries")[0]
                .GetProperty("bundlePath").GetString()
                ?? throw new InvalidOperationException("Promotion evidence fixture has no bundle path.");
            var evidencePath = Path.Combine(
                bundle,
                ReleaseBundleBuilder.PromotionEvidenceDirectoryName,
                entryPath.Replace('/', Path.DirectorySeparatorChar));
            var original = await File.ReadAllBytesAsync(evidencePath, TestContext.Current.CancellationToken);
            File.Delete(evidencePath);
            var missing = await RunAsync(
                "pwsh",
                ["-NoProfile", "-File", verify, "-AllowUnsigned"],
                environment);
            Assert.NotEqual(0, missing.ExitCode);
            Assert.Contains("Missing bundle file", missing.StandardOutput + missing.StandardError, StringComparison.Ordinal);

            await File.WriteAllBytesAsync(evidencePath, original, TestContext.Current.CancellationToken);
            foreach (var requiredKind in new[] { "performance-policy", "active-profile" })
            {
                var requiredPath = document.RootElement.GetProperty("entries")
                    .EnumerateArray()
                    .First(entry => entry.GetProperty("kind").GetString() == requiredKind)
                    .GetProperty("bundlePath").GetString()
                    ?? throw new InvalidOperationException($"Promotion evidence fixture has no {requiredKind} path.");
                var requiredFile = Path.Combine(
                    bundle,
                    ReleaseBundleBuilder.PromotionEvidenceDirectoryName,
                    requiredPath.Replace('/', Path.DirectorySeparatorChar));
                var requiredBytes = await File.ReadAllBytesAsync(requiredFile, TestContext.Current.CancellationToken);
                File.Delete(requiredFile);
                var requiredMissing = await RunAsync(
                    "pwsh",
                    ["-NoProfile", "-File", verify, "-AllowUnsigned"],
                    environment);
                Assert.NotEqual(0, requiredMissing.ExitCode);
                Assert.Contains("Missing bundle file", requiredMissing.StandardOutput + requiredMissing.StandardError, StringComparison.Ordinal);
                await File.WriteAllBytesAsync(requiredFile, requiredBytes, TestContext.Current.CancellationToken);
            }

            var tsvPath = Path.Combine(bundle, ReleaseBundleBuilder.PromotionEvidenceDirectoryName, "manifest.tsv");
            var tsv = await File.ReadAllTextAsync(tsvPath, TestContext.Current.CancellationToken);
            var promotedLine = tsv.Split('\n')[4];
            var firstRuntimeId = promotedLine.Split('\t')[1].Split(',')[0];
            await File.WriteAllTextAsync(
                tsvPath,
                tsv.Replace(promotedLine, promotedLine + "," + firstRuntimeId, StringComparison.Ordinal),
                TestContext.Current.CancellationToken);
            await ReleaseBundleBuilder.WriteChecksumsAsync(bundle, TestContext.Current.CancellationToken);
            var duplicated = await RunAsync(
                "pwsh",
                ["-NoProfile", "-File", verify, "-AllowUnsigned"],
                environment);
            Assert.NotEqual(0, duplicated.ExitCode);
            Assert.Contains("verification manifests disagree", duplicated.StandardOutput + duplicated.StandardError, StringComparison.OrdinalIgnoreCase);
            await File.WriteAllTextAsync(tsvPath, tsv, TestContext.Current.CancellationToken);

            var reorderedLines = tsv.Split('\n');
            (reorderedLines[5], reorderedLines[6]) = (reorderedLines[6], reorderedLines[5]);
            await File.WriteAllTextAsync(
                tsvPath,
                string.Join('\n', reorderedLines),
                TestContext.Current.CancellationToken);
            await ReleaseBundleBuilder.WriteChecksumsAsync(bundle, TestContext.Current.CancellationToken);
            var reordered = await RunAsync(
                "pwsh",
                ["-NoProfile", "-File", verify, "-AllowUnsigned"],
                environment);
            Assert.NotEqual(0, reordered.ExitCode);
            Assert.Contains("verification manifests disagree", reordered.StandardOutput + reordered.StandardError, StringComparison.OrdinalIgnoreCase);
            await File.WriteAllTextAsync(tsvPath, tsv, TestContext.Current.CancellationToken);

            await File.WriteAllTextAsync(
                tsvPath,
                tsv.TrimEnd('\n'),
                TestContext.Current.CancellationToken);
            await ReleaseBundleBuilder.WriteChecksumsAsync(bundle, TestContext.Current.CancellationToken);
            var missingTerminalLf = await RunAsync(
                "pwsh",
                ["-NoProfile", "-File", verify, "-AllowUnsigned"],
                environment);
            Assert.NotEqual(0, missingTerminalLf.ExitCode);
            Assert.Contains("verification manifests disagree", missingTerminalLf.StandardOutput + missingTerminalLf.StandardError, StringComparison.OrdinalIgnoreCase);
            await File.WriteAllTextAsync(tsvPath, tsv, TestContext.Current.CancellationToken);

            var manifestPath = Path.Combine(bundle, ReleaseBundleBuilder.PromotionEvidenceDirectoryName, "manifest.json");
            var manifestBytes = await File.ReadAllBytesAsync(manifestPath, TestContext.Current.CancellationToken);
            var originalManifest = JsonNode.Parse(manifestBytes)?.AsObject()
                ?? throw new InvalidOperationException("Promotion evidence fixture manifest is invalid.");
            Assert.Equal(tsv, CreatePromotionEvidenceVerificationManifest(originalManifest, manifestBytes));
            const string retainedMatrixPath = "profiles/runtime-matrix.json";
            var retainedMatrixFile = Path.Combine(
                bundle,
                ReleaseBundleBuilder.PromotionEvidenceDirectoryName,
                "source",
                retainedMatrixPath.Replace('/', Path.DirectorySeparatorChar));
            var retainedMatrixBytes = await File.ReadAllBytesAsync(
                retainedMatrixFile,
                TestContext.Current.CancellationToken);
            var forgedMatrix = JsonNode.Parse(retainedMatrixBytes)?.AsObject()
                ?? throw new InvalidOperationException("Promotion runtime matrix fixture is invalid.");
            var forgedTarget = forgedMatrix["coreClr"]?.AsArray()
                .Select(static item => item!.AsObject())
                .Single(static item => item["id"]!.GetValue<string>() == "dotnet-10")
                ?? throw new InvalidOperationException("Promotion runtime matrix fixture has no dotnet-10 row.");
            var forgedCapability = forgedTarget["linuxCapability"]!.AsObject();
            forgedCapability["promotionState"] = "blocked";
            forgedCapability.Remove("promotionReceipt");
            var forgedMatrixBytes = Encoding.UTF8.GetBytes(
                (forgedMatrix.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n")
                .ReplaceLineEndings("\n"));
            await ReplacePromotionEvidenceSourcesAndRechecksumAsync(
                bundle,
                new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    [retainedMatrixPath] = forgedMatrixBytes
                });
            var matrixForgery = await RunAsync(
                "pwsh",
                ["-NoProfile", "-File", verify, "-AllowUnsigned"],
                environment);
            Assert.NotEqual(0, matrixForgery.ExitCode);
            var matrixForgeryOutput = matrixForgery.StandardOutput + matrixForgery.StandardError;
            Assert.True(
                matrixForgeryOutput.Contains("derived from runtime matrix", StringComparison.OrdinalIgnoreCase),
                matrixForgeryOutput);
            await ReplacePromotionEvidenceSourcesAndRechecksumAsync(
                bundle,
                new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    [retainedMatrixPath] = retainedMatrixBytes
                });
            await File.WriteAllBytesAsync(manifestPath, manifestBytes, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(tsvPath, tsv, TestContext.Current.CancellationToken);
            await ReleaseBundleBuilder.WriteChecksumsAsync(bundle, TestContext.Current.CancellationToken);

            await File.WriteAllBytesAsync(manifestPath, [0xEF, 0xBB, 0xBF, .. manifestBytes], TestContext.Current.CancellationToken);
            await ReleaseBundleBuilder.WriteChecksumsAsync(bundle, TestContext.Current.CancellationToken);
            var bom = await RunAsync(
                "pwsh",
                ["-NoProfile", "-File", verify, "-AllowUnsigned"],
                environment);
            Assert.NotEqual(0, bom.ExitCode);
            Assert.Contains("without BOM", bom.StandardOutput + bom.StandardError, StringComparison.OrdinalIgnoreCase);
            await File.WriteAllBytesAsync(manifestPath, manifestBytes, TestContext.Current.CancellationToken);

            await File.WriteAllTextAsync(
                tsvPath,
                tsv.Replace("manifestJsonSha256\tsha256:", "manifestJsonSha256\tsha256:0", StringComparison.Ordinal),
                TestContext.Current.CancellationToken);
            await ReleaseBundleBuilder.WriteChecksumsAsync(bundle, TestContext.Current.CancellationToken);
            var mismatched = await RunAsync(
                "pwsh",
                ["-NoProfile", "-File", verify, "-AllowUnsigned"],
                environment);
            Assert.NotEqual(0, mismatched.ExitCode);
            Assert.Contains("verification manifests disagree", mismatched.StandardOutput + mismatched.StandardError, StringComparison.OrdinalIgnoreCase);
            await File.WriteAllTextAsync(tsvPath, tsv, TestContext.Current.CancellationToken);
            await ReleaseBundleBuilder.WriteChecksumsAsync(bundle, TestContext.Current.CancellationToken);

            var tsvBytes = await File.ReadAllBytesAsync(tsvPath, TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(tsvPath, [0xEF, 0xBB, 0xBF, .. tsvBytes], TestContext.Current.CancellationToken);
            await ReleaseBundleBuilder.WriteChecksumsAsync(bundle, TestContext.Current.CancellationToken);
            var tsvBom = await RunAsync(
                "pwsh",
                ["-NoProfile", "-File", verify, "-AllowUnsigned"],
                environment);
            Assert.NotEqual(0, tsvBom.ExitCode);
            Assert.Contains("without BOM", tsvBom.StandardOutput + tsvBom.StandardError, StringComparison.OrdinalIgnoreCase);
            await File.WriteAllBytesAsync(tsvPath, tsvBytes, TestContext.Current.CancellationToken);
            await ReleaseBundleBuilder.WriteChecksumsAsync(bundle, TestContext.Current.CancellationToken);

            await File.WriteAllTextAsync(
                Path.Combine(bundle, ReleaseBundleBuilder.PromotionEvidenceDirectoryName, "unexpected.txt"),
                "unexpected",
                TestContext.Current.CancellationToken);
            var extra = await RunAsync(
                "pwsh",
                ["-NoProfile", "-File", verify, "-AllowUnsigned"],
                environment);
            Assert.NotEqual(0, extra.ExitCode);
            Assert.Contains("unchecksummed", extra.StandardOutput + extra.StandardError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PosixVerifierVerifiesInstalledBundleAndRejectsRechecksummedPromotionTrustMutations()
    {
        if (!OperatingSystem.IsLinux() || !await HasPosixVerifierPrerequisitesAsync())
            return;

        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(Path.GetTempPath(), $"sharplabnext-posix-promotion-verify-{Guid.NewGuid():N}");
        var bundle = Path.Combine(testRoot, "bundle");
        var fakeBin = Path.Combine(testRoot, "bin");
        var environment = new Dictionary<string, string>
        {
            ["PATH"] = string.Concat(fakeBin, ":", Environment.GetEnvironmentVariable("PATH"))
        };
        Directory.CreateDirectory(fakeBin);
        try
        {
            await BuildBundleForReleaseAsync(repositoryRoot, testRoot, bundle, "promotion-evidence");
            await WriteExecutableAsync(
                Path.Combine(fakeBin, "docker"),
                """
                #!/usr/bin/env sh
                set -eu
                if [ "${1:-}" = image ] && [ "${2:-}" = inspect ]; then
                  for argument in "$@"; do
                    case "$argument" in sha256:*) printf '%s\n' "$argument"; exit 0;; esac
                  done
                fi
                if [ "${1:-}" = compose ]; then exit 0; fi
                exit 0
                """);

            await ReleaseBundleBuilder.WriteChecksumsAsync(bundle, TestContext.Current.CancellationToken);
            var initial = await RunAsync(
                "sh",
                [Path.Combine(bundle, "verify.sh"), "--allow-unsigned"],
                environment);
            Assert.True(
                initial.ExitCode == 0,
                $"Initial POSIX offline verification failed. stdout: {initial.StandardOutput}{Environment.NewLine}stderr: {initial.StandardError}");

            var installer = Path.Combine(testRoot, "install.sh");
            await WriteExecutableAsync(
                installer,
                """
                #!/usr/bin/env sh
                set -eu
                . "$1"
                install_release_assets "$2" "$3" "$4" >/dev/null
                """);
            var installRoot = Path.Combine(testRoot, "installed");
            var materialize = await RunAsync(
                "sh",
                [installer, Path.Combine(bundle, "deployment-common.sh"), bundle, installRoot, "promotion-evidence"],
                environment);
            Assert.True(
                materialize.ExitCode == 0,
                $"POSIX installed-copy materialization failed. stdout: {materialize.StandardOutput}{Environment.NewLine}stderr: {materialize.StandardError}");
            var installed = await RunAsync(
                "sh",
                [Path.Combine(installRoot, "releases", "promotion-evidence", "verify.sh"), "--allow-unsigned", "--installed-copy"],
                environment);
            Assert.True(
                installed.ExitCode == 0,
                $"POSIX installed-copy verification failed. stdout: {installed.StandardOutput}{Environment.NewLine}stderr: {installed.StandardError}");

            var promotionRoot = Path.Combine(bundle, ReleaseBundleBuilder.PromotionEvidenceDirectoryName, "source");
            var candidatePath = "profiles/runtimes/candidates/dotnet-10-linux-x64.json";
            var candidateBundle = Path.Combine(testRoot, "candidate-tampered");
            CopyDirectory(bundle, candidateBundle);
            var candidateBytes = await File.ReadAllBytesAsync(
                Path.Combine(promotionRoot, candidatePath.Replace('/', Path.DirectorySeparatorChar)),
                TestContext.Current.CancellationToken);
            await ReplacePromotionEvidenceSourcesAndRechecksumAsync(
                candidateBundle,
                new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    [candidatePath] = [.. candidateBytes, (byte)' ']
                });
            var candidateTampered = await RunAsync(
                "sh",
                [Path.Combine(candidateBundle, "verify.sh"), "--allow-unsigned"],
                environment);
            Assert.NotEqual(0, candidateTampered.ExitCode);
            Assert.Contains(
                "Promotion retained source digest mismatch",
                candidateTampered.StandardOutput + candidateTampered.StandardError,
                StringComparison.Ordinal);

            var signatureBundle = Path.Combine(testRoot, "signature-tampered");
            CopyDirectory(bundle, signatureBundle);
            await CreateRechecksummedPlanSignatureMutationAsync(signatureBundle);
            var signatureTampered = await RunAsync(
                "sh",
                [Path.Combine(signatureBundle, "verify.sh"), "--allow-unsigned"],
                environment);
            Assert.NotEqual(0, signatureTampered.ExitCode);
            Assert.Contains(
                "Promotion plan dotnet-10-linux-x64 signature verification failed",
                signatureTampered.StandardOutput + signatureTampered.StandardError,
                StringComparison.Ordinal);

            var familyBundle = Path.Combine(testRoot, "family-tampered");
            CopyDirectory(bundle, familyBundle);
            await CreateRechecksummedPlanFamilyMutationAsync(familyBundle);
            var familyTampered = await RunAsync(
                "sh",
                [Path.Combine(familyBundle, "verify.sh"), "--allow-unsigned"],
                environment);
            Assert.NotEqual(0, familyTampered.ExitCode);
            Assert.Contains(
                "missing its required Wine operator binding",
                familyTampered.StandardOutput + familyTampered.StandardError,
                StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PosixVerifierDoesNotMaskPromotionJsonFailuresInPipelines()
    {
        var repositoryRoot = FindRepositoryRoot();
        var script = await File.ReadAllTextAsync(
            Path.Combine(
                repositoryRoot,
                "src",
                "Tools",
                "SharpLabNext.BundleBuilder",
                "DeploymentScripts",
                "verify.sh"),
            TestContext.Current.CancellationToken);

        Assert.Contains("jq -se", script, StringComparison.Ordinal);
        Assert.Contains("\"$receipt_file\" > \"$promotion_checks\"", script, StringComparison.Ordinal);
        Assert.Contains("done < \"$promotion_checks\"", script, StringComparison.Ordinal);
        Assert.Contains(
            "promotion-evidence/manifest.json > \"$promotion_actual_triples_unsorted\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains("verify_canonical_ed25519_signature", script, StringComparison.Ordinal);
        Assert.Contains("plan-signature-public-key", script, StringComparison.Ordinal);
        Assert.Contains("operator-receipt-signature", script, StringComparison.Ordinal);
        Assert.Contains("candidate-profile", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\"$receipt_file\" | while", script, StringComparison.Ordinal);
        Assert.DoesNotContain("promotion-evidence/manifest.json | sort", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OfflineVerifiersDeriveWineOperatorRequirementsFromTheSignedPlanFamily()
    {
        var repositoryRoot = FindRepositoryRoot();
        var deploymentScripts = Path.Combine(
            repositoryRoot,
            "src",
            "Tools",
            "SharpLabNext.BundleBuilder",
            "DeploymentScripts");
        var powerShell = await File.ReadAllTextAsync(
            Path.Combine(deploymentScripts, "verify.ps1"),
            TestContext.Current.CancellationToken);
        var posix = await File.ReadAllTextAsync(
            Path.Combine(deploymentScripts, "verify.sh"),
            TestContext.Current.CancellationToken);

        foreach (var script in new[] { powerShell, posix })
        {
            Assert.Contains("coreclr-wine", script, StringComparison.Ordinal);
            Assert.Contains("netfx-clr-wine", script, StringComparison.Ordinal);
            Assert.Contains("missing its required Wine operator binding", script, StringComparison.Ordinal);
            Assert.Contains("missing required Wine operator evidence", script, StringComparison.Ordinal);
            Assert.Contains("must not declare a Wine operator binding", script, StringComparison.Ordinal);
            Assert.Contains("must not retain Wine operator evidence", script, StringComparison.Ordinal);
        }

        Assert.True(
            powerShell.IndexOf("$requiresWineOperator = switch", StringComparison.Ordinal) <
            powerShell.IndexOf("if ($requiresWineOperator)", StringComparison.Ordinal));
        Assert.True(
            posix.IndexOf("family=$(jq -er '.family' \"$plan_file\")", StringComparison.Ordinal) <
            posix.IndexOf("if [ \"$requires_wine_operator\" = true ]", StringComparison.Ordinal));
    }

    [Fact]
    public void ImagePrefixUsesTheDeploymentImageName()
    {
        var definition = new DeploymentImageDefinition
        {
            Id = "gateway",
            Repository = "sharplabnext/gateway",
            Always = true
        };

        Assert.Equal(
            "registry.example.test:5000/team/gateway:release-1",
            ReleaseBundleBuilder.CreateImageReference(
                definition,
                "release-1",
                "registry.example.test:5000/team"));
        Assert.Equal(
            "sharplabnext/gateway:release-1",
            ReleaseBundleBuilder.CreateImageReference(definition, "release-1", imagePrefix: null));
    }

    [Fact]
    public void SourceProvenanceRejectsMissingHeadDirtyTreesAndUnknownRevision()
    {
        var noHead = Assert.Throws<BundleValidationException>(() =>
            RepositorySourceProvenanceResolver.Resolve(
                new RepositorySourceState(true, null, true),
                requestedRevision: null,
                allowUncommittedSourceForDevelopment: false));
        Assert.Contains("HEAD", noHead.Message, StringComparison.Ordinal);

        var dirty = Assert.Throws<BundleValidationException>(() =>
            RepositorySourceProvenanceResolver.Resolve(
                new RepositorySourceState(true, TestSourceRevision, true),
                requestedRevision: null,
                allowUncommittedSourceForDevelopment: false));
        Assert.Contains("clean Git worktree", dirty.Message, StringComparison.Ordinal);

        var unknown = Assert.Throws<BundleValidationException>(() =>
            RepositorySourceProvenanceResolver.Resolve(
                new RepositorySourceState(false, null, true),
                requestedRevision: "unknown",
                allowUncommittedSourceForDevelopment: true));
        Assert.Contains("cannot be 'unknown'", unknown.Message, StringComparison.Ordinal);

        var mismatch = Assert.Throws<BundleValidationException>(() =>
            RepositorySourceProvenanceResolver.Resolve(
                new RepositorySourceState(true, TestSourceRevision, false),
                requestedRevision: "cccccccccccccccccccccccccccccccccccccccc",
                allowUncommittedSourceForDevelopment: false));
        Assert.Contains("does not match Git HEAD", mismatch.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceProvenanceAllowsExplicitDevelopmentOnlyOverride()
    {
        var source = RepositorySourceProvenanceResolver.Resolve(
            new RepositorySourceState(false, null, true),
            requestedRevision: null,
            allowUncommittedSourceForDevelopment: true);

        Assert.Equal(RepositorySourceProvenanceResolver.LocalUncommittedRevision, source.Revision);
        Assert.False(source.IsVerified);
        Assert.True(source.IsDirty);
        Assert.True(source.DevelopmentOverrideUsed);
    }

    [Fact]
    public async Task BuilderRejectsSigningWhenDevelopmentSourceOverrideIsUsed()
    {
        var repositoryRoot = FindRepositoryRoot();
        var command = BundleBuilderCommand.Parse(["--repository-root", repositoryRoot]) with
        {
            SigningKeyPath = Path.Combine(repositoryRoot, "local-private.pem"),
            SigningPublicKeyPath = Path.Combine(repositoryRoot, "local-public.pem"),
            SourceRevision = "local-test",
            AllowUncommittedSourceForDevelopment = true
        };
        var builder = new ReleaseBundleBuilder(
            new FakeDockerCli("local-test"),
            new FakeBundleSigner(),
            new FakeRepositorySourceInspector(new RepositorySourceState(false, null, true)));

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() =>
            builder.BuildAsync(command, TestContext.Current.CancellationToken));

        Assert.Contains("cannot be used to create a signed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuilderRejectsImageBuiltFromAnotherSourceRevision()
    {
        var repositoryRoot = FindRepositoryRoot();
        var output = Path.Combine(Path.GetTempPath(), $"sharplabnext-source-mismatch-{Guid.NewGuid():N}");
        var command = new BundleBuilderCommand(
            repositoryRoot,
            Path.Combine(repositoryRoot, "profiles", "catalog", "catalog.json"),
            Path.Combine(repositoryRoot, "profiles", "lock.json"),
            Path.Combine(repositoryRoot, "deploy", "images.json"),
            Path.Combine(repositoryRoot, "profiles", "license-policy.json"),
            Path.Combine(repositoryRoot, "deploy", "compose.prod.yaml"),
            Path.Combine(repositoryRoot, "THIRD-PARTY-NOTICES.md"),
            output,
            "docker",
            "openssl",
            null,
            null,
            null,
            null,
            MetadataOnly: true,
            new Dictionary<string, string>());

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() =>
            CreateBuilder(new FakeDockerCli("cccccccccccccccccccccccccccccccccccccccc")).BuildAsync(
                command,
                TestContext.Current.CancellationToken));

        Assert.Contains("source revision label", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task BuilderRejectsRuntimeImageWhoseCoreClrIdentityDiffersFromLock()
    {
        var repositoryRoot = FindRepositoryRoot();
        var output = Path.Combine(Path.GetTempPath(), $"sharplabnext-runtime-mismatch-{Guid.NewGuid():N}");
        var command = BundleBuilderCommand.Parse([
            "--repository-root", repositoryRoot,
            "--output", output,
            "--metadata-only"
        ]);

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() =>
            CreateBuilder(new FakeDockerCli(runtimeCommitOverride: new string('d', 40))).BuildAsync(
                command,
                TestContext.Current.CancellationToken));

        Assert.Contains(ReleaseBundleBuilder.RuntimeCommitLabel, exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task BuilderRejectsImageWithoutLockedComponentIdentityLabels()
    {
        var repositoryRoot = FindRepositoryRoot();
        var output = Path.Combine(Path.GetTempPath(), $"sharplabnext-component-label-missing-{Guid.NewGuid():N}");
        var command = BundleBuilderCommand.Parse([
            "--repository-root", repositoryRoot,
            "--output", output,
            "--metadata-only"
        ]);

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() =>
            CreateBuilder(new FakeDockerCli(omitComponentLabels: true)).BuildAsync(
                command,
                TestContext.Current.CancellationToken));

        Assert.Contains(ReleaseBundleBuilder.ComponentLabelPrefix, exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task BuilderRejectsImageWhoseComponentVersionDiffersFromLock()
    {
        var repositoryRoot = FindRepositoryRoot();
        var output = Path.Combine(Path.GetTempPath(), $"sharplabnext-component-label-mismatch-{Guid.NewGuid():N}");
        var command = BundleBuilderCommand.Parse([
            "--repository-root", repositoryRoot,
            "--output", output,
            "--metadata-only"
        ]);

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() =>
            CreateBuilder(new FakeDockerCli(
                componentIdOverride: "roslyn-stable",
                componentVersionOverride: "0.0.0")).BuildAsync(
                command,
                TestContext.Current.CancellationToken));

        Assert.Contains("roslyn-stable.version", exception.Message, StringComparison.Ordinal);
        Assert.Contains("lock requires", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task BuilderRejectsNetFxWorkerWhoseDerivedComponentDiffersFromLock()
    {
        var repositoryRoot = FindRepositoryRoot();
        var output = Path.Combine(Path.GetTempPath(), $"sharplabnext-netfx-component-mismatch-{Guid.NewGuid():N}");
        var command = BundleBuilderCommand.Parse([
            "--repository-root", repositoryRoot,
            "--output", output,
            "--metadata-only"
        ]);

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() =>
            CreateBuilder(new FakeDockerCli(
                componentIdOverride: "roslyn-stable-netfx48",
                componentVersionOverride: "0.0.0")).BuildAsync(
                command,
                TestContext.Current.CancellationToken));

        Assert.Contains("roslyn-stable-netfx48.version", exception.Message, StringComparison.Ordinal);
        Assert.Contains("lock requires", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task BuilderRejectsToolchainImageWithoutReferenceSetLabels()
    {
        var repositoryRoot = FindRepositoryRoot();
        var output = Path.Combine(Path.GetTempPath(), $"sharplabnext-reference-label-missing-{Guid.NewGuid():N}");
        var command = BundleBuilderCommand.Parse([
            "--repository-root", repositoryRoot,
            "--output", output,
            "--metadata-only"
        ]);

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() =>
            CreateBuilder(new FakeDockerCli(omitReferenceSetLabels: true)).BuildAsync(
                command,
                TestContext.Current.CancellationToken));

        Assert.Contains(ReleaseBundleBuilder.ReferenceSetLabelPrefix, exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task BuilderRejectsNetFxWorkerMissingOneHostedReferenceSetLabel()
    {
        var repositoryRoot = FindRepositoryRoot();
        var output = Path.Combine(Path.GetTempPath(), $"sharplabnext-netfx-label-missing-{Guid.NewGuid():N}");
        var command = BundleBuilderCommand.Parse([
            "--repository-root", repositoryRoot,
            "--output", output,
            "--metadata-only"
        ]);

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() =>
            CreateBuilder(new FakeDockerCli(
                omittedReferenceSetId: "netfx30-managed-ref")).BuildAsync(
                command,
                TestContext.Current.CancellationToken));

        Assert.Contains("netfx30-managed-ref", exception.Message, StringComparison.Ordinal);
        Assert.Contains(ReleaseBundleBuilder.ReferenceSetLabelPrefix, exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task BuilderRejectsToolchainImageWhoseReferenceSetIdentityDiffersFromLock()
    {
        var repositoryRoot = FindRepositoryRoot();
        var output = Path.Combine(Path.GetTempPath(), $"sharplabnext-reference-label-mismatch-{Guid.NewGuid():N}");
        var command = BundleBuilderCommand.Parse([
            "--repository-root", repositoryRoot,
            "--output", output,
            "--metadata-only"
        ]);

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() =>
            CreateBuilder(new FakeDockerCli(referenceSetDigestOverride: "sha512-wrong-reference-set")).BuildAsync(
                command,
                TestContext.Current.CancellationToken));

        Assert.Contains("net10-ref", exception.Message, StringComparison.Ordinal);
        Assert.Contains("lock requires", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task BuilderRejectsNetFxWorkerWhoseReferenceSetIdentityDiffersFromLock()
    {
        var repositoryRoot = FindRepositoryRoot();
        var output = Path.Combine(Path.GetTempPath(), $"sharplabnext-netfx-reference-mismatch-{Guid.NewGuid():N}");
        var command = BundleBuilderCommand.Parse([
            "--repository-root", repositoryRoot,
            "--output", output,
            "--metadata-only"
        ]);

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() =>
            CreateBuilder(new FakeDockerCli(
                netFxManagedReferenceSetDigestOverride: "sha512-wrong-netfx-reference-set")).BuildAsync(
                command,
                TestContext.Current.CancellationToken));

        Assert.Contains("netfx48-managed-ref", exception.Message, StringComparison.Ordinal);
        Assert.Contains("lock requires", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task BuilderRejectsArtifactWorkerWhoseFrameworkReferenceClosureDiffersFromLock()
    {
        var repositoryRoot = FindRepositoryRoot();
        var output = Path.Combine(Path.GetTempPath(), $"sharplabnext-artifact-framework-mismatch-{Guid.NewGuid():N}");
        var command = BundleBuilderCommand.Parse([
            "--repository-root", repositoryRoot,
            "--output", output,
            "--metadata-only"
        ]);

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() =>
            CreateBuilder(new FakeDockerCli(
                artifactsDefaultReferenceSetDigestOverride: "sha512-wrong-artifact-reference-set")).BuildAsync(
                command,
                TestContext.Current.CancellationToken));

        Assert.Contains("worker-artifacts-default", exception.Message, StringComparison.Ordinal);
        Assert.Contains("netfx20-managed-ref", exception.Message, StringComparison.Ordinal);
        Assert.Contains("lock requires", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task ActiveRuntimeProfilesExactlyCoverSelectableCatalogAndLock()
    {
        var repositoryRoot = FindRepositoryRoot();
        var catalog = await CatalogLoader.LoadCatalogAsync(
            Path.Combine(repositoryRoot, "profiles", "catalog", "catalog.json"),
            TestContext.Current.CancellationToken);
        var releaseLock = await CatalogLoader.LoadReleaseLockAsync(
            Path.Combine(repositoryRoot, "profiles", "lock.json"),
            TestContext.Current.CancellationToken);
        var profiles = await ReleaseBundleBuilder.LoadRuntimeProfilesAsync(
            Path.Combine(repositoryRoot, "profiles", "runtimes"),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            catalog.Runtimes
                .Where(static runtime => runtime.Availability.IsSelectable)
                .Select(static runtime => runtime.Id)
                .OrderBy(static id => id, StringComparer.Ordinal),
            profiles.Select(static profile => profile.Id).OrderBy(static id => id, StringComparer.Ordinal));
        ReleaseBundleBuilder.ValidateRuntimeProfileBindings(catalog, releaseLock, profiles);
        Assert.All(profiles, static profile =>
        {
            Assert.NotNull(profile.Operations);
            Assert.NotEmpty(profile.SecurityPolicies);
        });
    }

    [Fact]
    public async Task RuntimeProfileLoaderDoesNotReadCandidateSubdirectory()
    {
        var repositoryRoot = FindRepositoryRoot();
        var root = Path.Combine(Path.GetTempPath(), $"sharplabnext-runtime-candidate-boundary-{Guid.NewGuid():N}");
        var candidates = Path.Combine(root, "candidates");
        Directory.CreateDirectory(candidates);
        try
        {
            File.Copy(
                Path.Combine(repositoryRoot, "profiles", "runtimes", "dotnet-10-linux-x64.json"),
                Path.Combine(candidates, "dotnet-10-linux-x64.json"));

            var profiles = await ReleaseBundleBuilder.LoadRuntimeProfilesAsync(
                root,
                TestContext.Current.CancellationToken);

            Assert.Empty(profiles);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeProfileLoaderRejectsIncompleteAndDuplicateActiveProfiles()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourcePath = Path.Combine(
            repositoryRoot,
            "profiles",
            "runtimes",
            "dotnet-10-linux-x64.json");
        var root = Path.Combine(Path.GetTempPath(), $"sharplabnext-runtime-profile-invalid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var document = JsonNode.Parse(await File.ReadAllTextAsync(
                sourcePath,
                TestContext.Current.CancellationToken))!;
            document.AsObject().Remove("operations");
            await File.WriteAllTextAsync(
                Path.Combine(root, "incomplete.json"),
                document.ToJsonString(),
                TestContext.Current.CancellationToken);
            var incomplete = await Assert.ThrowsAsync<BundleValidationException>(() =>
                ReleaseBundleBuilder.LoadRuntimeProfilesAsync(root, TestContext.Current.CancellationToken));
            Assert.Contains("missing 'operations'", incomplete.Message, StringComparison.Ordinal);

            File.Delete(Path.Combine(root, "incomplete.json"));
            File.Copy(sourcePath, Path.Combine(root, "first.json"));
            File.Copy(sourcePath, Path.Combine(root, "second.json"));
            var duplicate = await Assert.ThrowsAsync<BundleValidationException>(() =>
                ReleaseBundleBuilder.LoadRuntimeProfilesAsync(root, TestContext.Current.CancellationToken));
            Assert.Contains("declared more than once", duplicate.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeProfileCatalogBindingFailsClosedForMissingOrDriftedProfile()
    {
        var repositoryRoot = FindRepositoryRoot();
        var catalog = await CatalogLoader.LoadCatalogAsync(
            Path.Combine(repositoryRoot, "profiles", "catalog", "catalog.json"),
            TestContext.Current.CancellationToken);
        var releaseLock = await CatalogLoader.LoadReleaseLockAsync(
            Path.Combine(repositoryRoot, "profiles", "lock.json"),
            TestContext.Current.CancellationToken);
        var profiles = (await ReleaseBundleBuilder.LoadRuntimeProfilesAsync(
            Path.Combine(repositoryRoot, "profiles", "runtimes"),
            TestContext.Current.CancellationToken)).ToList();

        var missing = Assert.Throws<BundleValidationException>(() =>
            ReleaseBundleBuilder.ValidateRuntimeProfileBindings(
                catalog,
                releaseLock,
                profiles.Where(static profile => profile.Id != "dotnet-10-linux-x64").ToArray()));
        Assert.Contains("has no active profile", missing.Message, StringComparison.Ordinal);

        var drifted = CloneRuntimeProfile(
            profiles.Single(static profile => profile.Id == "dotnet-10-linux-x64"));
        drifted.Capabilities.Remove("execution-flow");
        profiles[profiles.FindIndex(static profile => profile.Id == "dotnet-10-linux-x64")] = drifted;
        var mismatch = Assert.Throws<BundleValidationException>(() =>
            ReleaseBundleBuilder.ValidateRuntimeProfileBindings(catalog, releaseLock, profiles));
        Assert.Contains("capabilities do not match", mismatch.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComposeOverlayRejectsConflictingSecurityPolicyDefinitions()
    {
        var repositoryRoot = FindRepositoryRoot();
        var catalog = await CatalogLoader.LoadCatalogAsync(
            Path.Combine(repositoryRoot, "profiles", "catalog", "catalog.json"),
            TestContext.Current.CancellationToken);
        var profiles = (await ReleaseBundleBuilder.LoadRuntimeProfilesAsync(
            Path.Combine(repositoryRoot, "profiles", "runtimes"),
            TestContext.Current.CancellationToken)).ToList();
        profiles.Single(static profile => profile.Id == "dotnet-11-preview-linux-x64")
            .SecurityPolicies.Single(static policy => policy.Id == "runtime-job-default")
            .MemoryBytes++;
        var runtimeOnlyCatalog = catalog with
        {
            Toolchains = [],
            ArtifactProcessors = []
        };

        var exception = Assert.Throws<BundleValidationException>(() =>
            ReleaseBundleBuilder.CreateComposeOverlay(runtimeOnlyCatalog, [], profiles));

        Assert.Contains("conflicting active profile definitions", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeProfileOverlayReplacesLongerBaseConfigurationArrays()
    {
        var originalProfiles = Enumerable.Range(0, 7)
            .Select(index => new RuntimeProfileOptions { Id = $"stale-{index}" })
            .ToList();
        var originalPolicies = Enumerable.Range(0, 5)
            .Select(index => new RuntimeSecurityPolicyOptions { Id = $"stale-{index}" })
            .ToList();
        var options = new RuntimeSupervisorOptions
        {
            Profiles = originalProfiles,
            SecurityPolicies = originalPolicies
        };
        var overlay = new RuntimeSupervisorProfileOverlayOptions
        {
            Enabled = true,
            Profiles = [new RuntimeProfileOptions { Id = "release-runtime" }],
            SecurityPolicies = [new RuntimeSecurityPolicyOptions { Id = "release-policy" }]
        };

        overlay.ApplyTo(options);

        Assert.Same(overlay.Profiles, options.Profiles);
        Assert.Same(overlay.SecurityPolicies, options.SecurityPolicies);
        Assert.Equal("release-runtime", Assert.Single(options.Profiles).Id);
        Assert.Equal("release-policy", Assert.Single(options.SecurityPolicies).Id);
        Assert.DoesNotContain(options.Profiles, static profile => profile.Id.StartsWith("stale-", StringComparison.Ordinal));
    }

    [Fact]
    public void CommandParsesDevelopmentSourceOptions()
    {
        var repositoryRoot = FindRepositoryRoot();
        var command = BundleBuilderCommand.Parse(
        [
            "--repository-root", repositoryRoot,
            "--source-revision", "local-test",
            "--allow-uncommitted-source-for-development",
            "--runtime-profiles", Path.Combine(repositoryRoot, "profiles", "runtimes")
        ]);

        Assert.Equal("local-test", command.SourceRevision);
        Assert.True(command.AllowUncommittedSourceForDevelopment);
        Assert.Equal(
            Path.Combine(repositoryRoot, "profiles", "runtimes"),
            command.RuntimeProfilesPath);
    }

    [Fact]
    public async Task DeploymentSmokeTemplatesUsePascalCaseApiIdentityAndLowercaseBundleMetadata()
    {
        var scriptsRoot = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Tools",
            "SharpLabNext.BundleBuilder",
            "DeploymentScripts");
        var powerShell = await File.ReadAllTextAsync(
            Path.Combine(scriptsRoot, "smoke.ps1"),
            TestContext.Current.CancellationToken);
        var shell = await File.ReadAllTextAsync(
            Path.Combine(scriptsRoot, "smoke.sh"),
            TestContext.Current.CancellationToken);

        Assert.Contains("$system.PSObject.Properties.Name -cnotcontains 'ReleaseId'", powerShell, StringComparison.Ordinal);
        Assert.Contains("$catalog.PSObject.Properties.Name -cnotcontains 'ReleaseId'", powerShell, StringComparison.Ordinal);
        Assert.Contains("[string]$system.ReleaseId", powerShell, StringComparison.Ordinal);
        Assert.Contains("[string]$catalog.ReleaseId", powerShell, StringComparison.Ordinal);
        Assert.Contains("ConvertFrom-Json).releaseId", powerShell, StringComparison.Ordinal);
        Assert.Contains("\\\"ReleaseId\\\":\\\"$expected_release_id\\\"", shell, StringComparison.Ordinal);
        Assert.Contains("\"releaseId\"[[:space:]]*:", shell, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PowerShellSmokeAcceptsOnlyPascalCaseApiIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(Path.GetTempPath(), $"sharplabnext-smoke-ps-{Guid.NewGuid():N}");
        var fakeBin = Path.Combine(testRoot, "bin");
        var statePath = Path.Combine(testRoot, "active-release.txt");
        var failPath = Path.Combine(testRoot, "fail-release.txt");
        Directory.CreateDirectory(fakeBin);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(testRoot, "bundle.json"),
                "{\"releaseId\":\"candidate\"}",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(testRoot, "compose.prod.yaml"),
                "services: {}",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(testRoot, "compose.generated.yaml"),
                "services: {}",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                statePath,
                "candidate",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(fakeBin, "docker.cmd"),
                """
                @echo off
                echo %* | %SystemRoot%\System32\findstr.exe /C:"config --services" >nul && (
                  echo gateway
                  exit /b 0
                )
                echo %* | %SystemRoot%\System32\findstr.exe /C:"ps --status running --services" >nul && (
                  echo gateway
                  exit /b 0
                )
                exit /b 1
                """.ReplaceLineEndings("\r\n"),
                TestContext.Current.CancellationToken);

            var environment = new Dictionary<string, string>
            {
                ["PATH"] = $"{fakeBin};{Environment.GetEnvironmentVariable("PATH")}"
            };
            var smokeScript = Path.Combine(
                repositoryRoot,
                "src",
                "Tools",
                "SharpLabNext.BundleBuilder",
                "DeploymentScripts",
                "smoke.ps1");

            await using (var server = new FakeReleaseHttpServer(statePath, failPath))
            {
                var result = await RunAsync(
                    "pwsh",
                    ["-NoProfile", "-File", smokeScript, "-ReleaseRoot", testRoot, "-TimeoutSeconds", "3", "-BaseAddress", $"http://127.0.0.1:{server.Port}"],
                    environment);
                Assert.Equal(0, result.ExitCode);
            }

            await using (var server = new FakeReleaseHttpServer(statePath, failPath, usePascalCase: false))
            {
                var result = await RunAsync(
                    "pwsh",
                    ["-NoProfile", "-File", smokeScript, "-ReleaseRoot", testRoot, "-TimeoutSeconds", "1", "-BaseAddress", $"http://127.0.0.1:{server.Port}"],
                    environment);
                Assert.NotEqual(0, result.ExitCode);
                Assert.Contains(
                    "Gateway release identity does not match.",
                    result.StandardOutput + result.StandardError,
                    StringComparison.Ordinal);
            }
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CurrentOnlyRetentionRejectsNestedLinksBeforeAnyDeletion()
    {
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(Path.GetTempPath(), $"sharplabnext-current-only-links-{Guid.NewGuid():N}");
        var installRoot = Path.Combine(testRoot, "installed");
        var releasesRoot = Path.Combine(installRoot, "releases");
        var candidateRoot = Path.Combine(releasesRoot, "candidate");
        var rollbackRoot = Path.Combine(candidateRoot, "rollback");
        var previousRoot = Path.Combine(releasesRoot, "previous");
        var additionalPreviousRoot = Path.Combine(releasesRoot, "additional");
        var outsideRoot = Path.Combine(testRoot, "outside");
        var nestedLink = Path.Combine(additionalPreviousRoot, "nested-link");
        Directory.CreateDirectory(rollbackRoot);
        Directory.CreateDirectory(previousRoot);
        Directory.CreateDirectory(additionalPreviousRoot);
        Directory.CreateDirectory(outsideRoot);
        await File.WriteAllTextAsync(
            Path.Combine(rollbackRoot, "artifact.txt"),
            "retained",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(previousRoot, "bundle.json"),
            "retained",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(installRoot, "current-release"),
            "candidate\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(installRoot, "previous-release"),
            "previous\n",
            TestContext.Current.CancellationToken);

        try
        {
            ProcessResult result;
            if (OperatingSystem.IsWindows())
            {
                var junction = await RunAsync(
                    "cmd.exe",
                    ["/d", "/c", "mklink", "/J", nestedLink, outsideRoot],
                    new Dictionary<string, string>());
                Assert.True(
                    junction.ExitCode == 0,
                    $"Could not create the test junction. stdout: {junction.StandardOutput}{Environment.NewLine}stderr: {junction.StandardError}");
                var wrapper = Path.Combine(testRoot, "current-only-link-test.ps1");
                await File.WriteAllTextAsync(
                    wrapper,
                    "param([string]$Common, [string]$Root)\n" +
                    ". $Common\n" +
                    "Remove-CurrentOnlyPreviousRelease $Root 'candidate' 'previous' 'additional'\n",
                    TestContext.Current.CancellationToken);
                result = await RunAsync(
                    "pwsh",
                    [
                        "-NoProfile",
                        "-File",
                        wrapper,
                        Path.Combine(repositoryRoot, "src", "Tools", "SharpLabNext.BundleBuilder", "DeploymentScripts", "deployment-common.ps1"),
                        installRoot
                    ],
                    new Dictionary<string, string>());
            }
            else if (OperatingSystem.IsLinux())
            {
                Directory.CreateSymbolicLink(nestedLink, outsideRoot);
                var wrapper = Path.Combine(testRoot, "current-only-link-test.sh");
                await WriteExecutableAsync(
                    wrapper,
                    """
                    #!/usr/bin/env sh
                    set -eu
                    . "$1"
                    remove_current_only_previous_release "$2" candidate previous additional
                    """);
                result = await RunAsync(
                    "sh",
                    [
                        wrapper,
                        Path.Combine(repositoryRoot, "src", "Tools", "SharpLabNext.BundleBuilder", "DeploymentScripts", "deployment-common.sh"),
                        installRoot
                    ],
                    new Dictionary<string, string>());
            }
            else
            {
                return;
            }

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                OperatingSystem.IsWindows() ? "reparse point" : "symlink",
                result.StandardError,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal("candidate", ReadPointer(installRoot, "current-release"));
            Assert.Equal("previous", ReadPointer(installRoot, "previous-release"));
            Assert.True(Directory.Exists(rollbackRoot));
            Assert.True(Directory.Exists(previousRoot));
            Assert.True(Directory.Exists(additionalPreviousRoot));
        }
        finally
        {
            if (Directory.Exists(nestedLink))
            {
                Directory.Delete(nestedLink);
            }
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CurrentOnlyRetentionRejectsLinkedCurrentPointerBeforeAnyDeletion()
    {
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(Path.GetTempPath(), $"sharplabnext-current-only-pointer-{Guid.NewGuid():N}");
        var installRoot = Path.Combine(testRoot, "installed");
        var releasesRoot = Path.Combine(installRoot, "releases");
        var candidateRoot = Path.Combine(releasesRoot, "candidate");
        var rollbackRoot = Path.Combine(candidateRoot, "rollback");
        var previousRoot = Path.Combine(releasesRoot, "previous");
        var additionalPreviousRoot = Path.Combine(releasesRoot, "additional");
        var currentPointer = Path.Combine(installRoot, "current-release");
        var outsideRoot = Path.Combine(testRoot, "outside-pointer");
        Directory.CreateDirectory(rollbackRoot);
        Directory.CreateDirectory(previousRoot);
        Directory.CreateDirectory(additionalPreviousRoot);
        Directory.CreateDirectory(outsideRoot);
        await File.WriteAllTextAsync(
            Path.Combine(installRoot, "previous-release"),
            "previous\n",
            TestContext.Current.CancellationToken);

        try
        {
            ProcessResult result;
            if (OperatingSystem.IsWindows())
            {
                var junction = await RunAsync(
                    "cmd.exe",
                    ["/d", "/c", "mklink", "/J", currentPointer, outsideRoot],
                    new Dictionary<string, string>());
                Assert.True(
                    junction.ExitCode == 0,
                    $"Could not create the current-pointer junction. stdout: {junction.StandardOutput}{Environment.NewLine}stderr: {junction.StandardError}");
                var wrapper = Path.Combine(testRoot, "current-only-pointer-test.ps1");
                await File.WriteAllTextAsync(
                    wrapper,
                    "param([string]$Common, [string]$Root)\n" +
                    ". $Common\n" +
                    "Remove-CurrentOnlyPreviousRelease $Root 'candidate' 'previous' 'additional'\n",
                    TestContext.Current.CancellationToken);
                result = await RunAsync(
                    "pwsh",
                    [
                        "-NoProfile",
                        "-File",
                        wrapper,
                        Path.Combine(repositoryRoot, "src", "Tools", "SharpLabNext.BundleBuilder", "DeploymentScripts", "deployment-common.ps1"),
                        installRoot
                    ],
                    new Dictionary<string, string>());
            }
            else if (OperatingSystem.IsLinux())
            {
                Directory.CreateSymbolicLink(currentPointer, outsideRoot);
                var wrapper = Path.Combine(testRoot, "current-only-pointer-test.sh");
                await WriteExecutableAsync(
                    wrapper,
                    """
                    #!/usr/bin/env sh
                    set -eu
                    . "$1"
                    remove_current_only_previous_release "$2" candidate previous additional
                    """);
                result = await RunAsync(
                    "sh",
                    [
                        wrapper,
                        Path.Combine(repositoryRoot, "src", "Tools", "SharpLabNext.BundleBuilder", "DeploymentScripts", "deployment-common.sh"),
                        installRoot
                    ],
                    new Dictionary<string, string>());
            }
            else
            {
                return;
            }

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("regular non-link file", result.StandardError, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(currentPointer));
            Assert.True(Directory.Exists(outsideRoot));
            Assert.Equal("previous", ReadPointer(installRoot, "previous-release"));
            Assert.True(Directory.Exists(rollbackRoot));
            Assert.True(Directory.Exists(previousRoot));
            Assert.True(Directory.Exists(additionalPreviousRoot));
        }
        finally
        {
            if (Directory.Exists(currentPointer))
                Directory.Delete(currentPointer);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PosixCurrentOnlyRetentionRejectsMountPointBeforePointerSwitchOrDeletion()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(Path.GetTempPath(), $"sharplabnext current-only mount {Guid.NewGuid():N}");
        var installRoot = Path.Combine(testRoot, "installed");
        var releasesRoot = Path.Combine(installRoot, "releases");
        var candidateRoot = Path.Combine(releasesRoot, "candidate");
        var rollbackRoot = Path.Combine(candidateRoot, "rollback");
        var previousRoot = Path.Combine(releasesRoot, "previous");
        var additionalPreviousRoot = Path.Combine(releasesRoot, "additional");
        var nestedMount = Path.Combine(additionalPreviousRoot, "nested-mount");
        var mountInfo = Path.Combine(testRoot, "mountinfo");
        Directory.CreateDirectory(rollbackRoot);
        Directory.CreateDirectory(previousRoot);
        Directory.CreateDirectory(nestedMount);
        await File.WriteAllTextAsync(
            Path.Combine(installRoot, "current-release"),
            "previous\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(installRoot, "previous-release"),
            "additional\n",
            TestContext.Current.CancellationToken);
        var encodedMount = nestedMount
            .Replace("\\", "\\134", StringComparison.Ordinal)
            .Replace(" ", "\\040", StringComparison.Ordinal)
            .Replace("\t", "\\011", StringComparison.Ordinal)
            .Replace("\n", "\\012", StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            mountInfo,
            $"1 0 0:1 / / rw - rootfs rootfs rw\n2 1 0:1 / {encodedMount} rw - none none rw\n",
            TestContext.Current.CancellationToken);
        var wrapper = Path.Combine(testRoot, "current-only-mount-test.sh");
        await WriteExecutableAsync(
            wrapper,
            """
            #!/usr/bin/env sh
            set -eu
            . "$1"
            assert_current_only_retention_plan "$2" candidate previous additional previous additional "$3"
            atomic_pointer "$2/previous-release" previous
            atomic_pointer "$2/current-release" candidate
            remove_current_only_previous_release "$2" candidate previous additional "$3"
            """);

        try
        {
            var result = await RunAsync(
                "sh",
                [
                    wrapper,
                    Path.Combine(repositoryRoot, "src", "Tools", "SharpLabNext.BundleBuilder", "DeploymentScripts", "deployment-common.sh"),
                    installRoot,
                    mountInfo
                ],
                new Dictionary<string, string>());

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("mount point", result.StandardError, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("previous", ReadPointer(installRoot, "current-release"));
            Assert.Equal("additional", ReadPointer(installRoot, "previous-release"));
            Assert.True(Directory.Exists(rollbackRoot));
            Assert.True(Directory.Exists(previousRoot));
            Assert.True(Directory.Exists(additionalPreviousRoot));
            Assert.True(Directory.Exists(nestedMount));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ReleasePointerPairRestoresOriginalPreviousWhenCurrentUpdateFails()
    {
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(Path.GetTempPath(), $"sharplabnext-pointer-pair-{Guid.NewGuid():N}");
        var installRoot = Path.Combine(testRoot, "installed");
        Directory.CreateDirectory(installRoot);
        await File.WriteAllTextAsync(
            Path.Combine(installRoot, "current-release"),
            "old-current\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(installRoot, "previous-release"),
            "old-previous\n",
            TestContext.Current.CancellationToken);

        try
        {
            ProcessResult result;
            if (OperatingSystem.IsWindows())
            {
                var wrapper = Path.Combine(testRoot, "pointer-pair-failure.ps1");
                await File.WriteAllTextAsync(
                    wrapper,
                    "param([string]$Common, [string]$Root)\n" +
                    ". $Common\n" +
                    "$script:OriginalSetReleasePointer = ${function:Set-ReleasePointer}\n" +
                    "function Set-ReleasePointer([string]$InstallRoot, [string]$Name, [string]$Value) {\n" +
                    "  if ($Name -ceq 'current-release') { throw 'Injected current pointer failure.' }\n" +
                    "  & $script:OriginalSetReleasePointer $InstallRoot $Name $Value\n" +
                    "}\n" +
                    "Set-ReleasePointerPair $Root 'candidate' 'old-current' 'old-previous'\n",
                    TestContext.Current.CancellationToken);
                result = await RunAsync(
                    "pwsh",
                    [
                        "-NoProfile",
                        "-File",
                        wrapper,
                        Path.Combine(repositoryRoot, "src", "Tools", "SharpLabNext.BundleBuilder", "DeploymentScripts", "deployment-common.ps1"),
                        installRoot
                    ],
                    new Dictionary<string, string>());
            }
            else if (OperatingSystem.IsLinux())
            {
                var wrapper = Path.Combine(testRoot, "pointer-pair-failure.sh");
                await WriteExecutableAsync(
                    wrapper,
                    """
                    #!/usr/bin/env sh
                    set -eu
                    . "$1"
                    atomic_pointer() {
                      target=$1
                      value=$2
                      case "$target" in */current-release) echo 'Injected current pointer failure.' >&2; return 73;; esac
                      printf '%s\n' "$value" > "$target"
                    }
                    set_release_pointer_pair "$2" candidate old-current old-previous
                    """);
                result = await RunAsync(
                    "sh",
                    [
                        wrapper,
                        Path.Combine(repositoryRoot, "src", "Tools", "SharpLabNext.BundleBuilder", "DeploymentScripts", "deployment-common.sh"),
                        installRoot
                    ],
                    new Dictionary<string, string>());
            }
            else
            {
                return;
            }

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("Injected current pointer failure", result.StandardError, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("old-current", ReadPointer(installRoot, "current-release"));
            Assert.Equal("old-previous", ReadPointer(installRoot, "previous-release"));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ShellInstallerRestoresFailedCandidateAndRollsBackSuccessfulUpgrade()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(Path.GetTempPath(), $"sharplabnext-install-{Guid.NewGuid():N}");
        var bundleA = Path.Combine(testRoot, "bundle-a");
        var bundleB = Path.Combine(testRoot, "bundle-b");
        var bundleC = Path.Combine(testRoot, "bundle-c");
        var bundleD = Path.Combine(testRoot, "bundle-d");
        var installRoot = Path.Combine(testRoot, "installed");
        var fakeBin = Path.Combine(testRoot, "bin");
        var internalServiceToken = Path.Combine(testRoot, "internal-service-token");
        Directory.CreateDirectory(fakeBin);
        try
        {
            await BuildBundleForReleaseAsync(repositoryRoot, testRoot, bundleA, "development");
            await BuildBundleForReleaseAsync(repositoryRoot, testRoot, bundleB, "candidate");
            await BuildBundleForReleaseAsync(repositoryRoot, testRoot, bundleC, "current-only");
            await BuildBundleForReleaseAsync(repositoryRoot, testRoot, bundleD, "stale");
            await File.WriteAllTextAsync(
                internalServiceToken,
                "test-internal-service-token",
                TestContext.Current.CancellationToken);
            await WriteExecutableAsync(
                Path.Combine(fakeBin, "stat"),
                """
                #!/usr/bin/env sh
                set -eu
                printf '640 0 1654\n'
                """);
            await WriteExecutableAsync(
                Path.Combine(fakeBin, "docker"),
                $$"""
                #!/usr/bin/env sh
                set -eu
                if [ "${1:-}" = image ] && [ "${2:-}" = inspect ]; then
                  case " $* " in *"Config.User"*) echo 1654:1654; exit 0;; esac
                  for argument in "$@"; do
                    case "$argument" in
                      sha256:*) printf '%s\n' "$argument"; exit 0;;
                      *@sha256:*) printf 'sha256:%s\n' "${argument##*@sha256:}"; exit 0;;
                    esac
                  done
                  case " $* " in
                    *" {{FakeDockerCli.RoslynNetFx48ImageId}} "*) echo {{FakeDockerCli.RoslynNetFx48ImageId}} ;;
                    *" {{FakeDockerCli.RoslynStableImageId}} "*) echo {{FakeDockerCli.RoslynStableImageId}} ;;
                    *" {{FakeDockerCli.RoslynMainImageId}} "*) echo {{FakeDockerCli.RoslynMainImageId}} ;;
                    *" {{FakeDockerCli.RoslynConstGenericsImageId}} "*) echo {{FakeDockerCli.RoslynConstGenericsImageId}} ;;
                    *" {{FakeDockerCli.FSharpImageId}} "*) echo {{FakeDockerCli.FSharpImageId}} ;;
                    *" {{FakeDockerCli.GSharpImageId}} "*) echo {{FakeDockerCli.GSharpImageId}} ;;
                    *" {{FakeDockerCli.PeachPieImageId}} "*) echo {{FakeDockerCli.PeachPieImageId}} ;;
                    *" {{FakeDockerCli.CppCliImageId}} "*) echo {{FakeDockerCli.CppCliImageId}} ;;
                    *" {{FakeDockerCli.WineNetFxRuntimeImageId}} "*) echo {{FakeDockerCli.WineNetFxRuntimeImageId}} ;;
                    *" {{FakeDockerCli.JSharpImageId}} "*) echo {{FakeDockerCli.JSharpImageId}} ;;
                    *" {{FakeDockerCli.WineJSharpRuntimeImageId}} "*) echo {{FakeDockerCli.WineJSharpRuntimeImageId}} ;;
                    *" {{FakeDockerCli.IlImageId}} "*) echo {{FakeDockerCli.IlImageId}} ;;
                    *" {{FakeDockerCli.MinilangImageId}} "*) echo {{FakeDockerCli.MinilangImageId}} ;;
                    *" {{FakeDockerCli.ArtifactsDefaultImageId}} "*) echo {{FakeDockerCli.ArtifactsDefaultImageId}} ;;
                    *" {{FakeDockerCli.ArtifactsJsilImageId}} "*) echo {{FakeDockerCli.ArtifactsJsilImageId}} ;;
                    *" {{FakeDockerCli.ArtifactsConstGenericsImageId}} "*) echo {{FakeDockerCli.ArtifactsConstGenericsImageId}} ;;
                    *" {{FakeDockerCli.ArtifactsIlAssemblerImageId}} "*) echo {{FakeDockerCli.ArtifactsIlAssemblerImageId}} ;;
                    *) echo {{FakeDockerCli.ImageId}} ;;
                  esac
                  exit 0
                fi
                if [ "${1:-}" = image ] && [ "${2:-}" = load ]; then exit 0; fi
                if [ "${1:-}" = cp ]; then
                  mkdir -p "$3"
                  printf 'artifact-data\n' > "$3/artifact.txt"
                  exit 0
                fi
                if [ "${1:-}" = volume ] && [ "${2:-}" = ls ]; then
                  echo sharplabnext_artifact-data
                  exit 0
                fi
                case " $* " in
                  *" up -d "*)
                    if [ "${SHARPLABNEXT_FAKE_TAMPER_ROLLBACK:-}" = "${SHARPLABNEXT_RELEASE_ID:-}" ]; then
                      printf 'tampered\n' >> "$SHARPLABNEXT_HOME/releases/$SHARPLABNEXT_RELEASE_ID/rollback/artifact-data/artifact.txt"
                    fi
                    ;;
                  *" config --services "*) echo gateway ;;
                  *" ps --status running --services "*) echo gateway ;;
                  *" ps --all -q artifact-store "*) echo fake-artifact-store ;;
                  *) ;;
                esac
                exit 0
                """);
            await WriteExecutableAsync(
                Path.Combine(fakeBin, "curl"),
                """
                #!/usr/bin/env sh
                set -eu
                if [ "${SHARPLABNEXT_FAKE_FAIL_RELEASE:-}" = "${SHARPLABNEXT_RELEASE_ID:-}" ]; then exit 22; fi
                printf '{"ReleaseId":"%s"}\n' "${SHARPLABNEXT_RELEASE_ID:-}"
                """);

            var environment = new Dictionary<string, string>
            {
                ["PATH"] = $"{fakeBin}:{Environment.GetEnvironmentVariable("PATH")}",
                ["SHARPLABNEXT_HOME"] = installRoot,
                ["SHARPLABNEXT_INTERNAL_SERVICE_TOKEN_FILE"] = internalServiceToken
            };
            var first = await RunAsync(
                "sh",
                [Path.Combine(bundleA, "deploy.sh"), "--allow-unsigned", "--ready-timeout-seconds", "3"],
                environment);
            Assert.True(
                first.ExitCode == 0,
                $"First install failed. stdout: {first.StandardOutput}{Environment.NewLine}stderr: {first.StandardError}");
            Assert.Equal("development", ReadPointer(installRoot, "current-release"));
            Assert.True(File.Exists(Path.Combine(
                installRoot,
                "releases",
                "development",
                "images.tar")));
            Assert.True(File.Exists(Path.Combine(
                installRoot,
                "releases",
                "development",
                "sbom",
                "release.spdx.json")));
            Assert.True(File.Exists(Path.Combine(
                installRoot,
                "releases",
                "development",
                "security",
                "sharplabnext-runtime-job-v1.apparmor")));
            Assert.Contains(
                "security/sharplabnext-runtime-job-v1.apparmor",
                await File.ReadAllTextAsync(
                    Path.Combine(installRoot, "releases", "development", "deployment.sha256"),
                    TestContext.Current.CancellationToken),
                StringComparison.Ordinal);

            environment["SHARPLABNEXT_FAKE_FAIL_RELEASE"] = "candidate";
            var failedUpgrade = await RunAsync(
                "sh",
                [Path.Combine(bundleB, "install.sh"), "--allow-unsigned", "--skip-artifact-backup", "--ready-timeout-seconds", "1"],
                environment);
            Assert.NotEqual(0, failedUpgrade.ExitCode);
            Assert.Contains("was restored", failedUpgrade.StandardError, StringComparison.Ordinal);
            Assert.Equal("development", ReadPointer(installRoot, "current-release"));
            Assert.False(File.Exists(Path.Combine(installRoot, "previous-release")));

            environment.Remove("SHARPLABNEXT_FAKE_FAIL_RELEASE");
            var upgrade = await RunAsync(
                "sh",
                [Path.Combine(bundleB, "install.sh"), "--allow-unsigned", "--ready-timeout-seconds", "3"],
                environment);
            Assert.Equal(0, upgrade.ExitCode);
            Assert.Contains("Installed SharpLabNext release candidate", upgrade.StandardOutput, StringComparison.Ordinal);
            Assert.Equal("candidate", ReadPointer(installRoot, "current-release"));
            Assert.Equal("development", ReadPointer(installRoot, "previous-release"));
            Assert.Equal(
                "development",
                ReadPointer(Path.Combine(installRoot, "releases", "candidate", "rollback"), "predecessor-release"));
            Assert.True(File.Exists(Path.Combine(
                installRoot,
                "releases",
                "candidate",
                "rollback",
                "artifact-data",
                "artifact.txt")));

            var rollback = await RunAsync(
                "sh",
                [Path.Combine(bundleB, "rollback.sh"), "--install-root", installRoot, "--ready-timeout-seconds", "3"],
                environment);
            Assert.Equal(0, rollback.ExitCode);
            Assert.Equal("development", ReadPointer(installRoot, "current-release"));
            Assert.Equal("candidate", ReadPointer(installRoot, "previous-release"));

            environment["SHARPLABNEXT_FAKE_FAIL_RELEASE"] = "current-only";
            var failedCurrentOnly = await RunAsync(
                "sh",
                [Path.Combine(bundleC, "install.sh"), "--allow-unsigned", "--skip-artifact-backup", "--current-only", "--ready-timeout-seconds", "1"],
                environment);
            Assert.NotEqual(0, failedCurrentOnly.ExitCode);
            Assert.Equal("development", ReadPointer(installRoot, "current-release"));
            Assert.Equal("candidate", ReadPointer(installRoot, "previous-release"));
            Assert.True(Directory.Exists(Path.Combine(installRoot, "releases", "development")));
            Assert.True(Directory.Exists(Path.Combine(installRoot, "releases", "candidate")));
            Assert.True(Directory.Exists(Path.Combine(installRoot, "releases", "candidate", "rollback")));

            environment.Remove("SHARPLABNEXT_FAKE_FAIL_RELEASE");
            var releaseDirectoriesBeforeRetention = Directory.GetDirectories(Path.Combine(installRoot, "releases"))
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var tamperedPredecessorPath = Path.Combine(
                installRoot,
                "releases",
                "candidate",
                "compose.generated.yaml");
            var predecessorBytes = await File.ReadAllBytesAsync(
                tamperedPredecessorPath,
                TestContext.Current.CancellationToken);
            await File.AppendAllTextAsync(
                tamperedPredecessorPath,
                "\n# tampered predecessor\n",
                TestContext.Current.CancellationToken);
            var tamperedPredecessor = await RunAsync(
                "sh",
                [Path.Combine(bundleC, "install.sh"), "--allow-unsigned", "--skip-artifact-backup", "--current-only", "--ready-timeout-seconds", "3"],
                environment);
            Assert.NotEqual(0, tamperedPredecessor.ExitCode);
            Assert.Equal("development", ReadPointer(installRoot, "current-release"));
            Assert.Equal("candidate", ReadPointer(installRoot, "previous-release"));
            Assert.Equal(
                releaseDirectoriesBeforeRetention,
                Directory.GetDirectories(Path.Combine(installRoot, "releases"))
                    .Select(Path.GetFileName)
                    .Order(StringComparer.Ordinal));
            await File.WriteAllBytesAsync(
                tamperedPredecessorPath,
                predecessorBytes,
                TestContext.Current.CancellationToken);

            environment["SHARPLABNEXT_FAKE_TAMPER_ROLLBACK"] = "current-only";
            var tamperedRollback = await RunAsync(
                "sh",
                [Path.Combine(bundleC, "install.sh"), "--allow-unsigned", "--current-only", "--ready-timeout-seconds", "3"],
                environment);
            Assert.NotEqual(0, tamperedRollback.ExitCode);
            Assert.Contains("checksum", tamperedRollback.StandardError, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("development", ReadPointer(installRoot, "current-release"));
            Assert.Equal("candidate", ReadPointer(installRoot, "previous-release"));
            Assert.Equal(
                releaseDirectoriesBeforeRetention,
                Directory.GetDirectories(Path.Combine(installRoot, "releases"))
                    .Select(Path.GetFileName)
                    .Order(StringComparer.Ordinal));
            var currentOnlyRollbackArtifact = Path.Combine(
                installRoot,
                "releases",
                "current-only",
                "rollback",
                "artifact-data",
                "artifact.txt");
            Assert.True(File.Exists(currentOnlyRollbackArtifact));
            await File.WriteAllTextAsync(
                currentOnlyRollbackArtifact,
                "artifact-data\n",
                TestContext.Current.CancellationToken);
            environment.Remove("SHARPLABNEXT_FAKE_TAMPER_ROLLBACK");

            var currentOnlyUpgrade = await RunAsync(
                "sh",
                [Path.Combine(bundleC, "install.sh"), "--allow-unsigned", "--current-only", "--ready-timeout-seconds", "3"],
                environment);
            Assert.Equal(0, currentOnlyUpgrade.ExitCode);
            Assert.Equal("current-only", ReadPointer(installRoot, "current-release"));
            Assert.False(File.Exists(Path.Combine(installRoot, "previous-release")));
            Assert.False(Directory.Exists(Path.Combine(installRoot, "releases", "development")));
            Assert.False(Directory.Exists(Path.Combine(installRoot, "releases", "candidate")));
            Assert.False(Directory.Exists(Path.Combine(installRoot, "releases", "current-only", "rollback")));

            var materializeStale = await RunAsync(
                "sh",
                [
                    "-c",
                    ". \"$1/deployment-common.sh\"; install_release_assets \"$1\" \"$2\" stale >/dev/null",
                    "sh",
                    bundleD,
                    installRoot
                ],
                environment);
            Assert.Equal(0, materializeStale.ExitCode);
            var staleRelease = Path.Combine(installRoot, "releases", "stale");
            await File.WriteAllTextAsync(
                Path.Combine(installRoot, "previous-release"),
                "stale\n",
                TestContext.Current.CancellationToken);
            var currentOnlyReinstall = await RunAsync(
                "sh",
                [Path.Combine(bundleC, "install.sh"), "--allow-unsigned", "--skip-artifact-backup", "--current-only", "--ready-timeout-seconds", "3"],
                environment);
            Assert.Equal(0, currentOnlyReinstall.ExitCode);
            Assert.Equal("current-only", ReadPointer(installRoot, "current-release"));
            Assert.False(File.Exists(Path.Combine(installRoot, "previous-release")));
            Assert.False(Directory.Exists(staleRelease));
            Assert.Equal(
                ["current-only"],
                Directory.GetDirectories(Path.Combine(installRoot, "releases"))
                    .Select(Path.GetFileName)
                    .Order(StringComparer.Ordinal));
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PowerShellInstallerRestoresFailedCandidateAndRollsBackSuccessfulUpgrade()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(Path.GetTempPath(), $"sharplabnext-install-ps-{Guid.NewGuid():N}");
        var bundleA = Path.Combine(testRoot, "bundle-a");
        var bundleB = Path.Combine(testRoot, "bundle-b");
        var bundleC = Path.Combine(testRoot, "bundle-c");
        var bundleD = Path.Combine(testRoot, "bundle-d");
        var installRoot = Path.Combine(testRoot, "installed");
        var fakeBin = Path.Combine(testRoot, "bin");
        var statePath = Path.Combine(testRoot, "active-release.txt");
        var failPath = Path.Combine(testRoot, "fail-release.txt");
        var internalServiceToken = Path.Combine(testRoot, "internal-service-token");
        Directory.CreateDirectory(fakeBin);
        try
        {
            await BuildBundleForReleaseAsync(repositoryRoot, testRoot, bundleA, "development");
            await BuildBundleForReleaseAsync(repositoryRoot, testRoot, bundleB, "candidate");
            await BuildBundleForReleaseAsync(repositoryRoot, testRoot, bundleC, "current-only");
            await BuildBundleForReleaseAsync(repositoryRoot, testRoot, bundleD, "stale");
            await File.WriteAllTextAsync(
                internalServiceToken,
                "test-internal-service-token",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(fakeBin, "docker.cmd"),
                $$"""
                @echo off
                setlocal EnableExtensions EnableDelayedExpansion
                if "%1"=="image" if "%2"=="inspect" (
                  set "imageReference=%~5"
                  if /I "!imageReference:~0,7!"=="sha256:" (
                    echo !imageReference!
                    exit /b 0
                  )
                  set "arguments=%*"
                  set "immutableDigest=%arguments:*@sha256:=%"
                  if not "%immutableDigest%"=="%arguments%" (
                    echo sha256:%immutableDigest%
                    exit /b 0
                  )
                  echo %* | %SystemRoot%\System32\findstr.exe /C:"{{FakeDockerCli.RoslynNetFx48ImageId}}" >nul && (
                    echo {{FakeDockerCli.RoslynNetFx48ImageId}}
                    exit /b 0
                  )
                  echo %* | %SystemRoot%\System32\findstr.exe /C:"{{FakeDockerCli.RoslynStableImageId}}" >nul && (
                    echo {{FakeDockerCli.RoslynStableImageId}}
                    exit /b 0
                  )
                  echo %* | %SystemRoot%\System32\findstr.exe /C:"{{FakeDockerCli.RoslynMainImageId}}" >nul && (
                    echo {{FakeDockerCli.RoslynMainImageId}}
                    exit /b 0
                  )
                  echo %* | %SystemRoot%\System32\findstr.exe /C:"{{FakeDockerCli.RoslynConstGenericsImageId}}" >nul && (
                    echo {{FakeDockerCli.RoslynConstGenericsImageId}}
                    exit /b 0
                  )
                  echo %* | %SystemRoot%\System32\findstr.exe /C:"{{FakeDockerCli.FSharpImageId}}" >nul && (
                    echo {{FakeDockerCli.FSharpImageId}}
                    exit /b 0
                  )
                  echo %* | %SystemRoot%\System32\findstr.exe /C:"{{FakeDockerCli.GSharpImageId}}" >nul && (
                    echo {{FakeDockerCli.GSharpImageId}}
                    exit /b 0
                  )
                  echo %* | %SystemRoot%\System32\findstr.exe /C:"{{FakeDockerCli.PeachPieImageId}}" >nul && (
                    echo {{FakeDockerCli.PeachPieImageId}}
                    exit /b 0
                  )
                  echo %* | %SystemRoot%\System32\findstr.exe /C:"{{FakeDockerCli.CppCliImageId}}" >nul && (
                    echo {{FakeDockerCli.CppCliImageId}}
                    exit /b 0
                  )
                  echo %* | %SystemRoot%\System32\findstr.exe /C:"{{FakeDockerCli.WineNetFxRuntimeImageId}}" >nul && (
                    echo {{FakeDockerCli.WineNetFxRuntimeImageId}}
                    exit /b 0
                  )
                  echo %* | %SystemRoot%\System32\findstr.exe /C:"{{FakeDockerCli.JSharpImageId}}" >nul && (
                    echo {{FakeDockerCli.JSharpImageId}}
                    exit /b 0
                  )
                  echo %* | %SystemRoot%\System32\findstr.exe /C:"{{FakeDockerCli.WineJSharpRuntimeImageId}}" >nul && (
                    echo {{FakeDockerCli.WineJSharpRuntimeImageId}}
                    exit /b 0
                  )
                  echo %* | %SystemRoot%\System32\findstr.exe /C:"{{FakeDockerCli.IlImageId}}" >nul && (
                    echo {{FakeDockerCli.IlImageId}}
                    exit /b 0
                  )
                  echo %* | %SystemRoot%\System32\findstr.exe /C:"{{FakeDockerCli.MinilangImageId}}" >nul && (
                    echo {{FakeDockerCli.MinilangImageId}}
                    exit /b 0
                  )
                  echo %* | %SystemRoot%\System32\findstr.exe /C:"{{FakeDockerCli.ArtifactsDefaultImageId}}" >nul && (
                    echo {{FakeDockerCli.ArtifactsDefaultImageId}}
                    exit /b 0
                  )
                  echo %* | %SystemRoot%\System32\findstr.exe /C:"{{FakeDockerCli.ArtifactsJsilImageId}}" >nul && (
                    echo {{FakeDockerCli.ArtifactsJsilImageId}}
                    exit /b 0
                  )
                  echo %* | %SystemRoot%\System32\findstr.exe /C:"{{FakeDockerCli.ArtifactsConstGenericsImageId}}" >nul && (
                    echo {{FakeDockerCli.ArtifactsConstGenericsImageId}}
                    exit /b 0
                  )
                  echo %* | %SystemRoot%\System32\findstr.exe /C:"{{FakeDockerCli.ArtifactsIlAssemblerImageId}}" >nul && (
                    echo {{FakeDockerCli.ArtifactsIlAssemblerImageId}}
                    exit /b 0
                  )
                  echo {{FakeDockerCli.ImageId}}
                  exit /b 0
                )
                if "%1"=="cp" (
                  if not exist "%~3" mkdir "%~3"
                  > "%~3\artifact.txt" echo artifact-data
                  exit /b 0
                )
                echo %* | %SystemRoot%\System32\findstr.exe /C:"config --services" >nul && (
                  echo gateway
                  exit /b 0
                )
                echo %* | %SystemRoot%\System32\findstr.exe /C:"ps --status running --services" >nul && (
                  echo gateway
                  exit /b 0
                )
                echo %* | %SystemRoot%\System32\findstr.exe /C:"ps --all -q artifact-store" >nul && (
                  echo fake-artifact-store
                  exit /b 0
                )
                echo %* | %SystemRoot%\System32\findstr.exe /C:" up -d " >nul && (
                  > "%SHARPLABNEXT_FAKE_STATE%" echo %SHARPLABNEXT_RELEASE_ID%
                  if /I "%SHARPLABNEXT_FAKE_TAMPER_ROLLBACK%"=="%SHARPLABNEXT_RELEASE_ID%" (
                    >> "%SHARPLABNEXT_HOME%\releases\%SHARPLABNEXT_RELEASE_ID%\rollback\artifact-data\artifact.txt" echo tampered
                  )
                  exit /b 0
                )
                echo %* | %SystemRoot%\System32\findstr.exe /C:" down --remove-orphans" >nul && (
                  if exist "%SHARPLABNEXT_FAKE_STATE%" del /q "%SHARPLABNEXT_FAKE_STATE%"
                  exit /b 0
                )
                exit /b 0
                """.ReplaceLineEndings("\r\n"),
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(fakeBin, "openssl.cmd"),
                "@echo off\r\n\"C:\\Program Files\\Git\\mingw64\\bin\\openssl.exe\" %*\r\n",
                TestContext.Current.CancellationToken);

            await using var server = new FakeReleaseHttpServer(statePath, failPath);
            var environment = new Dictionary<string, string>
            {
                ["PATH"] = $"{fakeBin};{Environment.GetEnvironmentVariable("PATH")}",
                ["SHARPLABNEXT_HOME"] = installRoot,
                ["SHARPLABNEXT_FAKE_STATE"] = statePath,
                ["SHARPLABNEXT_INTERNAL_SERVICE_TOKEN_FILE"] = internalServiceToken
            };
            var smokeAddress = $"http://127.0.0.1:{server.Port}";
            var first = await RunAsync(
                "pwsh",
                ["-NoProfile", "-File", Path.Combine(bundleA, "install.ps1"), "-AllowUnsigned", "-SkipArtifactBackup", "-ReadyTimeoutSeconds", "3", "-SmokeBaseAddress", smokeAddress],
                environment);
            Assert.True(
                first.ExitCode == 0,
                $"First PowerShell install failed. stdout: {first.StandardOutput}{Environment.NewLine}stderr: {first.StandardError}");
            Assert.Equal("development", ReadPointer(installRoot, "current-release"));
            var directSmoke = await RunAsync(
                "pwsh",
                ["-NoProfile", "-File", Path.Combine(bundleA, "smoke.ps1"), "-ExpectedReleaseId", "development", "-TimeoutSeconds", "3", "-BaseAddress", smokeAddress],
                environment);
            Assert.Equal(0, directSmoke.ExitCode);
            Assert.True(File.Exists(Path.Combine(
                installRoot,
                "releases",
                "development",
                "security",
                "sharplabnext-runtime-job-v1.apparmor")));
            Assert.Contains(
                "security/sharplabnext-runtime-job-v1.apparmor",
                await File.ReadAllTextAsync(
                    Path.Combine(installRoot, "releases", "development", "deployment.sha256"),
                    TestContext.Current.CancellationToken),
                StringComparison.Ordinal);

            await File.WriteAllTextAsync(failPath, "candidate", TestContext.Current.CancellationToken);
            var failedUpgrade = await RunAsync(
                "pwsh",
                ["-NoProfile", "-File", Path.Combine(bundleB, "install.ps1"), "-AllowUnsigned", "-SkipArtifactBackup", "-ReadyTimeoutSeconds", "1", "-SmokeBaseAddress", smokeAddress],
                environment);
            Assert.NotEqual(0, failedUpgrade.ExitCode);
            Assert.Contains("was restored", failedUpgrade.StandardError, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("development", ReadPointer(installRoot, "current-release"));

            File.Delete(failPath);
            var upgrade = await RunAsync(
                "pwsh",
                ["-NoProfile", "-File", Path.Combine(bundleB, "install.ps1"), "-AllowUnsigned", "-SkipArtifactBackup", "-ReadyTimeoutSeconds", "3", "-SmokeBaseAddress", smokeAddress],
                environment);
            Assert.Equal(0, upgrade.ExitCode);
            Assert.Equal("candidate", ReadPointer(installRoot, "current-release"));
            Assert.Equal("development", ReadPointer(installRoot, "previous-release"));

            var rollback = await RunAsync(
                "pwsh",
                ["-NoProfile", "-File", Path.Combine(bundleB, "rollback.ps1"), "-InstallRoot", installRoot, "-ReadyTimeoutSeconds", "3", "-SmokeBaseAddress", smokeAddress],
                environment);
            Assert.Equal(0, rollback.ExitCode);
            Assert.Equal("development", ReadPointer(installRoot, "current-release"));
            Assert.Equal("candidate", ReadPointer(installRoot, "previous-release"));

            var previousRollbackRoot = Path.Combine(installRoot, "releases", "candidate", "rollback");
            var previousRollbackExisted = Directory.Exists(previousRollbackRoot);
            await File.WriteAllTextAsync(failPath, "current-only", TestContext.Current.CancellationToken);
            var failedCurrentOnly = await RunAsync(
                "pwsh",
                ["-NoProfile", "-File", Path.Combine(bundleC, "install.ps1"), "-AllowUnsigned", "-SkipArtifactBackup", "-CurrentOnly", "-ReadyTimeoutSeconds", "1", "-SmokeBaseAddress", smokeAddress],
                environment);
            Assert.NotEqual(0, failedCurrentOnly.ExitCode);
            Assert.Equal("development", ReadPointer(installRoot, "current-release"));
            Assert.Equal("candidate", ReadPointer(installRoot, "previous-release"));
            Assert.True(Directory.Exists(Path.Combine(installRoot, "releases", "development")));
            Assert.True(Directory.Exists(Path.Combine(installRoot, "releases", "candidate")));
            Assert.Equal(previousRollbackExisted, Directory.Exists(previousRollbackRoot));

            File.Delete(failPath);
            var releaseDirectoriesBeforeRetention = Directory.GetDirectories(Path.Combine(installRoot, "releases"))
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var tamperedPredecessorPath = Path.Combine(
                installRoot,
                "releases",
                "candidate",
                "compose.generated.yaml");
            var predecessorBytes = await File.ReadAllBytesAsync(
                tamperedPredecessorPath,
                TestContext.Current.CancellationToken);
            await File.AppendAllTextAsync(
                tamperedPredecessorPath,
                "\n# tampered predecessor\n",
                TestContext.Current.CancellationToken);
            var tamperedPredecessor = await RunAsync(
                "pwsh",
                ["-NoProfile", "-File", Path.Combine(bundleC, "install.ps1"), "-AllowUnsigned", "-SkipArtifactBackup", "-CurrentOnly", "-ReadyTimeoutSeconds", "3", "-SmokeBaseAddress", smokeAddress],
                environment);
            Assert.NotEqual(0, tamperedPredecessor.ExitCode);
            Assert.Equal("development", ReadPointer(installRoot, "current-release"));
            Assert.Equal("candidate", ReadPointer(installRoot, "previous-release"));
            Assert.Equal(
                releaseDirectoriesBeforeRetention,
                Directory.GetDirectories(Path.Combine(installRoot, "releases"))
                    .Select(Path.GetFileName)
                    .Order(StringComparer.Ordinal));
            await File.WriteAllBytesAsync(
                tamperedPredecessorPath,
                predecessorBytes,
                TestContext.Current.CancellationToken);

            environment["SHARPLABNEXT_FAKE_TAMPER_ROLLBACK"] = "current-only";
            var tamperedRollback = await RunAsync(
                "pwsh",
                ["-NoProfile", "-File", Path.Combine(bundleC, "install.ps1"), "-AllowUnsigned", "-CurrentOnly", "-ReadyTimeoutSeconds", "3", "-SmokeBaseAddress", smokeAddress],
                environment);
            Assert.NotEqual(0, tamperedRollback.ExitCode);
            Assert.Contains("checksum", tamperedRollback.StandardError, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("development", ReadPointer(installRoot, "current-release"));
            Assert.Equal("candidate", ReadPointer(installRoot, "previous-release"));
            Assert.Equal(
                releaseDirectoriesBeforeRetention,
                Directory.GetDirectories(Path.Combine(installRoot, "releases"))
                    .Select(Path.GetFileName)
                    .Order(StringComparer.Ordinal));
            var currentOnlyRollbackArtifact = Path.Combine(
                installRoot,
                "releases",
                "current-only",
                "rollback",
                "artifact-data",
                "artifact.txt");
            Assert.True(File.Exists(currentOnlyRollbackArtifact));
            await File.WriteAllTextAsync(
                currentOnlyRollbackArtifact,
                "artifact-data\r\n",
                TestContext.Current.CancellationToken);
            environment.Remove("SHARPLABNEXT_FAKE_TAMPER_ROLLBACK");

            var currentOnlyUpgrade = await RunAsync(
                "pwsh",
                ["-NoProfile", "-File", Path.Combine(bundleC, "install.ps1"), "-AllowUnsigned", "-CurrentOnly", "-ReadyTimeoutSeconds", "3", "-SmokeBaseAddress", smokeAddress],
                environment);
            Assert.Equal(0, currentOnlyUpgrade.ExitCode);
            Assert.Equal("current-only", ReadPointer(installRoot, "current-release"));
            Assert.False(File.Exists(Path.Combine(installRoot, "previous-release")));
            Assert.False(Directory.Exists(Path.Combine(installRoot, "releases", "development")));
            Assert.False(Directory.Exists(Path.Combine(installRoot, "releases", "candidate")));
            Assert.False(Directory.Exists(Path.Combine(installRoot, "releases", "current-only", "rollback")));

            var materializeStaleScript = Path.Combine(testRoot, "materialize-stale.ps1");
            await File.WriteAllTextAsync(
                materializeStaleScript,
                "param([string]$Bundle, [string]$InstallRoot)\n" +
                ". (Join-Path $Bundle 'deployment-common.ps1')\n" +
                "Install-ReleaseAssets $Bundle $InstallRoot 'stale' | Out-Null\n",
                TestContext.Current.CancellationToken);
            var materializeStale = await RunAsync(
                "pwsh",
                [
                    "-NoProfile",
                    "-File",
                    materializeStaleScript,
                    bundleD,
                    installRoot
                ],
                environment);
            Assert.Equal(0, materializeStale.ExitCode);
            var staleRelease = Path.Combine(installRoot, "releases", "stale");
            await File.WriteAllTextAsync(
                Path.Combine(installRoot, "previous-release"),
                "stale\n",
                TestContext.Current.CancellationToken);
            var currentOnlyReinstall = await RunAsync(
                "pwsh",
                ["-NoProfile", "-File", Path.Combine(bundleC, "install.ps1"), "-AllowUnsigned", "-SkipArtifactBackup", "-CurrentOnly", "-ReadyTimeoutSeconds", "3", "-SmokeBaseAddress", smokeAddress],
                environment);
            Assert.Equal(0, currentOnlyReinstall.ExitCode);
            Assert.Equal("current-only", ReadPointer(installRoot, "current-release"));
            Assert.False(File.Exists(Path.Combine(installRoot, "previous-release")));
            Assert.False(Directory.Exists(staleRelease));
            Assert.Equal(
                ["current-only"],
                Directory.GetDirectories(Path.Combine(installRoot, "releases"))
                    .Select(Path.GetFileName)
                    .Order(StringComparer.Ordinal));
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PowerShellInstallerRejectsMissingInternalServiceTokenBeforeDeploymentMutation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(Path.GetTempPath(), $"sharplabnext-install-ps-secret-{Guid.NewGuid():N}");
        var bundle = Path.Combine(testRoot, "bundle");
        var installRoot = Path.Combine(testRoot, "installed");
        var fakeBin = Path.Combine(testRoot, "bin");
        var dockerCallMarker = Path.Combine(testRoot, "docker-called.txt");
        var missingToken = Path.Combine(testRoot, "missing-internal-service-token");
        Directory.CreateDirectory(fakeBin);
        Directory.CreateDirectory(installRoot);
        try
        {
            await BuildBundleForReleaseAsync(repositoryRoot, testRoot, bundle, "candidate");
            await File.WriteAllTextAsync(
                Path.Combine(installRoot, "current-release"),
                "development\n",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(fakeBin, "docker.cmd"),
                """
                @echo off
                > "%SHARPLABNEXT_FAKE_DOCKER_CALLED%" echo called
                exit /b 99
                """.ReplaceLineEndings("\r\n"),
                TestContext.Current.CancellationToken);

            var environment = new Dictionary<string, string>
            {
                ["PATH"] = $"{fakeBin};{Environment.GetEnvironmentVariable("PATH")}",
                ["SHARPLABNEXT_HOME"] = installRoot,
                ["SHARPLABNEXT_INTERNAL_SERVICE_TOKEN_FILE"] = missingToken,
                ["SHARPLABNEXT_FAKE_DOCKER_CALLED"] = dockerCallMarker
            };
            var result = await RunAsync(
                "pwsh",
                ["-NoProfile", "-File", Path.Combine(bundle, "install.ps1"), "-AllowUnsigned"],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("Internal service token does not exist", result.StandardError, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(dockerCallMarker));
            Assert.Equal("development", ReadPointer(installRoot, "current-release"));
            Assert.False(Directory.Exists(Path.Combine(installRoot, "releases")));
            Assert.False(File.Exists(Path.Combine(installRoot, "previous-release")));
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void SelectionExcludesUnavailableAtomicProfiles()
    {
        var catalog = new SharpLabNext.Catalog.CatalogDocument
        {
            SchemaVersion = 1,
            Revision = "test",
            ReleaseId = "test",
            Languages = [],
            Toolchains =
            [
                Toolchain("stable", installed: true),
                Toolchain("experimental", installed: false)
            ],
            ReferenceSets = [],
            Runtimes = [],
            ArtifactProcessors = [],
            Outputs = [],
            Compatibility = [],
            Presets = []
        };
        var deployment = new DeploymentImageManifest
        {
            SchemaVersion = 1,
            Images =
            [
                new DeploymentImageDefinition { Id = "core", Repository = "test/core", Always = true },
                new DeploymentImageDefinition { Id = "stable", Repository = "test/stable", ToolchainId = "stable" },
                new DeploymentImageDefinition { Id = "experimental", Repository = "test/experimental", ToolchainId = "experimental" }
            ]
        };

        var selected = ReleaseBundleBuilder.SelectImages(catalog, deployment);

        Assert.Equal(["core", "stable"], selected.Select(static item => item.Id));
    }

    [Fact]
    public void SelectionIncludesOneImageWhenAnotherProfileOnTheSameWorkerIsSelectable()
    {
        var catalog = new CatalogDocument
        {
            SchemaVersion = 1,
            Revision = "test",
            ReleaseId = "test",
            Languages = [],
            Toolchains =
            [
                Toolchain("legacy", installed: false, workerId: "shared-worker"),
                Toolchain("stable", installed: true, workerId: "shared-worker")
            ],
            ReferenceSets = [],
            Runtimes = [],
            ArtifactProcessors = [],
            Outputs = [],
            Compatibility = [],
            Presets = []
        };
        var deployment = new DeploymentImageManifest
        {
            SchemaVersion = 1,
            Images =
            [
                new DeploymentImageDefinition
                {
                    Id = "shared",
                    Repository = "test/shared",
                    ToolchainId = "legacy"
                }
            ]
        };

        var selected = ReleaseBundleBuilder.SelectImages(catalog, deployment);

        Assert.Equal(["shared"], selected.Select(static item => item.Id));
    }

    [Fact]
    public void ComposeOverlaySharesOneImageExpectationAcrossToolchainProfiles()
    {
        var catalog = new CatalogDocument
        {
            SchemaVersion = 1,
            Revision = "test",
            ReleaseId = "release-test",
            Languages = [],
            Toolchains =
            [
                Toolchain("gsharp-stable", installed: true, workerId: "gsharp-worker"),
                Toolchain("gsharp-legacy", installed: true, workerId: "gsharp-worker")
            ],
            ReferenceSets = [],
            Runtimes = [],
            ArtifactProcessors = [],
            Outputs = [],
            Compatibility = [],
            Presets = []
        };
        var gateway = Inspected(
            "gateway",
            $"sha256:{new string('a', 64)}",
            composeService: "gateway");
        var worker = Inspected(
            "worker-gsharp",
            $"sha256:{new string('b', 64)}",
            composeService: "worker-gsharp",
            toolchainId: "gsharp-stable",
            releaseIdEnvironment: "GSharp__ReleaseId",
            imageIdEnvironment: "GSharp__WorkerImageId");

        var compose = ReleaseBundleBuilder.CreateComposeOverlay(catalog, [gateway, worker]);

        const string expectation =
            "Services__LanguageWorkers__gsharp-worker__ExpectedWorkerImageId";
        Assert.Equal(1, compose.Split(expectation, StringSplitOptions.None).Length - 1);
        Assert.Contains(
            $"      {expectation}: \"{worker.ImageId}\"",
            compose,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Services__LanguageWorkers__gsharp-legacy__ExpectedWorkerImageId",
            compose,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ComposeOverlayRejectsDuplicateImagesForOneLanguageWorker()
    {
        var catalog = new CatalogDocument
        {
            SchemaVersion = 1,
            Revision = "test",
            ReleaseId = "release-test",
            Languages = [],
            Toolchains =
            [
                Toolchain("stable", installed: true, workerId: "shared-worker"),
                Toolchain("legacy", installed: true, workerId: "shared-worker")
            ],
            ReferenceSets = [],
            Runtimes = [],
            ArtifactProcessors = [],
            Outputs = [],
            Compatibility = [],
            Presets = []
        };

        var exception = Assert.Throws<BundleValidationException>(() =>
            ReleaseBundleBuilder.CreateComposeOverlay(
                catalog,
                [
                    Inspected("stable", "sha256:stable", toolchainId: "stable"),
                    Inspected("legacy", "sha256:legacy", toolchainId: "legacy")
                ]));

        Assert.Contains("shared-worker", exception.Message, StringComparison.Ordinal);
        Assert.Contains("more than one", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledGitHubOAuthSecretPlaceholderOverwritesExistingContentWithEmptyFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sharplabnext-oauth-placeholder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var placeholderPath = Path.Combine(
            root,
            ReleaseBundleBuilder.DisabledGitHubOAuthSecretFileName);
        try
        {
            await File.WriteAllTextAsync(
                placeholderPath,
                "must not remain in the bundle",
                TestContext.Current.CancellationToken);

            await ReleaseBundleBuilder.WriteDisabledGitHubOAuthSecretPlaceholderAsync(
                root,
                TestContext.Current.CancellationToken);

            Assert.True(File.Exists(placeholderPath));
            Assert.Equal(0, new FileInfo(placeholderPath).Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeSandboxInventoryPinsMobyProfileAndCompleteLicense()
    {
        const string profileSha256 = "01536f1d1df938ae611eba20d6349e0de7a99b6ecdee1549427a0b01b8301e28";
        var repositoryRoot = FindRepositoryRoot();
        var inventoryPath = Path.Combine(repositoryRoot, "deploy", "security", "inventory.json");
        using var inventory = JsonDocument.Parse(await File.ReadAllTextAsync(
            inventoryPath,
            TestContext.Current.CancellationToken));
        var component = Assert.Single(inventory.RootElement.GetProperty("components").EnumerateArray());
        Assert.Equal("moby/profiles", component.GetProperty("name").GetString());
        Assert.Equal("seccomp/v0.1.0", component.GetProperty("version").GetString());
        Assert.Equal("c936cc7b4074219137bc0bee45670f5e4618d462", component.GetProperty("commit").GetString());
        Assert.Equal(profileSha256, component.GetProperty("sha256").GetString());
        Assert.Equal("Apache-2.0", component.GetProperty("license").GetString());
        var selectedBy = component.GetProperty("selectedBy");
        Assert.Equal("moby/moby", selectedBy.GetProperty("name").GetString());
        Assert.Equal("v28.5.2", selectedBy.GetProperty("version").GetString());
        Assert.Equal(
            "89c5e8fd66634b6128fc4c0e6f1236e2540e46e0",
            selectedBy.GetProperty("commit").GetString());

        var profilePath = Path.Combine(
            repositoryRoot,
            component.GetProperty("sourcePath").GetString()!.Replace('/', Path.DirectorySeparatorChar));
        await using (var profile = File.OpenRead(profilePath))
        {
            Assert.Equal(
                profileSha256,
                Convert.ToHexStringLower(await SHA256.HashDataAsync(
                    profile,
                    TestContext.Current.CancellationToken)));
        }

        var licensePath = Path.Combine(
            repositoryRoot,
            component.GetProperty("licensePath").GetString()!.Replace('/', Path.DirectorySeparatorChar));
        var license = await File.ReadAllTextAsync(licensePath, TestContext.Current.CancellationToken);
        Assert.Contains("Apache License", license, StringComparison.Ordinal);
        Assert.Contains("END OF TERMS AND CONDITIONS", license, StringComparison.Ordinal);
        Assert.True(license.Length >= 10_000);
    }

    [Fact]
    public async Task ChecksumsCoverEveryBundleFileAndDetectTampering()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sharplabnext-checksum-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "a.txt"),
                "alpha",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(root, "nested", "b.txt"),
                "beta",
                TestContext.Current.CancellationToken);

            await ReleaseBundleBuilder.WriteChecksumsAsync(root, TestContext.Current.CancellationToken);

            var lines = await File.ReadAllLinesAsync(
                Path.Combine(root, "checksums.sha256"),
                TestContext.Current.CancellationToken);
            Assert.Equal(2, lines.Length);
            Assert.Contains(lines, static line => line.EndsWith("  a.txt", StringComparison.Ordinal));
            Assert.Contains(lines, static line => line.EndsWith("  nested/b.txt", StringComparison.Ordinal));
            var expected = Convert.ToHexStringLower(SHA256.HashData("alpha"u8.ToArray()));
            Assert.Contains(lines, line => line.StartsWith(expected, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DependencyAuditRejectsAnUnapprovedTransitiveLicense()
    {
        var repositoryRoot = FindRepositoryRoot();
        var policyPath = Path.Combine(Path.GetTempPath(), $"sharplabnext-license-{Guid.NewGuid():N}.json");
        var policy = await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "profiles", "license-policy.json"),
            TestContext.Current.CancellationToken);
        policy = policy.Replace("\"MPL-2.0\"", "\"Test-Only-Unrelated-License\"", StringComparison.Ordinal);
        await File.WriteAllTextAsync(policyPath, policy, TestContext.Current.CancellationToken);
        try
        {
            var exception = await Assert.ThrowsAsync<BundleValidationException>(() =>
                DependencyInventory.LoadAsync(
                    repositoryRoot,
                    policyPath,
                    TestContext.Current.CancellationToken));

            Assert.Contains("dompurify", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("unapproved license", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(policyPath);
        }
    }

    [Fact]
    public async Task DependencyAuditIncludesSourceLocksWithoutDuplicatesOrBuildOutputs()
    {
        var repositoryRoot = await CreateDependencyInventoryRepositoryAsync();
        try
        {
            var projectRoot = Path.Combine(repositoryRoot, "src", "TestProject");
            Directory.CreateDirectory(projectRoot);
            await File.WriteAllTextAsync(
                Path.Combine(projectRoot, "packages.lock.json"),
                CreateNuGetLock("Default.Only.Package", "Shared.Package"),
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(projectRoot, "packages.source.lock.json"),
                CreateNuGetLock("Source.Only.Package", "Shared.Package"),
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(projectRoot, "packages.EleCho.ILSense.lock.json"),
                CreateNuGetLock("Named.Project.Package", "Shared.Package"),
                TestContext.Current.CancellationToken);

            foreach (var excludedDirectory in new[] { "bin", "obj" })
            {
                var path = Path.Combine(projectRoot, excludedDirectory);
                Directory.CreateDirectory(path);
                await File.WriteAllTextAsync(
                    Path.Combine(path, "packages.source.lock.json"),
                    CreateNuGetLock($"Ignored.{excludedDirectory}.Package"),
                    TestContext.Current.CancellationToken);
            }

            var (_, components) = await DependencyInventory.LoadAsync(
                repositoryRoot,
                Path.Combine(repositoryRoot, "profiles", "license-policy.json"),
                TestContext.Current.CancellationToken);
            var nuget = components
                .Where(static component => component.PackageManager == "nuget")
                .ToArray();

            Assert.Equal(4, nuget.Length);
            Assert.Contains(nuget, static component => component.Name == "Default.Only.Package");
            Assert.Contains(nuget, static component => component.Name == "Source.Only.Package");
            Assert.Contains(nuget, static component => component.Name == "Named.Project.Package");
            Assert.Single(nuget, static component => component.Name == "Shared.Package");
            Assert.DoesNotContain(nuget, static component => component.Name.StartsWith("Ignored.", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DependencyAuditWithoutNuGetLocksStillReturnsNonNuGetInventory()
    {
        var repositoryRoot = await CreateDependencyInventoryRepositoryAsync();
        try
        {
            var (_, components) = await DependencyInventory.LoadAsync(
                repositoryRoot,
                Path.Combine(repositoryRoot, "profiles", "license-policy.json"),
                TestContext.Current.CancellationToken);

            Assert.DoesNotContain(components, static component => component.PackageManager == "nuget");
            Assert.Contains(
                components,
                static component => component.PackageManager == "github" && component.Name == "test/reviewed-source");
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ApplicationDependencyInventoryExcludesWineOperatingSystemPackages()
    {
        var repositoryRoot = await CreateDependencyInventoryRepositoryAsync();
        try
        {
            var (_, components) = await DependencyInventory.LoadAsync(
                repositoryRoot,
                Path.Combine(repositoryRoot, "profiles", "license-policy.json"),
                TestContext.Current.CancellationToken);

            Assert.DoesNotContain(components, static component => component.PackageManager == "apt-source");
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task WinePackageInventoryRejectsUnknownFieldsAndUnreviewedClosure()
    {
        var repositoryRoot = await CreateDependencyInventoryRepositoryAsync();
        var manifestPath = Path.Combine(repositoryRoot, "profiles", "runtime-wine-packages.json");
        try
        {
            var reviewedBytes = await File.ReadAllBytesAsync(
                manifestPath,
                TestContext.Current.CancellationToken);
            Action<JsonObject>[] mutations =
            [
                static document => document["unexpected"] = true,
                static document =>
                    document["sourceOffer"]!["files"]![0]!["sha256"] = new string('f', 64),
                static document =>
                    document["sourceOffer"]!["baseUri"] = "https://127.0.0.1/private-wine/",
                static document => document["component"]!["id"] = "private-wine-userspace",
                static document => document["directPackages"]![0]!["architecture"] = "i386"
            ];

            foreach (var mutate in mutations)
            {
                var document = JsonNode.Parse(reviewedBytes)!.AsObject();
                mutate(document);
                await File.WriteAllTextAsync(
                    manifestPath,
                    document.ToJsonString(),
                    TestContext.Current.CancellationToken);

                await Assert.ThrowsAsync<BundleValidationException>(() =>
                    WineRuntimePackageManifestLoader.LoadAsync(
                        repositoryRoot,
                        TestContext.Current.CancellationToken));
            }
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task WinePackageInventorySnapshotMustMatchReleaseLockIdentity()
    {
        var repositoryRoot = FindRepositoryRoot();
        var snapshot = await WineRuntimePackageManifestLoader.LoadSnapshotAsync(
            repositoryRoot,
            TestContext.Current.CancellationToken);
        var releaseLock = await CatalogLoader.LoadReleaseLockAsync(
            Path.Combine(repositoryRoot, "profiles", "lock.json"),
            TestContext.Current.CancellationToken);

        WineRuntimePackageManifestLoader.ValidateReleaseLock(snapshot, releaseLock);

        var components = releaseLock.Components.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        components[snapshot.Manifest.Component.Id] = components[snapshot.Manifest.Component.Id] with
        {
            Digest = "sha256:" + new string('0', 64)
        };
        var tampered = releaseLock with { Components = components };
        Assert.Throws<BundleValidationException>(() =>
            WineRuntimePackageManifestLoader.ValidateReleaseLock(snapshot, tampered));
    }

    [Fact]
    public async Task BuilderRejectsRealWineManifestLockDriftBeforeDockerOrSourceDownload()
    {
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(Path.GetTempPath(), $"sharplabnext-wine-lock-{Guid.NewGuid():N}");
        var lockPath = Path.Combine(testRoot, "lock.json");
        var output = Path.Combine(testRoot, "bundle");
        try
        {
            Directory.CreateDirectory(testRoot);
            var releaseLock = JsonNode.Parse(await File.ReadAllTextAsync(
                Path.Combine(repositoryRoot, "profiles", "lock.json"),
                TestContext.Current.CancellationToken))!;
            releaseLock["components"]!["wine-coreclr-userspace"]!["digest"] =
                "sha256:" + new string('0', 64);
            await File.WriteAllTextAsync(
                lockPath,
                releaseLock.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                TestContext.Current.CancellationToken);
            var command = new BundleBuilderCommand(
                repositoryRoot,
                Path.Combine(repositoryRoot, "profiles", "catalog", "catalog.json"),
                lockPath,
                Path.Combine(repositoryRoot, "deploy", "images.json"),
                Path.Combine(repositoryRoot, "profiles", "license-policy.json"),
                Path.Combine(repositoryRoot, "deploy", "compose.prod.yaml"),
                Path.Combine(repositoryRoot, "THIRD-PARTY-NOTICES.md"),
                output,
                "docker",
                "openssl",
                null,
                null,
                null,
                null,
                MetadataOnly: true,
                new Dictionary<string, string>());
            var builder = new ReleaseBundleBuilder(
                new FakeDockerCli(),
                sourceInspector: new FakeRepositorySourceInspector(
                    new RepositorySourceState(true, TestSourceRevision, false)),
                runtimePromotionSourceInspector: new FakeRuntimePromotionSourceInspector(),
                externalSourceMaterialFetcher: new FakeExternalSourceMaterialFetcher());

            var exception = await Assert.ThrowsAsync<BundleValidationException>(() => builder.BuildAsync(
                command,
                TestContext.Current.CancellationToken));

            Assert.Contains("does not match its release lock identity", exception.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(output));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task OperatingSystemSourceClosureWritesTheExactReviewedFilesAndMaterialEntries()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"sharplabnext-wine-source-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var materials = new List<SourceMaterialComponent>();
            var builder = new ReleaseBundleBuilder(
                new FakeDockerCli(),
                externalSourceMaterialFetcher: new FakeExternalSourceMaterialFetcher());

            await builder.WriteOperatingSystemSourcesAsync(
                TestWineManifest,
                testRoot,
                materials,
                TestContext.Current.CancellationToken);

            Assert.Equal(162, materials.Count);
            var material = Assert.Single(materials, static item => item.Name == "wine");
            Assert.Equal("apt-source", material.PackageManager);
            Assert.Equal(
                "sources/ubuntu/20260810T000000Z/pool/universe/w/wine",
                material.MaterialPath);
            foreach (var (relativePath, expectedBytes) in TestOperatingSystemSourceFiles)
            {
                Assert.Equal(
                    expectedBytes,
                    await File.ReadAllBytesAsync(
                        Path.Combine(
                            testRoot,
                            "sources",
                            "ubuntu",
                            "20260810T000000Z",
                            relativePath.Replace('/', Path.DirectorySeparatorChar)),
                        TestContext.Current.CancellationToken));
            }
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task OperatingSystemSourceClosureRejectsRedirectAndTamperedContent()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"sharplabnext-wine-source-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var redirecting = new ReleaseBundleBuilder(
                new FakeDockerCli(),
                externalSourceMaterialFetcher: new RedirectingExternalSourceMaterialFetcher());
            await Assert.ThrowsAsync<BundleValidationException>(() => redirecting.WriteOperatingSystemSourcesAsync(
                TestWineManifest,
                testRoot,
                [],
                TestContext.Current.CancellationToken));
            Assert.Empty(Directory.EnumerateFiles(testRoot, "*", SearchOption.AllDirectories));

            var tampered = new ReleaseBundleBuilder(
                new FakeDockerCli(),
                externalSourceMaterialFetcher: new TamperedExternalSourceMaterialFetcher());
            await Assert.ThrowsAsync<BundleValidationException>(() => tampered.WriteOperatingSystemSourcesAsync(
                TestWineManifest,
                testRoot,
                [],
                TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void OperatingSystemInventoryRejectsMissingBinarySourceIndexAndNoticeEvidence()
    {
        var packageMutations = new Func<WineResolvedPackage, WineResolvedPackage>[]
        {
            static package => package with { Architecture = string.Empty },
            static package => package with { Sha256 = new string('0', 63) },
            static package => package with { SourceVersion = "missing" },
            static package => package with { ArchiveIndexPath = "main/binary-amd64/other.gz" },
            static package => package with { CopyrightSha256 = new string('0', 63) },
            static package => package with
            {
                CopyrightPath = "/usr/share/doc/libssl3/copyright (broken symlink target)"
            }
        };
        foreach (var mutate in packageMutations)
        {
            var packages = TestWineManifest.ResolvedPackages.ToArray();
            packages[0] = mutate(packages[0]);
            var manifest = TestWineManifest with { ResolvedPackages = packages };
            Assert.Throws<BundleValidationException>(() =>
                WineRuntimePackageManifestLoader.ValidateResolvedPackagesForBundle(manifest));
        }

        var sources = TestWineManifest.SourcePackages.ToArray();
        sources[0] = sources[0] with { Files = sources[0].Files.Skip(1).ToArray() };
        Assert.Throws<BundleValidationException>(() =>
            WineRuntimePackageManifestLoader.ValidateResolvedPackagesForBundle(
                TestWineManifest with { SourcePackages = sources }));

        Assert.Throws<BundleValidationException>(() =>
            WineRuntimePackageManifestLoader.ValidateResolvedPackagesForBundle(TestWineManifest with
            {
                NoticeArchive = TestWineManifest.NoticeArchive with { EntryCount = 2 }
            }));
    }

    [Fact]
    public async Task WineNoticeArchiveIsCopiedFromAnExactFinalImageAndValidated()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sharplabnext-wine-notices-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
                TestWineManifest,
                RuntimeProfileFixtureJsonOptions);
            var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
            var prefix = ReleaseBundleBuilder.ComponentLabelPrefix + "wine-coreclr-userspace.";
            var image = new InspectedImage(
                "wine-test",
                "test/wine@sha256:" + new string('a', 64),
                FakeDockerCli.WineNetFxRuntimeImageId,
                "linux",
                "amd64",
                1,
                [],
                new Dictionary<string, string>
                {
                    [prefix + "version"] = TestWineManifest.Component.ResolvedVersion,
                    [prefix + "digest"] = digest,
                    [prefix + "source-uri"] = TestWineManifest.Component.SourceUri
                },
                null,
                null,
                "wine-test",
                null,
                "wine-test",
                null,
                null);
            var builder = new ReleaseBundleBuilder(new FakeDockerCli());
            await builder.WriteWineNoticeArchiveAsync(
                new WineRuntimePackageManifestSnapshot(TestWineManifest, digest, manifestBytes),
                [image],
                root,
                TestContext.Current.CancellationToken);

            Assert.Equal(
                TestWineNoticeArchive,
                await File.ReadAllBytesAsync(
                    Path.Combine(root, "notices", "wine-coreclr-copyright-notices.tar"),
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WineNoticeArchiveRejectsLinksMissingEntriesAndTamperedBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sharplabnext-wine-notice-invalid-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var linkArchive = Path.Combine(root, "link.tar");
            using (var stream = File.Create(linkArchive))
            using (var writer = new TarWriter(stream, TarEntryFormat.Ustar))
            {
                writer.WriteEntry(new UstarTarEntry(TarEntryType.SymbolicLink, "usr/share/doc/test/copyright")
                {
                    LinkName = "elsewhere"
                });
            }
            await Assert.ThrowsAsync<BundleValidationException>(() =>
                ReleaseBundleBuilder.ValidateWineNoticeArchiveAsync(
                    linkArchive,
                    TestWineManifest,
                    TestContext.Current.CancellationToken));

            var emptyArchive = Path.Combine(root, "empty.tar");
            using (var stream = File.Create(emptyArchive))
            using (var writer = new TarWriter(stream, TarEntryFormat.Ustar)) { }
            await Assert.ThrowsAsync<BundleValidationException>(() =>
                ReleaseBundleBuilder.ValidateWineNoticeArchiveAsync(
                    emptyArchive,
                    TestWineManifest,
                    TestContext.Current.CancellationToken));

            var tamperedArchive = Path.Combine(root, "tampered.tar");
            var tampered = TestWineNoticeArchive.ToArray();
            tampered[512] ^= 0xff;
            await File.WriteAllBytesAsync(tamperedArchive, tampered, TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<Exception>(() =>
                ReleaseBundleBuilder.ValidateWineNoticeArchiveAsync(
                    tamperedArchive,
                    TestWineManifest,
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BuilderSignsChecksumsWithoutCopyingThePrivateKey()
    {
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(Path.GetTempPath(), $"sharplabnext-sign-{Guid.NewGuid():N}");
        var output = Path.Combine(testRoot, "bundle");
        var privateKey = Path.Combine(testRoot, "private.pem");
        var publicKey = Path.Combine(testRoot, "public.pem");
        Directory.CreateDirectory(testRoot);
        await File.WriteAllTextAsync(privateKey, "private test key", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(publicKey, "public test key", TestContext.Current.CancellationToken);
        try
        {
            var command = new BundleBuilderCommand(
                repositoryRoot,
                Path.Combine(repositoryRoot, "profiles", "catalog", "catalog.json"),
                Path.Combine(repositoryRoot, "profiles", "lock.json"),
                Path.Combine(repositoryRoot, "deploy", "images.json"),
                Path.Combine(repositoryRoot, "profiles", "license-policy.json"),
                Path.Combine(repositoryRoot, "deploy", "compose.prod.yaml"),
                Path.Combine(repositoryRoot, "THIRD-PARTY-NOTICES.md"),
                output,
                "docker",
                "openssl",
                privateKey,
                publicKey,
                "release-key-2026",
                null,
                MetadataOnly: false,
                new Dictionary<string, string>());
            var signer = new FakeBundleSigner();

            var result = await CreateBuilder(new FakeDockerCli(), signer).BuildAsync(
                command,
                TestContext.Current.CancellationToken);

            Assert.True(result.HasSignature);
            Assert.True(signer.WasCalled);
            Assert.True(File.Exists(Path.Combine(output, "checksums.sha256.sig")));
            Assert.Equal(
                "public test key",
                await File.ReadAllTextAsync(
                    Path.Combine(output, "signing-public-key.pem"),
                    TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(output, Path.GetFileName(privateKey))));
            using var bundle = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(output, "bundle.json"),
                TestContext.Current.CancellationToken));
            Assert.True(bundle.RootElement.GetProperty("hasSignature").GetBoolean());
            Assert.Equal("ed25519", bundle.RootElement.GetProperty("signatureAlgorithm").GetString());
            Assert.Equal("release-key-2026", bundle.RootElement.GetProperty("signatureKeyId").GetString());
            Assert.Equal(
                Convert.ToHexStringLower(SHA256.HashData("public test key"u8.ToArray())),
                bundle.RootElement.GetProperty("signingPublicKeySha256").GetString());
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static InspectedImage Inspected(
        string id,
        string imageId,
        string? composeService = null,
        string? toolchainId = null,
        string? releaseIdEnvironment = null,
        string? imageIdEnvironment = null) => new(
            id,
            $"test/{id}:release-test",
            imageId,
            "linux",
            "amd64",
            536870912,
            [],
            new Dictionary<string, string>(),
            composeService,
            toolchainId,
            null,
            null,
            toolchainId ?? id,
            releaseIdEnvironment,
            imageIdEnvironment);

    private static ToolchainManifest Toolchain(
        string id,
        bool installed,
        string? workerId = null) => new()
    {
        Id = id,
        DisplayName = id,
        WorkerId = workerId ?? id,
        ReleaseTrack = "test",
        ResolvedVersion = "1",
        DefaultReferenceSetId = "ref",
        SupportedLanguageIds = [],
        AllowedReferenceSetIds = [],
        ProducesArtifactFormats = [],
        Capabilities = [],
        Availability = new SharpLabNext.Catalog.ComponentAvailability
        {
            Installed = installed,
            Health = installed ? "healthy" : "not-built"
        }
    };

    private static void AssertMaintainedIdentityMatchesLock(
        JsonElement maintained,
        JsonElement lockComponents,
        string componentId,
        string sourceComponentId)
    {
        var entry = maintained.EnumerateArray().Single(item =>
            item.GetProperty("componentId").GetString() == componentId);
        Assert.Equal(sourceComponentId, entry.GetProperty("sourceComponentId").GetString());
        AssertResolvedIdentityMatchesLock(
            entry.GetProperty("component"),
            lockComponents.GetProperty(componentId),
            componentId);
        AssertResolvedIdentityMatchesLock(
            entry.GetProperty("source"),
            lockComponents.GetProperty(sourceComponentId),
            sourceComponentId);
    }

    private static void AssertResolvedIdentityMatchesLock(
        JsonElement actual,
        JsonElement expected,
        string componentId)
    {
        Assert.Equal(componentId, actual.GetProperty("componentId").GetString());
        foreach (var propertyName in MaintainedIdentityProperties)
        {
            if (expected.TryGetProperty(propertyName, out var expectedValue))
            {
                Assert.Equal(expectedValue.GetString(), actual.GetProperty(propertyName).GetString());
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        return PromotionFixtures.Value.RepositoryRoot;
    }

    private static string FindSourceRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SharpLabNext.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }

    private static WineRuntimePackageManifest CreateTestWineManifest()
    {
        const string snapshotUri = "https://snapshot.ubuntu.com/ubuntu/20260810T000000Z/";
        const string baseUri = snapshotUri + "pool/universe/w/wine/";
        const string wineVersion = "9.0~repack-4build3";
        var sourceFiles = TestWineSourceFiles
            .Select(pair => new WineSourceOfferFile
            {
                Path = pair.Key,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(pair.Value)),
                SizeBytes = pair.Value.Length
            })
            .OrderBy(static file => file.Path.EndsWith(".dsc", StringComparison.Ordinal) ? 0 :
                file.Path.EndsWith(".orig.tar.xz", StringComparison.Ordinal) ? 1 : 2)
            .ToArray();
        TestOperatingSystemSourceFiles.Clear();
        var sourcePackages = new List<WineSourcePackage>();
        for (var sourceIndex = 0; sourceIndex < 160; sourceIndex++)
        {
            var name = $"source-{sourceIndex:D3}";
            var count = sourceIndex < 39 ? 4 : 3;
            var files = Enumerable.Range(0, count).Select(fileIndex =>
            {
                var suffix = fileIndex == 0 ? ".dsc" : $".part-{fileIndex}.tar.xz";
                var path = $"pool/main/s/{name}/{name}_1.0.0{suffix}";
                var bytes = Encoding.UTF8.GetBytes($"{name}:{fileIndex}");
                TestOperatingSystemSourceFiles.Add(path, bytes);
                return new WineSourcePackageFile
                {
                    Path = path,
                    Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    SizeBytes = bytes.Length
                };
            }).ToArray();
            sourcePackages.Add(CreateSourcePackage(name, "1.0.0", "main", files));
        }
        var winePackageFiles = TestWineSourceFiles.Select(pair =>
        {
            var path = "pool/universe/w/wine/" + pair.Key;
            TestOperatingSystemSourceFiles.Add(path, pair.Value);
            return new WineSourcePackageFile
            {
                Path = path,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(pair.Value)),
                SizeBytes = pair.Value.Length
            };
        }).OrderBy(static file => file.Path, StringComparer.Ordinal).ToArray();
        sourcePackages.Add(CreateSourcePackage("wine", wineVersion, "universe", winePackageFiles));
        var xorgFiles = Enumerable.Range(0, 4).Select(fileIndex =>
        {
            var suffix = fileIndex == 0 ? ".dsc" : $".part-{fileIndex}.tar.xz";
            var path = $"pool/main/x/xorg-server/xorg-server_21.1.12-1ubuntu1.6{suffix}";
            var bytes = Encoding.UTF8.GetBytes($"xorg:{fileIndex}");
            TestOperatingSystemSourceFiles.Add(path, bytes);
            return new WineSourcePackageFile
            {
                Path = path,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                SizeBytes = bytes.Length
            };
        }).ToArray();
        sourcePackages.Add(CreateSourcePackage("xorg-server", "2:21.1.12-1ubuntu1.6", "main", xorgFiles));
        var sortedSources = sourcePackages.OrderBy(static package => package.Name, StringComparer.Ordinal).ToArray();

        var packageIdentities = new[] { (Name: "fonts-wine", Version: wineVersion) }
            .Concat(Enumerable.Range(0, 224).Select(index => (Name: $"package-{index:D3}", Version: "1.0.0")))
            .Append((Name: "wine", Version: wineVersion))
            .Append((Name: "wine64", Version: wineVersion))
            .Append((Name: "xvfb", Version: "2:21.1.12-1ubuntu1.6"))
            .ToArray();
        var resolvedPackages = packageIdentities.Select((identity, index) =>
        {
            var source = identity.Name is "fonts-wine" or "wine" or "wine64"
                ? sortedSources.Single(static package => package.Name == "wine")
                : identity.Name == "xvfb"
                    ? sortedSources.Single(static package => package.Name == "xorg-server")
                    : sortedSources[index % 160];
            var binaryBytes = Encoding.UTF8.GetBytes($"{identity.Name}@{identity.Version}");
            return new WineResolvedPackage
            {
                Name = identity.Name,
                Version = identity.Version,
                Architecture = "amd64",
                ArchiveSnapshotId = "20260810T000000Z",
                ArchiveSuite = "noble",
                ArchiveComponent = "main",
                ArchiveIndexPath = "main/binary-amd64/Packages.gz",
                Path = $"pool/main/t/test/{identity.Name}_{identity.Version.Replace(":", "", StringComparison.Ordinal)}_amd64.deb",
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(binaryBytes)),
                SizeBytes = binaryBytes.Length,
                SourcePackage = source.Name,
                SourceVersion = source.Version,
                CopyrightPath = "/usr/share/doc/test/copyright",
                CopyrightSha256 = Convert.ToHexStringLower(SHA256.HashData("test copyright notice"u8.ToArray())),
                CopyrightSizeBytes = "test copyright notice"u8.Length
            };
        }).ToArray();
        var resolvedBytes = Encoding.UTF8.GetBytes(string.Join(
            '\n',
            resolvedPackages.Select(static package => $"{package.Name}={package.Version}")) + "\n");
        var wineSource = sourceFiles[0];
        var xorgSource = xorgFiles.Single(static file => file.Path.EndsWith(".dsc", StringComparison.Ordinal));
        return new WineRuntimePackageManifest
        {
            SchemaVersion = 1,
            Platform = "linux/amd64",
            BaseImageId = "dotnet-runtime-deps",
            Component = new WineRuntimePackageComponent
            {
                Id = "wine-coreclr-userspace",
                Kind = "runtime-dependency",
                ResolvedVersion = "wine-9.0~repack-4build3+xvfb-2:21.1.12-1ubuntu1.6",
                License = "LGPL-2.1+",
                SourceUri = snapshotUri
            },
            ArchiveSnapshots = CreateTestArchiveSnapshots(),
            DirectPackages =
            [
                new WineDirectPackage
                {
                    Name = "wine",
                    Version = wineVersion,
                    Architecture = "all",
                    Path = "pool/universe/w/wine/wine_test_all.deb",
                    Sha256 = wineSource.Sha256,
                    SourcePackage = "wine",
                    License = "LGPL-2.1+",
                    SourceUri = baseUri + wineSource.Path,
                    SourceSha256 = wineSource.Sha256,
                    SourceSizeBytes = wineSource.SizeBytes
                },
                new WineDirectPackage
                {
                    Name = "wine64",
                    Version = wineVersion,
                    Architecture = "amd64",
                    Path = "pool/universe/w/wine/wine64_test_amd64.deb",
                    Sha256 = wineSource.Sha256,
                    SourcePackage = "wine",
                    License = "LGPL-2.1+",
                    SourceUri = baseUri + wineSource.Path,
                    SourceSha256 = wineSource.Sha256,
                    SourceSizeBytes = wineSource.SizeBytes
                },
                new WineDirectPackage
                {
                    Name = "fonts-wine",
                    Version = wineVersion,
                    Architecture = "all",
                    Path = "pool/universe/w/wine/fonts-wine_test_all.deb",
                    Sha256 = wineSource.Sha256,
                    SourcePackage = "wine",
                    License = "LGPL-2.1+",
                    SourceUri = baseUri + wineSource.Path,
                    SourceSha256 = wineSource.Sha256,
                    SourceSizeBytes = wineSource.SizeBytes
                },
                new WineDirectPackage
                {
                    Name = "xvfb",
                    Version = "2:21.1.12-1ubuntu1.6",
                    Architecture = "amd64",
                    Path = "pool/universe/x/xorg-server/xvfb_test_amd64.deb",
                    Sha256 = xorgSource.Sha256,
                    SourcePackage = "xorg-server",
                    License = "MIT",
                    SourceUri = snapshotUri + xorgSource.Path,
                    SourceSha256 = xorgSource.Sha256,
                    SourceSizeBytes = xorgSource.SizeBytes
                }
            ],
            ResolvedPackages = resolvedPackages,
            SourcePackages = sortedSources,
            ResolvedPackageListSha256 = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(resolvedBytes))}",
            SourceOffer = new WineSourceOffer
            {
                BaseUri = baseUri,
                Package = "wine",
                Version = wineVersion,
                License = "LGPL-2.1+",
                Files = sourceFiles
            },
            NoticeArchive = new WineNoticeArchive
            {
                ImagePath = WineRuntimePackageManifestLoader.RequiredNoticeArchiveImagePath,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(TestWineNoticeArchive)),
                SizeBytes = TestWineNoticeArchive.Length,
                EntryCount = 1
            }
        };

        static WineSourcePackage CreateSourcePackage(
            string name,
            string version,
            string component,
            IReadOnlyList<WineSourcePackageFile> files) => new()
        {
            Name = name,
            Version = version,
            ArchiveSnapshotId = "20260810T000000Z",
            ArchiveSuite = "noble",
            ArchiveComponent = component,
            ArchiveIndexPath = $"{component}/source/Sources.gz",
            Files = files
        };
    }

    private static IReadOnlyList<WineArchiveSnapshot> CreateTestArchiveSnapshots()
    {
        const string fingerprint = "F6ECB3762474EDA9D21B7022871920D1991BC93C";
        return
        [
            new WineArchiveSnapshot
            {
                Purpose = "operator-installation",
                Id = "20260810T000000Z",
                Uri = "https://snapshot.ubuntu.com/ubuntu/20260810T000000Z/",
                Suites =
                [
                    Suite("noble", "cdb2f31d809f589719a53c6ad15f255b27569c4059542ada282aaa21b8e164b0", 255_850),
                    Suite("noble-updates", "ef81441269d3a8bdd8cdfe9095de7deb7f1af70d42191f61f1af3c8fb72cfb32", 126_125),
                    Suite("noble-security", "3cfb1c8d7499c0bac1bfbe1e32675d200f0ca74b18afc4248c45325a073d0fd0", 126_127)
                ]
            },
            new WineArchiveSnapshot
            {
                Purpose = "base-image-package-evidence",
                Id = "20260610T000000Z",
                Uri = "https://snapshot.ubuntu.com/ubuntu/20260610T000000Z/",
                Suites =
                [
                    Suite("noble-updates", "f51355c88d0b337b45cede930d215a56f806b7c9339e95487b6600ea02c728ce", 126_125)
                ]
            }
        ];

        static WineArchiveSnapshotSuite Suite(string name, string sha256, long sizeBytes) => new()
        {
            Name = name,
            InReleaseSha256 = sha256,
            InReleaseSizeBytes = sizeBytes,
            SigningKeyFingerprint = fingerprint,
            Indexes =
            [
                new WineArchiveIndex
                {
                    Kind = "binary",
                    Component = "main",
                    Architecture = "amd64",
                    Path = "main/binary-amd64/Packages.gz",
                    Sha256 = new string('a', 64),
                    SizeBytes = 1
                },
                new WineArchiveIndex
                {
                    Kind = "source",
                    Component = "main",
                    Path = "main/source/Sources.gz",
                    Sha256 = new string('b', 64),
                    SizeBytes = 1
                },
                new WineArchiveIndex
                {
                    Kind = "source",
                    Component = "universe",
                    Path = "universe/source/Sources.gz",
                    Sha256 = new string('c', 64),
                    SizeBytes = 1
                }
            ]
        };
    }

    private static byte[] CreateTestWineNoticeArchive()
    {
        using var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, TarEntryFormat.Ustar, leaveOpen: true))
        {
            var entry = new UstarTarEntry(TarEntryType.RegularFile, "usr/share/doc/test/copyright")
            {
                DataStream = new MemoryStream("test copyright notice"u8.ToArray(), writable: false),
                ModificationTime = DateTimeOffset.UnixEpoch,
                Uid = 0,
                Gid = 0,
                UserName = string.Empty,
                GroupName = string.Empty
            };
            writer.WriteEntry(entry);
        }
        return stream.ToArray();
    }

    private static async Task<string> CreateDependencyInventoryRepositoryAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sharplabnext-dependency-inventory-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(root, "src", "reviewed-source.json");
        var inventoryPath = Path.Combine(root, "deploy", "security", "inventory.json");
        var licensePath = Path.Combine(root, "deploy", "security", "licenses", "reviewed-source-Apache-2.0.txt");
        var policyPath = Path.Combine(root, "profiles", "license-policy.json");
        var packageLockPath = Path.Combine(root, "frontend", "package-lock.json");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(inventoryPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(licensePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(policyPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(packageLockPath)!);

        const string source = "reviewed dependency source";
        var sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        await File.WriteAllTextAsync(sourcePath, source, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            licensePath,
            $"Apache License\nEND OF TERMS AND CONDITIONS\n{new string('x', 10_000)}",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            inventoryPath,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                components = new[]
                {
                    new
                    {
                        id = "test-reviewed-source",
                        packageManager = "github",
                        name = "test/reviewed-source",
                        version = "v1.0.0",
                        commit = new string('a', 40),
                        sourceUri = "https://example.test/reviewed-source.json",
                        sourcePath = "src/reviewed-source.json",
                        sha256 = sourceSha256,
                        license = "Apache-2.0",
                        licensePath = "deploy/security/licenses/reviewed-source-Apache-2.0.txt",
                        selectedBy = new
                        {
                            name = "test/selector",
                            version = "v1.0.0",
                            commit = new string('b', 40)
                        }
                    }
                }
            }),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            policyPath,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                allowedLicenses = DependencyInventoryAllowedLicenses,
                licenseAliases = new Dictionary<string, string>(),
                overrides = new Dictionary<string, string>
                {
                    ["nuget:Default.Only.Package@1.0.0"] = "MIT",
                    ["nuget:Named.Project.Package@1.0.0"] = "MIT",
                    ["nuget:Source.Only.Package@1.0.0"] = "MIT",
                    ["nuget:Shared.Package@1.0.0"] = "MIT"
                },
                deniedPrefixes = DependencyInventoryDeniedPrefixes
            }),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            packageLockPath,
            JsonSerializer.Serialize(new
            {
                lockfileVersion = 3,
                packages = new Dictionary<string, object>
                {
                    [string.Empty] = new { }
                }
            }),
            TestContext.Current.CancellationToken);
        File.Copy(
            Path.Combine(FindRepositoryRoot(), "profiles", "runtime-wine-packages.json"),
            Path.Combine(root, "profiles", "runtime-wine-packages.json"));
        return root;
    }

    private static string CreateNuGetLock(params string[] packageNames)
    {
        var packages = packageNames.ToDictionary(
            static name => name,
            static _ => new
            {
                type = "Direct",
                requested = "[1.0.0, )",
                resolved = "1.0.0",
                contentHash = "sha512-test"
            },
            StringComparer.Ordinal);
        return JsonSerializer.Serialize(new
        {
            version = 1,
            dependencies = new Dictionary<string, object>
            {
                ["net10.0"] = packages
            }
        });
    }

    private static async Task BuildBundleForReleaseAsync(
        string repositoryRoot,
        string testRoot,
        string output,
        string releaseId)
    {
        var catalogPath = Path.Combine(testRoot, $"catalog-{releaseId}.json");
        var lockPath = Path.Combine(testRoot, $"lock-{releaseId}.json");
        var catalog = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "profiles", "catalog", "catalog.json"),
            TestContext.Current.CancellationToken))
            ?? throw new InvalidOperationException("The test Catalog is empty.");
        var releaseLock = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "profiles", "lock.json"),
            TestContext.Current.CancellationToken))
            ?? throw new InvalidOperationException("The test release lock is empty.");
        catalog["releaseId"] = releaseId;
        releaseLock["releaseId"] = releaseId;
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(
            catalogPath,
            catalog.ToJsonString(jsonOptions),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            lockPath,
            releaseLock.ToJsonString(jsonOptions),
            TestContext.Current.CancellationToken);
        var command = new BundleBuilderCommand(
            repositoryRoot,
            catalogPath,
            lockPath,
            Path.Combine(repositoryRoot, "deploy", "images.json"),
            Path.Combine(repositoryRoot, "profiles", "license-policy.json"),
            Path.Combine(repositoryRoot, "deploy", "compose.prod.yaml"),
            Path.Combine(repositoryRoot, "THIRD-PARTY-NOTICES.md"),
            output,
            "docker",
            "openssl",
            null,
            null,
            null,
            null,
            MetadataOnly: false,
            new Dictionary<string, string>());
        await CreateBuilder(new FakeDockerCli(releaseLabelOverride: releaseId)).BuildAsync(
            command,
            TestContext.Current.CancellationToken);
    }

    private static async Task<bool> HasPosixVerifierPrerequisitesAsync()
    {
        foreach (var command in new[] { "sh", "jq", "openssl", "sha256sum" })
        {
            try
            {
                var result = await RunAsync(command, ["--version"], new Dictionary<string, string>());
                if (result.ExitCode != 0 && command != "sh")
                    return false;
            }
            catch (Win32Exception)
            {
                return false;
            }
        }

        return true;
    }

    private static async Task CreateRechecksummedPlanSignatureMutationAsync(string bundle)
    {
        const string signaturePath = "profiles/runtime-promotion-plans/dotnet-10-linux-x64.json.sig";
        var sourcePath = Path.Combine(
            bundle,
            ReleaseBundleBuilder.PromotionEvidenceDirectoryName,
            "source",
            signaturePath.Replace('/', Path.DirectorySeparatorChar));
        var signature = await File.ReadAllBytesAsync(sourcePath, TestContext.Current.CancellationToken);
        signature[0] = signature[0] == (byte)'A' ? (byte)'B' : (byte)'A';
        await ReplacePromotionEvidenceSourcesAndRechecksumAsync(bundle, await UpdatePlanTrustChainAsync(
            bundle,
            planBytes: null,
            signature));
    }

    private static async Task CreateRechecksummedPlanFamilyMutationAsync(string bundle)
    {
        const string planPath = "profiles/runtime-promotion-plans/dotnet-10-linux-x64.json";
        var sourcePath = Path.Combine(
            bundle,
            ReleaseBundleBuilder.PromotionEvidenceDirectoryName,
            "source",
            planPath.Replace('/', Path.DirectorySeparatorChar));
        var plan = JsonNode.Parse(await File.ReadAllBytesAsync(sourcePath, TestContext.Current.CancellationToken))?.AsObject()
            ?? throw new InvalidOperationException("Promotion plan fixture is invalid.");
        plan["family"] = "coreclr-wine";
        var planBytes = Encoding.UTF8.GetBytes(
            (plan.ToJsonString() + "\n").ReplaceLineEndings("\n"));
        await ReplacePromotionEvidenceSourcesAndRechecksumAsync(bundle, await UpdatePlanTrustChainAsync(
            bundle,
            planBytes,
            SignPromotionFixturePlan(planBytes)));
    }

    private static byte[] SignPromotionFixturePlan(byte[] bytes)
    {
        var key = new Ed25519PrivateKeyParameters(
            Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray(), 0);
        var signer = new Ed25519Signer();
        signer.Init(true, key);
        signer.BlockUpdate(bytes, 0, bytes.Length);
        return Encoding.ASCII.GetBytes(Convert.ToBase64String(signer.GenerateSignature()) + "\n");
    }

    private static async Task<IReadOnlyDictionary<string, byte[]>> UpdatePlanTrustChainAsync(
        string bundle,
        byte[]? planBytes,
        byte[] signatureBytes)
    {
        const string planPath = "profiles/runtime-promotion-plans/dotnet-10-linux-x64.json";
        const string signaturePath = planPath + ".sig";
        const string receiptPath = "profiles/runtime-promotion-receipts/dotnet-10-linux-x64.json";
        const string activePath = "profiles/runtimes/dotnet-10-linux-x64.json";
        var sourceRoot = Path.Combine(bundle, ReleaseBundleBuilder.PromotionEvidenceDirectoryName, "source");
        if (planBytes is null)
        {
            planBytes = await File.ReadAllBytesAsync(
                Path.Combine(sourceRoot, planPath.Replace('/', Path.DirectorySeparatorChar)),
                TestContext.Current.CancellationToken);
        }

        var receiptFile = Path.Combine(sourceRoot, receiptPath.Replace('/', Path.DirectorySeparatorChar));
        var receipt = JsonNode.Parse(await File.ReadAllBytesAsync(receiptFile, TestContext.Current.CancellationToken))?.AsObject()
            ?? throw new InvalidOperationException("Promotion receipt fixture is invalid.");
        receipt["planSha256"] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(planBytes));
        receipt["planSignature"]!["sha256"] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(signatureBytes));
        var receiptBytes = Encoding.UTF8.GetBytes(
            (receipt.ToJsonString() + "\n").ReplaceLineEndings("\n"));

        var activeFile = Path.Combine(sourceRoot, activePath.Replace('/', Path.DirectorySeparatorChar));
        var active = JsonNode.Parse(await File.ReadAllBytesAsync(activeFile, TestContext.Current.CancellationToken))?.AsObject()
            ?? throw new InvalidOperationException("Promotion active profile fixture is invalid.");
        active["promotionReceipt"]!["sha256"] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(receiptBytes));
        var activeBytes = Encoding.UTF8.GetBytes(
            (active.ToJsonString() + "\n").ReplaceLineEndings("\n"));

        return new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [planPath] = planBytes,
            [signaturePath] = signatureBytes,
            [receiptPath] = receiptBytes,
            [activePath] = activeBytes
        };
    }

    private static async Task ReplacePromotionEvidenceSourcesAndRechecksumAsync(
        string bundle,
        IReadOnlyDictionary<string, byte[]> replacements)
    {
        var promotionRoot = Path.Combine(bundle, ReleaseBundleBuilder.PromotionEvidenceDirectoryName);
        var sourceRoot = Path.Combine(promotionRoot, "source");
        var manifestPath = Path.Combine(promotionRoot, "manifest.json");
        var manifest = JsonNode.Parse(await File.ReadAllBytesAsync(manifestPath, TestContext.Current.CancellationToken))?.AsObject()
            ?? throw new InvalidOperationException("Promotion evidence fixture manifest is invalid.");
        foreach (var (relativePath, bytes) in replacements)
        {
            var destination = Path.Combine(sourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            await File.WriteAllBytesAsync(destination, bytes, TestContext.Current.CancellationToken);
            var entry = manifest["entries"]?.AsArray().Select(static item => item!.AsObject()).SingleOrDefault(item =>
                StringComparer.Ordinal.Equals(item["sourcePath"]?.GetValue<string>(), relativePath))
                ?? throw new InvalidOperationException($"Promotion evidence fixture has no '{relativePath}' entry.");
            entry["sha256"] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
            entry["sizeBytes"] = bytes.LongLength;
        }

        var manifestBytes = Encoding.UTF8.GetBytes(
            (manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n").ReplaceLineEndings("\n"));
        await File.WriteAllBytesAsync(manifestPath, manifestBytes, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(promotionRoot, "manifest.tsv"),
            CreatePromotionEvidenceVerificationManifest(manifest, manifestBytes),
            TestContext.Current.CancellationToken);
        await ReleaseBundleBuilder.WriteChecksumsAsync(bundle, TestContext.Current.CancellationToken);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static async Task WriteExecutableAsync(string path, string contents)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }
        await File.WriteAllTextAsync(path, contents.ReplaceLineEndings("\n"), TestContext.Current.CancellationToken);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var pair in environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var outputTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }

    private static string CreatePromotionEvidenceVerificationManifest(
        JsonObject manifest,
        byte[] manifestBytes)
    {
        static string JoinIds(JsonObject entry, string propertyName)
        {
            var ids = entry[propertyName]?.AsArray()
                ?? throw new InvalidOperationException($"Promotion evidence entry has no '{propertyName}'.");
            return ids.Count == 0
                ? "-"
                : string.Join(',', ids.Select(static id => id?.GetValue<string>()
                    ?? throw new InvalidOperationException("Promotion evidence ID is invalid.")));
        }

        var runtimeIds = manifest["promotedRuntimeIds"]?.AsArray()
            ?? throw new InvalidOperationException("Promotion evidence manifest has no runtime IDs.");
        var lines = new List<string>
        {
            "schemaVersion\t" + manifest["schemaVersion"]?.GetValue<int>().ToString(System.Globalization.CultureInfo.InvariantCulture),
            "buildSourceRevision\t" + manifest["buildSourceRevision"]?.GetValue<string>(),
            "releaseSourceRevision\t" + manifest["releaseSourceRevision"]?.GetValue<string>(),
            "manifestJsonSha256\tsha256:" + Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant(),
            "promotedRuntimeIds\t" + string.Join(',', runtimeIds.Select(static id => id?.GetValue<string>()
                ?? throw new InvalidOperationException("Promotion evidence runtime ID is invalid.")))
        };
        foreach (var entryNode in manifest["entries"]?.AsArray()
                     ?? throw new InvalidOperationException("Promotion evidence manifest has no entries."))
        {
            var entry = entryNode?.AsObject()
                ?? throw new InvalidOperationException("Promotion evidence entry is invalid.");
            lines.Add(string.Join(
                '\t',
                "entry",
                entry["kind"]?.GetValue<string>(),
                JoinIds(entry, "profileIds"),
                JoinIds(entry, "runtimeIds"),
                entry["sourcePath"]?.GetValue<string>(),
                entry["bundlePath"]?.GetValue<string>(),
                entry["sha256"]?.GetValue<string>(),
                entry["sizeBytes"]?.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        return string.Join('\n', lines) + "\n";
    }

    private static string ReadPointer(string installRoot, string name) =>
        File.ReadAllText(Path.Combine(installRoot, name)).Trim();

    private static RuntimeProfileDefinition CloneRuntimeProfile(RuntimeProfileDefinition profile) =>
        JsonSerializer.Deserialize<RuntimeProfileDefinition>(
            JsonSerializer.Serialize(profile, RuntimeProfileFixtureJsonOptions),
            RuntimeProfileFixtureJsonOptions)
        ?? throw new InvalidOperationException("The runtime profile fixture could not be cloned.");

    private static void AssertToolchainWorkerIdentityOverlay(
        string compose,
        string expectedReleaseId,
        string composeService,
        string releaseIdEnvironment,
        string workerImageIdEnvironment,
        string workerId,
        string expectedImageId,
        params string[] unexpectedImageIds)
    {
        var workerBlock = GetComposeServiceBlock(compose, composeService);
        Assert.Contains($"    image: \"{expectedImageId}\"", workerBlock, StringComparison.Ordinal);
        Assert.Contains($"      {releaseIdEnvironment}: \"{expectedReleaseId}\"", workerBlock, StringComparison.Ordinal);
        Assert.Contains($"      {workerImageIdEnvironment}: \"{expectedImageId}\"", workerBlock, StringComparison.Ordinal);

        var gatewayBlock = GetComposeServiceBlock(compose, "gateway");
        var gatewayImageIdEnvironment =
            $"Services__LanguageWorkers__{workerId}__ExpectedWorkerImageId";
        Assert.Contains(
            $"      {gatewayImageIdEnvironment}: \"{expectedImageId}\"",
            gatewayBlock,
            StringComparison.Ordinal);
        foreach (var unexpectedImageId in unexpectedImageIds)
        {
            Assert.DoesNotContain(unexpectedImageId, workerBlock, StringComparison.Ordinal);
            Assert.DoesNotContain(
                $"      {gatewayImageIdEnvironment}: \"{unexpectedImageId}\"",
                gatewayBlock,
                StringComparison.Ordinal);
        }
    }

    private static void AssertArtifactWorkerIdentityOverlay(
        string compose,
        string expectedReleaseId,
        string composeService,
        string releaseIdEnvironment,
        string workerImageIdEnvironment,
        string artifactWorkerId,
        string expectedImageId,
        params string[] unexpectedImageIds)
    {
        var workerBlock = GetComposeServiceBlock(compose, composeService);
        Assert.Contains($"    image: \"{expectedImageId}\"", workerBlock, StringComparison.Ordinal);
        Assert.Contains($"      {releaseIdEnvironment}: \"{expectedReleaseId}\"", workerBlock, StringComparison.Ordinal);
        Assert.Contains($"      {workerImageIdEnvironment}: \"{expectedImageId}\"", workerBlock, StringComparison.Ordinal);
        foreach (var unexpectedImageId in unexpectedImageIds)
        {
            Assert.DoesNotContain(unexpectedImageId, workerBlock, StringComparison.Ordinal);
        }

        var gatewayBlock = GetComposeServiceBlock(compose, "gateway");
        var gatewayImageIdEnvironment =
            $"Services__ArtifactWorkers__{artifactWorkerId}__ExpectedWorkerImageId";
        Assert.Contains(
            $"      {gatewayImageIdEnvironment}: \"{expectedImageId}\"",
            gatewayBlock,
            StringComparison.Ordinal);
        foreach (var unexpectedImageId in unexpectedImageIds)
        {
            Assert.DoesNotContain(
                $"      {gatewayImageIdEnvironment}: \"{unexpectedImageId}\"",
                gatewayBlock,
                StringComparison.Ordinal);
        }
    }

    private static string GetComposeServiceBlock(string compose, string composeService)
    {
        var lines = compose.Split('\n');
        var header = $"  {composeService}:";
        var start = Array.IndexOf(lines, header);
        Assert.True(start >= 0, $"Generated Compose does not contain service '{composeService}'.");
        if (start < 0)
        {
            return string.Empty;
        }

        var end = Array.FindIndex(
            lines,
            start + 1,
            static line => line.StartsWith("  ", StringComparison.Ordinal) &&
                           !line.StartsWith("    ", StringComparison.Ordinal));
        return string.Join('\n', lines[start..(end < 0 ? lines.Length : end)]);
    }

    private static Dictionary<string, string?> ReadComposeEnvironment(
        string compose,
        string composeService)
    {
        var block = GetComposeServiceBlock(compose, composeService);
        var lines = block.Split('\n');
        var environmentStart = Array.IndexOf(lines, "    environment:");
        Assert.True(environmentStart >= 0, $"Compose service '{composeService}' has no environment block.");
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var line in lines[(environmentStart + 1)..])
        {
            if (!line.StartsWith("      ", StringComparison.Ordinal))
                break;
            var entry = line[6..];
            var separator = entry.IndexOf(": ", StringComparison.Ordinal);
            Assert.True(separator > 0, $"Generated environment entry '{entry}' is invalid.");
            var value = entry[(separator + 2)..];
            Assert.StartsWith("\"", value, StringComparison.Ordinal);
            Assert.EndsWith("\"", value, StringComparison.Ordinal);
            result.Add(
                entry[..separator].Replace(
                    "__",
                    ConfigurationPath.KeyDelimiter,
                    StringComparison.Ordinal),
                value[1..^1]
                    .Replace("\\n", "\n", StringComparison.Ordinal)
                    .Replace("\\\"", "\"", StringComparison.Ordinal)
                    .Replace("\\\\", "\\", StringComparison.Ordinal));
        }
        return result;
    }

    private static ReleaseBundleBuilder CreateBuilder(IDockerCli docker, IBundleSigner? signer = null) =>
        new(
            docker,
            signer,
            new FakeRepositorySourceInspector(
                new RepositorySourceState(true, TestSourceRevision, false)),
            new FakeRuntimePromotionSourceInspector(),
            new FakeExternalSourceMaterialFetcher(),
            new TestWineRuntimePackageManifestSnapshotProvider(),
            RuntimePromotionTrustTests.CreateTestPlanVerifier());

    private sealed class TestWineRuntimePackageManifestSnapshotProvider
        : IWineRuntimePackageManifestSnapshotProvider
    {
        public Task<WineRuntimePackageManifestSnapshot> LoadValidatedAsync(
            string repositoryRoot,
            ReleaseLockDocument releaseLock,
            CancellationToken cancellationToken)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                TestWineManifest,
                RuntimeProfileFixtureJsonOptions);
            return Task.FromResult(new WineRuntimePackageManifestSnapshot(
                TestWineManifest,
                $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}",
                bytes));
        }
    }

    private static readonly Lazy<PromotionFixtureData> PromotionFixtures =
        new(PromotionFixtureData.Load);

    private sealed record PromotionFixtureImage(
        string RuntimeId,
        string Reference,
        string ImageId,
        long SizeBytes,
        string BuildSourceRevision,
        string RuntimeCommit,
        string JitCommit);

    private sealed record PromotionFixtureHelper(
        string Reference,
        string ImageId,
        long SizeBytes,
        string BuildSourceRevision);

    private sealed record PromotionFixtureFile(
        string ImageId,
        string Path,
        DockerImageFileInspection Inspection);

    private sealed class PromotionFixtureData(
        string repositoryRoot,
        IReadOnlyDictionary<string, PromotionFixtureImage> imagesByReference,
        IReadOnlyDictionary<string, PromotionFixtureImage> imagesById,
        IReadOnlyDictionary<string, PromotionFixtureHelper> helpersByReference,
        IReadOnlyDictionary<string, PromotionFixtureFile> files,
        IReadOnlyList<string> sourceClosurePaths)
    {
        public string RepositoryRoot { get; } = repositoryRoot;
        public IReadOnlyDictionary<string, PromotionFixtureImage> ImagesByReference { get; } = imagesByReference;
        public IReadOnlyDictionary<string, PromotionFixtureImage> ImagesById { get; } = imagesById;
        public IReadOnlyDictionary<string, PromotionFixtureHelper> HelpersByReference { get; } = helpersByReference;
        public IReadOnlyDictionary<string, PromotionFixtureFile> Files { get; } = files;
        public IReadOnlyList<string> SourceClosurePaths { get; } = sourceClosurePaths;

        public static PromotionFixtureData Load()
        {
            var sourceRoot = FindSourceRepositoryRoot();
            var root = Path.Combine(Path.GetTempPath(), $"sharplabnext-bundle-promotion-{Guid.NewGuid():N}");
            CopyRepositorySource(sourceRoot, root);
            CopyRequiredNpmSourceMaterial(sourceRoot, root);
            InstallCompletedPromotion(root);
            var deployment = JsonSerializer.Deserialize<DeploymentImageManifest>(
                File.ReadAllText(Path.Combine(root, "deploy", "images.json")),
                RuntimeProfileFixtureJsonOptions)
                ?? throw new InvalidOperationException("Deployment image fixture is invalid.");
            var imagesByReference = new Dictionary<string, PromotionFixtureImage>(StringComparer.Ordinal);
            var imagesById = new Dictionary<string, PromotionFixtureImage>(StringComparer.Ordinal);
            var helpersByReference = new Dictionary<string, PromotionFixtureHelper>(StringComparer.Ordinal);
            var files = new Dictionary<string, PromotionFixtureFile>(StringComparer.Ordinal);
            var closurePaths = new HashSet<string>(StringComparer.Ordinal)
            {
                "deploy/images.json",
                "profiles/catalog/catalog.json",
                "profiles/lock.json",
                "profiles/runtime-matrix.json"
            };

            foreach (var definition in deployment.Images.Where(static image => image.ImmutableReference is not null))
            {
                var runtimeId = definition.RuntimeId
                    ?? throw new InvalidOperationException("Promotion fixture contains a non-runtime immutable image.");
                using var profile = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                    root,
                    "profiles",
                    "runtimes",
                    runtimeId + ".json")));
                var receiptPath = profile.RootElement
                    .GetProperty("promotionReceipt")
                    .GetProperty("path")
                    .GetString()
                    ?? throw new InvalidOperationException("Promotion fixture profile has no receipt path.");
                using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                    root,
                    receiptPath.Replace('/', Path.DirectorySeparatorChar))));
                var receiptImage = receipt.RootElement.GetProperty("image");
                var identity = new PromotionFixtureImage(
                    runtimeId,
                    receiptImage.GetProperty("reference").GetString()
                        ?? throw new InvalidOperationException("Promotion receipt has no image reference."),
                    receiptImage.GetProperty("imageId").GetString()
                        ?? throw new InvalidOperationException("Promotion receipt has no image ID."),
                    receiptImage.GetProperty("sizeBytes").GetInt64(),
                    receipt.RootElement.GetProperty("sourceRevision").GetString()
                        ?? throw new InvalidOperationException("Promotion receipt has no source revision."),
                    receipt.RootElement.GetProperty("runtimeIdentity").GetProperty("runtimeCommit").GetString()
                        ?? throw new InvalidOperationException("Promotion receipt has no runtime commit."),
                    receipt.RootElement.GetProperty("runtimeIdentity").GetProperty("jitCommit").GetString()
                        ?? throw new InvalidOperationException("Promotion receipt has no JIT commit."));
                if (!StringComparer.Ordinal.Equals(identity.Reference, definition.ImmutableReference))
                    throw new InvalidOperationException("Promotion fixture deployment and receipt references differ.");
                imagesByReference.Add(identity.Reference, identity);
                imagesById.Add(identity.ImageId, identity);
                closurePaths.Add(receiptPath);
                closurePaths.Add($"profiles/runtimes/{runtimeId}.json");
                closurePaths.Add($"profiles/runtime-promotion-plans/{runtimeId}.json");
                closurePaths.Add($"profiles/runtime-promotion-plans/{runtimeId}.json.sig");
                closurePaths.Add($"profiles/runtime-promotion-plans/{runtimeId}.profile.json");

                if (receipt.RootElement.TryGetProperty("wineOperator", out var wineOperator))
                {
                    closurePaths.Add(wineOperator.GetProperty("receiptPath").GetString()
                        ?? throw new InvalidOperationException("Promotion Wine operator binding has no receipt path."));
                    closurePaths.Add(wineOperator.GetProperty("signaturePath").GetString()
                        ?? throw new InvalidOperationException("Promotion Wine operator binding has no signature path."));
                }

                foreach (var check in receipt.RootElement.GetProperty("checks").EnumerateArray())
                {
                    var evidencePath = check.GetProperty("evidencePath").GetString()
                        ?? throw new InvalidOperationException("Promotion check has no evidence path.");
                    closurePaths.Add(evidencePath);
                    AddEvidenceArtifacts(root, evidencePath, identity.ImageId, files);
                }

                var performancePath = receipt.RootElement.GetProperty("performance")
                    .GetProperty("evidencePath")
                    .GetString()
                    ?? throw new InvalidOperationException("Promotion performance binding has no evidence path.");
                closurePaths.Add(performancePath);
                using var performance = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                    root,
                    performancePath.Replace('/', Path.DirectorySeparatorChar))));
                var helperImage = performance.RootElement.GetProperty("measurementHelper").GetProperty("image");
                var helper = new PromotionFixtureHelper(
                    helperImage.GetProperty("reference").GetString()
                        ?? throw new InvalidOperationException("Promotion measurement helper has no reference."),
                    helperImage.GetProperty("imageId").GetString()
                        ?? throw new InvalidOperationException("Promotion measurement helper has no image ID."),
                    helperImage.GetProperty("sizeBytes").GetInt64(),
                    performance.RootElement.GetProperty("measurementHelper").GetProperty("sourceRevision").GetString()
                        ?? throw new InvalidOperationException("Promotion measurement helper has no source revision."));
                helpersByReference.TryAdd(helper.Reference, helper);
            }

            return new PromotionFixtureData(
                root,
                imagesByReference,
                imagesById,
                helpersByReference,
                files,
                closurePaths.OrderBy(static path => path, StringComparer.Ordinal).ToArray());
        }

        private static void InstallCompletedPromotion(string root)
        {
            var profilePath = Path.Combine(root, "profiles", "runtimes", "dotnet-10-linux-x64.json");
            var template = JsonSerializer.Deserialize<RuntimeProfileDefinition>(
                File.ReadAllBytes(profilePath),
                RuntimeProfileFixtureJsonOptions)
                ?? throw new InvalidOperationException("Bundle promotion fixture profile is invalid.");
            var releaseLock = JsonSerializer.Deserialize<ReleaseLockDocument>(
                File.ReadAllBytes(Path.Combine(root, "profiles", "lock.json")),
                RuntimeProfileFixtureJsonOptions)
                ?? throw new InvalidOperationException("Bundle promotion fixture release lock is invalid.");
            using var promotion = new RuntimePromotionTrustTests.PromotionFixture(
                profileTemplate: template,
                componentTemplate: releaseLock.Components[template.Id]);
            promotion.ExportCompletedPromotionMaterial(root);

            var profile = JsonNode.Parse(File.ReadAllBytes(profilePath))!.AsObject();
            var receipt = profile["promotionReceipt"]!.AsObject();
            var immutableReference = profile["image"]!.GetValue<string>();
            var runtimeImageId = profile["runtimeImageId"]!.GetValue<string>();

            var deploymentPath = Path.Combine(root, "deploy", "images.json");
            var deployment = JsonNode.Parse(File.ReadAllBytes(deploymentPath))!.AsObject();
            foreach (var image in deployment["images"]!.AsArray().Select(static item => item!.AsObject()))
            {
                if (image["runtimeId"]?.GetValue<string>() is not { } runtimeId)
                    continue;
                if (runtimeId == template.Id)
                {
                    image["repository"] = immutableReference[..immutableReference.LastIndexOf('@')];
                    image["immutableReference"] = immutableReference;
                }
                else
                {
                    image.Remove("immutableReference");
                }
            }
            WriteJson(deploymentPath, deployment);

            var catalogPath = Path.Combine(root, "profiles", "catalog", "catalog.json");
            var catalog = JsonNode.Parse(File.ReadAllBytes(catalogPath))!.AsObject();
            var catalogRuntime = catalog["runtimes"]!.AsArray()
                .Select(static item => item!.AsObject())
                .Single(item => StringComparer.Ordinal.Equals(
                    item["id"]!.GetValue<string>(),
                    template.Id));
            catalogRuntime["runtimeImageId"] = runtimeImageId;
            WriteJson(catalogPath, catalog);

            foreach (var runtimePath in Directory.EnumerateFiles(
                         Path.Combine(root, "profiles", "runtimes"), "*.json", SearchOption.TopDirectoryOnly))
            {
                if (StringComparer.Ordinal.Equals(runtimePath, profilePath))
                    continue;
                var runtime = JsonNode.Parse(File.ReadAllBytes(runtimePath))!.AsObject();
                runtime.Remove("promotionReceipt");
                var image = runtime["image"]?.GetValue<string>();
                if (image?.Contains("@sha256:", StringComparison.Ordinal) == true)
                    runtime["image"] = $"sharplabnext/{runtime["id"]!.GetValue<string>()}:test";
                WriteJson(runtimePath, runtime);
            }

            var matrixPath = Path.Combine(root, "profiles", "runtime-matrix.json");
            var matrix = JsonNode.Parse(File.ReadAllBytes(matrixPath))!.AsObject();
            var dotnet10 = matrix["coreClr"]!.AsArray()
                .Select(static item => item!.AsObject())
                .Single(static item => item["id"]!.GetValue<string>() == "dotnet-10");
            var capability = dotnet10["linuxCapability"]!.AsObject();
            capability["promotionState"] = "verified";
            capability.Remove("blockedReason");
            capability["promotionReceipt"] = receipt.DeepClone();
            WriteJson(matrixPath, matrix);
        }

        private static void CopyRepositorySource(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            var pending = new Stack<(string Source, string Destination)>();
            pending.Push((source, destination));
            while (pending.Count > 0)
            {
                var (currentSource, currentDestination) = pending.Pop();
                foreach (var file in Directory.EnumerateFiles(currentSource))
                {
                    File.Copy(file, Path.Combine(currentDestination, Path.GetFileName(file)));
                }
                foreach (var directory in Directory.EnumerateDirectories(currentSource))
                {
                    var name = Path.GetFileName(directory);
                    if (name is ".git" or ".tmp" or "bin" or "obj" or "node_modules" or ".vs")
                        continue;
                    var child = Path.Combine(currentDestination, name);
                    Directory.CreateDirectory(child);
                    pending.Push((directory, child));
                }
            }
        }

        private static void CopyRequiredNpmSourceMaterial(string sourceRoot, string destinationRoot)
        {
            using var packageLock = JsonDocument.Parse(File.ReadAllBytes(
                Path.Combine(sourceRoot, "frontend", "package-lock.json")));
            foreach (var package in packageLock.RootElement
                         .GetProperty("packages")
                         .EnumerateObject()
                         .Where(static property =>
                             property.Name.StartsWith("node_modules/", StringComparison.Ordinal) &&
                             property.Value.TryGetProperty("license", out var license) &&
                             license.GetString() is { } value &&
                             (value.Contains("LGPL-", StringComparison.OrdinalIgnoreCase) ||
                              value.Contains("MPL-", StringComparison.OrdinalIgnoreCase))))
            {
                var relativePackagePath = package.Name["node_modules/".Length..]
                    .Replace('/', Path.DirectorySeparatorChar);
                var source = Path.Combine(sourceRoot, "frontend", "node_modules", relativePackagePath);
                if (!Directory.Exists(source))
                    continue;
                var destination = Path.Combine(destinationRoot, "frontend", "node_modules", relativePackagePath);
                var pending = new Stack<(string Source, string Destination)>();
                pending.Push((source, destination));
                while (pending.Count > 0)
                {
                    var (currentSource, currentDestination) = pending.Pop();
                    Directory.CreateDirectory(currentDestination);
                    foreach (var file in Directory.EnumerateFiles(currentSource))
                        File.Copy(file, Path.Combine(currentDestination, Path.GetFileName(file)));
                    foreach (var directory in Directory.EnumerateDirectories(currentSource))
                    {
                        var child = Path.Combine(currentDestination, Path.GetFileName(directory));
                        pending.Push((directory, child));
                    }
                }
            }
        }

        private static void WriteJson(string path, JsonNode value) =>
            File.WriteAllText(
                path,
                value.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
                    .ReplaceLineEndings("\n") + "\n");

        private static void AddEvidenceArtifacts(
            string root,
            string evidencePath,
            string imageId,
            IDictionary<string, PromotionFixtureFile> files)
        {
            using var evidence = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                root,
                evidencePath.Replace('/', Path.DirectorySeparatorChar))));
            if (!evidence.RootElement.TryGetProperty("artifacts", out var artifacts))
                return;
            foreach (var artifact in artifacts.EnumerateArray())
            {
                var path = artifact.GetProperty("path").GetString()
                    ?? throw new InvalidOperationException("Promotion evidence artifact has no path.");
                var file = new PromotionFixtureFile(
                    imageId,
                    path,
                    new DockerImageFileInspection(
                        artifact.GetProperty("sha256").GetString()
                            ?? throw new InvalidOperationException("Promotion evidence artifact has no SHA-256."),
                        artifact.GetProperty("sizeBytes").GetInt64()));
                var key = FileKey(imageId, path);
                if (files.TryGetValue(key, out var existing) && existing != file)
                    throw new InvalidOperationException("Promotion fixture has conflicting retained image artifacts.");
                files[key] = file;
            }
        }

        public static string FileKey(string imageId, string path) => imageId + "\n" + path;
    }

    private sealed class FakeRuntimePromotionSourceInspector : IRuntimePromotionSourceInspector
    {
        public Task<bool> IsAncestorAsync(
            string repositoryRoot,
            string ancestorRevision,
            string descendantRevision,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                PromotionFixtures.Value.ImagesByReference.Values
                    .Select(static image => image.BuildSourceRevision)
                    .Distinct(StringComparer.Ordinal)
                    .Single() == ancestorRevision &&
                TestSourceRevision == descendantRevision);

        public Task<IReadOnlyList<RuntimePromotionSourceChange>> DiffAsync(
            string repositoryRoot,
            string ancestorRevision,
            string descendantRevision,
            CancellationToken cancellationToken = default)
        {
            if (!IsAncestorAsync(repositoryRoot, ancestorRevision, descendantRevision, cancellationToken).Result)
                throw new InvalidOperationException("Promotion fixture was asked for an unrelated source closure.");
            return Task.FromResult<IReadOnlyList<RuntimePromotionSourceChange>>(
                PromotionFixtures.Value.SourceClosurePaths
                    .Select(static path => new RuntimePromotionSourceChange("M", path))
                    .ToArray());
        }
    }

    private sealed class FakeDockerCli(
        string sourceRevision = TestSourceRevision,
        string? runtimeCommitOverride = null,
        string? referenceSetDigestOverride = null,
        string? netFxManagedReferenceSetDigestOverride = null,
        string? artifactsDefaultReferenceSetDigestOverride = null,
        bool omitReferenceSetLabels = false,
        string? omittedReferenceSetId = null,
        string? componentIdOverride = null,
        string? componentVersionOverride = null,
        bool omitComponentLabels = false,
        string? releaseLabelOverride = null) : IDockerCli
    {
        private static readonly JsonSerializerOptions FixtureJsonOptions = new(JsonSerializerDefaults.Web);

        public const string ImageId = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public const string RoslynStableImageId =
            "sha256:4444444444444444444444444444444444444444444444444444444444444444";
        public const string RoslynNetFx48ImageId =
            "sha256:4545454545454545454545454545454545454545454545454545454545454545";
        public const string RoslynMainImageId =
            "sha256:5555555555555555555555555555555555555555555555555555555555555555";
        public const string RoslynConstGenericsImageId =
            "sha256:6666666666666666666666666666666666666666666666666666666666666666";
        public const string FSharpImageId =
            "sha256:7777777777777777777777777777777777777777777777777777777777777777";
        public const string GSharpImageId =
            "sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        public const string PeachPieImageId =
            "sha256:abababababababababababababababababababababababababababababababab";
        public const string CppCliImageId =
            "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        public const string WineNetFxRuntimeImageId =
            "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
        public const string JSharpImageId =
            "sha256:f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1";
        public const string WineJSharpRuntimeImageId =
            "sha256:f2f2f2f2f2f2f2f2f2f2f2f2f2f2f2f2f2f2f2f2f2f2f2f2f2f2f2f2f2f2f2f2";
        public const string IlImageId =
            "sha256:8888888888888888888888888888888888888888888888888888888888888888";
        public const string MinilangImageId =
            "sha256:9999999999999999999999999999999999999999999999999999999999999999";
        public const string ArtifactsDefaultImageId =
            "sha256:1111111111111111111111111111111111111111111111111111111111111111";
        public const string ArtifactsJsilImageId =
            "sha256:1212121212121212121212121212121212121212121212121212121212121212";
        public const string ArtifactsConstGenericsImageId =
            "sha256:2222222222222222222222222222222222222222222222222222222222222222";
        public const string ArtifactsIlAssemblerImageId =
            "sha256:3333333333333333333333333333333333333333333333333333333333333333";

        public List<string> SavedReferences { get; } = [];

        public Task<DockerImageInspection> InspectImageAsync(
            string reference,
            CancellationToken cancellationToken)
        {
            if (PromotionFixtures.Value.HelpersByReference.TryGetValue(reference, out var helper))
            {
                return Task.FromResult(new DockerImageInspection(
                    helper.ImageId,
                    "linux",
                    "amd64",
                    helper.SizeBytes,
                    [helper.Reference],
                    new Dictionary<string, string>
                    {
                        [RepositorySourceProvenanceResolver.ImageLabel] = helper.BuildSourceRevision,
                        ["org.opencontainers.image.revision"] = helper.BuildSourceRevision
                    }));
            }

            var definition = FindDeploymentDefinition(reference);
            var promoted = definition.ImmutableReference is not null &&
                StringComparer.Ordinal.Equals(reference, definition.ImmutableReference);
            var promotion = promoted
                ? PromotionFixtures.Value.ImagesByReference[reference]
                : null;
            var releaseId = promotion is null
                ? ReleaseIdFromTaggedReference(reference)
                : releaseLabelOverride ?? CatalogReleaseId();
            var runtimeCommit = runtimeCommitOverride ?? promotion?.RuntimeCommit ?? RuntimeCommit(definition);
            var jitCommit = runtimeCommitOverride ?? promotion?.JitCommit ?? RuntimeJitCommit(definition);
            var labels = new Dictionary<string, string>
            {
                ["org.opencontainers.image.version"] = releaseId,
                ["org.opencontainers.image.revision"] = promotion?.BuildSourceRevision ?? sourceRevision,
                [RepositorySourceProvenanceResolver.ImageLabel] = promotion?.BuildSourceRevision ?? sourceRevision,
                [ReleaseBundleBuilder.RuntimeCommitLabel] = runtimeCommit,
                [ReleaseBundleBuilder.JitCommitLabel] = jitCommit
            };
            if (promotion is not null)
            {
                labels["io.sharplabnext.source.context"] = "committed";
                labels["com.sharplabnext.runtime-candidate.promotion-eligible"] = "true";
            }
            if (!omitReferenceSetLabels)
            {
                foreach (var referenceSetId in LockedReferenceSetIds())
                {
                    if (string.Equals(referenceSetId, omittedReferenceSetId, StringComparison.Ordinal))
                        continue;

                    var digest = referenceSetId switch
                    {
                        "net10-ref" => referenceSetDigestOverride ?? LockedComponentDigest(referenceSetId),
                        "netfx48-managed-ref" =>
                            netFxManagedReferenceSetDigestOverride ?? LockedComponentDigest(referenceSetId),
                        "netfx20-managed-ref" when artifactsDefaultReferenceSetDigestOverride is not null &&
                            reference.Contains("worker-artifacts-default:", StringComparison.Ordinal) =>
                            artifactsDefaultReferenceSetDigestOverride,
                        _ => LockedComponentDigest(referenceSetId)
                    };
                    labels[ReleaseBundleBuilder.ReferenceSetLabelPrefix + referenceSetId] = digest;
                }
            }
            AddBaseImageLabels(labels);
            if (!omitComponentLabels)
            {
                AddComponentLabels(reference, labels, componentIdOverride, componentVersionOverride);
                if (reference.Contains("runtime-wine-", StringComparison.Ordinal))
                {
                    var prefix = ReleaseBundleBuilder.ComponentLabelPrefix + "wine-coreclr-userspace.";
                    labels[prefix + "version"] = TestWineManifest.Component.ResolvedVersion;
                    labels[prefix + "digest"] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(
                        JsonSerializer.SerializeToUtf8Bytes(TestWineManifest, RuntimeProfileFixtureJsonOptions)));
                    labels[prefix + "source-uri"] = TestWineManifest.Component.SourceUri;
                }
            }

            var imageId = promotion?.ImageId ?? reference switch
            {
                var value when value.Contains("worker-roslyn-netfx48:", StringComparison.Ordinal) =>
                    RoslynNetFx48ImageId,
                var value when value.Contains("worker-roslyn-stable:", StringComparison.Ordinal) =>
                    RoslynStableImageId,
                var value when value.Contains("worker-roslyn-main:", StringComparison.Ordinal) =>
                    RoslynMainImageId,
                var value when value.Contains("worker-roslyn-const-generics:", StringComparison.Ordinal) =>
                    RoslynConstGenericsImageId,
                var value when value.Contains("worker-fsharp:", StringComparison.Ordinal) => FSharpImageId,
                var value when value.Contains("worker-gsharp:", StringComparison.Ordinal) => GSharpImageId,
                var value when value.Contains("worker-peachpie:", StringComparison.Ordinal) => PeachPieImageId,
                var value when value.Contains("worker-cppcli:", StringComparison.Ordinal) => CppCliImageId,
                var value when value.Contains("worker-jsharp:", StringComparison.Ordinal) => JSharpImageId,
                var value when value.Contains("runtime-wine-netfx48", StringComparison.Ordinal) =>
                    WineNetFxRuntimeImageId,
                var value when value.Contains("runtime-wine-jsharp20:", StringComparison.Ordinal) =>
                    WineJSharpRuntimeImageId,
                var value when value.Contains("worker-il:", StringComparison.Ordinal) => IlImageId,
                var value when value.Contains("worker-minilang:", StringComparison.Ordinal) => MinilangImageId,
                var value when value.Contains("worker-artifacts-default:", StringComparison.Ordinal) =>
                    ArtifactsDefaultImageId,
                var value when value.Contains("worker-artifacts-jsil:", StringComparison.Ordinal) =>
                    ArtifactsJsilImageId,
                var value when value.Contains("worker-artifacts-const-generics:", StringComparison.Ordinal) =>
                    ArtifactsConstGenericsImageId,
                var value when value.Contains("worker-artifacts-il-assembler:", StringComparison.Ordinal) =>
                    ArtifactsIlAssemblerImageId,
                _ => ImageId
            };
            return Task.FromResult(new DockerImageInspection(
                imageId,
                "linux",
                "amd64",
                promotion?.SizeBytes ?? 536870912,
                promotion is null ? [] : [promotion.Reference],
                labels));
        }

        public Task<DockerImageFileInspection> InspectImageFileAsync(
            string imageId,
            string absolutePath,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            if (absolutePath == RuntimeMeasurementHelperContract.Entrypoint)
            {
                return Task.FromResult(new DockerImageFileInspection(
                    RuntimeMeasurementHelperContract.ContentSha256,
                    10_365));
            }
            if (PromotionFixtures.Value.Files.TryGetValue(
                    PromotionFixtureData.FileKey(imageId, absolutePath),
                    out var file))
            {
                return Task.FromResult(file.Inspection);
            }
            return Task.FromResult(new DockerImageFileInspection(
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                1));
        }

        private static DeploymentImageDefinition FindDeploymentDefinition(string reference)
        {
            var root = FindRepositoryRoot();
            var deployment = JsonSerializer.Deserialize<DeploymentImageManifest>(
                File.ReadAllText(Path.Combine(root, "deploy", "images.json")),
                FixtureJsonOptions) ?? throw new InvalidOperationException("Deployment image fixture is invalid.");
            var exact = deployment.Images.SingleOrDefault(item =>
                StringComparer.Ordinal.Equals(item.ImmutableReference, reference));
            if (exact is not null)
                return exact;
            var name = ImageName(reference);
            return deployment.Images.Single(item =>
                StringComparer.Ordinal.Equals(ImageName(item.Repository), name));
        }

        private static string ImageName(string reference)
        {
            var namedReference = reference.Split('@', 2)[0];
            var slash = namedReference.LastIndexOf('/');
            var colon = namedReference.LastIndexOf(':');
            var start = slash + 1;
            var end = colon > slash ? colon : namedReference.Length;
            return namedReference[start..end];
        }

        private static string ReleaseIdFromTaggedReference(string reference)
        {
            if (reference.Contains('@'))
                return CatalogReleaseId();
            var slash = reference.LastIndexOf('/');
            var colon = reference.LastIndexOf(':');
            return colon > slash ? reference[(colon + 1)..] : "development";
        }

        private static string CatalogReleaseId()
        {
            using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                FindRepositoryRoot(), "profiles", "catalog", "catalog.json")));
            return catalog.RootElement.GetProperty("releaseId").GetString()
                ?? throw new InvalidOperationException("Catalog fixture has no release ID.");
        }

        private static string RuntimeCommit(DeploymentImageDefinition definition)
        {
            if (definition.RuntimeId is null)
                return TestSourceRevision;
            var releaseLock = LoadReleaseLock();
            return releaseLock.Components[definition.LockComponentId ?? definition.RuntimeId].Commit ?? "not-applicable";
        }

        private static string RuntimeJitCommit(DeploymentImageDefinition definition)
        {
            if (definition.RuntimeId is null)
                return TestSourceRevision;
            var releaseLock = LoadReleaseLock();
            return releaseLock.Components[definition.LockComponentId ?? definition.RuntimeId].JitCommit ?? "not-applicable";
        }

        private static ReleaseLockDocument LoadReleaseLock()
        {
            var root = FindRepositoryRoot();
            return JsonSerializer.Deserialize<ReleaseLockDocument>(
                File.ReadAllText(Path.Combine(root, "profiles", "lock.json")),
                FixtureJsonOptions) ?? throw new InvalidOperationException("Release lock fixture is invalid.");
        }

        private static void AddComponentLabels(
            string reference,
            Dictionary<string, string> labels,
            string? componentIdOverride,
            string? componentVersionOverride)
        {
            var root = FindRepositoryRoot();
            var releaseLock = LoadReleaseLock();
            var definition = FindDeploymentDefinition(reference);
            var primary = definition.LockComponentId ?? definition.ToolchainId ?? definition.RuntimeId ??
                definition.ArtifactProcessorId;
            var componentIds = primary is null
                ? definition.LockComponentIds
                : new[] { primary }.Concat(definition.LockComponentIds).ToArray();
            foreach (var componentId in componentIds)
            {
                var component = releaseLock.Components[componentId];
                var prefix = $"{ReleaseBundleBuilder.ComponentLabelPrefix}{componentId}.";
                labels[prefix + "version"] = string.Equals(componentId, componentIdOverride, StringComparison.Ordinal)
                    ? componentVersionOverride ?? component.ResolvedVersion
                    : component.ResolvedVersion;
                AddOptional(labels, prefix + "commit", component.Commit);
                AddOptional(labels, prefix + "digest", component.Digest);
                AddOptional(labels, prefix + "source-uri", component.SourceUri);
                AddOptional(labels, prefix + "patch-digest", component.PatchDigest);
            }
        }

        private static void AddBaseImageLabels(Dictionary<string, string> labels)
        {
            var root = FindRepositoryRoot();
            var manifest = JsonSerializer.Deserialize<BaseImageManifest>(
                File.ReadAllText(Path.Combine(root, "profiles", "base-images.json")),
                FixtureJsonOptions) ?? throw new InvalidOperationException("Base image fixture is invalid.");
            foreach (var image in manifest.Images)
                labels[ReleaseBundleBuilder.BaseImageLabelPrefix + image.Id] = image.Reference;
        }

        private static string LockedComponentDigest(string componentId)
        {
            var root = FindRepositoryRoot();
            var releaseLock = JsonSerializer.Deserialize<ReleaseLockDocument>(
                File.ReadAllText(Path.Combine(root, "profiles", "lock.json")),
                FixtureJsonOptions) ?? throw new InvalidOperationException("Release lock fixture is invalid.");
            var component = releaseLock.Components[componentId];
            return !string.IsNullOrWhiteSpace(component.Package)
                ? component.PackageContentHash
                    ?? throw new InvalidOperationException(
                        $"Release lock package component '{componentId}' has no package content hash.")
                : component.Digest
                    ?? throw new InvalidOperationException(
                        $"Release lock component '{componentId}' has no digest.");
        }

        private static string[] LockedReferenceSetIds() =>
            LoadReleaseLock().Components
                .Where(static pair => string.Equals(pair.Value.Kind, "reference-set", StringComparison.Ordinal))
                .Select(static pair => pair.Key)
                .Order(StringComparer.Ordinal)
                .ToArray();

        private static void AddOptional(Dictionary<string, string> labels, string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                labels[label] = value;
        }

        public async Task SaveImagesAsync(
            IReadOnlyList<string> references,
            string outputPath,
            CancellationToken cancellationToken)
        {
            SavedReferences.AddRange(references);
            await File.WriteAllTextAsync(outputPath, "fake image archive", cancellationToken);
        }

        public async Task<DockerImageFileInspection> CopyImageFileAsync(
            string imageId,
            string absolutePath,
            string destinationPath,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            Assert.Equal(WineRuntimePackageManifestLoader.RequiredNoticeArchiveImagePath, absolutePath);
            Assert.True(maximumBytes >= TestWineNoticeArchive.Length);
            await File.WriteAllBytesAsync(destinationPath, TestWineNoticeArchive, cancellationToken);
            return new DockerImageFileInspection(
                "sha256:" + Convert.ToHexStringLower(SHA256.HashData(TestWineNoticeArchive)),
                TestWineNoticeArchive.Length);
        }
    }

    private sealed class FakeRepositorySourceInspector(RepositorySourceState state)
        : IRepositorySourceInspector
    {
        public Task<RepositorySourceState> InspectAsync(
            string repositoryRoot,
            CancellationToken cancellationToken = default) => Task.FromResult(state);
    }

    private sealed class FakeBundleSigner : IBundleSigner
    {
        public bool WasCalled { get; private set; }

        public async Task SignAndVerifyAsync(
            string contentPath,
            string signaturePath,
            string privateKeyPath,
            string publicKeyPath,
            CancellationToken cancellationToken)
        {
            Assert.True(File.Exists(contentPath));
            Assert.True(File.Exists(privateKeyPath));
            Assert.True(File.Exists(publicKeyPath));
            WasCalled = true;
            await File.WriteAllTextAsync(signaturePath, "test signature", cancellationToken);
        }
    }

    private sealed class FakeExternalSourceMaterialFetcher : IExternalSourceMaterialFetcher
    {
        public Task<ExternalSourceMaterial> FetchAsync(Uri sourceUri, CancellationToken cancellationToken)
        {
            var poolIndex = sourceUri.AbsolutePath.IndexOf("/pool/", StringComparison.Ordinal);
            var path = poolIndex >= 0 ? sourceUri.AbsolutePath[(poolIndex + 1)..] : string.Empty;
            if (!TestOperatingSystemSourceFiles.TryGetValue(path, out var bytes))
                throw new BundleValidationException($"Unexpected test source URI '{sourceUri}'.");
            return Task.FromResult(new ExternalSourceMaterial(
                sourceUri,
                bytes.Length,
                new MemoryStream(bytes, writable: false)));
        }
    }

    private sealed class RedirectingExternalSourceMaterialFetcher : IExternalSourceMaterialFetcher
    {
        public Task<ExternalSourceMaterial> FetchAsync(Uri sourceUri, CancellationToken cancellationToken) =>
            Task.FromResult(new ExternalSourceMaterial(
                new Uri("https://redirect.example.test/wine-source"),
                1,
                new MemoryStream([0x00], writable: false)));
    }

    private sealed class TamperedExternalSourceMaterialFetcher : IExternalSourceMaterialFetcher
    {
        public Task<ExternalSourceMaterial> FetchAsync(Uri sourceUri, CancellationToken cancellationToken)
        {
            var poolIndex = sourceUri.AbsolutePath.IndexOf("/pool/", StringComparison.Ordinal);
            var path = poolIndex >= 0 ? sourceUri.AbsolutePath[(poolIndex + 1)..] : string.Empty;
            var length = TestOperatingSystemSourceFiles[path].Length;
            return Task.FromResult(new ExternalSourceMaterial(
                sourceUri,
                length,
                new MemoryStream(Enumerable.Repeat((byte)0x00, length).ToArray(), writable: false)));
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class FakeReleaseHttpServer : IAsyncDisposable
    {
        private readonly string _statePath;
        private readonly string _failPath;
        private readonly bool _usePascalCase;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _serveTask;

        public FakeReleaseHttpServer(string statePath, string failPath, bool usePascalCase = true)
        {
            _statePath = statePath;
            _failPath = failPath;
            _usePascalCase = usePascalCase;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serveTask = ServeAsync(_stopping.Token);
        }

        public int Port { get; }

        public async ValueTask DisposeAsync()
        {
            _stopping.Cancel();
            _listener.Stop();
            try
            {
                await _serveTask;
            }
            catch (OperationCanceledException)
            {
            }
            _stopping.Dispose();
        }

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch (SocketException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                _ = HandleAsync(client, cancellationToken);
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
                {
                }

                var releaseId = File.Exists(_statePath) ? (await File.ReadAllTextAsync(_statePath, cancellationToken)).Trim() : string.Empty;
                var failRelease = File.Exists(_failPath) ? (await File.ReadAllTextAsync(_failPath, cancellationToken)).Trim() : string.Empty;
                var success = releaseId.Length > 0 && !string.Equals(releaseId, failRelease, StringComparison.Ordinal);
                var body = Encoding.UTF8.GetBytes(_usePascalCase
                    ? JsonSerializer.Serialize(new { ReleaseId = releaseId })
                    : JsonSerializer.Serialize(new { releaseId }));
                var status = success ? "200 OK" : "503 Service Unavailable";
                var headers = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, cancellationToken);
                await stream.WriteAsync(body, cancellationToken);
            }
        }
    }
}
