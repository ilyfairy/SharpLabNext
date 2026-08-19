using SharpLabNext.Catalog;
using SharpLabNext.Contracts;
using SharpLabNext.PipelineResolver;
using Resolver = SharpLabNext.PipelineResolver.PipelineResolver;

namespace SharpLabNext.UnitTests;

public sealed class PipelineResolverTests
{
    [Fact]
    public async Task UnavailableRuntimeCannotBeSelectedEvenWhenAStaleCompatibilityEdgeRemains()
    {
        var catalog = await LoadCatalogAsync();
        var runtime = catalog.Runtimes.Single(static item => item.Id == "dotnet-11-preview-linux-x64");
        catalog = catalog with
        {
            Runtimes =
            [
                .. catalog.Runtimes.Where(item => item.Id != runtime.Id),
                runtime with
                {
                    Availability = new ComponentAvailability
                    {
                        Installed = false,
                        Health = "not-installed",
                        Reason = "candidate image is not installed"
                    }
                }
            ]
        };

        var request = new ResolveSelectionRequest(
            "csharp",
            "roslyn-main",
            "net11-preview-ref",
            "run",
            runtime.Id,
            BuildConfiguration.Release,
            catalog.Revision,
            0);

        var exception = Assert.Throws<SelectionResolutionException>(() =>
            Resolver.Resolve(catalog, request, DateTimeOffset.UnixEpoch));

        Assert.Equal(SelectionField.Runtime, exception.Field);
    }

    [Fact]
    public async Task FreshCSharpRunUsesRoslynMainAndTheLatestPreviewRuntime()
    {
        var catalog = await LoadCatalogAsync();
        var request = new ResolveSelectionRequest(
            "csharp",
            null,
            null,
            "run",
            null,
            BuildConfiguration.Release,
            catalog.Revision,
            1);

        var result = Resolver.Resolve(catalog, request, DateTimeOffset.UnixEpoch);

        Assert.Equal("roslyn-main", result.EffectiveSelection.ToolchainId);
        Assert.Equal("net11-preview-ref", result.EffectiveSelection.ReferenceSetId);
        Assert.Equal("dotnet-11-preview-linux-x64", result.EffectiveSelection.RuntimeId);
        Assert.Equal("roslyn-main", result.PipelinePlan.CompilerWorkerId);
        Assert.Equal("dotnet-11-preview-linux-x64", result.PipelinePlan.RuntimeId);
        Assert.Contains(result.SelectionChanges, static change =>
            change.Field == SelectionField.Toolchain &&
            change.Reason == SelectionChangeReason.DefaultApplied);
        Assert.Contains(result.SelectionChanges, static change =>
            change.Field == SelectionField.ReferenceSet &&
            change.Reason == SelectionChangeReason.DefaultApplied);
        Assert.Contains(result.SelectionChanges, static change =>
            change.Field == SelectionField.Runtime &&
            change.Reason == SelectionChangeReason.DefaultApplied);
    }

    [Fact]
    public async Task ChangingConstGenericsSelectionToVisualBasicNormalizesToolchainAndReferenceSet()
    {
        var catalog = await LoadCatalogAsync();
        var request = new ResolveSelectionRequest(
            "visual-basic",
            "roslyn-const-generics",
            "const-generics-ref",
            "ast",
            null,
            BuildConfiguration.Release,
            catalog.Revision,
            7);

        var result = Resolver.Resolve(catalog, request, DateTimeOffset.UnixEpoch);

        Assert.Equal("roslyn-stable", result.EffectiveSelection.ToolchainId);
        Assert.Equal("net10-ref", result.EffectiveSelection.ReferenceSetId);
        Assert.Contains(result.SelectionChanges, static change => change.Reason == SelectionChangeReason.UnsupportedByLanguage);
        Assert.Contains(result.SelectionChanges, static change => change.Reason == SelectionChangeReason.IncompatibleReferenceSet);
    }

