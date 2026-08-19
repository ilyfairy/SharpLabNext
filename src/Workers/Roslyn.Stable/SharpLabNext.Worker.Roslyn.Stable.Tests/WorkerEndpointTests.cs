using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpLabNext.Contracts;
using SharpLabNext.Observability;
using SharpLabNext.Worker.Roslyn;
using SharpLabNext.WorkerHost;

namespace SharpLabNext.Worker.Roslyn.Stable.Tests;

public sealed class WorkerEndpointTests : IClassFixture<RoslynStableWorkerFactory>
{
    private readonly RoslynStableWorkerFactory _factory;

    public WorkerEndpointTests(RoslynStableWorkerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SharedTokenProtectsWorkerApiButNotHealthEndpoints()
    {
        const string token = "roslyn-worker-internal-service-token-for-tests";
        await using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("InternalServiceAuth:Required", "true");
            builder.UseSetting("InternalServiceAuth:Token", token);
        });
        using var anonymous = factory.CreateClient();

        using var health = await anonymous.GetAsync(
            "/health/ready",
            TestContext.Current.CancellationToken);
        using var unauthorized = await anonymous.GetAsync(
            "/api/v1/worker/describe",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/worker/describe");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var authorized = await anonymous.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
    }

    [Fact]
    public async Task DescribeAndReadinessExposeRealCompilerAndReferenceSetHealth()
    {
        using var client = _factory.CreateClient();

        using var readyResponse = await client.GetAsync(
            "/health/ready",
            TestContext.Current.CancellationToken);
        using var describeResponse = await client.GetAsync(
            "/api/v1/worker/describe",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, readyResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, describeResponse.StatusCode);
        Assert.Same(
            SharpLabNextTelemetry.Metrics,
            _factory.Services.GetRequiredService<SharpLabNextMetrics>());
        var descriptor = await describeResponse.Content.ReadFromJsonAsync<WorkerDescriptor>(
            ContractJson.CreateSerializerOptions(),
            TestContext.Current.CancellationToken);
        Assert.NotNull(descriptor);
        Assert.Equal("roslyn-stable", descriptor.Service.Id);
        Assert.Equal("5.6.0", CSharpBuildService.GetLoadedCompilerVersion());
        Assert.Contains(descriptor.Capabilities, static capability => capability.Id == "compile-check" && capability.Available);
        Assert.Contains(descriptor.Capabilities, static capability => capability.Id == "completion" && capability.Available);
        Assert.Contains(descriptor.Capabilities, static capability => capability.Id == "explain" && capability.Available);
        Assert.Contains(descriptor.ProfileIds, static profile => profile == "roslyn-stable");
    }

