using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Sdk;

namespace SharpLabNext.Worker.CppCli.Tests;

public sealed class CppCliBuildServiceTests
{
    [Fact]
    public async Task ArtifactPublishesTruthfulMixedModeNetFxContract()
    {
        var root = CppCliTestSettings.CreateRoot();
        try
        {
            var compiler = new FakeCppCliCompilerProcess(new CppCliCompilerInvocation(
                true,
                CppCliTestSettings.CreateMixedModePe(),
                []));
            var service = new CppCliBuildService(
                compiler,
                CppCliTestSettings.CreateSettings(root),
                CppCliTestSettings.LoadManifest());

            var execution = await service.BuildAsync(
                CppCliTestSettings.CreateRequest(BuildTarget.Artifact),
                TestContext.Current.CancellationToken);

            var result = Assert.IsType<BuildResult>(execution.Result);
            Assert.Equal(BuildOutcome.Succeeded, result.Outcome);
            Assert.Null(result.Identity.CompilerCommit);
            var envelope = Assert.IsType<LanguageWorkerArtifactEnvelope>(execution.Artifact);
            Assert.Equal(CppCliToolchain.ArtifactFormat, envelope.ArtifactFormat);
            Assert.Equal(CppCliToolchain.TargetFramework, envelope.TargetFramework);
            Assert.Equal(CppCliToolchain.RuntimeFamily, envelope.Manifest.RuntimeRequirement.Family);
            Assert.Equal("x64", envelope.Manifest.RuntimeRequirement.Architecture);
            Assert.Equal(
                new FrameworkRequirement(CppCliToolchain.FrameworkName, CppCliToolchain.FrameworkVersion),
                Assert.Single(envelope.Manifest.RuntimeRequirement.Frameworks));
            Assert.Null(envelope.Manifest.EntryPoint);
            Assert.Equal(CppCliToolchain.OutputFileName, envelope.Manifest.EntryAssembly);
            Assert.Equal("primary-assembly", Assert.Single(envelope.Files).Role);
            Assert.Equal("true", envelope.Manifest.Metadata!["mixedMode"]);
            Assert.Equal("true", envelope.Manifest.Metadata!["deterministic"]);
            Assert.Equal("false", envelope.Manifest.Metadata!["portablePdb"]);
            ArtifactIdentity.Validate(envelope.Manifest);
            Assert.Equal(1, compiler.CallCount);
        }
        finally
        {
            CppCliTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CompileCheckRunsRealEmitContractWithoutReturningArtifact()
    {
        var root = CppCliTestSettings.CreateRoot();
        try
        {
            var compiler = new FakeCppCliCompilerProcess(new CppCliCompilerInvocation(
                true,
                CppCliTestSettings.CreateMixedModePe(),
                []));
            var service = new CppCliBuildService(
                compiler,
                CppCliTestSettings.CreateSettings(root),
                CppCliTestSettings.LoadManifest());

            var execution = await service.BuildAsync(
                CppCliTestSettings.CreateRequest(BuildTarget.CompileCheck),
                TestContext.Current.CancellationToken);

            var result = Assert.IsType<CompilationCheckResult>(execution.Result);
            Assert.True(result.CompilationSucceeded);
            Assert.Null(execution.Artifact);
            Assert.Equal(1, compiler.CallCount);
        }
        finally
        {
            CppCliTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CompilerDiagnosticsProduceCompilationFailure()
    {
        var root = CppCliTestSettings.CreateRoot();
        try
        {
            var diagnostic = new Diagnostic(
                "msvc-cl",
                "C2065",
                DiagnosticSeverity.Error,
                "undeclared identifier",
                "Program.cpp",
                new TextRange(0, 0, 0, 1),
                [],
                [],
                7,
                3);
            var compiler = new FakeCppCliCompilerProcess(new CppCliCompilerInvocation(false, [], [diagnostic]));
            var service = new CppCliBuildService(
                compiler,
                CppCliTestSettings.CreateSettings(root),
                CppCliTestSettings.LoadManifest());

            var execution = await service.BuildAsync(
                CppCliTestSettings.CreateRequest(BuildTarget.Artifact, "int main() { return missing; }"),
                TestContext.Current.CancellationToken);

            var result = Assert.IsType<BuildResult>(execution.Result);
            Assert.Equal(BuildOutcome.CompilationFailed, result.Outcome);
            Assert.Null(execution.Artifact);
            Assert.Equal("C2065", Assert.Single(result.Diagnostics).Code);
        }
        finally
        {
            CppCliTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task TraversalIsRejectedBeforeCompilerInvocation()
    {
        var root = CppCliTestSettings.CreateRoot();
        try
        {
            var compiler = new FakeCppCliCompilerProcess(new CppCliCompilerInvocation(
                true,
                CppCliTestSettings.CreateMixedModePe(),
                []));
            var service = new CppCliBuildService(
                compiler,
                CppCliTestSettings.CreateSettings(root),
                CppCliTestSettings.LoadManifest());
            var request = CppCliTestSettings.CreateRequest(BuildTarget.CompileCheck);
            request = request with
            {
                Workspace = request.Workspace with
                {
                    Files = [new WorkspaceFile("../Program.cpp", 1, request.Workspace.Files[0].Text)],
                    ActiveFile = "../Program.cpp",
                    SourceOrder = ["../Program.cpp"]
                }
            };

            var exception = await Assert.ThrowsAsync<LanguageWorkerRequestException>(() =>
                service.BuildAsync(request, TestContext.Current.CancellationToken));

            Assert.Equal("invalid-workspace", exception.Code);
            Assert.Equal(0, compiler.CallCount);
        }
        finally
        {
            CppCliTestSettings.DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData("#include \"/etc/passwd\"\nint main() { return 0; }")]
    [InlineData("#include \"../secret.h\"\nint main() { return 0; }")]
    [InlineData("#define FILE \"/etc/passwd\"\n#include FILE\nint main() { return 0; }")]
    [InlineData("#using \"../Secret.dll\"\nint main() { return 0; }")]
    [InlineData("#import \"secret.tlb\"\nint main() { return 0; }")]
    [InlineData("#embed \"secret.bin\"\nint main() { return 0; }")]
    [InlineData("#pragma comment(linker, \"/manifestinput:/etc/passwd\")\nint main() { return 0; }")]
    [InlineData("%:include \"/etc/passwd\"\nint main() { return 0; }")]
    public async Task CompilerFileAccessDirectivesAreRejectedBeforeInvocation(string source)
    {
        var root = CppCliTestSettings.CreateRoot();
        try
        {
            var compiler = new FakeCppCliCompilerProcess(new CppCliCompilerInvocation(
                true,
                CppCliTestSettings.CreateMixedModePe(),
                []));
            var service = new CppCliBuildService(
                compiler,
                CppCliTestSettings.CreateSettings(root),
                CppCliTestSettings.LoadManifest());

            var exception = await Assert.ThrowsAsync<LanguageWorkerRequestException>(() =>
                service.BuildAsync(
                    CppCliTestSettings.CreateRequest(BuildTarget.CompileCheck, source),
                    TestContext.Current.CancellationToken));

            Assert.Equal("unsafe-source-directive", exception.Code);
            Assert.Equal(0, compiler.CallCount);
        }
        finally
        {
            CppCliTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task SafeCompilerProvidedHeadersAndAssembliesAreAccepted()
    {
        var root = CppCliTestSettings.CreateRoot();
        try
        {
            var compiler = new FakeCppCliCompilerProcess(new CppCliCompilerInvocation(
                true,
                CppCliTestSettings.CreateMixedModePe(),
                []));
            var service = new CppCliBuildService(
                compiler,
                CppCliTestSettings.CreateSettings(root),
                CppCliTestSettings.LoadManifest());

            var execution = await service.BuildAsync(
                CppCliTestSettings.CreateRequest(
                    BuildTarget.CompileCheck,
                    "#include <vector>\n#using <System.dll>\nint main() { return 0; }"),
                TestContext.Current.CancellationToken);

            Assert.True(Assert.IsType<CompilationCheckResult>(execution.Result).CompilationSucceeded);
            Assert.Equal(1, compiler.CallCount);
        }
        finally
        {
            CppCliTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public void IlOnlyAssemblyIsRejectedAsWrongArtifactFormat()
    {
        var image = File.ReadAllBytes(typeof(CppCliBuildServiceTests).Assembly.Location);

        var exception = Assert.Throws<LanguageWorkerRequestException>(() =>
            CppCliBuildService.ValidateMixedModePe(image));

        Assert.Equal("compiler-invalid-output", exception.Code);
    }
}