    [Fact]
    public async Task Net10ArtifactCanUseApprovedNet11RuntimeWithoutChangingReferenceSet()
    {
        var catalog = await LoadCatalogAsync();
        var request = new ResolveSelectionRequest(
            "csharp",
            "roslyn-stable",
            "net10-ref",
            "jit-asm",
            "dotnet-11-preview-linux-x64",
            BuildConfiguration.Release,
            catalog.Revision,
            8);

        var result = Resolver.Resolve(catalog, request, DateTimeOffset.UnixEpoch);

        Assert.Equal("net10-ref", result.EffectiveSelection.ReferenceSetId);
        Assert.Equal("dotnet-11-preview-linux-x64", result.EffectiveSelection.RuntimeId);
        Assert.Equal("runtime-job-default", result.PipelinePlan.SecurityPolicyId);
        Assert.Equal(PipelineStageKind.Jit, result.PipelinePlan.Stages[^1].Kind);
    }

    [Fact]
    public async Task Net11ReferenceSetCannotRunOnNet10Runtime()
    {
        var catalog = await LoadCatalogAsync();
        var request = new ResolveSelectionRequest(
            "csharp",
            "roslyn-stable",
            "net11-preview-ref",
            "run",
            "dotnet-10-linux-x64",
            BuildConfiguration.Release,
            catalog.Revision,
            9);

        var exception = Assert.Throws<SelectionResolutionException>(() =>
            Resolver.Resolve(catalog, request, DateTimeOffset.UnixEpoch));

        Assert.Equal("unsupported-capability", exception.Code);
        Assert.Equal(SelectionField.Runtime, exception.Field);
    }

    [Fact]
    public async Task ConstGenericsArtifactCannotUseOrdinaryRuntime()
    {
        var catalog = await LoadCatalogAsync();
        var request = new ResolveSelectionRequest(
            "csharp",
            "roslyn-const-generics",
            "const-generics-ref",
            "run",
            "dotnet-10-linux-x64",
            BuildConfiguration.Release,
            catalog.Revision,
            10);

        var exception = Assert.Throws<SelectionResolutionException>(() =>
            Resolver.Resolve(catalog, request, DateTimeOffset.UnixEpoch));

        Assert.Equal(SelectionField.Runtime, exception.Field);
    }

    [Fact]
    public async Task OrdinaryArtifactCannotUseConstGenericsRuntime()
    {
        var catalog = await LoadCatalogAsync();
        var request = new ResolveSelectionRequest(
            "csharp",
            "roslyn-stable",
            "net10-ref",
            "run",
            "const-generics-linux-x64",
            BuildConfiguration.Release,
            catalog.Revision,
            10);

        var exception = Assert.Throws<SelectionResolutionException>(() =>
            Resolver.Resolve(catalog, request, DateTimeOffset.UnixEpoch));

        Assert.Equal(SelectionField.Runtime, exception.Field);
    }

    [Fact]
    public async Task RuntimeIsRemovedFromSourceOnlyOutput()
    {
        var catalog = await LoadCatalogAsync();
        var request = new ResolveSelectionRequest(
            "csharp",
            "roslyn-stable",
            "net10-ref",
            "ast",
            "dotnet-10-linux-x64",
            BuildConfiguration.Debug,
            catalog.Revision,
            11);

        var result = Resolver.Resolve(catalog, request, DateTimeOffset.UnixEpoch);

        Assert.Null(result.EffectiveSelection.RuntimeId);
        Assert.Contains(result.SelectionChanges, static change => change.Reason == SelectionChangeReason.RuntimeNotRequired);
    }

    [Fact]
    public async Task ExplainIsACSharpOnlySingleStageToolchainOperation()
    {
        var catalog = await LoadCatalogAsync();
        var request = new ResolveSelectionRequest(
            "csharp",
            "roslyn-stable",
            "net10-ref",
            "explain",
            null,
            BuildConfiguration.Release,
            catalog.Revision,
            12);

        var result = Resolver.Resolve(catalog, request, DateTimeOffset.UnixEpoch);

        var stage = Assert.Single(result.PipelinePlan.Stages);
        Assert.Equal(PipelineStageKind.Explain, stage.Kind);
        Assert.Equal("roslyn-stable", stage.ProviderId);
        Assert.Contains("explain", result.EffectiveCapabilities.OutputCapabilities);

        var visualBasic = request with { LanguageId = "visual-basic", WorkspaceRevision = 13 };
        var exception = Assert.Throws<SelectionResolutionException>(() =>
            Resolver.Resolve(catalog, visualBasic, DateTimeOffset.UnixEpoch));
        Assert.Equal(SelectionField.Output, exception.Field);
    }

