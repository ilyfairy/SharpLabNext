using Microsoft.Extensions.Configuration;
using SharpLabNext.Contracts;
using SharpLabNext.Worker.Roslyn;

namespace SharpLabNext.Worker.Roslyn.Stable.Tests;

public sealed class CSharpBuildServiceTests
{
    [Fact]
    public async Task MissingLanguageVersionDefaultsToPreview()
    {
        var service = CreateService();
        var original = CreateRequest(
            BuildTarget.CompileCheck,
            [new WorkspaceFile("Program.cs", 1, "#error version")]);
        var options = original.Workspace.BuildOptions with { LanguageVersion = null };
        var request = original with
        {
            Options = null,
            Workspace = original.Workspace with { BuildOptions = options }
        };

        var response = await service.ExecuteAsync(request, TestContext.Current.CancellationToken);

        var result = Assert.IsType<CompilationCheckResult>(response.Result);
        var version = Assert.Single(result.Diagnostics, static diagnostic => diagnostic.Code == "CS8304");
        Assert.Contains("preview", version.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkerSettingsResolvePinnedCompilerVersionAndRuntimeFeatureTags()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RoslynWorker:ReleaseId"] = "development",
                ["RoslynWorker:ToolchainId"] = "roslyn-stable-netfx48",
                ["RoslynWorker:WorkerImageId"] = "development-netfx-worker-image",
                ["RoslynWorker:CompilerVersion"] = "__pinned__",
                ["RoslynWorker:ArtifactContract:Format"] = "dotnet-framework-managed-pe-v1",
                ["RoslynWorker:ArtifactContract:RuntimeFamily"] = "netfx-clr-wine",
                ["RoslynWorker:ArtifactContract:FrameworkName"] = ".NETFramework",
                ["RoslynWorker:ArtifactContract:FrameworkVersion"] = "4.8",
                ["RoslynWorker:ArtifactContract:Architecture"] = "anycpu",
                ["RoslynWorker:ArtifactContract:ExecutableFileExtension"] = ".exe",
                ["RoslynWorker:ArtifactContract:LibraryFileExtension"] = ".dll",
                ["RoslynWorker:ArtifactContract:RequiredRuntimeFeatureTags:0"] = "runtime.netfx48-wine",
                ["ReferenceSets:netfx48-managed-ref:Path"] = GetNetFx48ReferencePathForHost(),
                ["ReferenceSets:netfx48-managed-ref:TargetFramework"] = "net48",
                ["ReferenceSets:netfx48-managed-ref:FrameworkVersion"] = "1.0.3",
                ["ReferenceSets:netfx48-managed-ref:IncludeSharpLabRuntime"] = "false"
            })
            .Build();

        var settings = RoslynWorkerSettings.FromConfiguration(configuration);

