using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Sdk;

namespace SharpLabNext.Worker.GSharp.Tests;

public sealed class GSharpBuildServiceTests
{
    private const string ValidProgram = """
        package SharpLabNextUser

        import System

        Console.WriteLine("Hello from G#")
        """;

    [Fact]
    public async Task ArtifactBuildUsesRealGscAndReturnsManagedPeWithPortablePdb()
    {
        var root = GSharpTestSettings.CreateRoot();
        try
        {
            var service = GSharpTestSettings.CreateBuildService(root, out var compiler);
            using (compiler)
            {
                var execution = await service.BuildAsync(GSharpTestSettings.CreateRequest(BuildTarget.Artifact, ValidProgram), TestContext.Current.CancellationToken);

                var result = Assert.IsType<BuildResult>(execution.Result);
                Assert.Equal(BuildOutcome.Succeeded, result.Outcome);
                Assert.Equal(GSharpTestSettings.CompilerVersion, result.Identity.CompilerVersion);
                Assert.Equal(GSharpTestSettings.CompilerCommit, result.Identity.CompilerCommit);
                var envelope = Assert.IsType<LanguageWorkerArtifactEnvelope>(execution.Artifact);
                Assert.Equal(GSharpToolchain.ArtifactFormat, envelope.ArtifactFormat);
                Assert.Equal(result.ArtifactRef, envelope.ArtifactRef);
                Assert.Equal(envelope.ArtifactRef, envelope.Manifest.ArtifactId);
                ArtifactIdentity.Validate(envelope.Manifest);
                Assert.Equal("coreclr", envelope.Manifest.RuntimeRequirement.Family);
                Assert.Equal("net10.0", envelope.TargetFramework);
                Assert.Equal("true", envelope.Manifest.Metadata!["portablePdb"]);
                Assert.Contains(envelope.Files, static file => file.Role == "primary-assembly");
                Assert.Contains(envelope.Files, static file => file.Role == "portable-pdb");

                var contents = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(envelope.FileContentsBase64);
                var pe = Convert.FromBase64String(contents[$"{GSharpToolchain.AssemblyName}.dll"]);
                var pdb = Convert.FromBase64String(contents[$"{GSharpToolchain.AssemblyName}.pdb"]);
                using var peReader = new PEReader(new MemoryStream(pe, writable: false));
                Assert.True(peReader.HasMetadata);
                Assert.True(peReader.GetMetadataReader().IsAssembly);
                Assert.NotEqual(0, peReader.PEHeaders.CorHeader!.EntryPointTokenOrRelativeVirtualAddress);
                using var pdbProvider = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(pdb, writable: false));
                Assert.NotEmpty(pdbProvider.GetMetadataReader().Documents);
                Assert.Equal(1, compiler.StartedProcessCount);
            }
            Assert.False(Directory.Exists(Path.Combine(root, "work")) && Directory.EnumerateFileSystemEntries(Path.Combine(root, "work")).Any());
        }
        finally
        {
            GSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CompileCheckRunsRealEmitAndMapsGscDiagnosticsWithoutArtifact()
    {
        var root = GSharpTestSettings.CreateRoot();
        try
        {
            var service = GSharpTestSettings.CreateBuildService(root, out var compiler);
            using (compiler)
            {
                var execution = await service.BuildAsync(GSharpTestSettings.CreateRequest(BuildTarget.CompileCheck, "package Broken\n\n\""), TestContext.Current.CancellationToken);

                var result = Assert.IsType<CompilationCheckResult>(execution.Result);
                Assert.False(result.CompilationSucceeded);
                Assert.Null(execution.Artifact);
                var diagnostic = Assert.Single(result.Diagnostics, static item => item.Code == "GS0003");
                Assert.Equal("gsc", diagnostic.Source);
                Assert.Equal("Program.gs", diagnostic.FilePath);
                Assert.NotNull(diagnostic.Range);
                Assert.Equal(7, diagnostic.WorkspaceRevision);
                Assert.Equal(3, diagnostic.SelectionRevision);
                Assert.Equal(1, compiler.StartedProcessCount);
            }
        }
        finally
        {
            GSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task MultiFileLibraryPreservesSourceOrderAndHasNoEntryPoint()
    {
        var root = GSharpTestSettings.CreateRoot();
        try
        {
            var service = GSharpTestSettings.CreateBuildService(root, out var compiler);
            using (compiler)
            {
                WorkspaceFile[] files =
                [
                    new("Library.gs", 1, "package Multi\n\nfunc Answer() int32 { return 42 }\n"),
                    new("More.gs", 1, "package Multi\n\nfunc Double(value int32) int32 { return value * 2 }\n")
                ];
                var execution = await service.BuildAsync(GSharpTestSettings.CreateRequest(BuildTarget.Artifact, files, ["More.gs", "Library.gs"], BuildOutputKind.Library), TestContext.Current.CancellationToken);

                var result = Assert.IsType<BuildResult>(execution.Result);
                Assert.Equal(BuildOutcome.Succeeded, result.Outcome);
                var envelope = Assert.IsType<LanguageWorkerArtifactEnvelope>(execution.Artifact);
                Assert.Null(envelope.Manifest.EntryPoint);
                Assert.Equal(BuildOutputKind.Library, envelope.Manifest.OutputKind);
                Assert.False(string.IsNullOrWhiteSpace(envelope.Manifest.Metadata!["sourceOrderSha256"]));
            }
        }
        finally
        {
            GSharpTestSettings.DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData(GSharpToolchain.ToolchainId)]
    [InlineData(GSharpToolchain.LegacyToolchainId)]
    public async Task AutoUsesConsoleWhenSelectedCompilerEmitsManagedEntryPoint(string toolchainId)
    {
        var root = GSharpTestSettings.CreateRoot();
        try
        {
            var service = GSharpTestSettings.CreateBuildService(root, out var compiler);
            using (compiler)
            {
                var execution = await service.BuildAsync(GSharpTestSettings.CreateRequest(BuildTarget.Artifact, ValidProgram, BuildOutputKind.Auto, toolchainId), TestContext.Current.CancellationToken);

                var result = Assert.IsType<BuildResult>(execution.Result);
                Assert.Equal(BuildOutcome.Succeeded, result.Outcome);
                var expected = toolchainId == GSharpToolchain.ToolchainId
                    ? GSharpTestSettings.StableToolchain : GSharpTestSettings.LegacyToolchain;
                Assert.Equal(toolchainId, result.Identity.ToolchainId);
                Assert.Equal(expected.CompilerVersion, result.Identity.CompilerVersion);
                Assert.Equal(expected.CompilerCommit, result.Identity.CompilerCommit);
                var envelope = Assert.IsType<LanguageWorkerArtifactEnvelope>(execution.Artifact);
                Assert.Equal(BuildOutputKind.Console, envelope.Manifest.OutputKind);
                Assert.NotNull(envelope.Manifest.EntryPoint);
                Assert.Equal(1, compiler.StartedProcessCount);
            }
        }
        finally
        {
            GSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AutoUsesLibraryWhenExeProbeHasNoManagedEntryPoint()
    {
        var root = GSharpTestSettings.CreateRoot();
        try
        {
            var service = GSharpTestSettings.CreateBuildService(root, out var compiler);
            using (compiler)
            {
                const string source = "package Library\n\nfunc Answer() int32 { return 42 }\n";
                var execution = await service.BuildAsync(GSharpTestSettings.CreateRequest(BuildTarget.Artifact, source, BuildOutputKind.Auto), TestContext.Current.CancellationToken);

                var result = Assert.IsType<BuildResult>(execution.Result);
                Assert.Equal(BuildOutcome.Succeeded, result.Outcome);
                var envelope = Assert.IsType<LanguageWorkerArtifactEnvelope>(execution.Artifact);
                Assert.Equal(BuildOutputKind.Library, envelope.Manifest.OutputKind);
                Assert.Null(envelope.Manifest.EntryPoint);
                var contents = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(envelope.FileContentsBase64);
                using var peReader = new PEReader(new MemoryStream(Convert.FromBase64String(contents[$"{GSharpToolchain.AssemblyName}.dll"]), writable: false));
                Assert.Equal(0, peReader.PEHeaders.CorHeader!.EntryPointTokenOrRelativeVirtualAddress);
                Assert.Equal(1, compiler.StartedProcessCount);
            }
        }
        finally
        {
            GSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ExplicitLibraryWithTopLevelStatementsPreservesCompilerDiagnostic()
    {
        var root = GSharpTestSettings.CreateRoot();
        try
        {
            var service = GSharpTestSettings.CreateBuildService(root, out var compiler);
            using (compiler)
            {
                var execution = await service.BuildAsync(GSharpTestSettings.CreateRequest(BuildTarget.Artifact, ValidProgram, BuildOutputKind.Library), TestContext.Current.CancellationToken);

                var result = Assert.IsType<BuildResult>(execution.Result);
                Assert.Equal(BuildOutcome.CompilationFailed, result.Outcome);
                Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "GS0285");
                Assert.Null(execution.Artifact);
                Assert.Equal(1, compiler.StartedProcessCount);
            }
        }
        finally
        {
            GSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ExplicitConsoleWithoutEntryPointReturnsCompilationDiagnostic()
    {
        var root = GSharpTestSettings.CreateRoot();
        try
        {
            var service = GSharpTestSettings.CreateBuildService(root, out var compiler);
            using (compiler)
            {
                const string source = "package Library\n\nfunc Answer() int32 { return 42 }\n";
                var execution = await service.BuildAsync(GSharpTestSettings.CreateRequest(BuildTarget.Artifact, source, BuildOutputKind.Console), TestContext.Current.CancellationToken);

                var result = Assert.IsType<BuildResult>(execution.Result);
                Assert.Equal(BuildOutcome.CompilationFailed, result.Outcome);
                var diagnostic = Assert.Single(result.Diagnostics);
                Assert.Equal("GS9999", diagnostic.Code);
                Assert.Contains("managed entry point", diagnostic.Message, StringComparison.Ordinal);
                Assert.Null(execution.Artifact);
            }
        }
        finally
        {
            GSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CompileCheckExplicitConsoleWithoutEntryPointFailsCompilation()
    {
        var root = GSharpTestSettings.CreateRoot();
        try
        {
            var service = GSharpTestSettings.CreateBuildService(root, out var compiler);
            using (compiler)
            {
                const string source = "package Library\n\nfunc Answer() int32 { return 42 }\n";
                var execution = await service.BuildAsync(GSharpTestSettings.CreateRequest(BuildTarget.CompileCheck, source, BuildOutputKind.Console), TestContext.Current.CancellationToken);

                var result = Assert.IsType<CompilationCheckResult>(execution.Result);
                Assert.False(result.CompilationSucceeded);
                Assert.Contains(result.Diagnostics, static item => item.Code == "GS9999");
                Assert.Null(execution.Artifact);
            }
        }
        finally
        {
            GSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task TraversalIsRejectedBeforeStartingCompiler()
    {
        var root = GSharpTestSettings.CreateRoot();
        try
        {
            var service = GSharpTestSettings.CreateBuildService(root, out var compiler);
            using (compiler)
            {
                var request = GSharpTestSettings.CreateRequest(BuildTarget.CompileCheck, [new WorkspaceFile("../Program.gs", 1, ValidProgram)], ["../Program.gs"]);
                var exception = await Assert.ThrowsAsync<LanguageWorkerRequestException>(() => service.BuildAsync(request, TestContext.Current.CancellationToken));
                Assert.Equal("invalid-workspace", exception.Code);
                Assert.Equal(0, compiler.StartedProcessCount);
            }
        }
        finally
        {
            GSharpTestSettings.DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData(BuildOutputKind.WindowsApplication)]
    [InlineData((BuildOutputKind)999)]
    public async Task UnsupportedOutputKindsAreRejectedBeforeStartingCompiler(BuildOutputKind outputKind)
    {
        var root = GSharpTestSettings.CreateRoot();
        try
        {
            var service = GSharpTestSettings.CreateBuildService(root, out var compiler);
            using (compiler)
            {
                var exception = await Assert.ThrowsAsync<LanguageWorkerRequestException>(() => service.BuildAsync(GSharpTestSettings.CreateRequest(BuildTarget.Artifact, ValidProgram, outputKind), TestContext.Current.CancellationToken));

                Assert.Equal("unsupported-option", exception.Code);
                Assert.Equal(0, compiler.StartedProcessCount);
            }
        }
        finally
        {
            GSharpTestSettings.DeleteRoot(root);
        }
    }
}