    [Fact]
    public async Task ExecutionFlowBuildsTransformsAndRunsTheDerivedArtifact()
    {
        var catalog = await LoadCatalogAsync();
        var request = new ResolveSelectionRequest(
            "csharp",
            "roslyn-stable",
            "net10-ref",
            "execution-flow",
            "dotnet-10-linux-x64",
            BuildConfiguration.Release,
            catalog.Revision,
            14);

        var result = Resolver.Resolve(catalog, request, DateTimeOffset.UnixEpoch);

        Assert.Collection(
            result.PipelinePlan.Stages,
            stage => Assert.Equal(PipelineStageKind.Build, stage.Kind),
            stage =>
            {
                Assert.Equal(PipelineStageKind.Transform, stage.Kind);
                Assert.Equal("runtime-instrumentation-v1", stage.Id);
                Assert.Equal("artifacts-default", stage.ProviderId);
            },
            stage =>
            {
                Assert.Equal(PipelineStageKind.Run, stage.Kind);
                Assert.Equal("execution-flow", stage.Id);
                Assert.Equal("dotnet-10-linux-x64", stage.ProviderId);
            });
    }

    [Fact]
    public async Task RunIlBuildsTransformsAndRendersWithoutSelectingARuntime()
    {
        var catalog = await LoadCatalogAsync();
        var request = new ResolveSelectionRequest(
            "csharp",
            "roslyn-stable",
            "net10-ref",
            "run-il",
            "dotnet-10-linux-x64",
            BuildConfiguration.Release,
            catalog.Revision,
            15);

        var result = Resolver.Resolve(catalog, request, DateTimeOffset.UnixEpoch);

        Assert.Null(result.EffectiveSelection.RuntimeId);
        Assert.Collection(
            result.PipelinePlan.Stages,
            stage => Assert.Equal(PipelineStageKind.Build, stage.Kind),
            stage =>
            {
                Assert.Equal(PipelineStageKind.Transform, stage.Kind);
                Assert.Equal("runtime-instrumentation-v1", stage.Id);
            },
            stage =>
            {
                Assert.Equal(PipelineStageKind.Render, stage.Kind);
                Assert.Equal("run-il", stage.Id);
            });
    }

    [Theory]
    [InlineData("run", PipelineStageKind.Run)]
    [InlineData("jit-asm", PipelineStageKind.Jit)]
    public async Task ThirdPartyCilLanguageUsesApprovedAssemblerBeforeRuntime(
        string outputId,
        PipelineStageKind terminalKind)
    {
        var catalog = await LoadCatalogAsync();
        var request = new ResolveSelectionRequest(
            "minilang",
            "minilang-stable",
            "net10-ref",
            outputId,
            "dotnet-10-linux-x64",
            BuildConfiguration.Release,
            catalog.Revision,
            16);

        var result = Resolver.Resolve(catalog, request, DateTimeOffset.UnixEpoch);

        Assert.Collection(
            result.PipelinePlan.Stages,
            stage =>
            {
                Assert.Equal(PipelineStageKind.Build, stage.Kind);
                Assert.Equal("cil-text-v1", stage.OutputArtifactFormat);
                Assert.Equal("minilang-stable", stage.ProviderId);
            },
            stage =>
            {
                Assert.Equal(PipelineStageKind.Transform, stage.Kind);
                Assert.Equal("assemble-il", stage.Id);
                Assert.Equal("il-assembler", stage.ProviderId);
                Assert.Equal("cil-text-v1", stage.InputArtifactFormat);
                Assert.Equal("dotnet-managed-pe-v1", stage.OutputArtifactFormat);
            },
            stage =>
            {
                Assert.Equal(terminalKind, stage.Kind);
                Assert.Equal("dotnet-10-linux-x64", stage.ProviderId);
                Assert.Equal("dotnet-managed-pe-v1", stage.InputArtifactFormat);
            });
    }

