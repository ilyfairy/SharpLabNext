using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.IL.Tests;

public sealed class IlBuildServiceTests
{
    [Theory]
    [InlineData("net10-ref", "net10.0", "10.0.9")]
    [InlineData("net11-preview-ref", "net11.0", "11.0.0-preview.5.26302.115")]
    public async Task ArtifactBuildRecordsTheExplicitReferenceSelection(
        string referenceSetId,
        string targetFramework,
        string frameworkVersion)
    {
        var root = IlTestSettings.CreateRoot();
        try
        {
            var settings = IlTestSettings.Create(root);
            using var assembler = new IlAssemblerProcess(settings, NullLogger<IlAssemblerProcess>.Instance);
            var service = CreateService(settings, assembler);
            var execution = await service.ExecuteAsync(
                IlTestSettings.CreateRequest(
                    BuildTarget.Artifact,
                    IlTestSettings.ValidMultiFileWorkspace(),
                    ["Program.il", "Helper.il"],
                    referenceSetId: referenceSetId),
                TestContext.Current.CancellationToken);

            var artifact = Assert.IsType<IlCompiledArtifact>(execution.Artifact);
            ArtifactIdentity.Validate(artifact.Manifest);
            Assert.Equal(artifact.Manifest.ArtifactId, artifact.ArtifactRef);
            Assert.Equal(referenceSetId, artifact.ReferenceSetId);
            Assert.Equal(targetFramework, artifact.TargetFramework);
            Assert.Equal(referenceSetId, artifact.Manifest.ReferenceSetId);
            Assert.Equal(targetFramework, artifact.Manifest.TargetFramework);
            var framework = Assert.Single(artifact.Manifest.RuntimeRequirement.Frameworks);
            Assert.Equal("Microsoft.NETCore.App", framework.Name);
            Assert.Equal(frameworkVersion, framework.MinimumVersion);
            var result = Assert.IsType<BuildResult>(execution.Result);
            Assert.Equal(referenceSetId, result.Identity.ReferenceSetId);
        }
        finally
        {
            IlTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ArtifactBuildAssemblesMultipleFilesIntoManagedPeWithoutPdb()
    {
        var root = IlTestSettings.CreateRoot();
        try
        {
            var settings = IlTestSettings.Create(root);
            using var assembler = new IlAssemblerProcess(settings, NullLogger<IlAssemblerProcess>.Instance);
            var service = CreateService(settings, assembler);
            var files = IlTestSettings.ValidMultiFileWorkspace();
            var execution = await service.ExecuteAsync(
                IlTestSettings.CreateRequest(BuildTarget.Artifact, files, ["Program.il", "Helper.il"]),
                TestContext.Current.CancellationToken);

            var result = Assert.IsType<BuildResult>(execution.Result);
            Assert.Equal(BuildOutcome.Succeeded, result.Outcome);
            var artifact = Assert.IsType<IlCompiledArtifact>(execution.Artifact);
            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            Assert.DoesNotContain(artifact.Files, static file => file.Role == "portable-pdb");
            Assert.Equal("false", artifact.Manifest.Metadata!["portablePdb"]);
            Assert.Equal("Program::Main", artifact.Manifest.EntryPoint);
            Assert.Equal("SharpLabNextMulti", artifact.Manifest.Metadata["sourceAssemblyName"]);
            using var peReader = new PEReader(new MemoryStream(artifact.PeImage, writable: false));
            Assert.True(peReader.HasMetadata);
            MetadataReader metadata = peReader.GetMetadataReader();
            Assert.True(metadata.IsAssembly);
            Assert.Equal("SharpLabNextMulti", metadata.GetString(metadata.GetAssemblyDefinition().Name));
            Assert.False(Directory.Exists(settings.WorkRoot) && Directory.EnumerateFileSystemEntries(settings.WorkRoot).Any());
        }
        finally
        {
            IlTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task LibraryArtifactBuildDoesNotRequireAnEntryPoint()
    {
        var root = IlTestSettings.CreateRoot();
        try
        {
            var settings = IlTestSettings.Create(root);
            using var assembler = new IlAssemblerProcess(settings, NullLogger<IlAssemblerProcess>.Instance);
            var service = CreateService(settings, assembler);
            var source = """
                .assembly SharpLabNext.Library {}
                .class public auto ansi Library extends [System.Runtime]System.Object
                {
                  .method public hidebysig static int32 Add(int32 left, int32 right) cil managed
                  {
                    .maxstack 2
                    ldarg.0
                    ldarg.1
                    add
                    ret
                  }
                }
                """;

            var execution = await service.ExecuteAsync(
                IlTestSettings.CreateRequest(
                    BuildTarget.Artifact,
                    [new WorkspaceFile("Library.il", 1, source)],
                    ["Library.il"],
                    BuildOutputKind.Library),
                TestContext.Current.CancellationToken);

            var result = Assert.IsType<BuildResult>(execution.Result);
            Assert.Equal(BuildOutcome.Succeeded, result.Outcome);
            var artifact = Assert.IsType<IlCompiledArtifact>(execution.Artifact);
            Assert.Equal(BuildOutputKind.Library, artifact.Manifest.OutputKind);
            Assert.Null(artifact.Manifest.EntryPoint);
            using var peReader = new PEReader(new MemoryStream(artifact.PeImage, writable: false));
            Assert.Equal(0, peReader.PEHeaders.CorHeader!.EntryPointTokenOrRelativeVirtualAddress);
        }
        finally
        {
            IlTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ConsoleArtifactBuildStillRequiresAnEntryPoint()
    {
        var root = IlTestSettings.CreateRoot();
        try
        {
            var settings = IlTestSettings.Create(root);
            using var assembler = new IlAssemblerProcess(settings, NullLogger<IlAssemblerProcess>.Instance);
            var service = CreateService(settings, assembler);
            var source = """
                .assembly SharpLabNext.ConsoleWithoutEntryPoint {}
                .class public auto ansi Program extends [System.Runtime]System.Object
                {
                  .method public hidebysig static void Method() cil managed
                  {
                    ret
                  }
                }
                """;

            var execution = await service.ExecuteAsync(
                IlTestSettings.CreateRequest(
                    BuildTarget.Artifact,
                    [new WorkspaceFile("Program.il", 1, source)],
                    ["Program.il"],
                    BuildOutputKind.Console),
                TestContext.Current.CancellationToken);

            var result = Assert.IsType<BuildResult>(execution.Result);
            Assert.Equal(BuildOutcome.CompilationFailed, result.Outcome);
            Assert.Null(execution.Artifact);
            Assert.Contains(result.Diagnostics, static diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Error &&
                diagnostic.Message.Contains("entry point", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            IlTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CompileCheckReturnsAssemblerDiagnosticsWithoutArtifact()
    {
        var root = IlTestSettings.CreateRoot();
        try
        {
            var settings = IlTestSettings.Create(root);
            using var assembler = new IlAssemblerProcess(settings, NullLogger<IlAssemblerProcess>.Instance);
            var service = CreateService(settings, assembler);
            var files = new[] { new WorkspaceFile("Broken.il", 1, ".assembly Broken {}\n.class public Broken {\n.method public static void Main() cil managed { definitely.not.an.opcode ret }\n") };
            var execution = await service.ExecuteAsync(
                IlTestSettings.CreateRequest(BuildTarget.CompileCheck, files, ["Broken.il"]),
                TestContext.Current.CancellationToken);

            var result = Assert.IsType<CompilationCheckResult>(execution.Result);
            Assert.False(result.CompilationSucceeded);
            Assert.Null(execution.Artifact);
            Assert.Contains(result.Diagnostics, static diagnostic =>
                diagnostic.Source == "mobius-ilasm" && diagnostic.Severity == DiagnosticSeverity.Error);
        }
        finally
        {
            IlTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task SuccessfulCompileCheckStillUsesTheRealAssemblerWithoutPublishingAnArtifact()
    {
        var root = IlTestSettings.CreateRoot();
        try
        {
            var settings = IlTestSettings.Create(root);
            using var assembler = new IlAssemblerProcess(settings, NullLogger<IlAssemblerProcess>.Instance);
            var service = CreateService(settings, assembler);
            var execution = await service.ExecuteAsync(
                IlTestSettings.CreateRequest(
                    BuildTarget.CompileCheck,
                    IlTestSettings.ValidMultiFileWorkspace(),
                    ["Program.il", "Helper.il"]),
                TestContext.Current.CancellationToken);

            var result = Assert.IsType<CompilationCheckResult>(execution.Result);
            Assert.True(result.CompilationSucceeded);
            Assert.Null(execution.Artifact);
            Assert.Equal(1, assembler.StartedProcessCount);
        }
        finally
        {
            IlTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task EachBuildStartsAndCleansUpASeparateCompilerProcessInvocation()
    {
        var root = IlTestSettings.CreateRoot();
        try
        {
            var settings = IlTestSettings.Create(root);
            using var assembler = new IlAssemblerProcess(settings, NullLogger<IlAssemblerProcess>.Instance);
            var service = CreateService(settings, assembler);
            var files = IlTestSettings.ValidMultiFileWorkspace();
            await service.ExecuteAsync(
                IlTestSettings.CreateRequest(BuildTarget.CompileCheck, files, ["Program.il", "Helper.il"]),
                TestContext.Current.CancellationToken);
            await service.ExecuteAsync(
                IlTestSettings.CreateRequest(BuildTarget.CompileCheck, files, ["Program.il", "Helper.il"]),
                TestContext.Current.CancellationToken);

            Assert.Equal(2, assembler.StartedProcessCount);
            Assert.False(Directory.Exists(settings.WorkRoot) && Directory.EnumerateFileSystemEntries(settings.WorkRoot).Any());
        }
        finally
        {
            IlTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task MultiFileAssemblerDiagnosticMapsToTheCorrectSourceAfterTrailingNewline()
    {
        var root = IlTestSettings.CreateRoot();
        try
        {
            var settings = IlTestSettings.Create(root);
            using var assembler = new IlAssemblerProcess(settings, NullLogger<IlAssemblerProcess>.Instance);
            var service = CreateService(settings, assembler);
            WorkspaceFile[] files =
            [
                new("Assembly.il", 1, ".assembly MappingTest {}\n.module MappingTest.dll\n"),
                new("Broken.il", 1, ".class public Broken {\n.method public static void Main() cil managed {\n  definitely.not.an.opcode\n  ret\n}\n}\n")
            ];
            var execution = await service.ExecuteAsync(
                IlTestSettings.CreateRequest(BuildTarget.CompileCheck, files, ["Assembly.il", "Broken.il"]),
                TestContext.Current.CancellationToken);

            var result = Assert.IsType<CompilationCheckResult>(execution.Result);
            Assert.False(result.CompilationSucceeded);
            Assert.Contains(result.Diagnostics, static item => item.FilePath == "Broken.il");
            var diagnostic = result.Diagnostics.First(static item => item.FilePath == "Broken.il");
            Assert.NotNull(diagnostic.Range);
            Assert.InRange(diagnostic.Range.StartLine, 0, 5);
        }
        finally
        {
            IlTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task BuildRejectsManifestResourceFilesystemAccessInsideCompilerChild()
    {
        var root = IlTestSettings.CreateRoot();
        try
        {
            var settings = IlTestSettings.Create(root);
            using var assembler = new IlAssemblerProcess(settings, NullLogger<IlAssemblerProcess>.Instance);
            var service = CreateService(settings, assembler);
            var source = ".assembly ResourceProbe {}\n.mresource public Probe from 'appsettings.json'\n.class public Probe {}\n";
            var execution = await service.ExecuteAsync(
                IlTestSettings.CreateRequest(
                    BuildTarget.Artifact,
                    [new WorkspaceFile("Probe.il", 1, source)],
                    ["Probe.il"],
                    BuildOutputKind.Library),
                TestContext.Current.CancellationToken);

            var result = Assert.IsType<BuildResult>(execution.Result);
            Assert.Equal(BuildOutcome.CompilationFailed, result.Outcome);
            Assert.Contains(result.Diagnostics, static diagnostic =>
                diagnostic.Message.Contains("Manifest resource", StringComparison.Ordinal));
        }
        finally
        {
            IlTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task WindowsApplicationIsRejectedBeforeStartingCompiler()
    {
        var root = IlTestSettings.CreateRoot();
        try
        {
            var settings = IlTestSettings.Create(root);
            using var assembler = new IlAssemblerProcess(settings, NullLogger<IlAssemblerProcess>.Instance);
            var service = CreateService(settings, assembler);
            await Assert.ThrowsAsync<IlBuildRequestValidationException>(() => service.ExecuteAsync(
                IlTestSettings.CreateRequest(
                    BuildTarget.Artifact,
                    IlTestSettings.ValidMultiFileWorkspace(),
                    ["Program.il", "Helper.il"],
                    BuildOutputKind.WindowsApplication),
                TestContext.Current.CancellationToken));
            Assert.Equal(0, assembler.StartedProcessCount);
        }
        finally
        {
            IlTestSettings.DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData(BuildOutputKind.Auto)]
    [InlineData((BuildOutputKind)999)]
    public async Task NonConcreteOutputKindsAreRejectedBeforeStartingCompiler(BuildOutputKind outputKind)
    {
        var root = IlTestSettings.CreateRoot();
        try
        {
            var settings = IlTestSettings.Create(root);
            using var assembler = new IlAssemblerProcess(settings, NullLogger<IlAssemblerProcess>.Instance);
            var service = CreateService(settings, assembler);

            await Assert.ThrowsAsync<IlBuildRequestValidationException>(() => service.ExecuteAsync(
                IlTestSettings.CreateRequest(
                    BuildTarget.Artifact,
                    IlTestSettings.ValidMultiFileWorkspace(),
                    ["Program.il", "Helper.il"],
                    outputKind),
                TestContext.Current.CancellationToken));

            Assert.Equal(0, assembler.StartedProcessCount);
        }
        finally
        {
            IlTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task InvalidSourceOrderIsRejectedBeforeStartingCompiler()
    {
        var root = IlTestSettings.CreateRoot();
        try
        {
            var settings = IlTestSettings.Create(root);
            using var assembler = new IlAssemblerProcess(settings, NullLogger<IlAssemblerProcess>.Instance);
            var service = CreateService(settings, assembler);
            var files = IlTestSettings.ValidMultiFileWorkspace();
            var request = IlTestSettings.CreateRequest(BuildTarget.Artifact, files, ["Program.il", "Program.il"]);
            await Assert.ThrowsAsync<IlBuildRequestValidationException>(() =>
                service.ExecuteAsync(request, TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(settings.WorkRoot));
        }
        finally
        {
            IlTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public void PathsThatDifferOnlyByCaseAreRejectedBeforeCreatingAnIlSenseSnapshot()
    {
        var request = IlTestSettings.CreateRequest(
            BuildTarget.CompileCheck,
            [
                new WorkspaceFile("Program.il", 1, ".assembly A {}"),
                new WorkspaceFile("program.il", 1, ".assembly B {}")
            ],
            ["Program.il", "program.il"]);

        Assert.Throws<IlBuildRequestValidationException>(() =>
            IlWorkspaceValidator.Validate(request, IlCompilationLimits.Default));
    }

    [Fact]
    public async Task ExpiredDeadlineAndOversizedInputAreRejectedBeforeStartingCompiler()
    {
        var root = IlTestSettings.CreateRoot();
        try
        {
            var settings = IlTestSettings.Create(root);
            using var assembler = new IlAssemblerProcess(settings, NullLogger<IlAssemblerProcess>.Instance);
            var service = CreateService(settings, assembler);
            var request = IlTestSettings.CreateRequest(
                BuildTarget.CompileCheck,
                [new WorkspaceFile("Program.il", 1, ".assembly A {}")],
                ["Program.il"]);
            await Assert.ThrowsAsync<IlBuildDeadlineExceededException>(() =>
                service.ExecuteAsync(request with { DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(-1) }, TestContext.Current.CancellationToken));

            var oversized = new string(' ', settings.CompilationLimits.MaxFileUtf8Bytes + 1);
            await Assert.ThrowsAsync<IlBuildRequestValidationException>(() =>
                service.ExecuteAsync(
                    IlTestSettings.CreateRequest(
                        BuildTarget.CompileCheck,
                        [new WorkspaceFile("Program.il", 1, oversized)],
                        ["Program.il"]),
                    TestContext.Current.CancellationToken));
            Assert.Equal(0, assembler.StartedProcessCount);
        }
        finally
        {
            IlTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CancellationKillsTheOneShotCompilerAndCleansItsWorkspace()
    {
        var root = IlTestSettings.CreateRoot();
        try
        {
            var settings = IlTestSettings.Create(root);
            using var assembler = new IlAssemblerProcess(settings, NullLogger<IlAssemblerProcess>.Instance);
            var service = CreateService(settings, assembler);
            var source = new StringBuilder(".assembly Cancellation {}\n.class public C {\n.method public static void M() cil managed {\n.maxstack 0\n");
            for (var index = 0; index < 100_000; index++)
                source.Append("nop\n");
            source.Append("ret\n}\n}\n");
            using var cancellation = new CancellationTokenSource();
            var execution = service.ExecuteAsync(
                IlTestSettings.CreateRequest(
                    BuildTarget.CompileCheck,
                    [new WorkspaceFile("Cancellation.il", 1, source.ToString())],
                    ["Cancellation.il"],
                    BuildOutputKind.Library),
                cancellation.Token);
            for (var attempt = 0; assembler.StartedProcessCount == 0 && !execution.IsCompleted && attempt < 1_000; attempt++)
                await Task.Delay(1, TestContext.Current.CancellationToken);
            Assert.Equal(1, assembler.StartedProcessCount);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
            Assert.False(Directory.Exists(settings.WorkRoot) && Directory.EnumerateFileSystemEntries(settings.WorkRoot).Any());
        }
        finally
        {
            IlTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CompilerProcessOutputLimitFailsHealthWithoutRetainingUnboundedOutput()
    {
        var root = IlTestSettings.CreateRoot();
        try
        {
            var original = IlTestSettings.Create(root);
            var settings = original with
            {
                CompilationLimits = original.CompilationLimits with { MaxProcessOutputBytes = 8 }
            };
            using var assembler = new IlAssemblerProcess(settings, NullLogger<IlAssemblerProcess>.Instance);
            var health = await assembler.CheckHealthAsync(TestContext.Current.CancellationToken);
            Assert.False(health.IsHealthy);
            Assert.Equal(1, assembler.StartedProcessCount);
        }
        finally
        {
            IlTestSettings.DeleteRoot(root);
        }
    }

    private static IlBuildService CreateService(IlWorkerSettings settings, IlAssemblerProcess assembler) =>
        new(
            new IlReferenceSetProvider(settings.ReferenceSets),
            assembler,
            settings.Identity,
            settings.CompilationLimits);
}
