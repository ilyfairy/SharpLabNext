using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private static readonly string[] DependencyInventoryAllowedLicenses = ["Apache-2.0", "MIT"];
    private static readonly string[] DependencyInventoryDeniedPrefixes = ["GPL-"];
    private static readonly string[] MaintainedIdentityProperties =
        ["kind", "resolvedVersion", "commit", "digest", "sourceUri"];
    private static readonly JsonSerializerOptions RuntimeProfileFixtureJsonOptions =
        new(JsonSerializerDefaults.Web);

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
            Assert.Equal(23, result.Images.Count);
            Assert.True(File.Exists(Path.Combine(output, "images.tar")));
            Assert.True(File.Exists(Path.Combine(output, "checksums.sha256")));
            Assert.True(File.Exists(Path.Combine(output, "sbom", "release.spdx.json")));
            Assert.True(File.Exists(Path.Combine(output, "sbom", "release.cdx.json")));
            Assert.True(File.Exists(Path.Combine(output, "sbom", "dependencies.json")));
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
            Assert.True(File.Exists(Path.Combine(output, "profile-update-status.json")));
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
            const string profilePrefix = "RuntimeSupervisorProfileOverlay__Profiles";
            Assert.Contains("RuntimeSupervisorProfileOverlay__Enabled: \"true\"", compose, StringComparison.Ordinal);
            Assert.Contains($"{profilePrefix}__0__Image: \"{FakeDockerCli.ImageId}\"", compose, StringComparison.Ordinal);
            Assert.Contains($"{profilePrefix}__1__Image: \"{FakeDockerCli.ImageId}\"", compose, StringComparison.Ordinal);
            Assert.Contains($"{profilePrefix}__0__Capabilities__3: \"execution-flow\"", compose, StringComparison.Ordinal);
            Assert.Contains($"{profilePrefix}__0__Operations__Run__ImplementationId: \"sharplabnext-runner-v1\"", compose, StringComparison.Ordinal);
            Assert.Contains($"{profilePrefix}__0__Operations__Run__Command__Executable: \"dotnet\"", compose, StringComparison.Ordinal);
            Assert.Contains($"{profilePrefix}__0__Operations__Jit__ImplementationId: \"sharplabnext-jit-inspector-v1\"", compose, StringComparison.Ordinal);
            Assert.Contains($"{profilePrefix}__0__Operations__Jit__SourceMappingKind: \"linux-profiler\"", compose, StringComparison.Ordinal);
            Assert.Contains($"{profilePrefix}__0__Layout__RunnerAssemblyPath: \"/opt/sharplabnext/SharpLabNext.Runner.dll\"", compose, StringComparison.Ordinal);
            Assert.Contains($"{profilePrefix}__3__Id: \"wine-netfx48-linux-x64\"", compose, StringComparison.Ordinal);
            Assert.Contains($"{profilePrefix}__3__RuntimeCommit: \"not-applicable\"", compose, StringComparison.Ordinal);
            Assert.Contains($"{profilePrefix}__3__JitVersion: \"not-applicable\"", compose, StringComparison.Ordinal);
            Assert.Contains($"{profilePrefix}__3__JitCommit: \"not-applicable\"", compose, StringComparison.Ordinal);
            Assert.Contains($"{profilePrefix}__3__Container__WinePrefixPath: \"/opt/wine-dotnet\"", compose, StringComparison.Ordinal);
            Assert.Contains($"{profilePrefix}__3__Operations__Run__ImplementationId: \"sharplabnext-wine-runner-v1\"", compose, StringComparison.Ordinal);
            Assert.Contains($"{profilePrefix}__3__Operations__Run__PathStyle: \"wine-z\"", compose, StringComparison.Ordinal);
            Assert.Contains($"{profilePrefix}__4__Id: \"wine-jsharp20-linux-x64\"", compose, StringComparison.Ordinal);
            Assert.Contains($"{profilePrefix}__4__Architecture: \"x64\"", compose, StringComparison.Ordinal);
            Assert.Contains($"{profilePrefix}__4__RuntimeCommit: \"not-applicable\"", compose, StringComparison.Ordinal);
            Assert.Contains($"{profilePrefix}__4__JitVersion: \"not-applicable\"", compose, StringComparison.Ordinal);
            Assert.Contains($"{profilePrefix}__4__JitCommit: \"not-applicable\"", compose, StringComparison.Ordinal);
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
            Assert.Equal(5, generatedProfileOverlay.Profiles.Count);
            Assert.Equal(3, generatedProfileOverlay.SecurityPolicies.Count);
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
                FakeDockerCli.ImageId,
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
                "901ca941248413c79832d2fdbd709da0c4386353",
                runtimeImage.GetProperty("runtimeCommit").GetString());
            Assert.Equal(
                "901ca941248413c79832d2fdbd709da0c4386353",
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
            Assert.All(result.Images, image => Assert.StartsWith(
                "registry.example.test/private/",
                image.SourceReference,
                StringComparison.Ordinal));
            using var bundle = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(output, "bundle.json"),
                TestContext.Current.CancellationToken));
            Assert.False(bundle.RootElement.GetProperty("containsImages").GetBoolean());
            Assert.Equal(23, bundle.RootElement.GetProperty("images").GetArrayLength());
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
        var installRoot = Path.Combine(testRoot, "installed");
        var fakeBin = Path.Combine(testRoot, "bin");
        var internalServiceToken = Path.Combine(testRoot, "internal-service-token");
        Directory.CreateDirectory(fakeBin);
        try
        {
            await BuildBundleForReleaseAsync(repositoryRoot, testRoot, bundleA, "development");
            await BuildBundleForReleaseAsync(repositoryRoot, testRoot, bundleB, "candidate");
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
                [Path.Combine(bundleA, "install.sh"), "--allow-unsigned", "--skip-artifact-backup", "--ready-timeout-seconds", "3"],
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
            await File.WriteAllTextAsync(
                internalServiceToken,
                "test-internal-service-token",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(fakeBin, "docker.cmd"),
                $$"""
                @echo off
                if "%1"=="image" if "%2"=="inspect" (
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
                echo %* | %SystemRoot%\System32\findstr.exe /C:"config --services" >nul && (
                  echo gateway
                  exit /b 0
                )
                echo %* | %SystemRoot%\System32\findstr.exe /C:"ps --status running --services" >nul && (
                  echo gateway
                  exit /b 0
                )
                echo %* | %SystemRoot%\System32\findstr.exe /C:" up -d " >nul && (
                  > "%SHARPLABNEXT_FAKE_STATE%" echo %SHARPLABNEXT_RELEASE_ID%
                  exit /b 0
                )
                echo %* | %SystemRoot%\System32\findstr.exe /C:" down --remove-orphans" >nul && (
                  if exist "%SHARPLABNEXT_FAKE_STATE%" del /q "%SHARPLABNEXT_FAKE_STATE%"
                  exit /b 0
                )
                exit /b 0
                """.ReplaceLineEndings("\r\n"),
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
            Assert.Equal(0, first.ExitCode);
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
        await CreateBuilder(new FakeDockerCli()).BuildAsync(
            command,
            TestContext.Current.CancellationToken);
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
                new RepositorySourceState(true, TestSourceRevision, false)));

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
        bool omitComponentLabels = false) : IDockerCli
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
            var tagSeparator = reference.LastIndexOf(':');
            var releaseId = tagSeparator < 0 ? "development" : reference[(tagSeparator + 1)..];
            var runtimeCommit = runtimeCommitOverride ?? (reference.Contains("runtime-dotnet10", StringComparison.Ordinal)
                ? "901ca941248413c79832d2fdbd709da0c4386353"
                : reference.Contains("runtime-dotnet11", StringComparison.Ordinal)
                    ? "f7b4c5716faaee8fb8a289aed29118cad955c45f"
                    : reference.Contains("runtime-const-generics", StringComparison.Ordinal)
                        ? "79f7f1408b2c811904c983419b45139e654f1e46"
                        : TestSourceRevision);
            var labels = new Dictionary<string, string>
            {
                ["org.opencontainers.image.version"] = releaseId,
                ["org.opencontainers.image.revision"] = "test-revision",
                [RepositorySourceProvenanceResolver.ImageLabel] = sourceRevision,
                [ReleaseBundleBuilder.RuntimeCommitLabel] = runtimeCommit,
                [ReleaseBundleBuilder.JitCommitLabel] = runtimeCommit
            };
            if (!omitReferenceSetLabels)
            {
                labels[ReleaseBundleBuilder.ReferenceSetLabelPrefix + "net10-ref"] =
                    referenceSetDigestOverride ??
                    "sha512-rWQyRVuTET24XM2aUdxbWPmhRgd5mIypan61IN8BOCfZoQbqP5VX5EjRDcxLwF3RzzIkxTyeh0SbVuGLt93xGw==";
                labels[ReleaseBundleBuilder.ReferenceSetLabelPrefix + "net11-preview-ref"] =
                    "sha512-16anh5wbcRpV0Dm2nZHURKka5JSL1VjMIlG4xD2NwYfXaI4ZSUknKttRhjH1TE1V70+d/XbwgbAfmCI7GNLK6A==";
                labels[ReleaseBundleBuilder.ReferenceSetLabelPrefix + "const-generics-ref"] =
                    "sha256:00f0f9fcfc083e931004ceaa914633990ad7e389ce8d21012b97af5844f501b4";
                labels[ReleaseBundleBuilder.ReferenceSetLabelPrefix + "netfx48-ref"] =
                    LockedComponentDigest("netfx48-ref");
                foreach (var referenceSetId in new[]
                         {
                             "netfx20-managed-ref",
                             "netfx30-managed-ref",
                             "netfx35-managed-ref",
                             "netfx40-managed-ref",
                             "netfx45-managed-ref",
                             "netfx451-managed-ref",
                             "netfx452-managed-ref",
                             "netfx46-managed-ref",
                             "netfx461-managed-ref",
                             "netfx462-managed-ref",
                             "netfx47-managed-ref",
                             "netfx471-managed-ref",
                             "netfx472-managed-ref"
                         })
                {
                    if (!string.Equals(referenceSetId, omittedReferenceSetId, StringComparison.Ordinal))
                    {
                        labels[ReleaseBundleBuilder.ReferenceSetLabelPrefix + referenceSetId] =
                            LockedComponentDigest(referenceSetId);
                    }
                }
                labels[ReleaseBundleBuilder.ReferenceSetLabelPrefix + "netfx48-managed-ref"] =
                    netFxManagedReferenceSetDigestOverride ?? LockedComponentDigest("netfx48-managed-ref");
                labels[ReleaseBundleBuilder.ReferenceSetLabelPrefix + "jsharp20-ref"] =
                    LockedComponentDigest("jsharp20-ref");
                if (artifactsDefaultReferenceSetDigestOverride is not null &&
                    reference.Contains("worker-artifacts-default:", StringComparison.Ordinal))
                {
                    labels[ReleaseBundleBuilder.ReferenceSetLabelPrefix + "netfx20-managed-ref"] =
                        artifactsDefaultReferenceSetDigestOverride;
                }
            }
            AddBaseImageLabels(labels);
            if (!omitComponentLabels)
                AddComponentLabels(reference, labels, componentIdOverride, componentVersionOverride);

            var imageId = reference switch
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
                var value when value.Contains("runtime-wine-netfx48:", StringComparison.Ordinal) =>
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
                536870912,
                [],
                labels));
        }

        public Task<DockerImageFileInspection> InspectImageFileAsync(
            string imageId,
            string absolutePath,
            long maximumBytes,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DockerImageFileInspection(
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                1));

        private static void AddComponentLabels(
            string reference,
            Dictionary<string, string> labels,
            string? componentIdOverride,
            string? componentVersionOverride)
        {
            var root = FindRepositoryRoot();
            var deployment = JsonSerializer.Deserialize<DeploymentImageManifest>(
                File.ReadAllText(Path.Combine(root, "deploy", "images.json")),
                FixtureJsonOptions) ?? throw new InvalidOperationException("Deployment image fixture is invalid.");
            var releaseLock = JsonSerializer.Deserialize<ReleaseLockDocument>(
                File.ReadAllText(Path.Combine(root, "profiles", "lock.json")),
                FixtureJsonOptions) ?? throw new InvalidOperationException("Release lock fixture is invalid.");
            var slash = reference.LastIndexOf('/');
            var colon = reference.LastIndexOf(':');
            var imageName = reference[(slash + 1)..colon];
            var definition = deployment.Images.Single(item =>
            {
                var repositorySlash = item.Repository.LastIndexOf('/');
                var repositoryName = item.Repository[(repositorySlash + 1)..];
                return string.Equals(repositoryName, imageName, StringComparison.Ordinal);
            });
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