    [Fact]
    public async Task ThirdPartyGeneratedIlUsesTextRendererWithoutUnneededAssembly()
    {
        var catalog = await LoadCatalogAsync();
        var request = new ResolveSelectionRequest(
            "minilang",
            "minilang-stable",
            "net10-ref",
            "generated-il",
            "dotnet-10-linux-x64",
            BuildConfiguration.Debug,
            catalog.Revision,
            17);

        var result = Resolver.Resolve(catalog, request, DateTimeOffset.UnixEpoch);

        Assert.Null(result.EffectiveSelection.RuntimeId);
        Assert.Collection(
            result.PipelinePlan.Stages,
            stage => Assert.Equal(PipelineStageKind.Build, stage.Kind),
            stage =>
            {
                Assert.Equal(PipelineStageKind.Render, stage.Kind);
                Assert.Equal("generated-il", stage.Id);
                Assert.Equal("il-assembler", stage.ProviderId);
                Assert.Equal("cil-text-v1", stage.InputArtifactFormat);
            });
    }

    [Theory]
    [InlineData("csharp", "il")]
    [InlineData("csharp", "decompiled-csharp")]
    [InlineData("visual-basic", "il")]
    [InlineData("visual-basic", "decompiled-csharp")]
    public async Task ManagedNetFxUsesTheDefaultArtifactProcessor(
        string languageId,
        string outputId)
    {
        var catalog = await LoadCatalogAsync();
        var request = new ResolveSelectionRequest(
            languageId,
            "roslyn-stable-netfx48",
            "netfx48-managed-ref",
            outputId,
            null,
            BuildConfiguration.Release,
            catalog.Revision,
            18);

        var result = Resolver.Resolve(catalog, request, DateTimeOffset.UnixEpoch);

        Assert.Null(result.EffectiveSelection.RuntimeId);
        Assert.Collection(
            result.PipelinePlan.Stages,
            stage =>
            {
                Assert.Equal(PipelineStageKind.Build, stage.Kind);
                Assert.Equal("roslyn-stable-netfx48", stage.ProviderId);
                Assert.Equal("dotnet-framework-managed-pe-v1", stage.OutputArtifactFormat);
            },
            stage =>
            {
                Assert.Equal(PipelineStageKind.Render, stage.Kind);
                Assert.Equal(outputId, stage.Id);
                Assert.Equal("artifacts-default", stage.ProviderId);
                Assert.Equal("dotnet-framework-managed-pe-v1", stage.InputArtifactFormat);
            });
    }

    [Theory]
    [InlineData("csharp")]
    [InlineData("visual-basic")]
    public async Task ManagedNetFxRunUsesWineRuntimeWhenSelected(string languageId)
    {
        var catalog = await LoadCatalogAsync();
        var request = new ResolveSelectionRequest(
            languageId,
            "roslyn-stable-netfx48",
            "netfx48-managed-ref",
            "run",
            "wine-netfx48-linux-x64",
            BuildConfiguration.Release,
            catalog.Revision,
            19);

        var result = Resolver.Resolve(catalog, request, DateTimeOffset.UnixEpoch);

        Assert.Equal("runtime-job-wine-netfx", result.PipelinePlan.SecurityPolicyId);
        Assert.Collection(
            result.PipelinePlan.Stages,
            stage =>
            {
                Assert.Equal(PipelineStageKind.Build, stage.Kind);
                Assert.Equal("dotnet-framework-managed-pe-v1", stage.OutputArtifactFormat);
            },
            stage =>
            {
                Assert.Equal(PipelineStageKind.Run, stage.Kind);
                Assert.Equal("wine-netfx48-linux-x64", stage.ProviderId);
                Assert.Equal("dotnet-framework-managed-pe-v1", stage.InputArtifactFormat);
            });
    }

