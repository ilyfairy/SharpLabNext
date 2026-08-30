using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Conformance;
using SharpLabNext.LanguageWorker.Sdk;
using SharpLabNext.Observability;
using SharpLabNext.SampleLanguage.Worker;
using SharpLabNext.Testing;

namespace SharpLabNext.SampleLanguage.Worker.Tests;

public sealed class MiniLanguageWorkerEndpointTests : IClassFixture<WebApplicationFactory<global::Program>>
{
    private static readonly JsonSerializerOptions JsonOptions = ContractJson.CreateSerializerOptions();
    private readonly WebApplicationFactory<global::Program> _factory;

    public MiniLanguageWorkerEndpointTests(WebApplicationFactory<global::Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ReferenceSets:net10-ref:Path", TestReferenceSets.Net10.Path);
            builder.UseSetting("ReferenceSets:net10-ref:FrameworkVersion", TestReferenceSets.Net10.Version);
            builder.UseSetting("ReferenceSets:net10-ref:Digest", TestReferenceSets.Net10.Digest);
            builder.UseSetting("ReferenceSets:net11-preview-ref:Path", TestReferenceSets.Net11.Path);
            builder.UseSetting("ReferenceSets:net11-preview-ref:FrameworkVersion", TestReferenceSets.Net11.Version);
            builder.UseSetting("ReferenceSets:net11-preview-ref:Digest", TestReferenceSets.Net11.Digest);
        });
    }

    [Fact]
    public async Task WorkerPassesTheReusableLanguageConformanceSuite()
    {
        using var client = _factory.CreateClient();
        Assert.Same(SharpLabNextTelemetry.Metrics, _factory.Services.GetRequiredService<SharpLabNextMetrics>());
        var manifest = await client.GetFromJsonAsync<LanguageWorkerCapabilityManifest>("/api/v1/worker/capabilities", JsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(manifest);
        var expectedIdentity = new ServiceIdentity(MiniLanguageCompiler.ToolchainId, ServiceKind.ToolchainWorker, "development", ProtocolVersion.WorkerV1, manifest.Capabilities, "ready");
        const string validSource = "print \"Hello from MiniLang\"";
        const string invalidSource = "say \"This is not MiniLang\"";
        var compileCheck = CreateBuildRequest(BuildTarget.CompileCheck, validSource);
        var artifact = CreateBuildRequest(BuildTarget.Artifact, validSource);
        var sessionWorkspace = CreateWorkspace(invalidSource);
        var openSession = new OpenLanguageSessionRequest("request-minilang-lsp", "pipeline-minilang-lsp", MiniLanguageCompiler.LanguageId, MiniLanguageCompiler.ToolchainId, sessionWorkspace.ReferenceSetId, sessionWorkspace);
        var scenario = new LanguageWorkerConformanceScenario(
            expectedIdentity,
            $"sha256:{new string('0', 64)}",
            manifest,
            compileCheck,
            artifact,
            openSession,
            "sharplabnext:///Program.mini",
            invalidSource,
            validSource,
            new LanguageWorkerCompletionPosition(0, 0),
            "print",
            "MINI1001");
        var webSocketClient = _factory.Server.CreateWebSocketClient();
        var runner = new LanguageWorkerConformanceRunner(client, (uri, cancellationToken) => webSocketClient.ConnectAsync(uri, cancellationToken));

        var report = await runner.VerifyAsync(scenario, TestContext.Current.CancellationToken);

        Assert.True(report.Succeeded);
        Assert.Equal(6, report.PassedChecks.Count);
    }

    [Fact]
    public async Task ArtifactBuildReturnsGeneratedCilTextInTheGenericEnvelope()
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/build", CreateBuildRequest(BuildTarget.Artifact, "print \"one\"\nprint \"two\""), JsonOptions, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode, body);
        var build = JsonSerializer.Deserialize<LanguageWorkerBuildHttpResponse>(body, JsonOptions);
        Assert.NotNull(build);
        var result = Assert.IsType<BuildResult>(build.Result);
        Assert.Equal(BuildOutcome.Succeeded, result.Outcome);
        var envelope = Assert.IsType<LanguageWorkerArtifactEnvelope>(build.DevelopmentArtifact);
        Assert.Equal(MiniLanguageCompiler.ArtifactFormat, envelope.ArtifactFormat);
        Assert.Null(envelope.PeImageBase64);
        Assert.Null(envelope.PortablePdbBase64);
        Assert.NotNull(envelope.FileContentsBase64);
        var cil = Encoding.UTF8.GetString(Convert.FromBase64String(envelope.FileContentsBase64[MiniLanguageCompiler.GeneratedFileName]));
        Assert.Contains(".entrypoint", cil, StringComparison.Ordinal);
        Assert.Contains("ldstr \"one\"", cil, StringComparison.Ordinal);
        Assert.Contains("ldstr \"two\"", cil, StringComparison.Ordinal);
        Assert.Contains("System.Console::WriteLine(string)", cil, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LibraryBuildOmitsEntrypointAndUnsupportedOutputKindsAreRejected()
    {
        using var client = _factory.CreateClient();
        using var libraryResponse = await client.PostAsJsonAsync("/api/v1/build", CreateBuildRequest(BuildTarget.Artifact, "print \"library\"", BuildOutputKind.Library), JsonOptions, TestContext.Current.CancellationToken);
        var libraryBody = await libraryResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(libraryResponse.IsSuccessStatusCode, libraryBody);
        var libraryBuild = JsonSerializer.Deserialize<LanguageWorkerBuildHttpResponse>(libraryBody, JsonOptions);
        Assert.NotNull(libraryBuild);
        var envelope = Assert.IsType<LanguageWorkerArtifactEnvelope>(libraryBuild.DevelopmentArtifact);
        Assert.Null(envelope.Manifest.EntryPoint);
        var cil = Encoding.UTF8.GetString(Convert.FromBase64String(envelope.FileContentsBase64![MiniLanguageCompiler.GeneratedFileName]));
        Assert.DoesNotContain(".entrypoint", cil, StringComparison.Ordinal);

        using var windowsResponse = await client.PostAsJsonAsync("/api/v1/build", CreateBuildRequest(BuildTarget.Artifact, "print \"windows\"", BuildOutputKind.WindowsApplication), JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, windowsResponse.StatusCode);

        using var autoResponse = await client.PostAsJsonAsync("/api/v1/build", CreateBuildRequest(BuildTarget.Artifact, "print \"automatic\"", BuildOutputKind.Auto), JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, autoResponse.StatusCode);
    }

    [Fact]
    public async Task EffectiveOptionsDriveBothGeneratedCodeAndManifest()
    {
        using var client = _factory.CreateClient();
        var request = CreateBuildRequest(BuildTarget.Artifact, "print \"effective\"", BuildOutputKind.Auto);
        request = request with { Options = request.Workspace.BuildOptions with { OutputKind = BuildOutputKind.Console } };

        using var response = await client.PostAsJsonAsync("/api/v1/build", request, JsonOptions, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode, body);
        var build = JsonSerializer.Deserialize<LanguageWorkerBuildHttpResponse>(body, JsonOptions);
        var envelope = Assert.IsType<LanguageWorkerArtifactEnvelope>(build?.DevelopmentArtifact);
        Assert.Equal(BuildOutputKind.Console, envelope.Manifest.OutputKind);
        var cil = Encoding.UTF8.GetString(Convert.FromBase64String(envelope.FileContentsBase64![MiniLanguageCompiler.GeneratedFileName]));
        Assert.Contains(".entrypoint", cil, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidProgramReturnsRevisionedDiagnosticsWithoutAnArtifact()
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/build", CreateBuildRequest(BuildTarget.Artifact, "write \"invalid\""), JsonOptions, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode, body);
        var build = JsonSerializer.Deserialize<LanguageWorkerBuildHttpResponse>(body, JsonOptions);
        Assert.NotNull(build);
        var result = Assert.IsType<BuildResult>(build.Result);
        Assert.Equal(BuildOutcome.CompilationFailed, result.Outcome);
        Assert.Null(result.ArtifactRef);
        Assert.Null(build.DevelopmentArtifact);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("MINI1001", diagnostic.Code);
        Assert.Equal(7, diagnostic.WorkspaceRevision);
        Assert.Equal(3, diagnostic.SelectionRevision);
        Assert.Equal(MiniLanguageCompiler.DefaultFileName, diagnostic.FilePath);
    }

    [Fact]
    public async Task CompileCheckUsesTheSameCompilerButNeverReturnsAnArtifact()
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/build", CreateBuildRequest(BuildTarget.CompileCheck, "not-a-statement"), JsonOptions, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode, body);
        var build = JsonSerializer.Deserialize<LanguageWorkerBuildHttpResponse>(body, JsonOptions);
        Assert.NotNull(build);
        var result = Assert.IsType<CompilationCheckResult>(build.Result);
        Assert.False(result.CompilationSucceeded);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "MINI1001");
        Assert.Null(build.DevelopmentArtifact);
    }

    [Fact]
    public async Task SdkRejectsRequestsThatExceedTheManifestSourceLimit()
    {
        using var client = _factory.CreateClient();
        var oversized = new string('x', 262_145);
        using var response = await client.PostAsJsonAsync("/api/v1/build", CreateBuildRequest(BuildTarget.CompileCheck, oversized), JsonOptions, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(413, (int)response.StatusCode);
        Assert.Contains("workspace-too-large", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SdkRejectsAWorkspaceForAnotherLanguageBeforeCallingTheCompiler()
    {
        using var client = _factory.CreateClient();
        var request = CreateBuildRequest(BuildTarget.CompileCheck, "print \"valid\"");
        request = request with { Workspace = request.Workspace with { LanguageId = "another-language" } };
        using var response = await client.PostAsJsonAsync("/api/v1/build", request, JsonOptions, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(400, (int)response.StatusCode);
        Assert.Contains("wrong-language", body, StringComparison.Ordinal);
    }

    [Fact]
    public void CapabilityManifestLoaderRejectsUnknownProperties()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "language-worker.json");
        var validJson = File.ReadAllText(path);
        var closingBrace = validJson.LastIndexOf('}');
        Assert.True(closingBrace >= 0);
        var invalidJson = $"{validJson[..closingBrace]},\"unknownProperty\":true}}";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(invalidJson));

        Assert.Throws<JsonException>(() => LanguageWorkerCapabilityManifestSerializer.Load(stream));
    }

    [Fact]
    public void CapabilityManifestSupportsMultipleToolchainsBehindAWorkerIdentity()
    {
        var manifest = LanguageWorkerCapabilityManifestSerializer.Load(Path.Combine(AppContext.BaseDirectory, "language-worker.json")) with { WorkerId = "minilang-worker", ToolchainIds = ["minilang-stable", "minilang-preview"] };
        var identity = new ServiceIdentity(manifest.WorkerId, ServiceKind.ToolchainWorker, "development", ProtocolVersion.WorkerV1, manifest.Capabilities, "ready");

        LanguageWorkerCapabilityManifestSerializer.Validate(manifest, identity);

        Assert.Equal("minilang-worker", manifest.WorkerId);
        Assert.Equal(["minilang-stable", "minilang-preview"], manifest.ToolchainIds);
        Assert.Throws<ArgumentException>(() =>
            LanguageWorkerCapabilityManifestSerializer.Validate(manifest with { ToolchainIds = ["minilang-stable", "minilang-stable"] }));
    }

    private static BuildRequest CreateBuildRequest(BuildTarget target, string source, BuildOutputKind outputKind = BuildOutputKind.Console)
    {
        var workspace = CreateWorkspace(source, outputKind);
        return new BuildRequest($"request-{Guid.NewGuid():N}", $"idempotency-{Guid.NewGuid():N}", "pipeline-minilang-test", MiniLanguageCompiler.ToolchainId, workspace.ReferenceSetId, workspace, DateTimeOffset.UtcNow.AddSeconds(20), workspace.BuildOptions, target);
    }

    private static WorkspaceSnapshot CreateWorkspace(string source, BuildOutputKind outputKind = BuildOutputKind.Console)
    {
        var options = new BuildOptions(BuildConfiguration.Release, Optimize: true, outputKind, AllowUnsafe: false, EmitPortablePdb: false, NullableContextMode.Disable, LanguageVersion: MiniLanguageCompiler.Version);
        return new WorkspaceSnapshot(ContractSchemaVersions.WorkspaceSnapshot, 7, 3, MiniLanguageCompiler.LanguageId, [new WorkspaceFile(MiniLanguageCompiler.DefaultFileName, 5, source)], MiniLanguageCompiler.DefaultFileName, [MiniLanguageCompiler.DefaultFileName], "net10-ref", options);
    }
}
