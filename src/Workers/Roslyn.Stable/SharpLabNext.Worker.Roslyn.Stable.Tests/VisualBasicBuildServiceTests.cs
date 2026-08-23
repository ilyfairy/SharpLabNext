using SharpLabNext.Contracts;
using SharpLabNext.Worker.Roslyn;

namespace SharpLabNext.Worker.Roslyn.Stable.Tests;

public sealed class VisualBasicBuildServiceTests
{
    [Fact]
    public async Task AutomaticOutputKindBuildsVisualBasicAsLibraryWithoutSubMain()
    {
        var response = await CreateService().ExecuteAsync(
            CreateRequest(
                BuildTarget.Artifact,
                [new WorkspaceFile(
                    "Program.vb",
                    1,
                    "Public NotInheritable Class Calculator\n    Public Function Add(a As Integer, b As Integer) As Integer\n        Return a + b\n    End Function\nEnd Class")],
                outputKind: BuildOutputKind.Auto),
            TestContext.Current.CancellationToken);

        var result = Assert.IsType<BuildResult>(response.Result);
        var artifact = Assert.IsType<CompiledArtifact>(response.Artifact);
        Assert.Equal(BuildOutcome.Succeeded, result.Outcome);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "BC30420");
        Assert.Equal(BuildOutputKind.Library, artifact.Manifest.OutputKind);
        Assert.Null(artifact.Manifest.EntryPoint);
    }

    [Fact]
    public async Task CompileCheckIncludesTheSharpLabRuntimeApi()
    {
        var response = await CreateService().ExecuteAsync(
            CreateRequest(
                BuildTarget.CompileCheck,
                [new WorkspaceFile(
                    "Program.vb",
                    1,
                    "Module Program\n    Sub Main()\n        Dim value = 42.Dump()\n        Inspect.MemoryGraph(value)\n    End Sub\nEnd Module")]),
            TestContext.Current.CancellationToken);

        var result = Assert.IsType<CompilationCheckResult>(response.Result);
        Assert.True(result.CompilationSucceeded);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task ArtifactBuildCompilesMultipleVisualBasicFilesAndProducesPdb()
    {
        var service = CreateService();
        var request = CreateRequest(
            BuildTarget.Artifact,
            [
                new WorkspaceFile(
                    "Greeter.vb",
                    2,
                    "Namespace Demo\n    Public Module Greeter\n        Public Function Message() As String\n            Return \"Hello\"\n        End Function\n    End Module\nEnd Namespace"),
                new WorkspaceFile(
                    "Program.vb",
                    4,
                    "Imports System\nImports Demo\nModule Program\n    Sub Main()\n        Console.WriteLine(Greeter.Message())\n    End Sub\nEnd Module")
            ],
            revision: 50,
            selectionRevision: 8);

        var response = await service.ExecuteAsync(request, TestContext.Current.CancellationToken);

        var result = Assert.IsType<BuildResult>(response.Result);
        Assert.Equal(BuildOutcome.Succeeded, result.Outcome);
        Assert.Equal("visual-basic", result.Identity.LanguageId);
        Assert.Equal(50, result.WorkspaceRevision);
        Assert.Equal(8, result.SelectionRevision);
        Assert.NotNull(response.Artifact);
        Assert.Equal([0x4D, 0x5A], response.Artifact.PeImage[..2]);
        Assert.NotEmpty(response.Artifact.PortablePdb);
        Assert.Equal("visual-basic", response.Artifact.Manifest.Producer.LanguageId);
    }

    [Fact]
    public async Task FrameworkArtifactBuildUsesRealNet48ReferencesAndWineRuntimeContract()
    {
        using var references = new ReferenceSetProvider(
            [new ReferenceSetDefinition(
                "netfx48-managed-ref",
                CSharpBuildServiceTests.GetNetFx48ReferencePathForHost(),
                "net48",
                "1.0.3",
                IncludeSharpLabRuntime: false)
            {
                RequiredRuntimeFeatureTags = ["runtime.netfx48-wine"]
            }]);
        var identity = new RoslynWorkerIdentity(
            "development",
            "roslyn-stable",
            "5.6.0",
            null,
            "development-worker-image");
        var service = new VisualBasicBuildService(
            references,
            identity,
            CompilationLimits.Default,
            AstLimits.Default);
        var request = CreateRequest(
            BuildTarget.Artifact,
            [new WorkspaceFile(
                "Program.vb",
                1,
                "Imports System\nImports Microsoft.VisualBasic\nModule Program\n    Sub Main()\n        Console.WriteLine(Strings.UCase(\"net48\"))\n    End Sub\nEnd Module")]);
        var frameworkOptions = request.EffectiveOptions with { OutputKind = BuildOutputKind.Console };
        request = request with
        {
            ReferenceSetId = "netfx48-managed-ref",
            Options = frameworkOptions,
            Workspace = request.Workspace with
            {
                ReferenceSetId = "netfx48-managed-ref",
                BuildOptions = frameworkOptions
            }
        };

        var response = await service.ExecuteAsync(request, TestContext.Current.CancellationToken);

        var result = Assert.IsType<BuildResult>(response.Result);
        Assert.Equal(BuildOutcome.Succeeded, result.Outcome);
        var artifact = Assert.IsType<CompiledArtifact>(response.Artifact);
        Assert.Equal("dotnet-framework-managed-pe-v1", artifact.ArtifactFormat);
        Assert.Equal("SharpLabNext.User.exe", artifact.Manifest.EntryAssembly);
        Assert.Equal("net48", artifact.Manifest.TargetFramework);
        Assert.Equal("netfx-clr-wine", artifact.Manifest.RuntimeRequirement.Family);
        Assert.Equal(["runtime.netfx48-wine"], artifact.Manifest.RuntimeRequirement.RequiredRuntimeFeatureTags);
    }

    [Fact]
    public async Task CompileCheckReturnsVisualBasicDiagnosticsWithRevisions()
    {
        var service = CreateService();
        var request = CreateRequest(
            BuildTarget.CompileCheck,
            [new WorkspaceFile(
                "Program.vb",
                2,
                "Module Program\n    Sub Main()\n        Dim value As Integer =\n    End Sub\nEnd Module")],
            revision: 51,
            selectionRevision: 9);

        var response = await service.ExecuteAsync(request, TestContext.Current.CancellationToken);

        var result = Assert.IsType<CompilationCheckResult>(response.Result);
        Assert.False(result.CompilationSucceeded);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code.StartsWith("BC", StringComparison.Ordinal));
        Assert.All(result.Diagnostics, diagnostic =>
        {
            Assert.Equal(51, diagnostic.WorkspaceRevision);
            Assert.Equal(9, diagnostic.SelectionRevision);
        });
    }

    [Fact]
    public async Task VisualBasicAstContainsDocumentsTokensAndTrivia()
    {
        var service = CreateService();
        var request = CreateRequest(
            BuildTarget.Ast,
            [
                new WorkspaceFile("First.vb", 1, "' first\nPublic Class First\nEnd Class"),
                new WorkspaceFile("Second.vb", 1, "Public Class Second\n    Public Property Value As Integer\nEnd Class")
            ],
            revision: 52,
            selectionRevision: 10);

        var response = await service.ExecuteAsync(request, TestContext.Current.CancellationToken);

        var result = Assert.IsType<AstResult>(response.Result);
        Assert.Equal("visual-basic", result.Document.LanguageId);
        Assert.Equal(["First.vb", "Second.vb"], result.Document.Root.Children.Select(static child => child.Properties["path"]));
        Assert.Contains(Flatten(result.Document.Root), static node => node.Properties.GetValueOrDefault("isToken") == "true");
        Assert.Contains(Flatten(result.Document.Root), static node => node.Properties.GetValueOrDefault("isTrivia") == "true");
    }

    private static VisualBasicBuildService CreateService() =>
        new(
            new ReferenceSetProvider(
                [new ReferenceSetDefinition(
                    "net10-ref",
                    CSharpBuildServiceTests.GetNet10ReferencePathForHost(),
                    "net10.0",
                    CSharpBuildServiceTests.GetNet10ReferenceVersionForHost())]),
            new RoslynWorkerIdentity("development", "roslyn-stable", "5.6.0", null, "development-worker-image"),
            CompilationLimits.Default,
            AstLimits.Default);

    internal static BuildRequest CreateRequest(
        BuildTarget target,
        IReadOnlyList<WorkspaceFile> files,
        long revision = 1,
        long selectionRevision = 1,
        BuildOutputKind outputKind = BuildOutputKind.Console)
    {
        var options = new BuildOptions(
            BuildConfiguration.Release,
            Optimize: true,
            outputKind,
            AllowUnsafe: false,
            EmitPortablePdb: true,
            NullableContextMode.Disable,
            LanguageVersion: "latest",
            PreprocessorSymbols: ["SHARPLABNEXT"],
            CheckOverflow: true);
        var workspace = new WorkspaceSnapshot(
            ContractSchemaVersions.WorkspaceSnapshot,
            revision,
            selectionRevision,
            "visual-basic",
            files,
            files[^1].Path,
            files.Select(static file => file.Path).ToArray(),
            "net10-ref",
            options);
        return new BuildRequest(
            $"vb-request-{Guid.NewGuid():N}",
            $"vb-idempotency-{Guid.NewGuid():N}",
            "vb-pipeline",
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
}