    [Theory]
    [InlineData("csharp")]
    [InlineData("visual-basic")]
    public async Task ManagedNetFx48CanRunOnMonoWhenTheMonoCandidateIsSelectable(string languageId)
    {
        var catalog = EnableMono(await LoadCatalogAsync());
        var request = new ResolveSelectionRequest(
            languageId,
            "roslyn-stable-netfx48",
            "netfx48-managed-ref",
            "run",
            "mono-6.12-linux-x64",
            BuildConfiguration.Release,
            catalog.Revision,
            19);

        var result = Resolver.Resolve(catalog, request, DateTimeOffset.UnixEpoch);

        Assert.Equal("mono-6.12-linux-x64", result.EffectiveSelection.RuntimeId);
        Assert.Collection(
            result.PipelinePlan.Stages,
            stage => Assert.Equal("dotnet-framework-managed-pe-v1", stage.OutputArtifactFormat),
            stage =>
            {
                Assert.Equal(PipelineStageKind.Run, stage.Kind);
                Assert.Equal("mono-6.12-linux-x64", stage.ProviderId);
                Assert.Equal("dotnet-framework-managed-pe-v1", stage.InputArtifactFormat);
            });
    }

    [Fact]
    public async Task CppCliMixedPeCannotRunOnMonoWhenTheMonoCandidateIsSelectable()
    {
        var catalog = EnableMono(await LoadCatalogAsync());
        var request = new ResolveSelectionRequest(
            "cppcli",
            "msvc-cppcli-netfx48",
            "netfx48-ref",
            "run",
            "mono-6.12-linux-x64",
            BuildConfiguration.Release,
            catalog.Revision,
            19);

        var exception = Assert.Throws<SelectionResolutionException>(() =>
            Resolver.Resolve(catalog, request, DateTimeOffset.UnixEpoch));

        Assert.Equal(SelectionField.Runtime, exception.Field);
    }

    [Theory]
    [InlineData("csharp", "compile-check,ast,il,decompiled-csharp,run,explain")]
    [InlineData("visual-basic", "compile-check,ast,il,decompiled-csharp,run")]
    public async Task ManagedNetFxOffersOnlyTruthfulOutputs(
        string languageId,
        string expectedOutputIds)
    {
        var catalog = await LoadCatalogAsync();
        var request = new ResolveSelectionRequest(
            languageId,
            "roslyn-stable-netfx48",
            "netfx48-managed-ref",
            "decompiled-csharp",
            null,
            BuildConfiguration.Release,
            catalog.Revision,
            20);

        var result = Resolver.Resolve(catalog, request, DateTimeOffset.UnixEpoch);

        Assert.Equal(
            expectedOutputIds.Split(','),
            result.EffectiveCapabilities.OutputCapabilities);
    }

