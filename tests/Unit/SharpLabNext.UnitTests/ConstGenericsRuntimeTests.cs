using System.Security.Cryptography;
using System.Text.Json;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.Catalog;
using SharpLabNext.Contracts;
using SharpLabNext.RuntimeProfile.Sdk;
using SharpLabNext.RuntimeSupervisor;

namespace SharpLabNext.UnitTests;

public sealed class ConstGenericsRuntimeTests
{
    private const string Commit = "79f7f1408b2c811904c983419b45139e654f1e46";
    private const string ArchiveSha256 = "00f0f9fcfc083e931004ceaa914633990ad7e389ce8d21012b97af5844f501b4";
    private const string ReferenceDigest = "sha256:00f0f9fcfc083e931004ceaa914633990ad7e389ce8d21012b97af5844f501b4";
    private const string RuntimeImageTag = "sharplabnext/runtime-const-generics:content";
    private const string RoslynImageTag = "sharplabnext/worker-roslyn-const-generics:content";
    private const string ArtifactWorkerImageTag = "sharplabnext/worker-artifacts-const-generics:content";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SourceIdentityIsPinnedAcrossBuildLockAndProvenance()
    {
        var root = FindRepositoryRoot();
        var dockerfile = await File.ReadAllTextAsync(Path.Combine(root, "deploy", "docker", "Dockerfile.runtime-const-generics"), TestContext.Current.CancellationToken);
        var workerDockerfile = await File.ReadAllTextAsync(Path.Combine(root, "deploy", "docker", "Dockerfile.worker-roslyn-const-generics"), TestContext.Current.CancellationToken);
        var releaseLock = await CatalogLoader.LoadReleaseLockAsync(Path.Combine(root, "profiles", "lock.json"), TestContext.Current.CancellationToken);
        var catalog = await CatalogLoader.LoadCatalogAsync(Path.Combine(root, "profiles", "catalog", "catalog.json"), TestContext.Current.CancellationToken);
        await using var provenanceStream = File.OpenRead(Path.Combine(root, "profiles", "provenance", "const-generics-runtime.json"));
        using var provenance = await JsonDocument.ParseAsync(provenanceStream, cancellationToken: TestContext.Current.CancellationToken);
        await using var roslynProvenanceStream = File.OpenRead(Path.Combine(root, "profiles", "provenance", "const-generics-roslyn.json"));
        using var roslynProvenance = await JsonDocument.ParseAsync(roslynProvenanceStream, cancellationToken: TestContext.Current.CancellationToken);
        await using var ilspyProvenanceStream = File.OpenRead(Path.Combine(root, "profiles", "provenance", "const-generics-ilspy.json"));
        using var ilspyProvenance = await JsonDocument.ParseAsync(ilspyProvenanceStream, cancellationToken: TestContext.Current.CancellationToken);
        await using var runtimeProfileStream = File.OpenRead(Path.Combine(root, "profiles", "runtimes", "const-generics-linux-x64.json"));
        using var runtimeProfile = await JsonDocument.ParseAsync(runtimeProfileStream, cancellationToken: TestContext.Current.CancellationToken);
        await using var baseImagesStream = File.OpenRead(Path.Combine(root, "profiles", "base-images.json"));
        using var baseImages = await JsonDocument.ParseAsync(baseImagesStream, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("ARG CONST_GENERICS_RUNTIME_COMMIT", dockerfile, StringComparison.Ordinal);
        Assert.Contains("ARG CONST_GENERICS_RUNTIME_ARCHIVE_SHA256", dockerfile, StringComparison.Ordinal);
        Assert.Contains("sha256sum --check --strict", dockerfile, StringComparison.Ordinal);
        Assert.Contains("clr+libs+host.native+host.tools+host.pkg+packs.product", dockerfile, StringComparison.Ordinal);
        Assert.Contains("/p:EnableSourceLink=false", dockerfile, StringComparison.Ordinal);
        Assert.Contains("Microsoft.NETCore.App.Ref.${CONST_GENERICS_REFERENCE_VERSION}.nupkg", dockerfile, StringComparison.Ordinal);
        Assert.Contains("FROM runtime-source-build-base AS runtime-assets", dockerfile, StringComparison.Ordinal);
        Assert.Contains("ConstGenerics reference source identity digest mismatch", dockerfile, StringComparison.Ordinal);
        Assert.Contains("referenceIdentityKind", dockerfile, StringComparison.Ordinal);
        Assert.Contains("referenceContentDigest", dockerfile, StringComparison.Ordinal);
        Assert.Contains("ARG CONST_GENERICS_REFERENCE_DIGEST", workerDockerfile, StringComparison.Ordinal);
        Assert.Contains("--digest \"${CONST_GENERICS_REFERENCE_DIGEST}\"", workerDockerfile, StringComparison.Ordinal);

        var source = releaseLock.Components["const-generics-runtime-source"];
        Assert.Equal(Commit, source.Commit);
        Assert.Equal($"sha256:{ArchiveSha256}", source.Digest);
        var versionTools = releaseLock.Components["const-generics-versiontools"];
        Assert.Equal("build-dependency", versionTools.Kind);
        Assert.Equal("Microsoft.DotNet.VersionTools.Tasks", versionTools.Package);
        Assert.Matches("^sha256:[0-9a-f]{64}$", versionTools.Digest);
        Assert.Equal(ReferenceDigest, releaseLock.Components["const-generics-ref"].Digest);
        Assert.Equal("https://codeload.github.com/hez2010/runtime/tar.gz/79f7f1408b2c811904c983419b45139e654f1e46", releaseLock.Components["const-generics-ref"].SourceUri);
        Assert.Equal(ReferenceDigest, Assert.Single(catalog.ReferenceSets, static item => item.Id == "const-generics-ref").Digest);
        Assert.Null(releaseLock.Components["const-generics-linux-x64"].ImageId);
        Assert.Null(releaseLock.Components["roslyn-const-generics"].ImageId);
        Assert.Null(releaseLock.Components["artifacts-const-generics"].ImageId);
        Assert.All(releaseLock.Components.Values, static component => Assert.Null(component.PatchDigest));
        AssertMaintainedSourceReference(provenance.RootElement, releaseLock, "const-generics-runtime-source");
        AssertPatchSeriesIsDerivedFromFiles(root, provenance.RootElement, "eng/patches/const-generics-runtime");
        Assert.Equal("const-generics-ref", provenance.RootElement.GetProperty("build").GetProperty("referenceSet").GetProperty("componentId").GetString());
        var bootstrapOverride = provenance.RootElement.GetProperty("build").GetProperty("bootstrapDependencyOverrides")[0];
        Assert.Equal("const-generics-versiontools", bootstrapOverride.GetProperty("componentId").GetString());
        Assert.False(bootstrapOverride.TryGetProperty("resolvedVersion", out _));
        Assert.False(bootstrapOverride.TryGetProperty("sourceUri", out _));
        Assert.False(bootstrapOverride.TryGetProperty("sha256", out _));
        Assert.Equal(RuntimeImageTag, runtimeProfile.RootElement.GetProperty("runtimeImageId").GetString());

        AssertMaintainedSourceReference(roslynProvenance.RootElement, releaseLock, "const-generics-roslyn-source");
        AssertPatchSeriesIsDerivedFromFiles(root, roslynProvenance.RootElement, "eng/patches/roslyn-const-generics");
        Assert.Equal("const-generics-runtime-source", roslynProvenance.RootElement.GetProperty("build").GetProperty("metadataRuntimeSourceComponentId").GetString());
        Assert.False(roslynProvenance.RootElement.GetProperty("build").TryGetProperty("compilerVersion", out _));
        Assert.Equal("const-generics-ref", roslynProvenance.RootElement.GetProperty("build").GetProperty("referenceSetId").GetString());

        AssertMaintainedSourceReference(ilspyProvenance.RootElement, releaseLock, "const-generics-ilspy-source");
        AssertPatchSeriesIsDerivedFromFiles(root, ilspyProvenance.RootElement, "eng/patches/const-generics-runtime", "eng/patches/ilspy-const-generics");
        var runtimeDependency = ilspyProvenance.RootElement.GetProperty("runtimeDependency");
        Assert.Equal("const-generics-runtime-source", runtimeDependency.GetProperty("sourceComponentId").GetString());
        Assert.Equal("const-generics-linux-x64", runtimeDependency.GetProperty("runtimeComponentId").GetString());
        Assert.False(runtimeDependency.TryGetProperty("runtimeVersion", out _));
        Assert.Equal("target:runtime-const-generics", runtimeDependency.GetProperty("namedContext").GetString());
        Assert.Equal("/opt/const-runtime/dotnet", ilspyProvenance.RootElement.GetProperty("build").GetProperty("processorRuntime").GetString());

        AssertStaticProvenanceContainsOnlyMaintainedInputs(provenance.RootElement);
        AssertStaticProvenanceContainsOnlyMaintainedInputs(roslynProvenance.RootElement);
        AssertStaticProvenanceContainsOnlyMaintainedInputs(ilspyProvenance.RootElement);
        var baseImageIds = baseImages.RootElement.GetProperty("images").EnumerateArray().Select(static image => image.GetProperty("id").GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(provenance.RootElement.GetProperty("builder").GetProperty("imageId").GetString(), baseImageIds);
        Assert.Contains(roslynProvenance.RootElement.GetProperty("builder").GetProperty("imageId").GetString(), baseImageIds);
        Assert.Contains(ilspyProvenance.RootElement.GetProperty("builder").GetProperty("imageId").GetString(), baseImageIds);
    }

    [Fact]
    public async Task RuntimeProfileDeclaresOnlyTheAtomicFeatureContract()
    {
        var profilePath = Path.Combine(FindRepositoryRoot(), "profiles", "runtimes", "const-generics-linux-x64.json");
        await using var stream = File.OpenRead(profilePath);
        var profile = await JsonSerializer.DeserializeAsync<RuntimeProfileDefinition>(stream, JsonOptions, TestContext.Current.CancellationToken);

        Assert.NotNull(profile);
        Assert.Empty(RuntimeProfileValidation.ValidatePackage(profile, requireDigestPinnedImage: false));
        Assert.Equal("coreclr-const-generics", profile.Family);
        Assert.Equal(Commit, profile.RuntimeCommit);
        Assert.Equal(Commit, profile.JitCommit);
        Assert.Equal(RuntimeImageTag, profile.Image);
        Assert.Equal(RuntimeImageTag, profile.RuntimeImageId);
        Assert.Equal(["runtime.const-generics.v1"], profile.ProvidedRuntimeFeatureTags);
        Assert.Equal(["metadata.const-generics.v1"], profile.ProvidedMetadataFeatureTags);
        Assert.Equal("/usr/share/dotnet/dotnet", profile.Layout.DotNetHostPath);
    }

    [Fact]
    public async Task RuntimeTemplateUsesAContentTagAndDocumentsGeneratedReleaseIdentity()
    {
        var root = FindRepositoryRoot();
        await using var stream = File.OpenRead(Path.Combine(root, "samples", "Runtimes", "dotnet-runtime-template", "runtime-profile.json"));
        var profile = await JsonSerializer.DeserializeAsync<RuntimeProfileDefinition>(stream, JsonOptions, TestContext.Current.CancellationToken);
        var readme = await File.ReadAllTextAsync(Path.Combine(root, "samples", "Runtimes", "dotnet-runtime-template", "README.md"), TestContext.Current.CancellationToken);

        Assert.NotNull(profile);
        Assert.Equal("sharplabnext/runtime-example:content", profile.Image);
        Assert.Equal(profile.Image, profile.RuntimeImageId);
        Assert.Empty(RuntimeProfileValidation.ValidatePackage(profile, requireDigestPinnedImage: false));
        Assert.Contains("bundle generation inspects the built image", readme, StringComparison.Ordinal);
        Assert.Contains("generated release lock", readme, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComposeTemplatesDoNotHardCodeLocalBuildOutputDigests()
    {
        var root = FindRepositoryRoot();
        var localCompose = await File.ReadAllTextAsync(Path.Combine(root, "deploy", "compose.dev.yaml"), TestContext.Current.CancellationToken);
        var productionCompose = await File.ReadAllTextAsync(Path.Combine(root, "deploy", "compose.prod.yaml"), TestContext.Current.CancellationToken);

        Assert.Contains($"Services__LanguageWorkers__roslyn-const-generics__ExpectedWorkerImageId: {RoslynImageTag}", localCompose, StringComparison.Ordinal);
        Assert.Contains($"RoslynWorker__WorkerImageId: {RoslynImageTag}", localCompose, StringComparison.Ordinal);
        Assert.Contains($"Services__ArtifactWorkers__artifacts-const-generics__ExpectedWorkerImageId: {ArtifactWorkerImageTag}", localCompose, StringComparison.Ordinal);
        Assert.Contains($"ConstGenericsArtifactWorker__WorkerImageId: {ArtifactWorkerImageTag}", localCompose, StringComparison.Ordinal);
        Assert.Contains($"RuntimeSupervisor__Profiles__2__RuntimeImageId: ${{SHARPLABNEXT_CONST_GENERICS_RUNTIME_IMAGE_ID:-{RuntimeImageTag}}}", localCompose, StringComparison.Ordinal);

        Assert.Contains("Services__LanguageWorkers__roslyn-const-generics__ExpectedWorkerImageId: ${SHARPLABNEXT_ROSLYN_CONST_GENERICS_IMAGE_ID:-bundle-overlay-required}", productionCompose, StringComparison.Ordinal);
        Assert.Contains("RoslynWorker__WorkerImageId: ${SHARPLABNEXT_ROSLYN_CONST_GENERICS_IMAGE_ID:-unverified}", productionCompose, StringComparison.Ordinal);
        Assert.Contains("Services__ArtifactWorkers__artifacts-const-generics__ExpectedWorkerImageId: ${SHARPLABNEXT_ARTIFACTS_CONST_GENERICS_IMAGE_ID:-bundle-overlay-required}", productionCompose, StringComparison.Ordinal);
        Assert.Contains("ConstGenericsArtifactWorker__WorkerImageId: ${SHARPLABNEXT_ARTIFACTS_CONST_GENERICS_IMAGE_ID:-unverified}", productionCompose, StringComparison.Ordinal);
        Assert.Contains("RuntimeSupervisor__Profiles__2__RuntimeImageId: ${SHARPLABNEXT_CONST_GENERICS_RUNTIME_IMAGE_ID:-unverified}", productionCompose, StringComparison.Ordinal);

        Assert.All(ConstGenericsIdentityLines(localCompose), AssertNoSha256Identity);
        Assert.All(ConstGenericsIdentityLines(productionCompose), AssertNoSha256Identity);
    }

    [Fact]
    public async Task ComposeTemplatesBoundResidentServiceLogs()
    {
        var root = FindRepositoryRoot();
        foreach (var fileName in new[] { "compose.dev.yaml", "compose.prod.yaml" })
        {
            var compose = await File.ReadAllTextAsync(Path.Combine(root, "deploy", fileName), TestContext.Current.CancellationToken);

            Assert.Contains(
                """
                  logging:
                    driver: local
                    options:
                      max-size: "10m"
                      max-file: "3"
                """,
                compose,
                StringComparison.Ordinal);
            Assert.Equal(1, compose.Split("  logging:", StringSplitOptions.None).Length - 1);
        }
    }

    [Fact]
    public async Task CatalogDoesNotAdvertiseUnsupportedConstGenericsTransforms()
    {
        var catalog = await CatalogLoader.LoadCatalogAsync(Path.Combine(FindRepositoryRoot(), "profiles", "catalog", "catalog.json"), TestContext.Current.CancellationToken);
        var runtime = Assert.Single(catalog.Runtimes, static item => item.Id == "const-generics-linux-x64");
        var processor = Assert.Single(catalog.ArtifactProcessors, static item => item.Id == "artifacts-const-generics");

        Assert.Contains("inspection", runtime.Capabilities);
        Assert.DoesNotContain("execution-flow", runtime.Capabilities);
        Assert.Equal(["il", "decompiled-csharp", "il-verify"], processor.Capabilities);
        Assert.Empty(processor.Transformations);
    }

    [Fact]
    public void OrdinaryRuntimeRejectsConstGenericsArtifact()
    {
        var exception = Assert.Throws<RuntimeJobFailureException>(() => RuntimeJobExecutor.ValidateCompatibility(ConstGenericsManifest(), OrdinaryRuntime()));

        Assert.Equal("incompatible-artifact", exception.Code);
        Assert.Equal(WorkerErrorCategory.IncompatibleArtifact, exception.Category);
    }

    [Fact]
    public void RuntimeWithMatchingFamilyButMissingTagsRejectsConstGenericsArtifact()
    {
        var profile = OrdinaryRuntime();
        profile.Family = "coreclr-const-generics";

        var exception = Assert.Throws<RuntimeJobFailureException>(() => RuntimeJobExecutor.ValidateCompatibility(ConstGenericsManifest(), profile));

        Assert.Equal("incompatible-feature-tags", exception.Code);
    }

    [Fact]
    public void ConstGenericsRuntimeRejectsOrdinaryArtifact()
    {
        var exception = Assert.Throws<RuntimeJobFailureException>(() => RuntimeJobExecutor.ValidateCompatibility(OrdinaryManifest(), ConstGenericsRuntime()));

        Assert.Equal("incompatible-artifact", exception.Code);
    }

    [Fact]
    public void ConstGenericsRuntimeAcceptsMatchingAtomicArtifact()
    {
        RuntimeJobExecutor.ValidateCompatibility(ConstGenericsManifest(), ConstGenericsRuntime());
    }

    private static RuntimeProfileOptions OrdinaryRuntime() => new()
    {
        Id = "dotnet-10-linux-x64",
        Image = "sharplabnext/runtime-dotnet10:content",
        Family = "coreclr",
        RuntimeVersion = "10.0.9",
        JitVersion = "10.0.9",
        RuntimeImageId = "test-image",
        AcceptedArtifactFormats = ["dotnet-managed-pe-v1"],
        Capabilities = ["run", "jit-asm"],
        AllowedSecurityPolicyIds = ["runtime-job-default"]
    };

    private static RuntimeProfileOptions ConstGenericsRuntime() => new()
    {
        Id = "const-generics-linux-x64",
        Image = "sharplabnext/runtime-const-generics:content",
        Family = "coreclr-const-generics",
        RuntimeVersion = "9.0.0-const-generics.79f7f140",
        RuntimeCommit = Commit,
        JitVersion = "9.0.0-const-generics.79f7f140",
        JitCommit = Commit,
        RuntimeImageId = "test-image",
        AcceptedArtifactFormats = ["dotnet-managed-pe-v1"],
        Capabilities = ["run", "jit-asm"],
        ProvidedRuntimeFeatureTags = ["runtime.const-generics.v1"],
        ProvidedMetadataFeatureTags = ["metadata.const-generics.v1"],
        AllowedSecurityPolicyIds = ["runtime-job-default"]
    };

    private static ArtifactManifest ConstGenericsManifest() => Manifest("coreclr-const-generics", ["runtime.const-generics.v1"], ["metadata.const-generics.v1"]);

    private static ArtifactManifest OrdinaryManifest() => Manifest("coreclr", [], []);

    private static ArtifactManifest Manifest(string family, IReadOnlyList<string> runtimeFeatureTags, IReadOnlyList<string> metadataFeatureTags)
    {
        var artifactRef = new ArtifactRef($"sha256:{new string('a', 64)}");
        return new ArtifactManifest(
            1,
            artifactRef,
            new ArtifactProducer("content", "csharp", family == "coreclr-const-generics" ? "roslyn-const-generics" : "roslyn-stable", "test", null, "test-image"),
            family == "coreclr-const-generics" ? "const-generics-ref" : "net10-ref",
            family == "coreclr-const-generics" ? "net9.0-const-generics" : "net10.0",
            "dotnet-managed-pe-v1",
            new ArtifactRuntimeRequirement(family, [], "anycpu", runtimeFeatureTags),
            metadataFeatureTags,
            BuildOutputKind.Console,
            "app.dll",
            "Program.Main",
            [],
            null,
            null);
    }

    private static void AssertPatchSeriesIsDerivedFromFiles(string repositoryRoot, JsonElement provenance, params string[] patchDirectories)
    {
        var expectedPaths = patchDirectories.SelectMany(patchDirectory => Directory.EnumerateFiles(Path.Combine(repositoryRoot, patchDirectory), "*.patch")).Select(path => Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
        var declaredPaths = provenance.GetProperty("patchSeries").EnumerateArray().Select(item =>
            {
                Assert.False(item.TryGetProperty("sha256", out _));
                return item.GetProperty("path").GetString()!;
            }).ToArray();

        Assert.Equal(expectedPaths, declaredPaths);

        using var series = new MemoryStream();
        foreach (var relativePath in declaredPaths)
        {
            var fullPath = Path.GetFullPath(Path.Combine(repositoryRoot, relativePath));
            Assert.StartsWith(Path.TrimEndingDirectorySeparator(repositoryRoot) + Path.DirectorySeparatorChar, fullPath, StringComparison.OrdinalIgnoreCase);
            var bytes = File.ReadAllBytes(fullPath);
            series.Write(bytes);
            Assert.Matches("^[0-9a-f]{64}$", Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }

        Assert.Matches("^[0-9a-f]{64}$", Convert.ToHexStringLower(SHA256.HashData(series.ToArray())));
    }

    private static void AssertMaintainedSourceReference(JsonElement provenance, ReleaseLockDocument releaseLock, string expectedSourceComponentId)
    {
        Assert.Equal(expectedSourceComponentId, provenance.GetProperty("sourceComponentId").GetString());
        Assert.Equal("MIT", provenance.GetProperty("license").GetString());
        Assert.False(provenance.TryGetProperty("source", out _));

        var source = releaseLock.Components[expectedSourceComponentId];
        Assert.Equal("source", source.Kind);
        Assert.False(string.IsNullOrWhiteSpace(source.ResolvedVersion));
        Assert.Matches("^[0-9a-f]{40,64}$", source.Commit);
        Assert.Matches("^sha256:[0-9a-f]{64}$", source.Digest);
        Assert.True(Uri.TryCreate(source.SourceUri, UriKind.Absolute, out _));
    }

    private static void AssertStaticProvenanceContainsOnlyMaintainedInputs(JsonElement value)
    {
        string[] forbiddenProperties =
        [
            "archiveSha256",
            "archiveUrl",
            "assemblySha256",
            "commit",
            "compilerVersion",
            "compilerAssetSha256",
            "legacyMetadataSha256",
            "metadataRuntimeCommit",
            "observedBuildOutputs",
            "observedReferenceContentDigests",
            "patchSeriesSha256",
            "referenceSetAttestation",
            "repository",
            "runtimeVersion",
            "source",
            "tree",
            "validatedImage",
            "validatedImageId",
            "verifiedAt",
            "verificationTimestamp"
        ];

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                AssertStaticProvenanceContainsOnlyMaintainedInputs(item);
            return;
        }
        if (value.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in value.EnumerateObject())
        {
            Assert.DoesNotContain(property.Name, forbiddenProperties);
            AssertStaticProvenanceContainsOnlyMaintainedInputs(property.Value);
        }
    }

    private static IEnumerable<string> ConstGenericsIdentityLines(string compose) =>
        compose.ReplaceLineEndings("\n").Split('\n').Where(static line =>
                line.Contains("roslyn-const-generics__ExpectedWorkerImageId", StringComparison.Ordinal) ||
                line.Contains("artifacts-const-generics__ExpectedWorkerImageId", StringComparison.Ordinal) ||
                line.Contains("RuntimeSupervisor__Profiles__2__RuntimeImageId", StringComparison.Ordinal) ||
                line.Contains("RoslynWorker__WorkerImageId", StringComparison.Ordinal) &&
                line.Contains("ROSLYN_CONST_GENERICS", StringComparison.Ordinal) ||
                line.Contains("ConstGenericsArtifactWorker__WorkerImageId", StringComparison.Ordinal));

    private static void AssertNoSha256Identity(string line) => Assert.DoesNotContain("sha256:", line, StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SharpLabNext.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
