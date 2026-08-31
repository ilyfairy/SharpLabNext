using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Conformance;
using SharpLabNext.LanguageWorker.Sdk;
using SharpLabNext.Observability;

namespace SharpLabNext.Worker.GSharp.Tests;

[Collection(GSharpEndpointTestGroup.Name)]
public sealed class GSharpWorkerEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = ContractJson.CreateSerializerOptions();
    private static readonly JsonSerializerOptions LspJsonOptions = ContractJson.CreateLspSerializerOptions();

    [Fact]
    public async Task WorkerPassesReusableLanguageWorkerConformanceWithRealGSharpTools()
    {
        var root = GSharpTestSettings.CreateRoot();
        try
        {
            await using var factory = new GSharpWebApplicationFactory(root);
            using var client = factory.CreateClient();
            Assert.Same(SharpLabNextTelemetry.Metrics, factory.Services.GetRequiredService<SharpLabNextMetrics>());
            var manifest = await client.GetFromJsonAsync<LanguageWorkerCapabilityManifest>("/api/v1/worker/capabilities", JsonOptions, TestContext.Current.CancellationToken);
            Assert.NotNull(manifest);
            const string validSource = "package Conformance\n\nlet answer = 42\n";
            const string invalidSource = "package Conformance\n\n\"";
            var compileCheck = GSharpTestSettings.CreateRequest(BuildTarget.CompileCheck, validSource);
            var artifact = GSharpTestSettings.CreateRequest(BuildTarget.Artifact, validSource);
            var sessionWorkspace = GSharpTestSettings.CreateRequest(BuildTarget.CompileCheck, invalidSource).Workspace;
            var scenario = new LanguageWorkerConformanceScenario(
                new ServiceIdentity(GSharpToolchain.ToolchainId, ServiceKind.ToolchainWorker, "content", ProtocolVersion.WorkerV1, manifest.Capabilities, "ready"),
                $"sha256:{new string('0', 64)}",
                manifest,
                compileCheck,
                artifact,
                new OpenLanguageSessionRequest("request-gsharp-lsp", "pipeline-gsharp-lsp", GSharpToolchain.LanguageId, GSharpToolchain.ToolchainId, sessionWorkspace.ReferenceSetId, sessionWorkspace),
                "sharplabnext:///Program.gs",
                invalidSource,
                validSource,
                new LanguageWorkerCompletionPosition(0, 0),
                "let",
                "Syntax");
            var webSocketClient = factory.Server.CreateWebSocketClient();
            var runner = new LanguageWorkerConformanceRunner(client, (uri, cancellationToken) => webSocketClient.ConnectAsync(uri, cancellationToken));

            var report = await runner.VerifyAsync(scenario, TestContext.Current.CancellationToken);

            Assert.True(report.Succeeded);
            Assert.Equal(6, report.PassedChecks.Count);
        }
        finally
        {
            GSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ArtifactEndpointReturnsManagedPeAndPortablePdbFiles()
    {
        var root = GSharpTestSettings.CreateRoot();
        try
        {
            await using var factory = new GSharpWebApplicationFactory(root);
            using var client = factory.CreateClient();
            var request = GSharpTestSettings.CreateRequest(BuildTarget.Artifact, "package Endpoint\n\nimport System\n\nConsole.WriteLine(42)\n");
            using var response = await client.PostAsJsonAsync("/api/v1/build", request, JsonOptions, TestContext.Current.CancellationToken);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.True(response.IsSuccessStatusCode, body);
            var build = JsonSerializer.Deserialize<LanguageWorkerBuildHttpResponse>(body, JsonOptions);
            Assert.NotNull(build);
            Assert.Equal(BuildOutcome.Succeeded, Assert.IsType<BuildResult>(build.Result).Outcome);
            var envelope = Assert.IsType<LanguageWorkerArtifactEnvelope>(build.DevelopmentArtifact);
            Assert.Equal(GSharpToolchain.ArtifactFormat, envelope.ArtifactFormat);
            Assert.NotNull(envelope.FileContentsBase64);
            Assert.Contains($"{GSharpToolchain.AssemblyName}.dll", envelope.FileContentsBase64.Keys);
            Assert.Contains($"{GSharpToolchain.AssemblyName}.pdb", envelope.FileContentsBase64.Keys);
        }
        finally
        {
            GSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task WebSocketReturnsHoverAndSemanticTokensFromRealGSharpLanguageServer()
    {
        var root = GSharpTestSettings.CreateRoot();
        try
        {
            await using var factory = new GSharpWebApplicationFactory(root);
            using var client = factory.CreateClient();
            const string source = "package Lsp\n\nlet answer = 42\n";
            var workspace = GSharpTestSettings.CreateRequest(BuildTarget.CompileCheck, source).Workspace;
            var openRequest = new OpenLanguageSessionRequest("request-gsharp-semantic-lsp", "pipeline-gsharp-semantic-lsp", GSharpToolchain.LanguageId, GSharpToolchain.ToolchainId, workspace.ReferenceSetId, workspace);
            using var opened = await client.PostAsJsonAsync("/api/v1/language-sessions", openRequest, JsonOptions, TestContext.Current.CancellationToken);
            var openBody = await opened.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(opened.IsSuccessStatusCode, openBody);
            var session = JsonSerializer.Deserialize<LanguageSession>(openBody, JsonOptions);
            Assert.NotNull(session);

            var webSocketClient = factory.Server.CreateWebSocketClient();
            using var socket = await webSocketClient.ConnectAsync(new Uri($"ws://localhost/api/v1/language-sessions/{session.SessionId}/lsp"), TestContext.Current.CancellationToken);
            await SendAsync(socket, new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { capabilities = new { } } });
            using (var initialized = await ReceiveUntilAsync(socket, static rootElement => HasId(rootElement, 1))) {
                var capabilities = initialized.RootElement.GetProperty("result").GetProperty("capabilities");
                Assert.True(capabilities.GetProperty("hoverProvider").GetBoolean());
                Assert.True(capabilities.GetProperty("semanticTokensProvider").GetProperty("full").ValueKind is JsonValueKind.True or JsonValueKind.Object);
            }

            const string uri = "sharplabnext:///Program.gs";
            await SendAsync(socket, new { jsonrpc = "2.0", method = "initialized", @params = new { } });
            await SendAsync(socket, new { jsonrpc = "2.0", method = "textDocument/didOpen", @params = new { textDocument = new { uri, languageId = GSharpToolchain.LanguageId, version = 1, text = source } } });

            await SendAsync(socket, new { jsonrpc = "2.0", id = 2, method = "textDocument/hover", @params = new { textDocument = new { uri }, position = new { line = 2, character = 5 } } });
            using (var hover = await ReceiveUntilAsync(socket, static rootElement => HasId(rootElement, 2))) {
                var result = hover.RootElement.GetProperty("result");
                Assert.NotEqual(JsonValueKind.Null, result.ValueKind);
                Assert.Contains("answer", result.ToString(), StringComparison.Ordinal);
            }

            await SendAsync(socket, new { jsonrpc = "2.0", id = 3, method = "textDocument/semanticTokens/full", @params = new { textDocument = new { uri } } });
            using (var semanticTokens = await ReceiveUntilAsync(socket, static rootElement => HasId(rootElement, 3))) {
                var data = semanticTokens.RootElement.GetProperty("result").GetProperty("data");
                Assert.NotEmpty(data.EnumerateArray());
                Assert.Equal(0, data.GetArrayLength() % 5);
            }

            await SendAsync(socket, new { jsonrpc = "2.0", id = 4, method = "shutdown", @params = new { } });
            using var shutdown = await ReceiveUntilAsync(socket, static rootElement => HasId(rootElement, 4));
            Assert.Equal(JsonValueKind.Null, shutdown.RootElement.GetProperty("result").ValueKind);
            await SendAsync(socket, new { jsonrpc = "2.0", method = "exit", @params = new { } });
        }
        finally
        {
            GSharpTestSettings.DeleteRoot(root);
        }
    }

    private static bool HasId(JsonElement root, int expected) =>
        root.TryGetProperty("id", out var id) &&
        id.ValueKind == JsonValueKind.Number &&
        id.GetInt32() == expected;

    private static Task SendAsync(WebSocket socket, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, LspJsonOptions);
        return socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonDocument> ReceiveUntilAsync(WebSocket socket, Func<JsonElement, bool> predicate)
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var buffer = new byte[64 * 1024];
            using var stream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), TestContext.Current.CancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException($"The G# LSP WebSocket closed before the expected response: {result.CloseStatusDescription}");
                stream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var document = JsonDocument.Parse(stream.ToArray());
            if (predicate(document.RootElement))
                return document;
            document.Dispose();
        }
        throw new TimeoutException("The expected G# LSP message was not received.");
    }

}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GSharpEndpointTestGroup
{
    public const string Name = "GSharp endpoint tests";
}

internal sealed class GSharpWebApplicationFactory : WebApplicationFactory<global::Program>
{
    private readonly IReadOnlyDictionary<string, string?> _previousEnvironment;

    public GSharpWebApplicationFactory(string root)
    {
        var previous = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in GSharpTestSettings.WebHostConfiguration(root))
        {
            var environmentKey = key.Replace(":", "__", StringComparison.Ordinal);
            previous[environmentKey] = Environment.GetEnvironmentVariable(environmentKey);
            Environment.SetEnvironmentVariable(environmentKey, value);
        }
        _previousEnvironment = previous;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");

    protected override void Dispose(bool disposing)
    {
        try
        {
            base.Dispose(disposing);
        }
        finally
        {
            foreach (var (key, value) in _previousEnvironment)
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}