    [Theory]
    [InlineData("il-verify")]
    [InlineData("jit-asm")]
    [InlineData("execution-flow")]
    [InlineData("run-il")]
    public async Task ManagedNetFxRejectsCoreClrOnlyOutputs(string outputId)
    {
        var catalog = await LoadCatalogAsync();
        var request = new ResolveSelectionRequest(
            "csharp",
            "roslyn-stable-netfx48",
            "netfx48-managed-ref",
            outputId,
            outputId is "jit-asm" or "execution-flow" ? "wine-netfx48-linux-x64" : null,
            BuildConfiguration.Release,
            catalog.Revision,
            21);

        Assert.Throws<SelectionResolutionException>(() =>
            Resolver.Resolve(catalog, request, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public async Task ManagedNetFxCannotRunOnCoreClr()
    {
        var catalog = await LoadCatalogAsync();
        var request = new ResolveSelectionRequest(
            "csharp",
            "roslyn-stable-netfx48",
            "netfx48-managed-ref",
            "run",
            "dotnet-10-linux-x64",
            BuildConfiguration.Release,
            catalog.Revision,
            22);

        var exception = Assert.Throws<SelectionResolutionException>(() =>
            Resolver.Resolve(catalog, request, DateTimeOffset.UnixEpoch));

        Assert.Equal(SelectionField.Runtime, exception.Field);
    }

    [Theory]
    [InlineData("il")]
    [InlineData("decompiled-csharp")]
    public async Task CppCliMixedPeUsesDefaultIlSpyWithoutCoreClr(
        string outputId)
    {
        var catalog = await LoadCatalogAsync();
        var request = new ResolveSelectionRequest(
            "cppcli",
            "msvc-cppcli-netfx48",
            "netfx48-ref",
            outputId,
            null,
            BuildConfiguration.Release,
            catalog.Revision,
            18);

        var result = Resolver.Resolve(catalog, request, DateTimeOffset.UnixEpoch);

        Assert.Null(result.EffectiveSelection.RuntimeId);
        Assert.Collection(
            result.PipelinePlan.Stages,
            stage =>
            {
                Assert.Equal(PipelineStageKind.Build, stage.Kind);
                Assert.Equal("dotnet-framework-mixed-pe-v1", stage.OutputArtifactFormat);
                Assert.Equal("msvc-cppcli-netfx48", stage.ProviderId);
            },
            stage =>
            {
                Assert.Equal(PipelineStageKind.Render, stage.Kind);
                Assert.Equal(outputId, stage.Id);
                Assert.Equal("artifacts-default", stage.ProviderId);
                Assert.Equal("dotnet-framework-mixed-pe-v1", stage.InputArtifactFormat);
            });
    }

    [Fact]
    public async Task CatalogDeclaredArtifactOutputRoutesWithoutAResolverCodeChange()
    {
        var catalog = await LoadCatalogAsync();
        catalog = catalog with
        {
            ArtifactProcessors =
            [
                .. catalog.ArtifactProcessors,
                new ArtifactProcessorManifest
                {
                    Id = "artifacts-test-ecmascript",
                    DisplayName = "Test ECMAScript translator",
                    ResolvedVersion = "1.0.0",
                    WorkerId = "artifacts-test-ecmascript",
                    AcceptsArtifactFormats = ["dotnet-managed-pe-v1"],
                    ProducesArtifactFormats = ["ecmascript-test-v1"],
                    Capabilities = ["ecmascript-test"],
                    AcceptedMetadataFeatureTags = [],
                    Availability = new ComponentAvailability { Installed = true, Health = "healthy" }
                }
            ],
            Outputs =
            [
                .. catalog.Outputs,
                new OutputManifest
                {
                    Id = "ecmascript-test",
                    DisplayName = "Test ECMAScript",
                    Renderer = "javascript",
                    RequiresRuntime = false,
                    RequiredCapabilities = ["managed-pe", "ecmascript-test"],
                    AcceptedArtifactFormats = ["dotnet-managed-pe-v1"],
                    OutputArtifactFormat = "ecmascript-test-v1"
                }
            ],
            Compatibility =
            [
                .. catalog.Compatibility,
                new CompatibilityRule
                {
                    Id = "managed-pe-test-ecmascript",
                    Kind = CompatibilityRuleKind.ArtifactProcessor,
                    FromId = "dotnet-managed-pe-v1",
                    ToId = "artifacts-test-ecmascript",
                    Allowed = true,
                    RequiredMetadataFeatureTags = []
                }
            ]
        };
        var request = new ResolveSelectionRequest(
            "csharp",
            "roslyn-main",
            "net11-preview-ref",
            "ecmascript-test",
            null,
            BuildConfiguration.Release,
            catalog.Revision,
            25);

        var result = Resolver.Resolve(catalog, request, DateTimeOffset.UnixEpoch);

        Assert.Contains("ecmascript-test", result.EffectiveCapabilities.OutputCapabilities);
        Assert.Collection(
            result.PipelinePlan.Stages,
            stage => Assert.Equal(PipelineStageKind.Build, stage.Kind),
            stage =>
            {
                Assert.Equal("ecmascript-test", stage.Id);
                Assert.Equal(PipelineStageKind.Render, stage.Kind);
                Assert.Equal("artifacts-test-ecmascript", stage.ProviderId);
                Assert.Equal("dotnet-managed-pe-v1", stage.InputArtifactFormat);
                Assert.Equal("ecmascript-test-v1", stage.OutputArtifactFormat);
            });
    }

    [Fact]
    public async Task CppCliRunUsesOnlyTheWineNetFxRuntime()
    {
        var catalog = await LoadCatalogAsync();
        var request = new ResolveSelectionRequest(
            "cppcli",
            "msvc-cppcli-netfx48",
            "netfx48-ref",
            "run",
            "wine-netfx48-linux-x64",
            BuildConfiguration.Release,
            catalog.Revision,
            19);

        var result = Resolver.Resolve(catalog, request, DateTimeOffset.UnixEpoch);

        Assert.Equal("wine-netfx48-linux-x64", result.EffectiveSelection.RuntimeId);
        Assert.Equal("runtime-job-wine-netfx", result.PipelinePlan.SecurityPolicyId);
        Assert.Collection(
            result.PipelinePlan.Stages,
            stage => Assert.Equal(PipelineStageKind.Build, stage.Kind),
            stage =>
            {
                Assert.Equal(PipelineStageKind.Run, stage.Kind);
                Assert.Equal("wine-netfx48-linux-x64", stage.ProviderId);
                Assert.Equal("dotnet-framework-mixed-pe-v1", stage.InputArtifactFormat);
            });
    }

    [Theory]
    [InlineData("il-verify")]
    [InlineData("jit-asm")]
    [InlineData("execution-flow")]
    [InlineData("run-il")]
    public async Task CppCliRejectsUnsupportedMixedPeOperations(string outputId)
    {
        var catalog = await LoadCatalogAsync();
        var request = new ResolveSelectionRequest(
            "cppcli",
            "msvc-cppcli-netfx48",
            "netfx48-ref",
            outputId,
            outputId is "jit-asm" or "execution-flow" ? "wine-netfx48-linux-x64" : null,
            BuildConfiguration.Release,
            catalog.Revision,
            20);

        var exception = Assert.Throws<SelectionResolutionException>(() =>
            Resolver.Resolve(catalog, request, DateTimeOffset.UnixEpoch));

        Assert.Equal(
            outputId is "jit-asm" or "execution-flow"
                ? SelectionField.Runtime
                : SelectionField.Output,
            exception.Field);
    }

    [Fact]
    public async Task CppCliMixedPeCannotRunOnCoreClr()
    {
        var catalog = await LoadCatalogAsync();
        var request = new ResolveSelectionRequest(
            "cppcli",
            "msvc-cppcli-netfx48",
            "netfx48-ref",
            "run",
            "dotnet-10-linux-x64",
            BuildConfiguration.Release,
            catalog.Revision,
            21);

        var exception = Assert.Throws<SelectionResolutionException>(() =>
            Resolver.Resolve(catalog, request, DateTimeOffset.UnixEpoch));

        Assert.Equal(SelectionField.Runtime, exception.Field);
    }

    [Theory]
    [InlineData("compile-check", PipelineStageKind.Build)]
    [InlineData("il", PipelineStageKind.Render)]
    [InlineData("decompiled-csharp", PipelineStageKind.Render)]
    public async Task JSharpUsesTheExactNet20BuildAndInspectionRoute(
        string outputId,
        PipelineStageKind terminalKind)
    {
        var catalog = await LoadCatalogAsync();
        var request = new ResolveSelectionRequest(
            "jsharp",
            "vjc-jsharp20",
            "jsharp20-ref",
            outputId,
            null,
            BuildConfiguration.Release,
            catalog.Revision,
            22);

        var result = Resolver.Resolve(catalog, request, DateTimeOffset.UnixEpoch);

        Assert.Equal("vjc-jsharp20", result.PipelinePlan.LanguageWorkerId);
        Assert.Equal("jsharp20-ref", result.PipelinePlan.ReferenceSetId);
        Assert.Null(result.EffectiveSelection.RuntimeId);
        Assert.Equal(terminalKind, result.PipelinePlan.Stages[^1].Kind);
        if (terminalKind == PipelineStageKind.Render)
            Assert.Equal("artifacts-default", result.PipelinePlan.Stages[^1].ProviderId);
    }

    [Fact]
    public async Task JSharpRunUsesOnlyTheDedicatedClr2WineRuntime()
    {
        var catalog = await LoadCatalogAsync();
        var request = new ResolveSelectionRequest(
            "jsharp",
            "vjc-jsharp20",
            "jsharp20-ref",
            "run",
            "wine-jsharp20-linux-x64",
            BuildConfiguration.Release,
            catalog.Revision,
            23);

        var result = Resolver.Resolve(catalog, request, DateTimeOffset.UnixEpoch);

        Assert.Equal("wine-jsharp20-linux-x64", result.EffectiveSelection.RuntimeId);
        Assert.Equal("runtime-job-wine-jsharp20", result.PipelinePlan.SecurityPolicyId);
        Assert.Equal(
            ["compile-check", "il", "decompiled-csharp", "run"],
            result.EffectiveCapabilities.OutputCapabilities);
        Assert.Collection(
            result.PipelinePlan.Stages,
            stage =>
            {
                Assert.Equal(PipelineStageKind.Build, stage.Kind);
                Assert.Equal("vjc-jsharp20", stage.ProviderId);
                Assert.Equal("dotnet-framework-managed-pe-v1", stage.OutputArtifactFormat);
            },
            stage =>
            {
                Assert.Equal(PipelineStageKind.Run, stage.Kind);
                Assert.Equal("wine-jsharp20-linux-x64", stage.ProviderId);
                Assert.Equal("dotnet-framework-managed-pe-v1", stage.InputArtifactFormat);
            });
    }

    [Theory]
    [InlineData("wine-netfx48-linux-x64")]
    [InlineData("dotnet-10-linux-x64")]
    public async Task JSharpCannotRunOnAnotherFrameworkOrCoreClrRuntime(string runtimeId)
    {
        var catalog = await LoadCatalogAsync();
        var request = new ResolveSelectionRequest(
            "jsharp",
            "vjc-jsharp20",
            "jsharp20-ref",
            "run",
            runtimeId,
            BuildConfiguration.Release,
            catalog.Revision,
            24);

        var exception = Assert.Throws<SelectionResolutionException>(() =>
            Resolver.Resolve(catalog, request, DateTimeOffset.UnixEpoch));

        Assert.Equal(SelectionField.Runtime, exception.Field);
    }

    [Theory]
    [InlineData("ast")]
    [InlineData("il-verify")]
    [InlineData("jit-asm")]
    [InlineData("execution-flow")]
    [InlineData("run-il")]
    public async Task JSharpRejectsUnsupportedOutputs(string outputId)
    {
        var catalog = await LoadCatalogAsync();
        var request = new ResolveSelectionRequest(
            "jsharp",
            "vjc-jsharp20",
            "jsharp20-ref",
            outputId,
            outputId is "jit-asm" or "execution-flow" ? "wine-jsharp20-linux-x64" : null,
            BuildConfiguration.Release,
            catalog.Revision,
            25);

        Assert.Throws<SelectionResolutionException>(() =>
            Resolver.Resolve(catalog, request, DateTimeOffset.UnixEpoch));
    }

    private static Task<CatalogDocument> LoadCatalogAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "catalog.json");
        return CatalogLoader.LoadCatalogAsync(path, TestContext.Current.CancellationToken);
    }

    private static CatalogDocument EnableMono(CatalogDocument catalog) => catalog with
    {
        Runtimes = catalog.Runtimes.Select(runtime => runtime.Id == "mono-6.12-linux-x64"
            ? runtime with
            {
                Capabilities = ["run"],
                Availability = new ComponentAvailability { Installed = true, Health = "healthy" }
            }
            : runtime).ToArray(),
        Compatibility = catalog.Compatibility.Select(rule =>
            rule.Kind == CompatibilityRuleKind.ArtifactRuntime &&
            rule.FromId == "dotnet-framework-managed-pe-v1" &&
            rule.ToId == "mono-6.12-linux-x64"
                ? rule with { Allowed = true, Reason = null }
                : rule).ToArray()
    };
}
