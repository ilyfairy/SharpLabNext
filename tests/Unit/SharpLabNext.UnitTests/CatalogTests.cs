using SharpLabNext.Catalog;

namespace SharpLabNext.UnitTests;

public sealed class CatalogTests
{
    private static readonly string[] CppCliSharedReleaseComponentIds =
    [
        "msvc-cppcli-private-image",
        "msvc-cppcli-netfx48",
        "netfx48-ref"
    ];

    private static readonly string[] FrameworkManagedReferenceSetIds =
    [
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
        "netfx472-managed-ref",
        "netfx48-managed-ref"
    ];

    [Fact]
    public async Task InitialCatalogLoadsAndContainsStableIds()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "catalog.json");

        var catalog = await CatalogLoader.LoadCatalogAsync(path, TestContext.Current.CancellationToken);

        Assert.StartsWith("runtime-promotion-", catalog.Revision, StringComparison.Ordinal);
        Assert.Equal("development", catalog.ReleaseId);
        Assert.Contains(catalog.Languages, static language =>
            language.Id == "csharp" && language.DefaultToolchainId == "roslyn-main");
        Assert.Contains(catalog.Toolchains, static toolchain =>
            toolchain.Id == "roslyn-main" &&
            toolchain.DefaultReferenceSetId == "net11-preview-ref");
        Assert.Contains(catalog.Presets, static preset =>
            preset.Id == "csharp-main-net11-preview" &&
            preset.LanguageId == "csharp" &&
            preset.ToolchainId == "roslyn-main" &&
            preset.ReferenceSetId == "net11-preview-ref" &&
            preset.DefaultOutputId == "decompiled-csharp" &&
            preset.DefaultRuntimeId == "dotnet-11-preview-linux-x64");
        Assert.Contains(catalog.Languages, static language =>
            language.Id == "gsharp" &&
            language.DefaultToolchainId == "gsharp-stable" &&
            language.Extensions.SequenceEqual([".gs"], StringComparer.Ordinal));
        Assert.Contains(catalog.Toolchains, static toolchain => toolchain.Id == "roslyn-stable");
        Assert.Contains(catalog.Toolchains, static toolchain =>
            toolchain.Id == "roslyn-stable-netfx48" &&
            toolchain.DisplayName == "Roslyn Stable 5.6.0 / .NET Framework" &&
            toolchain.WorkerId == "roslyn-stable-netfx48" &&
            toolchain.ResolvedVersion == "5.6.0" &&
            toolchain.SupportedLanguageIds.SequenceEqual(["csharp", "visual-basic"], StringComparer.Ordinal) &&
            toolchain.AllowedReferenceSetIds.SequenceEqual(FrameworkManagedReferenceSetIds, StringComparer.Ordinal) &&
            toolchain.ProducesArtifactFormats.SequenceEqual(
                ["dotnet-framework-managed-pe-v1"],
                StringComparer.Ordinal));
        Assert.Contains(catalog.Toolchains, static toolchain =>
            toolchain.Id == "gsharp-stable" &&
            toolchain.DisplayName == "0.3.33" &&
            toolchain.ResolvedVersion == "0.3.33" &&
            toolchain.AllowedReferenceSetIds.SequenceEqual(["net10-ref"], StringComparer.Ordinal));
        Assert.Contains(catalog.Toolchains, static toolchain =>
            toolchain.Id == "gsharp-legacy-0.3.8" &&
            toolchain.DisplayName == "0.3.8" &&
            toolchain.ResolvedVersion == "0.3.8" &&
            toolchain.WorkerId == "gsharp-stable" &&
            toolchain.AllowedReferenceSetIds.SequenceEqual(["net10-ref"], StringComparer.Ordinal));
        Assert.All(
            catalog.Compatibility.Where(static rule =>
                rule.Kind == CompatibilityRuleKind.ToolchainReferenceSet &&
                rule.FromId is "fsharp-stable" or "gsharp-stable" or "gsharp-legacy-0.3.8"),
            static rule => Assert.True(rule.Allowed, $"{rule.FromId} -> {rule.ToId} must be selectable."));
        Assert.Contains(catalog.Languages, static language =>
            language.Id == "php" &&
            language.DefaultFileName == "index.php" &&
            language.DefaultToolchainId == "peachpie-stable" &&
            language.Capabilities.SequenceEqual(["diagnostics", "multi-file"], StringComparer.Ordinal));
        Assert.Contains(catalog.Toolchains, static toolchain =>
            toolchain.Id == "peachpie-stable" &&
            toolchain.WorkerId == "peachpie-stable" &&
            toolchain.ResolvedVersion == "1.1.13" &&
            toolchain.AllowedReferenceSetIds.SequenceEqual(["net10-ref"], StringComparer.Ordinal) &&
            !toolchain.Capabilities.Contains("lsp", StringComparer.Ordinal));
        Assert.Contains(catalog.Toolchains, static toolchain =>
            toolchain.Id == "mobius-ilasm-stable" &&
            toolchain.ResolvedVersion == "0.1.0" &&
            toolchain.Capabilities.Contains("code-actions", StringComparer.Ordinal) &&
            toolchain.Availability.Installed);
        Assert.Contains(catalog.Languages, static language =>
            language.Id == "il" &&
            language.Capabilities.Contains("code-actions", StringComparer.Ordinal));
        Assert.Contains(catalog.Runtimes, static runtime =>
            runtime.Id == "dotnet-10-linux-x64" &&
            runtime.DisplayName == ".NET 10.0.10 / Linux x64" &&
            runtime.ResolvedVersion == "10.0.10");
        Assert.Contains(catalog.Runtimes, static runtime =>
            runtime.Id == "dotnet-11-preview-linux-x64" &&
            runtime.DisplayName == ".NET 11.0.0-preview.6.26359.118 / Linux x64" &&
            runtime.ResolvedVersion == "11.0.0-preview.6.26359.118");
        Assert.Contains(catalog.ReferenceSets, static reference =>
            reference.Id == "net10-ref" &&
            reference.DisplayName == "10.0.10" &&
            reference.TargetFramework == "net10.0" &&
            reference.Availability.Installed);
        Assert.Contains(catalog.ReferenceSets, static reference =>
            reference.Id == "net11-preview-ref" &&
            reference.DisplayName == "11.0.0-preview.6.26359.118" &&
            reference.Availability.Installed);
        Assert.Contains(catalog.ReferenceSets, static reference =>
            reference.Id == "const-generics-ref" &&
            reference.DisplayName == "Const Generics" &&
            reference.Availability.Installed);
        Assert.Contains(catalog.ReferenceSets, static reference =>
            reference.Id == "netfx48-ref" &&
            reference.DisplayName == ".NET Framework 4.8" &&
            reference.Availability.Installed);
        Assert.Contains(catalog.ReferenceSets, static reference =>
            reference.Id == "netfx48-managed-ref" &&
            reference.TargetFramework == "net48" &&
            reference.RuntimeFamily == "netfx-clr-wine" &&
            reference.Digest == "sha512-XWKgyeNadNcTQaIVvQB8BrdCNrEar6fo/de1OdQRZ9HFy0jcBSaM8IV5q64ZampsSnC8AlTsACaGZUuoFw41RA==" &&
            reference.RequiredRuntimeFeatureTags.Count == 0);
        string[] healthyPromotedPresets =
        [
            "csharp-roslyn-stable-dotnet-core-2.0",
            "visual-basic-roslyn-stable-dotnet-core-2.0",
            "csharp-roslyn-stable-dotnet-5",
            "visual-basic-roslyn-stable-dotnet-11-preview",
            "csharp-roslyn-stable-netfx48-mono-6.12-linux-x64",
            "visual-basic-roslyn-stable-netfx48-netfx20",
            "csharp-roslyn-stable-netfx48-netfx48",
            "csharp-const-generics",
            "jsharp-vjc-net20"
        ];
        Assert.All(
            healthyPromotedPresets,
            id => Assert.Contains(catalog.Presets, preset => preset.Id == id && preset.Availability.IsSelectable));
        Assert.All(catalog.Presets, static preset => Assert.Equal("decompiled-csharp", preset.DefaultOutputId));
        Assert.Contains(catalog.Outputs, static output => output.Id == "compile-check");
        Assert.Contains(catalog.Outputs, static output => output.Id == "il-verify");
        Assert.Contains(catalog.Outputs, static output =>
            output.Id == "javascript" &&
            output.Renderer == "javascript" &&
            output.DisplayName == "JavaScript (JSIL)" &&
            output.OutputArtifactFormat == "javascript-v1");
        Assert.Contains(catalog.ArtifactProcessors, static processor =>
            processor.Id == "artifacts-jsil" &&
            processor.WorkerId == "artifacts-jsil" &&
            processor.AcceptsArtifactFormats.SequenceEqual(["dotnet-managed-pe-v1"], StringComparer.Ordinal) &&
            processor.ProducesArtifactFormats.SequenceEqual(["javascript-v1"], StringComparer.Ordinal) &&
            processor.Capabilities.SequenceEqual(["javascript"], StringComparer.Ordinal));
        Assert.Contains(catalog.Outputs, static output =>
            output.Id == "execution-flow" && output.RequiredCapabilities.Contains("portable-pdb", StringComparer.Ordinal));
        Assert.Contains(catalog.Presets, static preset =>
            preset.Id == "php-peachpie-net10" &&
            preset.DefaultOutputId == "decompiled-csharp" &&
            preset.DefaultRuntimeId == "dotnet-10-linux-x64");
        Assert.Contains(catalog.Languages, static language =>
            language.Id == "minilang" &&
            language.MonacoLanguageId == "minilang");
        Assert.Contains(catalog.ArtifactProcessors, static processor =>
            processor.Id == "il-assembler" &&
            processor.Transformations.Any(transformation =>
                transformation.Id == "assemble-il" &&
                transformation.InputArtifactFormat == "cil-text-v1" &&
                transformation.OutputArtifactFormat == "dotnet-managed-pe-v1"));
        Assert.Contains(catalog.Languages, static language =>
            language.Id == "cppcli" &&
            language.DefaultToolchainId == "msvc-cppcli-netfx48" &&
            language.DefaultFileName == "Program.cpp");
        Assert.Contains(catalog.Toolchains, static toolchain =>
            toolchain.Id == "msvc-cppcli-netfx48" &&
            toolchain.DefaultReferenceSetId == "netfx48-ref" &&
            toolchain.ProducesArtifactFormats.SequenceEqual(
                ["dotnet-framework-mixed-pe-v1"],
                StringComparer.Ordinal));
        Assert.Contains(catalog.Runtimes, static runtime =>
            runtime.Id == "wine-netfx48-linux-x64" &&
            runtime.Family == "netfx-clr-wine" &&
            runtime.Capabilities.SequenceEqual(["run", "jit-asm"], StringComparer.Ordinal) &&
            runtime.AcceptedArtifactFormats.SequenceEqual(
                ["dotnet-framework-managed-pe-v1", "dotnet-framework-mixed-pe-v1"],
                StringComparer.Ordinal));
        Assert.Contains(catalog.Languages, static language =>
            language.Id == "jsharp" &&
            language.DisplayName == "J#" &&
            language.DefaultToolchainId == "vjc-jsharp20" &&
            language.DefaultFileName == "Program.jsl" &&
            language.Extensions.SequenceEqual([".jsl"], StringComparer.Ordinal) &&
            language.Capabilities.SequenceEqual(["diagnostics"], StringComparer.Ordinal));
        Assert.Contains(catalog.Toolchains, static toolchain =>
            toolchain.Id == "vjc-jsharp20" &&
            toolchain.DisplayName == "Visual J# 2.0 Second Edition" &&
            toolchain.WorkerId == "vjc-jsharp20" &&
            toolchain.ResolvedVersion == "2.0.50727.937" &&
            toolchain.SupportedLanguageIds.SequenceEqual(["jsharp"], StringComparer.Ordinal) &&
            toolchain.AllowedReferenceSetIds.SequenceEqual(["jsharp20-ref"], StringComparer.Ordinal) &&
            toolchain.ProducesArtifactFormats.SequenceEqual(
                ["dotnet-framework-managed-pe-v1"],
                StringComparer.Ordinal) &&
            toolchain.Capabilities.SequenceEqual(
                ["diagnostics", "compile-check", "managed-pe"],
                StringComparer.Ordinal));
        Assert.Contains(catalog.ReferenceSets, static reference =>
            reference.Id == "jsharp20-ref" &&
            reference.DisplayName == "Visual J# 2.0 / CLR 2.0 Reference Assemblies" &&
            reference.TargetFramework == "net20" &&
            reference.Digest == "sha256:25288dc53b3190f14a65ebf96258601a262ebd4a8fa68e4881c897258b122013" &&
            reference.RuntimeFamily == "netfx-clr-wine" &&
            reference.RequiredRuntimeFeatureTags.SequenceEqual(
                ["runtime.jsharp20-wine"],
                StringComparer.Ordinal));
        Assert.Contains(catalog.Runtimes, static runtime =>
            runtime.Id == "wine-jsharp20-linux-x64" &&
            runtime.DisplayName == "Visual J# 2.0 / CLR 2.0 / Wine 9.0" &&
            runtime.Family == "netfx-clr-wine" &&
            runtime.Rid == "linux-x64" &&
            runtime.Architecture == "x64" &&
            runtime.AcceptedArtifactFormats.SequenceEqual(
                ["dotnet-framework-managed-pe-v1"],
                StringComparer.Ordinal) &&
            runtime.Capabilities.SequenceEqual(["run"], StringComparer.Ordinal) &&
            runtime.AcceptedFrameworks.Count == 1 &&
            runtime.AcceptedFrameworks[0].Name == ".NETFramework" &&
            runtime.AcceptedFrameworks[0].ExactVersion == "2.0" &&
            runtime.ProvidedRuntimeFeatureTags.SequenceEqual(
                ["runtime.jsharp20-wine"],
                StringComparer.Ordinal));
        Assert.Contains(catalog.Presets, static preset =>
            preset.Id == "jsharp-vjc-net20" &&
            preset.DisplayName == "J# / Visual J# 2.0 / CLR 2.0" &&
            preset.LanguageId == "jsharp" &&
            preset.ToolchainId == "vjc-jsharp20" &&
            preset.ReferenceSetId == "jsharp20-ref" &&
            preset.DefaultOutputId == "decompiled-csharp" &&
            preset.DefaultRuntimeId == "wine-jsharp20-linux-x64");
    }

    [Fact]
    public async Task DevelopmentReleaseLockUsesExactInputsWithoutLocalImageIds()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "lock.json");

        var releaseLock = await CatalogLoader.LoadReleaseLockAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal("10.0.10", releaseLock.Components["dotnet-10-linux-x64"].ResolvedVersion);
        Assert.Equal("11.0.0-preview.6.26359.118", releaseLock.Components["dotnet-11-preview-linux-x64"].ResolvedVersion);
        Assert.Equal("5.6.0", releaseLock.Components["roslyn-stable"].ResolvedVersion);
        Assert.Equal(
            releaseLock.Components["roslyn-stable"] with { PatchDigest = null, ImageId = null },
            releaseLock.Components["roslyn-stable-netfx48"]);
        Assert.Equal("0.1.0", releaseLock.Components["mobius-ilasm-stable"].ResolvedVersion);
        Assert.Equal("0.3.33", releaseLock.Components["gsharp-stable"].ResolvedVersion);
        Assert.Equal(
            "aaf35bb8d5e1e8704e982ad0ab95263451bd2d3d",
            releaseLock.Components["gsharp-source"].Commit);
        Assert.Equal(
            "sha256:f52d21ef09b198bad69b7ac8dd5f6d2eaa91216b80bfc22e9610a1fef28f06d4",
            releaseLock.Components["gsharp-source"].Digest);
        Assert.Equal("0.3.8", releaseLock.Components["gsharp-legacy-0.3.8"].ResolvedVersion);
        Assert.Equal(
            "723cbdaeb3374ce9c7b36a6bf2c4e97ba25edf01",
            releaseLock.Components["gsharp-legacy-0.3.8-source"].Commit);
        Assert.Equal(
            "8.0.0-beta.23516.4",
            releaseLock.Components["const-generics-versiontools"].ResolvedVersion);
        Assert.Equal(
            "sha256:ad0a9c0ef28dd49bd2bfd7eb1be7ec355bd11a9671c0e2d8f1c08016b56be1bf",
            releaseLock.Components["const-generics-versiontools"].Digest);
        Assert.DoesNotContain(releaseLock.Components, static pair => pair.Value.Kind == "frontend");
        Assert.All(releaseLock.Components.Values, static component => Assert.Null(component.PatchDigest));
        Assert.Null(releaseLock.Components["const-generics-linux-x64"].ImageId);
        Assert.Null(releaseLock.Components["roslyn-const-generics"].ImageId);
        Assert.Null(releaseLock.Components["artifacts-const-generics"].ImageId);
        Assert.Null(releaseLock.Components["gsharp-stable"].ImageId);
        Assert.Equal("source", releaseLock.Components["msvc-wine-source"].Kind);
        Assert.Equal("operator-image", releaseLock.Components["msvc-cppcli-private-image"].Kind);
        Assert.Equal("operator-image", releaseLock.Components["msvc-cppcli-prepared-base"].Kind);
        Assert.Equal("toolchain", releaseLock.Components["msvc-cppcli-netfx48"].Kind);
        Assert.Equal("reference-set", releaseLock.Components["netfx48-ref"].Kind);
        Assert.Equal("reference-set", releaseLock.Components["netfx48-managed-ref"].Kind);
        Assert.Equal(
            "Microsoft.NETFramework.ReferenceAssemblies.net48",
            releaseLock.Components["netfx48-managed-ref"].Package);
        Assert.Equal(
            "sha512-XWKgyeNadNcTQaIVvQB8BrdCNrEar6fo/de1OdQRZ9HFy0jcBSaM8IV5q64ZampsSnC8AlTsACaGZUuoFw41RA==",
            releaseLock.Components["netfx48-managed-ref"].PackageContentHash);
        Assert.Equal("runtime", releaseLock.Components["wine-netfx48-linux-x64"].Kind);
        Assert.All(
            CppCliSharedReleaseComponentIds,
            id => Assert.Equal(
                "sha256:463e30099e98f760e5f67cbe5aedeae5679f3fa4d3d1e9f9fee5232a5c06e743",
                releaseLock.Components[id].Digest));
        Assert.Equal(
            "sha256:dedd9a2d14337930bbe73870a3b4a814838a96401657dc19f3c4a91fe34b0458",
            releaseLock.Components["wine-netfx48-linux-x64"].Digest);
        Assert.Equal(
            "sha256:dfd2473d9faae804d8514e583cec77fe5622c3c955d2e97eeaa3a7952969e3e8",
            releaseLock.Components["msvc-cppcli-prepared-base"].Digest);
        Assert.Equal(
            "docker://localhost:5000/sharplabnext/msvc-cppcli-prepared-base@sha256:dfd2473d9faae804d8514e583cec77fe5622c3c955d2e97eeaa3a7952969e3e8",
            releaseLock.Components["msvc-cppcli-prepared-base"].SourceUri);
    }

    [Fact]
    public void DuplicateLanguageIdsAreRejected()
    {
        var language = new LanguageManifest
        {
            Id = "csharp",
            DisplayName = "C#",
            MonacoLanguageId = "csharp",
            Extensions = [".cs"],
            DefaultFileName = "Program.cs",
            DefaultSource = "",
            DefaultToolchainId = "missing",
            Capabilities = []
        };
        var catalog = new CatalogDocument
        {
            SchemaVersion = 1,
            Revision = "test",
            ReleaseId = "test",
            Languages = [language, language],
            Toolchains = [],
            ReferenceSets = [],
            Runtimes = [],
            ArtifactProcessors = [],
            Outputs = [],
            Compatibility = [],
            Presets = []
        };

        var errors = CatalogValidator.Validate(catalog);

        Assert.Contains(errors, static error => error.Contains("Duplicate language id", StringComparison.Ordinal));
    }

    [Fact]
    public void ProcessorTransformationMustUseDeclaredFormats()
    {
        var processor = new ArtifactProcessorManifest
        {
            Id = "processor",
            DisplayName = "Processor",
            ResolvedVersion = "1.0.0",
            WorkerId = "processor",
            AcceptsArtifactFormats = ["input-v1"],
            ProducesArtifactFormats = ["output-v1"],
            Capabilities = ["convert"],
            Transformations =
            [
                new ArtifactTransformationManifest
                {
                    Id = "convert",
                    InputArtifactFormat = "wrong-input-v1",
                    OutputArtifactFormat = "wrong-output-v1"
                }
            ],
            Availability = new ComponentAvailability { Installed = true, Health = "healthy" }
        };
        var catalog = new CatalogDocument
        {
            SchemaVersion = 1,
            Revision = "test",
            ReleaseId = "test",
            Languages = [],
            Toolchains = [],
            ReferenceSets = [],
            Runtimes = [],
            ArtifactProcessors = [processor],
            Outputs = [],
            Compatibility = [],
            Presets = []
        };

        var errors = CatalogValidator.Validate(catalog);

        Assert.Contains(errors, static error => error.Contains("wrong-input-v1", StringComparison.Ordinal));
        Assert.Contains(errors, static error => error.Contains("wrong-output-v1", StringComparison.Ordinal));
    }

    [Fact]
    public void ArtifactRuntimeRuleMustUseAFormatAcceptedByTheRuntime()
    {
        var runtime = new RuntimeManifest
        {
            Id = "wine-netfx48-linux-x64",
            DisplayName = "Wine",
            Family = "netfx-clr-wine",
            ResolvedVersion = "wine-9.0+netfx48",
            Rid = "linux-x64",
            Architecture = "x64",
            AcceptedArtifactFormats = ["dotnet-framework-mixed-pe-v1"],
            Capabilities = ["run"],
            Availability = new ComponentAvailability { Installed = true, Health = "healthy" }
        };
        var catalog = new CatalogDocument
        {
            SchemaVersion = 1,
            Revision = "test",
            ReleaseId = "test",
            Languages = [],
            Toolchains = [],
            ReferenceSets = [],
            Runtimes = [runtime],
            ArtifactProcessors = [],
            Outputs = [],
            Compatibility =
            [
                new CompatibilityRule
                {
                    Id = "invalid-framework-route",
                    Kind = CompatibilityRuleKind.ArtifactRuntime,
                    FromId = "dotnet-framework-managed-pe-v1",
                    ToId = runtime.Id,
                    Allowed = true
                }
            ],
            Presets = []
        };

        var errors = CatalogValidator.Validate(catalog);

        Assert.Contains(errors, static error =>
            error.Contains("dotnet-framework-managed-pe-v1", StringComparison.Ordinal) &&
            error.Contains("wine-netfx48-linux-x64", StringComparison.Ordinal));
    }

    [Fact]
    public void LifecycleAllowsExplicitlyVisibleLegacyButRejectsInvalidReplacement()
    {
        var referenceSet = new ReferenceSetManifest
        {
            Id = "legacy-ref",
            DisplayName = "Legacy",
            TargetFramework = "net20",
            Digest = "sha512-test",
            RuntimeFamily = "coreclr",
            SupportStatus = "legacy",
            Visibility = "visible",
            ReplacementReferenceSetId = "legacy-ref",
            Availability = new ComponentAvailability { Installed = true, Health = "healthy" }
        };
        var catalog = new CatalogDocument
        {
            SchemaVersion = 1,
            Revision = "test",
            ReleaseId = "test",
            Languages = [],
            Toolchains = [],
            ReferenceSets = [referenceSet],
            Runtimes = [],
            ArtifactProcessors = [],
            Outputs = [],
            Compatibility = [],
            Presets = []
        };

        var errors = CatalogValidator.Validate(catalog);

        Assert.DoesNotContain(errors, static error => error.Contains("must be hidden", StringComparison.Ordinal));
        Assert.Contains(errors, static error => error.Contains("cannot replace itself", StringComparison.Ordinal));
    }
}
