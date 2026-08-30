using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpLabNext.Contracts;
using SharpLabNext.Worker.Roslyn;

namespace SharpLabNext.Worker.Roslyn.Main.Tests;

public sealed class RoslynMainWorkerTests
{
    [Fact]
    public async Task DevelopmentHostExposesDistinctMainIdentityAndHealthyLspCapabilities()
    {
        await using var factory = new RoslynMainWorkerFactory("Development");
        using var client = factory.CreateClient();

        using var readyResponse = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        using var describeResponse = await client.GetAsync("/api/v1/worker/describe", TestContext.Current.CancellationToken);

        var readyBody = await readyResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(readyResponse.IsSuccessStatusCode, readyBody);
        Assert.Equal(HttpStatusCode.OK, describeResponse.StatusCode);
        var descriptor = await describeResponse.Content.ReadFromJsonAsync<WorkerDescriptor>(ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        Assert.NotNull(descriptor);
        Assert.Equal("roslyn-main", descriptor.Service.Id);
        Assert.StartsWith("roslyn-main-", descriptor.InstanceId, StringComparison.Ordinal);
        Assert.Equal("5.10.0", CSharpBuildService.GetLoadedCompilerVersion());
        Assert.Equal(RoslynMainTestSettings.LocalValidationCommit, CSharpBuildService.GetLoadedCompilerCommit());
        Assert.Contains(descriptor.Capabilities, static capability => capability.Id == "completion" && capability.Available);
        Assert.Contains(descriptor.Capabilities, static capability => capability.Id == "code-actions" && capability.Available);
    }

    [Theory]
    [InlineData("csharp", "net10-ref", "Program.cs", "System.Console.WriteLine(42);")]
    [InlineData("csharp", "net11-preview-ref", "Program.cs", "System.Console.WriteLine(42);")]
    [InlineData("visual-basic", "net10-ref", "Program.vb", "Imports System\nModule Program\n Sub Main()\n  Console.WriteLine(42)\n End Sub\nEnd Module")]
    [InlineData("visual-basic", "net11-preview-ref", "Program.vb", "Imports System\nModule Program\n Sub Main()\n  Console.WriteLine(42)\n End Sub\nEnd Module")]
    public async Task CompileCheckSupportsCSharpAndVisualBasicAcrossNet10AndNet11(string languageId, string referenceSetId, string fileName, string source)
    {
        await using var factory = new RoslynMainWorkerFactory("Development");
        using var client = factory.CreateClient();
        var request = CreateBuildRequest(languageId, referenceSetId, fileName, source);

        using var response = await client.PostAsJsonAsync("/api/v1/build", request, ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkerBuildHttpResponse>(ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        var result = Assert.IsType<CompilationCheckResult>(body.Result);
        Assert.True(result.CompilationSucceeded);
        Assert.Equal("roslyn-main", result.Identity.ToolchainId);
        Assert.Equal("5.10.0", result.Identity.CompilerVersion);
        Assert.Equal(RoslynMainTestSettings.LocalValidationCommit, result.Identity.CompilerCommit);
        Assert.Equal(referenceSetId, result.Identity.ReferenceSetId);
    }

    [Fact]
    public async Task CompileCheckDefaultsOmittedCSharpLanguageVersionToPreviewForUnions()
    {
        await using var factory = new RoslynMainWorkerFactory("Development");
        using var client = factory.CreateClient();
        var request = CreateBuildRequest(
            "csharp",
            "net11-preview-ref",
            "Program.cs",
            """
            using System;

            Console.WriteLine("ok");

            public union Pet(Cat, Dog, Bird);
            public sealed record Cat(string Name);
            public sealed record Dog(string Name);
            public sealed record Bird(string Name);
            """);
        var options = request.Workspace.BuildOptions with { LanguageVersion = null };
        request = request with { Options = null, Workspace = request.Workspace with { BuildOptions = options } };

        using var response = await client.PostAsJsonAsync("/api/v1/build", request, ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, responseBody);
        var body = await response.Content.ReadFromJsonAsync<WorkerBuildHttpResponse>(ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        var result = Assert.IsType<CompilationCheckResult>(body!.Result);
        Assert.True(result.CompilationSucceeded);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task CompileCheckAllowsUnsafeCSharp()
    {
        await using var factory = new RoslynMainWorkerFactory("Development");
        using var client = factory.CreateClient();
        var request = CreateBuildRequest("csharp", "net10-ref", "Program.cs", "unsafe class Program { static void Main() { int value = 42; int* pointer = &value; System.Console.WriteLine(*pointer); } }");
        var options = request.EffectiveOptions with { AllowUnsafe = true };
        request = request with { Options = options, Workspace = request.Workspace with { BuildOptions = options } };

        using var response = await client.PostAsJsonAsync("/api/v1/build", request, ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, responseBody);
        var body = await response.Content.ReadFromJsonAsync<WorkerBuildHttpResponse>(ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        var result = Assert.IsType<CompilationCheckResult>(body!.Result);
        Assert.True(result.CompilationSucceeded);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("csharp", "Program.cs", "System.Console.WriteLine(42);")]
    [InlineData("visual-basic", "Program.vb", "Imports System\nModule Program\n Sub Main()\n  Console.WriteLine(42)\n End Sub\nEnd Module")]
    public async Task ArtifactBuildUsesMainCompilerForCSharpAndVisualBasic(string languageId, string fileName, string source)
    {
        await using var factory = new RoslynMainWorkerFactory("Development");
        using var client = factory.CreateClient();
        var request = CreateBuildRequest(languageId, "net11-preview-ref", fileName, source) with { Target = BuildTarget.Artifact };

        using var response = await client.PostAsJsonAsync("/api/v1/build", request, ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkerBuildHttpResponse>(ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        var result = Assert.IsType<BuildResult>(body.Result);
        Assert.Equal(BuildOutcome.Succeeded, result.Outcome);
        Assert.Equal("roslyn-main", result.Identity.ToolchainId);
        Assert.Equal(RoslynMainTestSettings.LocalValidationCommit, result.Identity.CompilerCommit);
        Assert.NotNull(body.DevelopmentArtifact);
        var peImage = Convert.FromBase64String(body.DevelopmentArtifact.PeImageBase64);
        Assert.Equal([0x4d, 0x5a], peImage[..2]);
        Assert.NotEmpty(Convert.FromBase64String(body.DevelopmentArtifact.PortablePdbBase64!));
    }

    [Theory]
    [InlineData("csharp", "Program.cs", "namespace Demo { public class Sample { public int Value => 42; } }")]
    [InlineData("visual-basic", "Program.vb", "Public Class Sample\n Public ReadOnly Property Value As Integer = 42\nEnd Class")]
    public async Task AstUsesMainParserForCSharpAndVisualBasic(string languageId, string fileName, string source)
    {
        await using var factory = new RoslynMainWorkerFactory("Development");
        using var client = factory.CreateClient();
        var request = CreateBuildRequest(languageId, "net11-preview-ref", fileName, source) with { Target = BuildTarget.Ast };

        using var response = await client.PostAsJsonAsync("/api/v1/build", request, ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkerBuildHttpResponse>(ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        var result = Assert.IsType<AstResult>(body.Result);
        Assert.Equal(languageId, result.Document.LanguageId);
        Assert.Equal("roslyn-main", result.Document.ToolchainId);
        Assert.Equal(fileName, Assert.Single(result.Document.Root.Children).Properties["path"]);
        Assert.NotEmpty(result.Document.Root.Children[0].Children);
    }

    [Fact]
    public async Task LanguageSessionUsesMainCompilerIdentityAndLifecycleEndpoint()
    {
        await using var factory = new RoslynMainWorkerFactory("Development");
        using var client = factory.CreateClient();
        var build = CreateBuildRequest("csharp", "net11-preview-ref", "Program.cs", "System.Console.WriteLine(42);");
        var request = new OpenLanguageSessionRequest("main-lsp-request", build.PipelineResolutionId, "csharp", "roslyn-main", "net11-preview-ref", build.Workspace);

        using var response = await client.PostAsJsonAsync("/api/v1/language-sessions", request, ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await response.Content.ReadFromJsonAsync<LanguageSession>(ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        Assert.NotNull(session);
        Assert.Equal("roslyn-main", session.ToolchainId);
        Assert.Equal("roslyn-main/5.10.0", session.CompilerBuildIdentity);

        using var nonWebSocket = await client.GetAsync($"/api/v1/language-sessions/{session.SessionId}/lsp", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UpgradeRequired, nonWebSocket.StatusCode);

        using var delete = await client.DeleteAsync($"/api/v1/language-sessions/{session.SessionId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task ProductionHostEnforcesExactLockedCompilerCommit()
    {
        await using var factory = new RoslynMainWorkerFactory("Production");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", RoslynMainTestSettings.InternalServiceToken);
        var request = CreateBuildRequest("csharp", "net10-ref", "Program.cs", "System.Console.WriteLine(42);");

        using var readyResponse = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        using var buildResponse = await client.PostAsJsonAsync("/api/v1/build", request, ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);

        if (RoslynMainTestSettings.IsSourceBuild)
        {
            var readyBody = await readyResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            var buildBody = await buildResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(readyResponse.IsSuccessStatusCode, readyBody);
            Assert.True(buildResponse.IsSuccessStatusCode, buildBody);
            var body = await buildResponse.Content.ReadFromJsonAsync<WorkerBuildHttpResponse>(ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
            var result = Assert.IsType<CompilationCheckResult>(body!.Result);
            Assert.Equal(RoslynMainTestSettings.LockedCommit, result.Identity.CompilerCommit);
        }
        else
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, readyResponse.StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, buildResponse.StatusCode);
            var problem = await buildResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(problem.Contains(RoslynMainTestSettings.LockedCommit, StringComparison.OrdinalIgnoreCase), problem);
            Assert.Contains("roslyn-main", problem, StringComparison.Ordinal);
        }
    }

    private static BuildRequest CreateBuildRequest(string languageId, string referenceSetId, string fileName, string source)
    {
        var options = new BuildOptions(BuildConfiguration.Release, Optimize: true, BuildOutputKind.Console, AllowUnsafe: false, EmitPortablePdb: true, languageId == "csharp" ? NullableContextMode.Enable : NullableContextMode.Disable, LanguageVersion: languageId == "csharp" ? "preview" : "latest");
        var workspace = new WorkspaceSnapshot(ContractSchemaVersions.WorkspaceSnapshot, Revision: 17, SelectionRevision: 9, languageId, [new WorkspaceFile(fileName, 3, source)], fileName, [fileName], referenceSetId, options);
        return new BuildRequest($"main-{Guid.NewGuid():N}", $"main-idempotency-{Guid.NewGuid():N}", "main-pipeline", "roslyn-main", referenceSetId, workspace, DateTimeOffset.UtcNow.AddMinutes(1), options, BuildTarget.CompileCheck);
    }
}

public sealed class RoslynMainWorkerFactory(string environment, int? maxCompletionItems = null) : WebApplicationFactory<global::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        if (string.Equals(environment, "Production", StringComparison.Ordinal))
        {
            builder.UseSetting("InternalServiceAuth:TokenFile", RoslynMainTestSettings.GetInternalServiceTokenFile());
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ReferenceSetProvider>();
                services.AddSingleton(new ReferenceSetProvider(
                [
                    new ReferenceSetDefinition("net10-ref", RoslynMainTestSettings.Net10ReferenceSet.Path, "net10.0", RoslynMainTestSettings.Net10ReferenceSet.Version),
                    new ReferenceSetDefinition("net11-preview-ref", RoslynMainTestSettings.Net11ReferenceSet.Path, "net11.0", RoslynMainTestSettings.Net11ReferenceSet.Version)
                ]));

                // TestServer runs under testhost, so its entry assembly cannot serve as the compiler child.
                services.RemoveAll<IRoslynBuildExecutor>();
                services.AddSingleton<IRoslynBuildExecutor, InProcessRoslynBuildExecutor>();
            });
        }
        builder.UseSetting("ReferenceSets:net10-ref:Path", RoslynMainTestSettings.Net10ReferenceSet.Path);
        builder.UseSetting("ReferenceSets:net11-preview-ref:Path", RoslynMainTestSettings.Net11ReferenceSet.Path);
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["ReferenceSets:net10-ref:Path"] = RoslynMainTestSettings.Net10ReferenceSet.Path,
                ["ReferenceSets:net10-ref:FrameworkVersion"] = RoslynMainTestSettings.Net10ReferenceSet.Version,
                ["ReferenceSets:net10-ref:Digest"] = RoslynMainTestSettings.Net10ReferenceSet.Digest,
                ["ReferenceSets:net11-preview-ref:Path"] = RoslynMainTestSettings.Net11ReferenceSet.Path,
                ["ReferenceSets:net11-preview-ref:FrameworkVersion"] = RoslynMainTestSettings.Net11ReferenceSet.Version,
                ["ReferenceSets:net11-preview-ref:Digest"] = RoslynMainTestSettings.Net11ReferenceSet.Digest
            };
            if (maxCompletionItems is not null)
            {
                settings["RoslynWorker:LspLimits:MaxCompletionItems"] =
                    maxCompletionItems.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            configuration.AddInMemoryCollection(settings);
        });
    }

    private sealed class InProcessRoslynBuildExecutor(RoslynBuildService buildService) : IRoslynBuildExecutor
    {
        public Task<WorkerBuildExecution> ExecuteAsync(BuildRequest request, CancellationToken cancellationToken) =>
            buildService.ExecuteAsync(request, cancellationToken);
    }
}
