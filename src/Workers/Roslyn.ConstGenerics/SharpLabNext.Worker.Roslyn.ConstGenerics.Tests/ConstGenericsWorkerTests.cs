using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Configuration;
using SharpLabNext.Contracts;
using SharpLabNext.Worker.Roslyn;

namespace SharpLabNext.Worker.Roslyn.ConstGenerics.Tests;

public sealed class ConstGenericsWorkerTests
{
    [Fact]
    public void ProductionConfigurationDeclaresTheAtomicCSharpProfile()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "const-generics-appsettings.json"));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var settings = RoslynWorkerSettings.FromConfiguration(configuration);
        var identity = settings.Identity;

        Assert.Equal(["csharp"], identity.SupportedLanguageIds);
        Assert.Equal("coreclr-const-generics", identity.ArtifactRuntimeFamily);
        Assert.Equal(["runtime.const-generics.v1"], identity.RequiredRuntimeFeatureTags);
        Assert.Equal(["metadata.const-generics.v1"], identity.MetadataFeatureTags);
        Assert.Equal("const-generics-bcaed316", identity.CompatibilityGroup);
        var referenceSet = Assert.Single(settings.ReferenceSets);
        Assert.Equal("coreclr-const-generics", referenceSet.RuntimeFamily);
        Assert.Equal(["runtime.const-generics.v1"], referenceSet.RequiredRuntimeFeatureTags);
        Assert.Equal(["metadata.const-generics.v1"], referenceSet.MetadataFeatureTags);
        Assert.Equal("const-generics-bcaed316", referenceSet.CompatibilityGroup);
    }

    [Fact]
    public async Task ArtifactBuildPublishesAtomicRuntimeAndMetadataRequirements()
    {
        var execution = await CreateCSharpService().ExecuteAsync(CreateRequest(BuildTarget.Artifact, FeatureSource), TestContext.Current.CancellationToken);

        var result = Assert.IsType<BuildResult>(execution.Result);
        Assert.Equal(BuildOutcome.Succeeded, result.Outcome);
        var artifact = Assert.IsType<CompiledArtifact>(execution.Artifact);
        Assert.Equal("coreclr-const-generics", artifact.Manifest.RuntimeRequirement.Family);
        Assert.Equal(["runtime.const-generics.v1"], artifact.Manifest.RuntimeRequirement.RequiredRuntimeFeatureTags);
        Assert.Equal(["metadata.const-generics.v1"], artifact.Manifest.MetadataFeatureTags);
        Assert.Equal("const-generics-bcaed316", artifact.Manifest.Metadata?["compatibilityGroup"]);
        Assert.Equal("const-generics-ref", artifact.Manifest.ReferenceSetId);
        Assert.Equal("net9.0", artifact.Manifest.TargetFramework);
        var framework = Assert.Single(artifact.Manifest.RuntimeRequirement.Frameworks);
        Assert.Equal("Microsoft.NETCore.App", framework.Name);
        Assert.Equal(ConstGenericsTestSettings.ReferenceVersion, framework.MinimumVersion);
        Assert.Equal("roslyn-const-generics", artifact.Manifest.Producer.ToolchainId);
        Assert.Equal([0x4d, 0x5a], artifact.PeImage[..2]);
        Assert.NotEmpty(artifact.PortablePdb);
        if (ConstGenericsTestSettings.IsSourceBuild)
            Assert.Equal(ConstGenericsTestSettings.ExpectedCompilerCommit, result.Identity.CompilerCommit);
    }

    [Fact]
    public async Task CompileCheckAndAstUseTheForkParserContract()
    {
        var service = CreateCSharpService();
        var check = await service.ExecuteAsync(CreateRequest(BuildTarget.CompileCheck, FeatureSource), TestContext.Current.CancellationToken);
        var checkResult = Assert.IsType<CompilationCheckResult>(check.Result);
        Assert.True(checkResult.CompilationSucceeded);
        Assert.DoesNotContain(checkResult.Diagnostics, static diagnostic => diagnostic.Severity == SharpLabNext.Contracts.DiagnosticSeverity.Error);

        var ast = await service.ExecuteAsync(CreateRequest(BuildTarget.Ast, FeatureSource), TestContext.Current.CancellationToken);
        var astResult = Assert.IsType<AstResult>(ast.Result);
        Assert.Equal("roslyn-const-generics", astResult.Document.ToolchainId);
        if (ConstGenericsTestSettings.IsSourceBuild)
            Assert.Contains(Flatten(astResult.Document.Root), static node => node.Kind.Contains("LiteralTypeArgument", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompileCheckAllowsUnsafeCSharpWithTheForkCompiler()
    {
        var request = CreateRequest(BuildTarget.CompileCheck, "unsafe class Program { static void Main() { int value = 42; int* pointer = &value; System.Console.WriteLine(*pointer); } }");
        var options = request.EffectiveOptions with { AllowUnsafe = true };
        request = request with { Options = options, Workspace = request.Workspace with { BuildOptions = options } };

        var execution = await CreateCSharpService().ExecuteAsync(request, TestContext.Current.CancellationToken);

        var result = Assert.IsType<CompilationCheckResult>(execution.Result);
        Assert.True(result.CompilationSucceeded);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Severity == SharpLabNext.Contracts.DiagnosticSeverity.Error);
    }

    [Fact]
    public void SourceBuildExposesTheLockedForkSurfaceAndIdentity()
    {
        Assert.Equal(ConstGenericsTestSettings.CompilerVersion, CSharpBuildService.GetLoadedCompilerVersion());
        if (!ConstGenericsTestSettings.IsSourceBuild)
            return;

        Assert.Equal(ConstGenericsTestSettings.ExpectedCompilerCommit, CSharpBuildService.GetLoadedCompilerCommit());
        Assert.NotNull(typeof(CSharpCompilation).Assembly.GetType("Microsoft.CodeAnalysis.CSharp.Syntax.LiteralTypeArgumentSyntax"));
        Assert.NotNull(typeof(ITypeParameterSymbol).GetProperty("Type"));
    }

    [Fact]
    public async Task LanguageSessionProvidesDiagnosticsAndCompletionWithTheSameCompiler()
    {
        await using var manager = new RoslynLanguageSessionManager(CreateReferenceSets(), ConstGenericsTestSettings.CreateIdentity(), CompilationLimits.Default, LspLimits.Default);
        const string source = "using System; class Demo { void Run() { Console. } }";
        var request = CreateOpenRequest(source);
        var contract = await manager.OpenAsync(request, TestContext.Current.CancellationToken);
        var session = manager.GetRequired(contract.SessionId);
         await session.DidOpenAsync(new LspDidOpenTextDocumentParams(new LspTextDocumentItem("file:///Program.cs", "csharp", 2, source)), TestContext.Current.CancellationToken);

         var completion = await session.GetCompletionsAsync(new LspCompletionParams(new LspTextDocumentIdentifier("file:///Program.cs"), new LspPosition(0, 48), new LspCompletionContext(1, null)), TestContext.Current.CancellationToken);
         var diagnostics = await session.GetDiagnosticsAsync("file:///Program.cs", 2, TestContext.Current.CancellationToken);

        Assert.Equal($"roslyn-const-generics/{ConstGenericsTestSettings.CompilerVersion}", contract.CompilerBuildIdentity);
        Assert.Contains(completion.Items, static item => item.Label == "WriteLine");
        Assert.NotNull(diagnostics);
    }

    [Fact]
    public async Task WorkerRejectsVisualBasicBeforeCompilation()
    {
        var references = CreateReferenceSets();
        var identity = ConstGenericsTestSettings.CreateIdentity();
        var router = new RoslynBuildService(new CSharpBuildService(references, identity, CompilationLimits.Default, AstLimits.Default), new VisualBasicBuildService(references, identity, CompilationLimits.Default, AstLimits.Default), identity);
        var options = CreateOptions() with { NullableContext = NullableContextMode.Disable };
        var workspace = new WorkspaceSnapshot(ContractSchemaVersions.WorkspaceSnapshot, 1, 1, "visual-basic", [new WorkspaceFile("Program.vb", 1, "Module Program\nEnd Module")], "Program.vb", ["Program.vb"], "const-generics-ref", options);
        var request = new BuildRequest("const-vb", "const-vb-idempotency", "const-vb-pipeline", "roslyn-const-generics", "const-generics-ref", workspace, DateTimeOffset.UtcNow.AddMinutes(1), options, BuildTarget.CompileCheck);

        var exception = await Assert.ThrowsAsync<BuildRequestValidationException>(() => router.ExecuteAsync(request, TestContext.Current.CancellationToken));
        Assert.Contains("does not support", exception.Message, StringComparison.Ordinal);
    }

    private static string FeatureSource => ConstGenericsTestSettings.IsSourceBuild
        ? """
          using System;
          public static class FixedValue<int Value>
          {
              public static int GetValue() => Value;
          }
          public static class Program
          {
              public static void Main() => Console.WriteLine(FixedValue<42>.GetValue());
          }
          """
        : "using System; public static class Program { public static void Main() => Console.WriteLine(42); }";

    private static CSharpBuildService CreateCSharpService()
    {
        var identity = ConstGenericsTestSettings.CreateIdentity();
        return new CSharpBuildService(CreateReferenceSets(), identity, CompilationLimits.Default, AstLimits.Default);
    }

    private static ReferenceSetProvider CreateReferenceSets() => new(
        [new ReferenceSetDefinition("const-generics-ref", ConstGenericsTestSettings.GetReferencePath(), "net9.0", ConstGenericsTestSettings.ReferenceVersion)
        {
            RuntimeFamily = "coreclr-const-generics",
            RequiredRuntimeFeatureTags = ["runtime.const-generics.v1"],
            MetadataFeatureTags = ["metadata.const-generics.v1"],
            CompatibilityGroup = "const-generics-bcaed316"
        }]);

    private static BuildRequest CreateRequest(BuildTarget target, string source)
    {
        var options = CreateOptions();
        var workspace = new WorkspaceSnapshot(ContractSchemaVersions.WorkspaceSnapshot, 7, 3, "csharp", [new WorkspaceFile("Program.cs", 1, source)], "Program.cs", ["Program.cs"], "const-generics-ref", options);
        return new BuildRequest($"const-{target}", $"const-{target}-idempotency", "const-pipeline", "roslyn-const-generics", "const-generics-ref", workspace, DateTimeOffset.UtcNow.AddMinutes(1), options, target);
    }

    private static OpenLanguageSessionRequest CreateOpenRequest(string source)
    {
        var build = CreateRequest(BuildTarget.CompileCheck, source);
        return new OpenLanguageSessionRequest("const-lsp", build.PipelineResolutionId, "csharp", "roslyn-const-generics", "const-generics-ref", build.Workspace);
    }

    private static BuildOptions CreateOptions() => new(BuildConfiguration.Release, Optimize: true, BuildOutputKind.Console, AllowUnsafe: false, EmitPortablePdb: true, NullableContextMode.Enable, LanguageVersion: "preview");

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
