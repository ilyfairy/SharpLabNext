using System.Reflection.PortableExecutable;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Sdk;

namespace SharpLabNext.Worker.JSharp.Tests;

public sealed class JSharpBuildServiceTests
{
    [Fact]
    public async Task ArtifactPublishesX64Clr2ManagedContract()
    {
        var root = JSharpTestSettings.CreateRoot();
        try
        {
            var compiler = new FakeJSharpCompilerProcess(new JSharpCompilerInvocation(true, JSharpTestSettings.CreateClr2ManagedPe(), []));
            var service = new JSharpBuildService(compiler, JSharpTestSettings.CreateSettings(root), JSharpTestSettings.LoadManifest());

            var execution = await service.BuildAsync(JSharpTestSettings.CreateRequest(BuildTarget.Artifact), TestContext.Current.CancellationToken);

            var result = Assert.IsType<BuildResult>(execution.Result);
            Assert.Equal(BuildOutcome.Succeeded, result.Outcome);
            var envelope = Assert.IsType<LanguageWorkerArtifactEnvelope>(execution.Artifact);
            Assert.Equal(JSharpToolchain.ArtifactFormat, envelope.ArtifactFormat);
            Assert.Equal(JSharpToolchain.TargetFramework, envelope.TargetFramework);
            Assert.Equal(JSharpToolchain.RuntimeFamily, envelope.Manifest.RuntimeRequirement.Family);
            Assert.Equal(JSharpToolchain.Architecture, envelope.Manifest.RuntimeRequirement.Architecture);
            Assert.Equal([JSharpToolchain.RuntimeFeatureTag], envelope.Manifest.RuntimeRequirement.RequiredRuntimeFeatureTags);
            Assert.Equal(new FrameworkRequirement(JSharpToolchain.FrameworkName, JSharpToolchain.FrameworkVersion), Assert.Single(envelope.Manifest.RuntimeRequirement.Frameworks));
            Assert.Equal("Program::main", envelope.Manifest.EntryPoint);
            Assert.Equal(JSharpToolchain.OutputFileName, envelope.Manifest.EntryAssembly);
            Assert.Equal("primary-assembly", Assert.Single(envelope.Files).Role);
            Assert.Equal("false", envelope.Manifest.Metadata!["deterministic"]);
            Assert.Equal("false", envelope.Manifest.Metadata!["portablePdb"]);
            Assert.Equal("v2.0.50727", envelope.Manifest.Metadata!["clrMetadataVersion"]);
            ArtifactIdentity.Validate(envelope.Manifest);
            Assert.Equal(1, compiler.CallCount);
        }
        finally
        {
            JSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CompileCheckUsesRealCompilerContractWithoutPublishingArtifact()
    {
        var root = JSharpTestSettings.CreateRoot();
        try
        {
            var compiler = new FakeJSharpCompilerProcess(new JSharpCompilerInvocation(true, JSharpTestSettings.CreateClr2ManagedPe(), []));
            var service = new JSharpBuildService(compiler, JSharpTestSettings.CreateSettings(root), JSharpTestSettings.LoadManifest());

            var execution = await service.BuildAsync(JSharpTestSettings.CreateRequest(BuildTarget.CompileCheck), TestContext.Current.CancellationToken);

            Assert.True(Assert.IsType<CompilationCheckResult>(execution.Result).CompilationSucceeded);
            Assert.Null(execution.Artifact);
            Assert.Equal(1, compiler.CallCount);
        }
        finally
        {
            JSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CompilerDiagnosticsProduceCompilationFailure()
    {
        var root = JSharpTestSettings.CreateRoot();
        try
        {
            var diagnostic = new Diagnostic("vjc", "VJS1234", DiagnosticSeverity.Error, "synthetic failure", "Program.jsl", new TextRange(1, 2, 1, 3), [], [], 7, 3);
            var compiler = new FakeJSharpCompilerProcess(new JSharpCompilerInvocation(false, [], [diagnostic]));
            var service = new JSharpBuildService(compiler, JSharpTestSettings.CreateSettings(root), JSharpTestSettings.LoadManifest());

            var execution = await service.BuildAsync(JSharpTestSettings.CreateRequest(BuildTarget.Artifact, "DIAGNOSTIC"), TestContext.Current.CancellationToken);

            var result = Assert.IsType<BuildResult>(execution.Result);
            Assert.Equal(BuildOutcome.CompilationFailed, result.Outcome);
            Assert.Null(execution.Artifact);
            Assert.Equal("VJS1234", Assert.Single(result.Diagnostics).Code);
        }
        finally
        {
            JSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task BuildDeadlineCancelsCompilerInvocation()
    {
        var root = JSharpTestSettings.CreateRoot();
        try
        {
            var compiler = new FakeJSharpCompilerProcess(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            });
            var service = new JSharpBuildService(compiler, JSharpTestSettings.CreateSettings(root), JSharpTestSettings.LoadManifest());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.BuildAsync(JSharpTestSettings.CreateRequest(BuildTarget.Artifact, deadlineUtc: DateTimeOffset.UtcNow.AddMilliseconds(100)), CancellationToken.None));
        }
        finally
        {
            JSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task TraversalIsRejectedBeforeCompilerInvocation()
    {
        var root = JSharpTestSettings.CreateRoot();
        try
        {
            var compiler = new FakeJSharpCompilerProcess(new JSharpCompilerInvocation(true, JSharpTestSettings.CreateClr2ManagedPe(), []));
            var service = new JSharpBuildService(compiler, JSharpTestSettings.CreateSettings(root), JSharpTestSettings.LoadManifest());
            var request = JSharpTestSettings.CreateRequest(BuildTarget.CompileCheck);
            request = request with { Workspace = request.Workspace with { Files = [new WorkspaceFile("../Program.jsl", 1, request.Workspace.Files[0].Text)], ActiveFile = "../Program.jsl", SourceOrder = ["../Program.jsl"] } };

            var exception = await Assert.ThrowsAsync<LanguageWorkerRequestException>(() => service.BuildAsync(request, TestContext.Current.CancellationToken));

            Assert.Equal("invalid-workspace", exception.Code);
            Assert.Equal(0, compiler.CallCount);
        }
        finally
        {
            JSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public void AnyCpuAndModernClrAssembliesAreRejected()
    {
        var anyCpu = Assert.Throws<LanguageWorkerRequestException>(() => JSharpBuildService.InspectManagedClr2Pe(JSharpTestSettings.CreateClr2ManagedPe(Machine.I386)));
        var modernClr = Assert.Throws<LanguageWorkerRequestException>(() => JSharpBuildService.InspectManagedClr2Pe(File.ReadAllBytes(typeof(JSharpBuildServiceTests).Assembly.Location)));

        Assert.Equal("compiler-invalid-output", anyCpu.Code);
        Assert.Equal("compiler-invalid-output", modernClr.Code);
    }

    [Fact]
    public void Preferred32BitClrFlagIsRejected()
    {
        var exception = Assert.Throws<LanguageWorkerRequestException>(() => JSharpBuildService.InspectManagedClr2Pe(JSharpTestSettings.CreateClr2ManagedPe(flags: CorFlags.ILOnly | CorFlags.Prefers32Bit)));

        Assert.Equal("compiler-invalid-output", exception.Code);
    }
}