    [Fact]
    public async Task ExplainEndpointReturnsRevisionedStructuredDocument()
    {
        using var client = _factory.CreateClient();
        var request = CSharpExplainServiceTests.CreateRequest(
            [new WorkspaceFile("Program.cs", 1, "class Program { static void Main() { } }")],
            revision: 91,
            selectionRevision: 15);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/explain",
            request,
            ContractJson.CreateSerializerOptions(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WorkerExplainHttpResponse>(
            ContractJson.CreateSerializerOptions(),
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal(request.RequestId, body.RequestId);
        Assert.Equal(91, body.Result.Document.WorkspaceRevision);
        Assert.Contains(
            Assert.Single(body.Result.Document.Files).Nodes,
            static node => node.Kind == "MethodDeclaration");
    }

    [Fact]
    public async Task BuildEndpointAcceptsContractRequestAndReturnsTypedCompileCheckResult()
    {
        using var client = _factory.CreateClient();
        var options = new BuildOptions(
            BuildConfiguration.Release,
            Optimize: true,
            BuildOutputKind.Console,
            AllowUnsafe: false,
            EmitPortablePdb: true,
            NullableContextMode.Enable,
            LanguageVersion: "14.0");
        var workspace = new WorkspaceSnapshot(
            ContractSchemaVersions.WorkspaceSnapshot,
            Revision: 88,
            SelectionRevision: 12,
            LanguageId: "csharp",
            Files: [new WorkspaceFile("Program.cs", 4, "System.Console.WriteLine(42);")],
            ActiveFile: "Program.cs",
            SourceOrder: ["Program.cs"],
            ReferenceSetId: "net10-ref",
            BuildOptions: options);
        var request = new BuildRequest(
            "http-request",
            "http-idempotency",
            "http-pipeline",
            "roslyn-stable",
            "net10-ref",
            workspace,
            DateTimeOffset.UtcNow.AddMinutes(1),
            options,
            BuildTarget.CompileCheck);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/build",
            request,
            ContractJson.CreateSerializerOptions(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var buildResponse = await response.Content.ReadFromJsonAsync<WorkerBuildHttpResponse>(
            ContractJson.CreateSerializerOptions(),
            TestContext.Current.CancellationToken);
        Assert.NotNull(buildResponse);
        var result = Assert.IsType<CompilationCheckResult>(buildResponse.Result);
        Assert.True(result.CompilationSucceeded);
        Assert.Equal(88, result.WorkspaceRevision);
        Assert.Equal(12, result.SelectionRevision);
        Assert.Null(buildResponse.DevelopmentArtifact);
    }

    [Fact]
    public async Task BuildEndpointDispatchesVisualBasicAndReturnsVisualBasicIdentity()
    {
        using var client = _factory.CreateClient();
        var request = VisualBasicBuildServiceTests.CreateRequest(
            BuildTarget.CompileCheck,
            [new WorkspaceFile(
                "Program.vb",
                3,
                "Imports System\nModule Program\n    Sub Main()\n        Console.WriteLine(42)\n    End Sub\nEnd Module")],
            revision: 89,
            selectionRevision: 14);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/build",
            request,
            ContractJson.CreateSerializerOptions(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var buildResponse = await response.Content.ReadFromJsonAsync<WorkerBuildHttpResponse>(
            ContractJson.CreateSerializerOptions(),
            TestContext.Current.CancellationToken);
        Assert.NotNull(buildResponse);
        var result = Assert.IsType<CompilationCheckResult>(buildResponse.Result);
        Assert.True(result.CompilationSucceeded);
        Assert.Equal("visual-basic", result.Identity.LanguageId);
        Assert.Equal(89, result.WorkspaceRevision);
        Assert.Equal(14, result.SelectionRevision);
    }

    [Fact]
    public async Task DevelopmentBuildEndpointReturnsBoundedPePdbEnvelopeAndManifest()
    {
        using var client = _factory.CreateClient();
        var options = new BuildOptions(
            BuildConfiguration.Debug,
            Optimize: false,
            BuildOutputKind.Library,
            AllowUnsafe: false,
            EmitPortablePdb: true,
            NullableContextMode.Enable,
            LanguageVersion: "14.0");
        var workspace = new WorkspaceSnapshot(
            ContractSchemaVersions.WorkspaceSnapshot,
            Revision: 99,
            SelectionRevision: 13,
            LanguageId: "csharp",
            Files: [new WorkspaceFile("Library.cs", 1, "public static class Library { public static int Value => 42; }")],
            ActiveFile: "Library.cs",
            SourceOrder: ["Library.cs"],
            ReferenceSetId: "net10-ref",
            BuildOptions: options);
        var request = new BuildRequest(
            "http-artifact-request",
            "http-artifact-idempotency",
            "http-artifact-pipeline",
            "roslyn-stable",
            "net10-ref",
            workspace,
            DateTimeOffset.UtcNow.AddMinutes(1),
            options,
            BuildTarget.Artifact);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/build",
            request,
            ContractJson.CreateSerializerOptions(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var buildResponse = await response.Content.ReadFromJsonAsync<WorkerBuildHttpResponse>(
            ContractJson.CreateSerializerOptions(),
            TestContext.Current.CancellationToken);
        Assert.NotNull(buildResponse);
        var result = Assert.IsType<BuildResult>(buildResponse.Result);
        Assert.Equal(BuildOutcome.Succeeded, result.Outcome);
        Assert.NotNull(buildResponse.DevelopmentArtifact);
        Assert.NotEmpty(Convert.FromBase64String(buildResponse.DevelopmentArtifact.PeImageBase64));
        Assert.NotEmpty(Convert.FromBase64String(buildResponse.DevelopmentArtifact.PortablePdbBase64!));
        Assert.Equal(result.ArtifactRef, buildResponse.DevelopmentArtifact.Manifest.ArtifactId);
    }

    [Fact]
    public async Task CompilerChildCrashDoesNotTakeDownLanguageSessions()
    {
        await using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RoslynWorker:BuildProcess:Enabled", "true");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICompilerProcessRunner>();
                services.AddSingleton<ICompilerProcessRunner>(new FailingCompilerProcessRunner(
                    new CompilerProcessCrashedException(134, "Simulated compiler crash.")));
            });
        });
        using var client = factory.CreateClient();
        var request = CreateCompileCheckRequest();

        using var failed = await client.PostAsJsonAsync(
            "/api/v1/build",
            request,
            ContractJson.CreateSerializerOptions(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        using var problem = JsonDocument.Parse(
            await failed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            "compiler-process-unavailable",
            problem.RootElement.GetProperty("Code").GetString());

        using var opened = await client.PostAsJsonAsync(
            "/api/v1/language-sessions",
            LanguageSessionTests.CreateOpenRequest("after-crash", "System.Console.WriteLine(42);"),
            ContractJson.CreateSerializerOptions(),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
    }

    private static BuildRequest CreateCompileCheckRequest()
    {
        var options = new BuildOptions(
            BuildConfiguration.Release,
            Optimize: true,
            BuildOutputKind.Console,
            AllowUnsafe: false,
            EmitPortablePdb: true,
            NullableContextMode.Enable,
            LanguageVersion: "14.0");
        return new BuildRequest(
            "compiler-crash-request",
            "compiler-crash-key",
            "compiler-crash-pipeline",
            "roslyn-stable",
            "net10-ref",
            new WorkspaceSnapshot(
                ContractSchemaVersions.WorkspaceSnapshot,
                1,
                1,
                "csharp",
                [new WorkspaceFile("Program.cs", 1, "System.Console.WriteLine(42);")],
                "Program.cs",
                ["Program.cs"],
                "net10-ref",
                options),
            DateTimeOffset.UtcNow.AddMinutes(1),
            options,
            BuildTarget.CompileCheck);
    }

    private sealed class FailingCompilerProcessRunner(CompilerProcessException exception)
        : ICompilerProcessRunner
    {
        public Task<TResponse> RunAsync<TRequest, TResponse>(
            string childArgument,
            TRequest request,
            TimeSpan timeout,
            CancellationToken cancellationToken)
            where TRequest : class
            where TResponse : class => Task.FromException<TResponse>(exception);
    }
}

public sealed class RoslynStableWorkerFactory : WebApplicationFactory<global::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReferenceSets:net10-ref:Path"] = CSharpBuildServiceTests.GetNet10ReferencePathForHost()
            });
        });
    }
}
