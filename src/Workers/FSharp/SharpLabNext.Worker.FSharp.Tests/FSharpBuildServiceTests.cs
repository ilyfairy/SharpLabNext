using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Contracts;
using SharpLabNext.Worker.FSharp.Compiler;

namespace SharpLabNext.Worker.FSharp.Tests;

public sealed class FSharpBuildServiceTests
{
    [Fact]
    public async Task ReferenceSetUsesTheAttestedRuntimeApiCopyFromItsRoot()
    {
        var root = FSharpTestSettings.CreateRoot();
        var referenceRoot = Path.Combine(root, "reference-set");
        Directory.CreateDirectory(referenceRoot);
        try
        {
            var source = FSharpTestSettings.GetNet10ReferencePath();
            foreach (var fileName in new[]
                     {
                         "System.Runtime.dll",
                         "System.Console.dll",
                         "System.Collections.dll",
                         "netstandard.dll"
                     })
            {
                File.Copy(Path.Combine(source, fileName), Path.Combine(referenceRoot, fileName));
            }
            File.Copy(typeof(SharpLab.Runtime.RuntimeServices).Assembly.Location, Path.Combine(referenceRoot, "SharpLab.Runtime.dll"));
            using var provider = new FSharpReferenceSetProvider([new FSharpReferenceSetDefinition("net10-ref", referenceRoot, "net10.0", FSharpTestSettings.Net10Version)]);

            var loaded = await provider.GetAsync("net10-ref", TestContext.Current.CancellationToken);

            var attestedPaths = Directory.EnumerateFiles(referenceRoot, "*.dll", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(attestedPaths, loaded.ReferenceAssemblyPaths);
            Assert.DoesNotContain(typeof(SharpLab.Runtime.RuntimeServices).Assembly.Location, loaded.ReferenceAssemblyPaths, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            FSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CompileCheckIncludesTheSharpLabRuntimeApi()
    {
        var root = FSharpTestSettings.CreateRoot();
        try
        {
            var execution = await CreateService(root).ExecuteAsync(CreateRequest(BuildTarget.CompileCheck, [new WorkspaceFile("Program.fs", 1, "module Program\nInspect.Heap(box 42)\n")], ["Program.fs"], outputKind: BuildOutputKind.Library), TestContext.Current.CancellationToken);

            var result = Assert.IsType<CompilationCheckResult>(execution.Result);
            Assert.True(result.CompilationSucceeded);
            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        }
        finally
        {
            FSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ArtifactBuildCompilesMultipleFilesAndProducesPortablePdb()
    {
        var root = FSharpTestSettings.CreateRoot();
        try
        {
            var service = CreateService(root);
            var request = CreateRequest(
                BuildTarget.Artifact,
                [
                    new WorkspaceFile("Domain/Message.fs", 1, "namespace Demo\nmodule Message =\n    let text = \"Hello from F#\"\n"),
                    new WorkspaceFile("Program.fs", 2, "module Program\nopen System\nopen Demo\n[<EntryPoint>]\nlet main _ = Console.WriteLine(Message.text); 0\n")
                ],
                ["Domain/Message.fs", "Program.fs"]);

            var execution = await service.ExecuteAsync(request, TestContext.Current.CancellationToken);

            var result = Assert.IsType<BuildResult>(execution.Result);
            Assert.Equal(BuildOutcome.Succeeded, result.Outcome);
            Assert.DoesNotContain(result.Diagnostics, static item => item.Severity == DiagnosticSeverity.Error);
            Assert.NotNull(execution.Artifact);
            Assert.Equal([0x4d, 0x5a], execution.Artifact.PeImage[..2]);
            Assert.NotEmpty(execution.Artifact.PortablePdb);
            Assert.Equal("fsharp", execution.Artifact.Identity.LanguageId);
            Assert.Equal(FSharpCompilerFacade.CompilerVersion, execution.Artifact.Identity.CompilerVersion);
            Assert.Equal(FSharpCompilerFacade.CompilerVersion, FSharpCompilerFacade.LoadedCompilerVersion);
            Assert.Equal(FSharpCompilerFacade.FSharpCorePackageVersion, execution.Artifact.Manifest.Metadata!["fsharpCorePackageVersion"]);
            Assert.Equal("bundled-support-assembly", execution.Artifact.Manifest.Metadata!["fsharpCoreLinkMode"]);
            Assert.Contains("FSharp.Core", GetAssemblyReferences(execution.Artifact.PeImage));
            var supportAssembly = Assert.Single(execution.Artifact.Manifest.Files, static file => file.Role == "support-assembly");
            Assert.Equal("FSharp.Core.dll", supportAssembly.Path);
            Assert.Equal(execution.Artifact.FSharpCoreImage.LongLength, supportAssembly.Size);
            Assert.Equal(ContentIdentity.Compute(execution.Artifact.FSharpCoreImage).Value, supportAssembly.Digest);
            var tamperedPe = execution.Artifact.PeImage.ToArray();
            tamperedPe[^1] ^= 0xff;
            var tampered = execution.Artifact with { PeImage = tamperedPe };
            Assert.Throws<InvalidOperationException>(() => FSharpArtifactPublisher.ValidateArtifact(tampered));
            ArtifactIdentity.Validate(execution.Artifact.Manifest);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            FSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CompileCheckReturnsRevisionedFSharpDiagnostics()
    {
        var root = FSharpTestSettings.CreateRoot();
        try
        {
            var service = CreateService(root);
            var request = CreateRequest(BuildTarget.CompileCheck, [new WorkspaceFile("Program.fs", 5, "module Program\nlet value: int = \"text\"\n")], ["Program.fs"], revision: 21, selectionRevision: 8, outputKind: BuildOutputKind.Library);

            var execution = await service.ExecuteAsync(request, TestContext.Current.CancellationToken);

            var result = Assert.IsType<CompilationCheckResult>(execution.Result);
            Assert.False(result.CompilationSucceeded);
            Assert.Null(execution.Artifact);
            Assert.Contains(result.Diagnostics, static item => item.Code == "FS0001");
            Assert.All(result.Diagnostics, item =>
            {
                Assert.Equal(21, item.WorkspaceRevision);
                Assert.Equal(8, item.SelectionRevision);
                Assert.Equal("Program.fs", item.FilePath);
            });
        }
        finally
        {
            FSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Net11PreviewArtifactBuildUsesThePinnedReferenceSet()
    {
        var root = FSharpTestSettings.CreateRoot();
        try
        {
            var service = CreateService(root);
            var request = CreateRequest(BuildTarget.Artifact, [new WorkspaceFile("Program.fs", 1, "module Program\nlet value = System.Int128.One\n")], ["Program.fs"], outputKind: BuildOutputKind.Library, referenceSetId: "net11-preview-ref");

            var execution = await service.ExecuteAsync(request, TestContext.Current.CancellationToken);

            var result = Assert.IsType<BuildResult>(execution.Result);
            Assert.Equal(BuildOutcome.Succeeded, result.Outcome);
            Assert.NotNull(execution.Artifact);
            Assert.Equal("net11-preview-ref", execution.Artifact.ReferenceSetId);
            Assert.Equal("net11.0", execution.Artifact.TargetFramework);
            Assert.Equal(FSharpTestSettings.Net11PreviewVersion, Assert.Single(execution.Artifact.Manifest.RuntimeRequirement.Frameworks).MinimumVersion);
        }
        finally
        {
            FSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Net11PreviewCompileCheckReturnsFSharpDiagnostics()
    {
        var root = FSharpTestSettings.CreateRoot();
        try
        {
            var service = CreateService(root);
            var request = CreateRequest(BuildTarget.CompileCheck, [new WorkspaceFile("Program.fs", 1, "module Program\nlet value: int = System.Int128.One\n")], ["Program.fs"], outputKind: BuildOutputKind.Library, referenceSetId: "net11-preview-ref");

            var execution = await service.ExecuteAsync(request, TestContext.Current.CancellationToken);

            var result = Assert.IsType<CompilationCheckResult>(execution.Result);
            Assert.False(result.CompilationSucceeded);
            Assert.Contains(result.Diagnostics, static item => item.Code == "FS0193");
            Assert.Equal("net11-preview-ref", result.Identity.ReferenceSetId);
        }
        finally
        {
            FSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task SourceOrderControlsFSharpNameResolution()
    {
        var root = FSharpTestSettings.CreateRoot();
        try
        {
            var service = CreateService(root);
            WorkspaceFile[] files =
            [
                new("Definitions.fs", 1, "namespace Demo\ntype Message = { Text: string }\n"),
                new("Use.fs", 1, "namespace Demo\nmodule Use =\n    let value: Message = { Text = \"ok\" }\n")
            ];
            var correct = CreateRequest(BuildTarget.CompileCheck, files, ["Definitions.fs", "Use.fs"], outputKind: BuildOutputKind.Library);
            var reversed = CreateRequest(BuildTarget.CompileCheck, files, ["Use.fs", "Definitions.fs"], outputKind: BuildOutputKind.Library);

            var correctResult = Assert.IsType<CompilationCheckResult>((await service.ExecuteAsync(correct, TestContext.Current.CancellationToken)).Result);
            var reversedResult = Assert.IsType<CompilationCheckResult>((await service.ExecuteAsync(reversed, TestContext.Current.CancellationToken)).Result);

            Assert.True(correctResult.CompilationSucceeded);
            Assert.False(reversedResult.CompilationSucceeded);
            Assert.Contains(reversedResult.Diagnostics, static item => item.Code == "FS0039");
        }
        finally
        {
            FSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AstPreservesSourceOrderAndFSharpUnionCaseKinds()
    {
        var root = FSharpTestSettings.CreateRoot();
        try
        {
            var service = CreateService(root, new FSharpAstLimits(5_000, 128, 1024 * 1024, 100));
            var request = CreateRequest(
                BuildTarget.Ast,
                [
                    new WorkspaceFile("First.fs", 1, "namespace Demo\ntype Shape = | Circle of float | Square of float\n"),
                    new WorkspaceFile("Second.fs", 2, "namespace Demo\nmodule Area =\n    let calculate shape = match shape with | Circle r -> r * r | Square s -> s * s\n")
                ],
                ["First.fs", "Second.fs"],
                outputKind: BuildOutputKind.Library);

            var result = Assert.IsType<AstResult>((await service.ExecuteAsync(request, TestContext.Current.CancellationToken)).Result);

            Assert.Equal("Workspace", result.Document.Root.Kind);
            Assert.Equal("fsharp-stable", result.Identity?.ToolchainId);
            Assert.Equal("net10-ref", result.Identity?.ReferenceSetId);
            Assert.Equal($"sha256:{new string('a', 64)}", result.Identity?.WorkerImageId);
            Assert.Equal(["First.fs", "Second.fs"], result.Document.Root.Children.Select(static item => item.Properties["path"]));
            var kinds = Flatten(result.Document.Root).Select(static item => item.Kind).ToHashSet(StringComparer.Ordinal);
            Assert.Contains("ImplFile", kinds);
            Assert.Contains(kinds, static kind => kind.Contains("Type", StringComparison.Ordinal));
            Assert.Contains(Flatten(result.Document.Root), static node => node.Properties.ContainsKey("textPreview"));
        }
        finally
        {
            FSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task BuildRejectsWorkspaceLoadingAndReferenceDirectives()
    {
        var root = FSharpTestSettings.CreateRoot();
        try
        {
            var service = CreateService(root);
            var request = CreateRequest(BuildTarget.CompileCheck, [new WorkspaceFile("Program.fs", 1, "#load \"outside.fs\"\nmodule Program\n")], ["Program.fs"], outputKind: BuildOutputKind.Library);

            var exception = await Assert.ThrowsAsync<FSharpBuildRequestValidationException>(() => service.ExecuteAsync(request, TestContext.Current.CancellationToken));
            Assert.Contains("#load", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            FSharpTestSettings.DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData(BuildOutputKind.Auto)]
    [InlineData((BuildOutputKind)999)]
    public async Task NonConcreteOutputKindsAreRejectedBeforeStartingCompiler(BuildOutputKind outputKind)
    {
        var root = FSharpTestSettings.CreateRoot();
        try
        {
            var request = CreateRequest(BuildTarget.CompileCheck, [new WorkspaceFile("Program.fs", 1, "module Program\nlet value = 42\n")], ["Program.fs"], outputKind: outputKind);

            await Assert.ThrowsAsync<FSharpBuildRequestValidationException>(() => CreateService(root).ExecuteAsync(request, TestContext.Current.CancellationToken));

            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            FSharpTestSettings.DeleteRoot(root);
        }
    }

    private static FSharpBuildService CreateService(string root, FSharpAstLimits? astLimits = null)
    {
        var settings = FSharpTestSettings.Create(root, astLimits);
        return new FSharpBuildService(new FSharpReferenceSetProvider(settings.ReferenceSets), new FSharpCompilerFacade(), settings);
    }

    internal static BuildRequest CreateRequest(BuildTarget target, IReadOnlyList<WorkspaceFile> files, IReadOnlyList<string> sourceOrder, long revision = 1, long selectionRevision = 1, BuildOutputKind outputKind = BuildOutputKind.Console, string referenceSetId = "net10-ref")
    {
        var options = new BuildOptions(BuildConfiguration.Release, Optimize: true, outputKind, AllowUnsafe: false, EmitPortablePdb: true, NullableContextMode.Disable, LanguageVersion: "9.0", PreprocessorSymbols: ["SHARPLABNEXT"], CheckOverflow: true);
        var workspace = new WorkspaceSnapshot(ContractSchemaVersions.WorkspaceSnapshot, revision, selectionRevision, "fsharp", files, files[^1].Path, sourceOrder, referenceSetId, options);
        return new BuildRequest($"request-{Guid.NewGuid():N}", $"idempotency-{Guid.NewGuid():N}", "pipeline-test", "fsharp-stable", referenceSetId, workspace, DateTimeOffset.UtcNow.AddMinutes(1), options, target);
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

    private static string[] GetAssemblyReferences(byte[] peImage)
    {
        using var stream = new MemoryStream(peImage, writable: false);
        using var peReader = new PEReader(stream);
        var reader = peReader.GetMetadataReader();
        return reader.AssemblyReferences.Select(handle => reader.GetString(reader.GetAssemblyReference(handle).Name)).ToArray();
    }
}