        Assert.Equal(CSharpBuildService.GetLoadedCompilerVersion(), settings.Identity.CompilerVersion);
        Assert.Equal(["runtime.netfx48-wine"], settings.Identity.RequiredRuntimeFeatureTags);
        var referenceSet = Assert.Single(settings.ReferenceSets);
        Assert.Equal("dotnet-framework-managed-pe-v1", referenceSet.ArtifactFormat);
        Assert.Equal("netfx-clr-wine", referenceSet.RuntimeFamily);
        Assert.Equal(".NETFramework", referenceSet.FrameworkName);
        Assert.Equal("4.8", referenceSet.GetRuntimeFrameworkVersion());
        Assert.Equal(["runtime.netfx48-wine"], referenceSet.RequiredRuntimeFeatureTags);
    }

    [Theory]
    [InlineData("netcoreapp2.1", "dotnet-managed-pe-v1", "coreclr", "Microsoft.NETCore.App", ".dll", false, "2.1.30")]
    [InlineData("netcoreapp3.1", "dotnet-managed-pe-v1", "coreclr", "Microsoft.NETCore.App", ".dll", true, "3.1.32")]
    [InlineData("net9.0", "dotnet-managed-pe-v1", "coreclr", "Microsoft.NETCore.App", ".dll", true, "9.0.7")]
    [InlineData("net20", "dotnet-framework-managed-pe-v1", "netfx-clr-wine", ".NETFramework", ".exe", false, "2.0")]
    [InlineData("net48", "dotnet-framework-managed-pe-v1", "netfx-clr-wine", ".NETFramework", ".exe", false, "4.8")]
    public void ReferenceSetDefaultsAreDerivedFromTheTargetFramework(
        string targetFramework,
        string artifactFormat,
        string runtimeFamily,
        string frameworkName,
        string executableExtension,
        bool includeSharpLabRuntime,
        string runtimeFrameworkVersion)
    {
        var referenceSet = new ReferenceSetDefinition(
            "test-ref",
            "test-path",
            targetFramework,
            targetFramework switch
            {
                "netcoreapp2.1" => "2.1.30",
                "netcoreapp3.1" => "3.1.32",
                "net9.0" => "9.0.7",
                _ => "1.0.3"
            });

        Assert.Equal(artifactFormat, referenceSet.ArtifactFormat);
        Assert.Equal(runtimeFamily, referenceSet.RuntimeFamily);
        Assert.Equal(frameworkName, referenceSet.FrameworkName);
        Assert.Equal(executableExtension, referenceSet.ExecutableFileExtension);
        Assert.Equal(includeSharpLabRuntime, referenceSet.IncludeSharpLabRuntime);
        Assert.Equal(runtimeFrameworkVersion, referenceSet.GetRuntimeFrameworkVersion());
    }

    [Fact]
    public void ReferenceSetConfigurationOverridesTheLegacyWorkerArtifactContract()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RoslynWorker:ReleaseId"] = "development",
                ["RoslynWorker:ToolchainId"] = "roslyn-stable",
                ["RoslynWorker:WorkerImageId"] = "development-worker-image",
                ["RoslynWorker:ArtifactContract:RuntimeFamily"] = "legacy-family",
                ["RoslynWorker:ArtifactContract:MetadataFeatureTags:0"] = "metadata.legacy",
                ["ReferenceSets:custom-ref:Path"] = "custom-path",
                ["ReferenceSets:custom-ref:TargetFramework"] = "net9.0",
                ["ReferenceSets:custom-ref:FrameworkVersion"] = "9.0.7",
                ["ReferenceSets:custom-ref:RuntimeFamily"] = "custom-coreclr",
                ["ReferenceSets:custom-ref:FrameworkName"] = "Custom.Framework",
                ["ReferenceSets:custom-ref:RuntimeFrameworkVersion"] = "9.0.1",
                ["ReferenceSets:custom-ref:Architecture"] = "x64",
                ["ReferenceSets:custom-ref:ExecutableExtension"] = ".exe",
                ["ReferenceSets:custom-ref:LibraryExtension"] = ".dll",
                ["ReferenceSets:custom-ref:RequiredRuntimeFeatureTags:0"] = "runtime.custom",
                ["ReferenceSets:custom-ref:MetadataFeatureTags:0"] = "metadata.custom",
                ["ReferenceSets:custom-ref:CompatibilityGroup"] = "custom-group",
                ["ReferenceSets:custom-ref:IncludeSharpLabRuntime"] = "false"
            })
            .Build();

        var referenceSet = Assert.Single(RoslynWorkerSettings.FromConfiguration(configuration).ReferenceSets);

        Assert.Equal("custom-coreclr", referenceSet.RuntimeFamily);
        Assert.Equal("Custom.Framework", referenceSet.FrameworkName);
        Assert.Equal("9.0.1", referenceSet.GetRuntimeFrameworkVersion());
        Assert.Equal("x64", referenceSet.Architecture);
        Assert.Equal(".exe", referenceSet.ExecutableFileExtension);
        Assert.Equal(["runtime.custom"], referenceSet.RequiredRuntimeFeatureTags);
        Assert.Equal(["metadata.custom"], referenceSet.MetadataFeatureTags);
        Assert.Equal("custom-group", referenceSet.CompatibilityGroup);
        Assert.False(referenceSet.IncludeSharpLabRuntime);
    }

    [Fact]
    public async Task ArtifactBuildCompilesMultipleFilesAndProducesPortablePdb()
    {
        var service = CreateService();
        var request = CreateRequest(
            BuildTarget.Artifact,
            [
                new WorkspaceFile("Helpers/Message.cs", 3, "namespace Demo; public static class Message { public static string Text => \"Hello\"; }"),
                new WorkspaceFile("Program.cs", 8, "using System; using Demo; Console.WriteLine(Message.Text);")
            ],
            revision: 41,
            selectionRevision: 7);

        var response = await service.ExecuteAsync(request, TestContext.Current.CancellationToken);

        var result = Assert.IsType<BuildResult>(response.Result);
        Assert.Equal(BuildOutcome.Succeeded, result.Outcome);
        Assert.Equal(41, result.WorkspaceRevision);
        Assert.Equal(7, result.SelectionRevision);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.NotNull(response.Artifact);
        Assert.Equal([0x4D, 0x5A], response.Artifact.PeImage[..2]);
        Assert.NotEmpty(response.Artifact.PortablePdb);
        Assert.Equal("net10-ref", response.Artifact.ReferenceSetId);
        Assert.Equal("dotnet-managed-pe-v1", response.Artifact.ArtifactFormat);
        Assert.StartsWith("sha256:", response.Artifact.ArtifactRef.Value, StringComparison.Ordinal);
        Assert.Equal(response.Artifact.ArtifactRef, result.ArtifactRef);
        Assert.Equal(response.Artifact.ArtifactRef, response.Artifact.Manifest.ArtifactId);
        Assert.Equal("Microsoft.NETCore.App", Assert.Single(response.Artifact.Manifest.RuntimeRequirement.Frameworks).Name);
        Assert.Equal("10.0.9", Assert.Single(response.Artifact.Manifest.RuntimeRequirement.Frameworks).MinimumVersion);
        Assert.Equal("coreclr", response.Artifact.Manifest.RuntimeRequirement.Family);
        Assert.Empty(response.Artifact.Manifest.RuntimeRequirement.RequiredRuntimeFeatureTags);
        Assert.Empty(response.Artifact.Manifest.MetadataFeatureTags);
        Assert.Null(response.Artifact.Manifest.Metadata);
    }

    [Fact]
    public async Task AutomaticOutputKindBuildsOrdinaryCodeAsLibraryWithoutMain()
    {
        var response = await CreateService().ExecuteAsync(
            CreateRequest(
                BuildTarget.Artifact,
                [new WorkspaceFile("Program.cs", 1, "public sealed class Calculator { public int Add(int a, int b) => a + b; }")],
                outputKind: BuildOutputKind.Auto),
            TestContext.Current.CancellationToken);

        var result = Assert.IsType<BuildResult>(response.Result);
        var artifact = Assert.IsType<CompiledArtifact>(response.Artifact);
        Assert.Equal(BuildOutcome.Succeeded, result.Outcome);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "CS5001");
        Assert.Equal(BuildOutputKind.Library, artifact.Manifest.OutputKind);
        Assert.Null(artifact.Manifest.EntryPoint);
    }

    [Fact]
    public async Task AutomaticOutputKindBuildsTopLevelStatementsAsConsoleApplication()
    {
        var response = await CreateService().ExecuteAsync(
            CreateRequest(
                BuildTarget.Artifact,
                [new WorkspaceFile("Program.cs", 1, "System.Console.WriteLine(42);")],
                outputKind: BuildOutputKind.Auto),
            TestContext.Current.CancellationToken);

        var result = Assert.IsType<BuildResult>(response.Result);
        var artifact = Assert.IsType<CompiledArtifact>(response.Artifact);
        Assert.Equal(BuildOutcome.Succeeded, result.Outcome);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "CS8805");
        Assert.Equal(BuildOutputKind.Console, artifact.Manifest.OutputKind);
        Assert.NotNull(artifact.Manifest.EntryPoint);
    }

    [Fact]
    public async Task ExplicitConsoleOutputStillRequiresMain()
    {
        var response = await CreateService().ExecuteAsync(
            CreateRequest(
                BuildTarget.Artifact,
                [new WorkspaceFile("Program.cs", 1, "public sealed class Calculator { }")],
                outputKind: BuildOutputKind.Console),
            TestContext.Current.CancellationToken);

        var result = Assert.IsType<BuildResult>(response.Result);
        Assert.Equal(BuildOutcome.CompilationFailed, result.Outcome);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "CS5001");
        Assert.Null(response.Artifact);
    }

    [Fact]
    public async Task CompileCheckReturnsRevisionedDiagnosticsWithoutRetainingArtifact()
    {
        var service = CreateService();
        var request = CreateRequest(
            BuildTarget.CompileCheck,
            [new WorkspaceFile("Program.cs", 2, "int value = \"not an int\";")],
            revision: 19,
            selectionRevision: 5);

        var response = await service.ExecuteAsync(request, TestContext.Current.CancellationToken);

        var result = Assert.IsType<CompilationCheckResult>(response.Result);
        Assert.False(result.CompilationSucceeded);
        Assert.Null(response.Artifact);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "CS0029");
        Assert.All(result.Diagnostics, diagnostic =>
        {
            Assert.Equal(19, diagnostic.WorkspaceRevision);
            Assert.Equal(5, diagnostic.SelectionRevision);
        });
    }

    [Fact]
    public async Task CompileCheckIncludesTheSharpLabRuntimeApi()
    {
        var response = await CreateService().ExecuteAsync(
            CreateRequest(
                BuildTarget.CompileCheck,
                [new WorkspaceFile(
                    "Program.cs",
                    1,
                    "var value = 42.Dump(); Inspect.MemoryGraph(value); System.Console.WriteLine(value);")]),
            TestContext.Current.CancellationToken);

        var result = Assert.IsType<CompilationCheckResult>(response.Result);
        Assert.True(result.CompilationSucceeded);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task AstNormalizesMultipleDocumentsAndReportsTruncation()
    {
        var service = CreateService(astLimits: new AstLimits(MaxNodes: 24, MaxDepth: 32, MaxUtf8Bytes: 16_384, MaxTextPreviewCharacters: 80));
        var request = CreateRequest(
            BuildTarget.Ast,
            [
                new WorkspaceFile("First.cs", 1, "// first\nnamespace Demo { class First { int Value => 1 + 2 + 3; } }"),
                new WorkspaceFile("Second.cs", 1, "namespace Demo { class Second { void M() { for (var i = 0; i < 3; i++) { } } } }")
            ],
            revision: 23,
            selectionRevision: 9,
            activeFile: "Second.cs");

        var response = await service.ExecuteAsync(request, TestContext.Current.CancellationToken);

        var result = Assert.IsType<AstResult>(response.Result);
        Assert.Equal("Workspace", result.Document.Root.Kind);
        Assert.Equal(23, result.Document.WorkspaceRevision);
        Assert.Equal("5.6.0", result.Identity?.CompilerVersion);
        Assert.Equal("net10-ref", result.Identity?.ReferenceSetId);
        Assert.Equal("development-worker-image", result.Identity?.WorkerImageId);
        Assert.True(result.Document.Truncated);
        Assert.Equal(["First.cs", "Second.cs"], result.Document.Root.Children.Select(static child => child.Properties["path"]));
        Assert.Contains(Flatten(result.Document.Root), static node => node.Properties.ContainsKey("rawKind"));
        Assert.Contains(Flatten(result.Document.Root), static node => node.Properties.GetValueOrDefault("isToken") == "true");
    }

    [Fact]
    public async Task AstIncludesSyntaxVisualizerPropertiesForNodesTokensAndTrivia()
    {
        var response = await CreateService().ExecuteAsync(
            CreateRequest(
                BuildTarget.Ast,
                [new WorkspaceFile("Program.cs", 1, "// lead\nclass Sample { int Value => 42; }")]),
            TestContext.Current.CancellationToken);

        var result = Assert.IsType<AstResult>(response.Result);
        var nodes = Flatten(result.Document.Root).ToArray();
        var declaration = Assert.Single(nodes, static node => node.Kind == "ClassDeclaration");
        Assert.Equal("ClassDeclarationSyntax", declaration.Properties["type"]);
        Assert.Equal("true", declaration.Properties["isNode"]);
        Assert.Equal("C#", declaration.Properties["language"]);
        Assert.Contains(declaration.Properties.Keys, static key => key == "containsDiagnostics");

        var keyword = Assert.Single(nodes, static node =>
            node.Kind == "ClassKeyword" && node.Properties.GetValueOrDefault("isToken") == "true");
        Assert.Equal("SyntaxToken", keyword.Properties["type"]);
        Assert.Equal("true", keyword.Properties["isKeyword"]);

        var comment = Assert.Single(nodes, static node => node.Kind == "SingleLineCommentTrivia");
        Assert.Equal("SyntaxTrivia", comment.Properties["type"]);
        Assert.Equal("true", comment.Properties["isTrivia"]);
    }

    [Fact]
    public async Task CancelledBuildStopsBeforeCompilationCompletes()
    {
        var service = CreateService();
        var request = CreateRequest(
            BuildTarget.Artifact,
            [new WorkspaceFile("Program.cs", 1, "Console.WriteLine(1);")]);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ExecuteAsync(request, cancellation.Token));
    }

    [Fact]
    public async Task MissingExplicitReferenceBundleIsUnhealthyAndCannotCompile()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var provider = new ReferenceSetProvider(
            [new ReferenceSetDefinition("net10-ref", missingPath, "net10.0", "10.0.9")]);

        var health = await provider.CheckHealthAsync(TestContext.Current.CancellationToken);

        Assert.False(health.IsHealthy);
        Assert.Contains("does not exist", health.Message, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<ReferenceSetUnavailableException>(
            () => provider.GetAsync("net10-ref", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReferenceSetUsesTheAttestedRuntimeApiCopyFromItsRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SharpLabNext.RoslynReferenceSet.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = GetNet10ReferencePathForHost();
            foreach (var fileName in new[]
                     {
                         "System.Runtime.dll",
                         "System.Console.dll",
                         "System.Collections.dll",
                         "netstandard.dll"
                     })
            {
                File.Copy(Path.Combine(source, fileName), Path.Combine(root, fileName));
            }
            File.Copy(
                typeof(SharpLab.Runtime.RuntimeServices).Assembly.Location,
                Path.Combine(root, "SharpLab.Runtime.dll"));
            using var provider = new ReferenceSetProvider(
                [new ReferenceSetDefinition("net10-ref", root, "net10.0", "10.0.9")]);

            var loaded = await provider.GetAsync("net10-ref", TestContext.Current.CancellationToken);

            var attestedPaths = Directory.EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(attestedPaths, loaded.AssemblyPaths);
            Assert.DoesNotContain(
                typeof(SharpLab.Runtime.RuntimeServices).Assembly.Location,
                loaded.AssemblyPaths,
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FrameworkReferenceSetDoesNotInjectTheCoreClrRuntimeApi()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SharpLabNext.FrameworkReferenceSet.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.Copy(
                Path.Combine(GetNetFx48ReferencePathForHost(), "mscorlib.dll"),
                Path.Combine(root, "mscorlib.dll"));
            File.Copy(
                typeof(SharpLab.Runtime.RuntimeServices).Assembly.Location,
                Path.Combine(root, "SharpLab.Runtime.dll"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "native-helper.dll"),
                "not a managed PE",
                TestContext.Current.CancellationToken);
            using var provider = new ReferenceSetProvider(
                [new ReferenceSetDefinition(
                    "netfx48-managed-ref",
                    root,
                    "net48",
                    "1.0.3",
                    IncludeSharpLabRuntime: false)]);

            var loaded = await provider.GetAsync(
                "netfx48-managed-ref",
                TestContext.Current.CancellationToken);

            Assert.DoesNotContain(
                loaded.AssemblyPaths,
                path => Path.GetFileName(path).Equals("SharpLab.Runtime.dll", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                loaded.AssemblyPaths,
                path => Path.GetFileName(path).Equals("native-helper.dll", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LegacyFrameworkReferenceSetAcceptsAnAttestedMscorlibContractWithoutModernMarker()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SharpLabNext.Net20ReferenceSet.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.Copy(
                Path.Combine(GetNetFx20ReferencePathForHost(), "mscorlib.dll"),
                Path.Combine(root, "mscorlib.dll"));
            using var provider = new ReferenceSetProvider(
                [new ReferenceSetDefinition("netfx20-managed-ref", root, "net20", "1.0.3")]);

            var loaded = await provider.GetAsync(
                "netfx20-managed-ref",
                TestContext.Current.CancellationToken);

            Assert.Single(loaded.AssemblyPaths);
            Assert.Equal("mscorlib.dll", Path.GetFileName(loaded.AssemblyPaths[0]));
            Assert.False(loaded.Definition.IncludeSharpLabRuntime);
            Assert.Equal("2.0", loaded.Definition.GetRuntimeFrameworkVersion());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BuildNeverLoadsOrExecutesUserModuleInitializer()
    {
        var service = CreateService();
        var request = CreateRequest(
            BuildTarget.Artifact,
            [new WorkspaceFile(
                "Library.cs",
                1,
                "using System; using System.Runtime.CompilerServices; public static class Startup { [ModuleInitializer] public static void Initialize() => throw new Exception(\"must not execute in compiler worker\"); }")]);
        request = request with
        {
            Options = request.EffectiveOptions with { OutputKind = BuildOutputKind.Library },
            Workspace = request.Workspace with
            {
                BuildOptions = request.Workspace.BuildOptions with { OutputKind = BuildOutputKind.Library }
            }
        };

        var response = await service.ExecuteAsync(request, TestContext.Current.CancellationToken);

        var result = Assert.IsType<BuildResult>(response.Result);
        Assert.Equal(BuildOutcome.Succeeded, result.Outcome);
        Assert.NotNull(response.Artifact);
    }

    [Fact]
    public async Task OneWorkerBuildsCoreClrAndFrameworkReferenceSetsWithIndependentArtifactContracts()
    {
        var identity = new RoslynWorkerIdentity(
            "development",
            "roslyn-stable",
            "5.6.0",
            null,
            "development-worker-image");
        using var references = new ReferenceSetProvider(
            [
                new ReferenceSetDefinition(
                    "net10-ref",
                    GetNet10ReferencePathForHost(),
                    "net10.0",
                    "10.0.9"),
                new ReferenceSetDefinition(
                    "netfx20-managed-ref",
                    GetNetFx20ReferencePathForHost(),
                    "net20",
                    "1.0.3")
                {
                    RequiredRuntimeFeatureTags = ["runtime.netfx20-wine"]
                },
                new ReferenceSetDefinition(
                    "netfx48-managed-ref",
                    GetNetFx48ReferencePathForHost(),
                    "net48",
                    "1.0.3")
                {
                    RequiredRuntimeFeatureTags = ["runtime.netfx48-wine"]
                }
            ]);
        var service = new CSharpBuildService(
            references,
            identity,
            CompilationLimits.Default,
            AstLimits.Default);
        var executableRequest = CreateRequest(
            BuildTarget.Artifact,
            [new WorkspaceFile(
                "Program.cs",
                1,
                "public static class Program { public static void Main() { System.Console.WriteLine(42); } }")]);
        var coreRequest = ForReference(executableRequest, "net10-ref", BuildOutputKind.Console);
        var net20Request = ForReference(executableRequest, "netfx20-managed-ref", BuildOutputKind.Console);
        var net48Request = ForReference(executableRequest, "netfx48-managed-ref", BuildOutputKind.Console);
        var libraryRequest = ForReference(net48Request, "netfx48-managed-ref", BuildOutputKind.Library);

        var core = Assert.IsType<CompiledArtifact>((await service.ExecuteAsync(
            coreRequest,
            TestContext.Current.CancellationToken)).Artifact);
        var net20 = Assert.IsType<CompiledArtifact>((await service.ExecuteAsync(
            net20Request,
            TestContext.Current.CancellationToken)).Artifact);
        var net48 = Assert.IsType<CompiledArtifact>((await service.ExecuteAsync(
            net48Request,
            TestContext.Current.CancellationToken)).Artifact);
        var library = Assert.IsType<CompiledArtifact>((await service.ExecuteAsync(
            libraryRequest,
            TestContext.Current.CancellationToken)).Artifact);

        Assert.Equal("dotnet-managed-pe-v1", core.ArtifactFormat);
        Assert.Equal("SharpLabNext.User.dll", core.Manifest.EntryAssembly);
        Assert.Equal("coreclr", core.Manifest.RuntimeRequirement.Family);
        Assert.Equal("10.0.9", Assert.Single(core.Manifest.RuntimeRequirement.Frameworks).MinimumVersion);

        Assert.Equal("dotnet-framework-managed-pe-v1", net20.ArtifactFormat);
        Assert.Equal("SharpLabNext.User.exe", net20.Manifest.EntryAssembly);
        Assert.Equal("net20", net20.Manifest.TargetFramework);
        Assert.Equal("netfx-clr-wine", net20.Manifest.RuntimeRequirement.Family);
        Assert.Equal("2.0", Assert.Single(net20.Manifest.RuntimeRequirement.Frameworks).MinimumVersion);
        Assert.Equal(["runtime.netfx20-wine"], net20.Manifest.RuntimeRequirement.RequiredRuntimeFeatureTags);

        Assert.Equal("dotnet-framework-managed-pe-v1", net48.ArtifactFormat);
        Assert.Equal("SharpLabNext.User.exe", net48.Manifest.EntryAssembly);
        Assert.Equal("SharpLabNext.User.dll", library.Manifest.EntryAssembly);
        Assert.Equal("netfx-clr-wine", net48.Manifest.RuntimeRequirement.Family);
        Assert.Equal("anycpu", net48.Manifest.RuntimeRequirement.Architecture);
        Assert.Equal(
            ["runtime.netfx48-wine"],
            net48.Manifest.RuntimeRequirement.RequiredRuntimeFeatureTags);
        var framework = Assert.Single(net48.Manifest.RuntimeRequirement.Frameworks);
        Assert.Equal(".NETFramework", framework.Name);
        Assert.Equal("4.8", framework.MinimumVersion);

        static BuildRequest ForReference(
            BuildRequest request,
            string referenceSetId,
            BuildOutputKind outputKind)
        {
            var options = request.EffectiveOptions with { OutputKind = outputKind };
            return request with
            {
                ReferenceSetId = referenceSetId,
                Options = options,
                Workspace = request.Workspace with
                {
                    ReferenceSetId = referenceSetId,
                    BuildOptions = options
                }
            };
        }
    }

    private static CSharpBuildService CreateService(AstLimits? astLimits = null)
    {
        var references = new ReferenceSetProvider(
            [new ReferenceSetDefinition("net10-ref", GetNet10ReferencePathForHost(), "net10.0", "10.0.9")]);
        return new CSharpBuildService(
            references,
            new RoslynWorkerIdentity("development", "roslyn-stable", "5.6.0", null, "development-worker-image"),
            CompilationLimits.Default,
            astLimits ?? AstLimits.Default);
    }

    private static BuildRequest CreateRequest(
        BuildTarget target,
        IReadOnlyList<WorkspaceFile> files,
        long revision = 1,
        long selectionRevision = 1,
        string? activeFile = null,
        BuildOutputKind outputKind = BuildOutputKind.Console)
    {
        activeFile ??= files[^1].Path;
        var options = new BuildOptions(
            BuildConfiguration.Release,
            Optimize: true,
            outputKind,
            AllowUnsafe: false,
            EmitPortablePdb: true,
            NullableContextMode.Enable,
            LanguageVersion: "14.0",
            PreprocessorSymbols: ["SHARPLABNEXT"],
            CheckOverflow: true);
        var workspace = new WorkspaceSnapshot(
            ContractSchemaVersions.WorkspaceSnapshot,
            revision,
            selectionRevision,
            "csharp",
            files,
            activeFile,
            files.Select(static file => file.Path).ToArray(),
            "net10-ref",
            options);

        return new BuildRequest(
            $"request-{Guid.NewGuid():N}",
            $"idempotency-{Guid.NewGuid():N}",
            "pipeline-test",
            "roslyn-stable",
            "net10-ref",
            workspace,
            DateTimeOffset.UtcNow.AddMinutes(1),
            options,
            target);
    }

    private static IEnumerable<AstNode> Flatten(AstNode root)
    {
        var pending = new Stack<AstNode>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            yield return node;
            foreach (var child in node.Children)
                pending.Push(child);
        }
    }

    internal static string GetNet10ReferencePathForHost()
    {
        var explicitPath = Environment.GetEnvironmentVariable("SHARPLABNEXT_NET10_REF_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath) && Directory.Exists(explicitPath))
            return explicitPath;

        var roots = new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
            "/usr/share/dotnet",
            "/usr/local/share/dotnet"
        };

        foreach (var root in roots.Where(static root => !string.IsNullOrWhiteSpace(root)))
        {
            var candidate = Path.Combine(root!, "packs", "Microsoft.NETCore.App.Ref", "10.0.9", "ref", "net10.0");
            if (Directory.Exists(candidate))
                return candidate;
        }

        throw new InvalidOperationException(
            "The .NET 10.0.9 reference pack was not found. Set SHARPLABNEXT_NET10_REF_PATH to its ref/net10.0 directory.");
    }

    internal static string GetNetFx48ReferencePathForHost()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "netfx48-managed-ref");
        Assert.True(Directory.Exists(path), $"The test .NET Framework 4.8 reference set is unavailable at '{path}'.");
        Assert.True(File.Exists(Path.Combine(path, "mscorlib.dll")));
        Assert.True(File.Exists(Path.Combine(path, "System.Runtime.dll")));
        return path;
    }

    internal static string GetNetFx20ReferencePathForHost()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "netfx20-managed-ref");
        Assert.True(Directory.Exists(path), $"The test .NET Framework 2.0 reference set is unavailable at '{path}'.");
        Assert.True(File.Exists(Path.Combine(path, "mscorlib.dll")));
        return path;
    }
}
