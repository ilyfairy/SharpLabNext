using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpLabNext.Contracts;
using SharpLabNext.Observability;
using SharpLabNext.WorkerHost;

namespace SharpLabNext.Worker.FSharp.Tests;

public sealed class FSharpWorkerEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = ContractJson.CreateSerializerOptions();
    private static readonly JsonSerializerOptions LspJsonOptions = ContractJson.CreateLspSerializerOptions();
    private static readonly string[] QuickFixOnly = ["quickfix"];

    [Fact]
    public async Task DescribeAndBuildEndpointsExposeThePinnedFSharpToolchain()
    {
        var root = FSharpTestSettings.CreateRoot();
        try
        {
            await using var factory = CreateFactory(root);
            using var client = factory.CreateClient();
            var descriptor = await client.GetFromJsonAsync<WorkerDescriptor>(
                "/api/v1/worker/describe",
                JsonOptions,
                TestContext.Current.CancellationToken);
            Assert.NotNull(descriptor);
            Assert.Same(
                SharpLabNextTelemetry.Metrics,
                factory.Services.GetRequiredService<SharpLabNextMetrics>());
            Assert.Equal("fsharp-stable", descriptor.Service.Id);
            using var ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
            var readyBody = await ready.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(
                descriptor.Capabilities.Any(static item => item.Id == "completion" && item.Available),
                readyBody);
            Assert.Contains(descriptor.Capabilities, static item => item.Id == "semantic-tokens" && item.Available);
            Assert.Contains(descriptor.Capabilities, static item => item.Id == "code-actions" && item.Available);

            var request = FSharpBuildServiceTests.CreateRequest(
                BuildTarget.Artifact,
                [new WorkspaceFile(
                    "Program.fs",
                    1,
                    "module Program\nopen System\n[<EntryPoint>]\nlet main _ = Console.WriteLine(\"HTTP\"); 0\n")],
                ["Program.fs"]);
            using var response = await client.PostAsJsonAsync(
                "/api/v1/build",
                request,
                JsonOptions,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var build = await response.Content.ReadFromJsonAsync<FSharpWorkerBuildHttpResponse>(
                JsonOptions,
                TestContext.Current.CancellationToken);
            Assert.NotNull(build);
            var result = Assert.IsType<BuildResult>(build.Result);
            Assert.Equal(BuildOutcome.Succeeded, result.Outcome);
            Assert.NotNull(build.DevelopmentArtifact);
            Assert.Null(build.DevelopmentArtifact.PeImageBase64);
            Assert.Null(build.DevelopmentArtifact.PortablePdbBase64);
            Assert.NotNull(build.DevelopmentArtifact.FileContentsBase64);
            Assert.Equal(
                ["FSharp.Core.dll", "SharpLabNext.User.dll", "SharpLabNext.User.pdb"],
                build.DevelopmentArtifact.FileContentsBase64.Keys.Order(StringComparer.Ordinal));
            Assert.All(
                build.DevelopmentArtifact.FileContentsBase64.Values,
                static value => Assert.NotEmpty(Convert.FromBase64String(value)));
        }
        finally
        {
            FSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CompilerChildTimeoutDoesNotTakeDownLanguageSessions()
    {
        var root = FSharpTestSettings.CreateRoot();
        try
        {
            await using var factory = CreateFactory(root).WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    var settings = FSharpTestSettings.Create(root) with
                    {
                        BuildProcess = CompilerProcessIsolationOptions.Default with { Enabled = true }
                    };
                    services.RemoveAll<FSharpWorkerSettings>();
                    services.RemoveAll<ICompilerProcessRunner>();
                    services.AddSingleton(settings);
                    services.AddSingleton<ICompilerProcessRunner>(new FailingCompilerProcessRunner(
                        new CompilerProcessTimeoutException("Simulated compiler timeout.")));
                });
            });
            using var client = factory.CreateClient();
            var request = FSharpBuildServiceTests.CreateRequest(
                BuildTarget.CompileCheck,
                [new WorkspaceFile("Program.fs", 1, "module Program\nprintfn \"hello\"\n")],
                ["Program.fs"]);

            using var failed = await client.PostAsJsonAsync(
                "/api/v1/build",
                request,
                JsonOptions,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.RequestTimeout, failed.StatusCode);
            using var problem = JsonDocument.Parse(
                await failed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("deadline-exceeded", problem.RootElement.GetProperty("Code").GetString());

            using var opened = await client.PostAsJsonAsync(
                "/api/v1/language-sessions",
                CreateOpenRequest("module Program\nlet value = 42\n"),
                JsonOptions,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
            var session = await opened.Content.ReadFromJsonAsync<LanguageSession>(
                JsonOptions,
                TestContext.Current.CancellationToken);
            Assert.NotNull(session);
            using var closed = await client.DeleteAsync(
                $"/api/v1/language-sessions/{session.SessionId}",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, closed.StatusCode);
        }
        finally
        {
            FSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task WebSocketUsesStandardLspForDiagnosticsCompletionSemanticTokensAndCodeActions()
    {
        var root = FSharpTestSettings.CreateRoot();
        try
        {
            await using var factory = CreateFactory(root);
            using var client = factory.CreateClient();
            const string source = "module Program\nopen System\nopen System.Text\nlet write () = Console.Wri\n";
            var openRequest = CreateOpenRequest(source);
            using var opened = await client.PostAsJsonAsync(
                "/api/v1/language-sessions",
                openRequest,
                JsonOptions,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
            var session = await opened.Content.ReadFromJsonAsync<LanguageSession>(
                JsonOptions,
                TestContext.Current.CancellationToken);
            Assert.NotNull(session);

            var webSocketClient = factory.Server.CreateWebSocketClient();
            using var socket = await webSocketClient.ConnectAsync(
                new Uri($"ws://localhost/api/v1/language-sessions/{session.SessionId}/lsp"),
                TestContext.Current.CancellationToken);
            await SendAsync(socket, new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new { capabilities = new { } }
            });
            using var initialized = await ReceiveUntilAsync(socket, static rootElement =>
                rootElement.TryGetProperty("id", out var id) && id.GetInt32() == 1);
            var capabilities = initialized.RootElement.GetProperty("result").GetProperty("capabilities");
            Assert.True(capabilities.GetProperty("completionProvider").GetProperty("resolveProvider").ValueKind == JsonValueKind.False);
            var semanticProvider = capabilities.GetProperty("semanticTokensProvider");
            Assert.True(semanticProvider.GetProperty("full").GetBoolean());
            Assert.False(semanticProvider.GetProperty("range").GetBoolean());
            Assert.Equal(
                FSharpLanguageSession.SemanticTokenTypes,
                semanticProvider.GetProperty("legend").GetProperty("tokenTypes")
                    .EnumerateArray().Select(static item => item.GetString()!).ToArray());
            Assert.Empty(semanticProvider.GetProperty("legend").GetProperty("tokenModifiers").EnumerateArray());
            var actionKinds = capabilities.GetProperty("codeActionProvider").GetProperty("codeActionKinds")
                .EnumerateArray().Select(static item => item.GetString()!).ToArray();
            Assert.Equal(["quickfix", "source.organizeImports"], actionKinds);

            const string uri = "sharplabnext:///Program.fs";
            await SendAsync(socket, new
            {
                jsonrpc = "2.0",
                method = "initialized",
                @params = new { }
            });
            await SendAsync(socket, new
            {
                jsonrpc = "2.0",
                method = "textDocument/didOpen",
                @params = new
                {
                    textDocument = new { uri, languageId = "fsharp", version = 1, text = source }
                }
            });
            using var diagnostics = await ReceiveUntilAsync(socket, static rootElement =>
                rootElement.TryGetProperty("method", out var method) &&
                method.GetString() == "textDocument/publishDiagnostics");
            Assert.Equal(uri, diagnostics.RootElement.GetProperty("params").GetProperty("uri").GetString());

            await SendAsync(socket, new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "textDocument/completion",
                @params = new
                {
                    textDocument = new { uri },
                    position = new { line = 3, character = "let write () = Console.Wri".Length },
                    context = new { triggerKind = 1, triggerCharacter = (string?)null }
                }
            });
            using var completion = await ReceiveUntilAsync(socket, static rootElement =>
                rootElement.TryGetProperty("id", out var id) && id.GetInt32() == 2);
            var items = completion.RootElement.GetProperty("result").GetProperty("items");
            Assert.Contains(items.EnumerateArray(), static item => item.GetProperty("label").GetString() == "WriteLine");

            await SendAsync(socket, new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "textDocument/semanticTokens/full",
                @params = new { textDocument = new { uri } }
            });
            using var semanticTokens = await ReceiveUntilAsync(socket, static rootElement =>
                rootElement.TryGetProperty("id", out var id) && id.GetInt32() == 3);
            var semanticData = semanticTokens.RootElement.GetProperty("result").GetProperty("data");
            Assert.NotEmpty(semanticData.EnumerateArray());
            Assert.Equal(0, semanticData.GetArrayLength() % 5);

            await SendAsync(socket, new
            {
                jsonrpc = "2.0",
                id = 4,
                method = "textDocument/codeAction",
                @params = new
                {
                    textDocument = new { uri },
                    range = new
                    {
                        start = new { line = 2, character = 0 },
                        end = new { line = 2, character = "open System.Text".Length }
                    },
                    context = new { diagnostics = Array.Empty<object>(), only = QuickFixOnly }
                }
            });
            using var codeActions = await ReceiveUntilAsync(socket, static rootElement =>
                rootElement.TryGetProperty("id", out var id) && id.GetInt32() == 4);
            var action = Assert.Single(codeActions.RootElement.GetProperty("result").EnumerateArray());
            Assert.Equal("quickfix", action.GetProperty("kind").GetString());

            await SendAsync(socket, new { jsonrpc = "2.0", id = 5, method = "shutdown", @params = new { } });
            using var shutdown = await ReceiveUntilAsync(socket, static rootElement =>
                rootElement.TryGetProperty("id", out var id) && id.GetInt32() == 5);
            Assert.Equal(JsonValueKind.Null, shutdown.RootElement.GetProperty("result").ValueKind);
            await SendAsync(socket, new { jsonrpc = "2.0", method = "exit", @params = new { } });
            await ReceiveCloseAsync(socket);
        }
        finally
        {
            FSharpTestSettings.DeleteRoot(root);
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(string root) => new FSharpWorkerFactory(root);

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

    private sealed class FSharpWorkerFactory(string root) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["FSharpWorker:ReleaseId"] = "test-release",
                    ["FSharpWorker:WorkerImageId"] = $"sha256:{new string('a', 64)}",
                    ["FSharpWorker:CompilerVersion"] = "43.12.204",
                    ["FSharpWorker:FSharpCorePackageVersion"] = "10.1.204",
                    ["FSharpWorker:WorkRoot"] = root,
                    ["FSharpWorker:DevelopmentArtifactEnvelope:Enabled"] = "true",
                    ["FSharpWorker:DevelopmentArtifactEnvelope:MaxBytes"] = (8 * 1024 * 1024).ToString(CultureInfo.InvariantCulture),
                    ["ReferenceSets:net10-ref:Path"] = FSharpTestSettings.GetNet10ReferencePath(),
                    ["ReferenceSets:net10-ref:TargetFramework"] = "net10.0",
                    ["ReferenceSets:net10-ref:FrameworkVersion"] = "10.0.9",
                    ["ReferenceSets:net11-preview-ref:Path"] = FSharpTestSettings.GetNet11PreviewReferencePath(),
                    ["ReferenceSets:net11-preview-ref:TargetFramework"] = "net11.0",
                    ["ReferenceSets:net11-preview-ref:FrameworkVersion"] = FSharpTestSettings.Net11PreviewVersion
                }));
            builder.ConfigureTestServices(services =>
            {
                var settings = FSharpTestSettings.Create(root);
                services.RemoveAll<FSharpWorkerSettings>();
                services.RemoveAll<FSharpWorkerIdentity>();
                services.RemoveAll<FSharpCompilationLimits>();
                services.RemoveAll<FSharpAstLimits>();
                services.RemoveAll<FSharpLspLimits>();
                services.RemoveAll<FSharpDevelopmentArtifactEnvelopeOptions>();
                services.RemoveAll<FSharpReferenceSetProvider>();
                services.AddSingleton(settings);
                services.AddSingleton(settings.Identity);
                services.AddSingleton(settings.CompilationLimits);
                services.AddSingleton(settings.AstLimits);
                services.AddSingleton(settings.LspLimits);
                services.AddSingleton(settings.DevelopmentArtifactEnvelope);
                services.AddSingleton(new FSharpReferenceSetProvider(settings.ReferenceSets));
            });
        }
    }

    private static OpenLanguageSessionRequest CreateOpenRequest(string source)
    {
        var options = new BuildOptions(
            BuildConfiguration.Debug,
            false,
            BuildOutputKind.Library,
            false,
            true,
            NullableContextMode.Disable,
            "9.0");
        var workspace = new WorkspaceSnapshot(
            ContractSchemaVersions.WorkspaceSnapshot,
            1,
            1,
            "fsharp",
            [new WorkspaceFile("Program.fs", 1, source)],
            "Program.fs",
            ["Program.fs"],
            "net10-ref",
            options);
        return new OpenLanguageSessionRequest(
            "request-lsp",
            "pipeline-lsp",
            "fsharp",
            "fsharp-stable",
            "net10-ref",
            workspace);
    }

    private static Task SendAsync(WebSocket socket, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, LspJsonOptions);
        return socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            true,
            TestContext.Current.CancellationToken);
    }

    private static async Task<JsonDocument> ReceiveUntilAsync(
        WebSocket socket,
        Func<JsonElement, bool> predicate)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var buffer = new byte[64 * 1024];
                using var stream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new InvalidOperationException("The LSP WebSocket closed before the expected response.");
                    stream.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);
                var document = JsonDocument.Parse(stream.ToArray());
                if (predicate(document.RootElement))
                    return document;
                document.Dispose();
            }
            throw new TimeoutException("The expected LSP message was not received.");
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested && !TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            socket.Abort();
            throw new TimeoutException("The expected LSP message was not received within 10 seconds.");
        }
    }

    private static async Task ReceiveCloseAsync(WebSocket socket)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var buffer = new byte[256];
        try
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            Assert.Equal(WebSocketMessageType.Close, result.MessageType);
            Assert.Equal(WebSocketCloseStatus.NormalClosure, result.CloseStatus);
            if (socket.State == WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Test completed.",
                    timeout.Token);
            }
        }
        catch (IOException)
        {
            // TestServer can dispose its in-memory socket after the server sends the close frame.
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested && !TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            socket.Abort();
            throw new TimeoutException("The LSP WebSocket did not complete its close handshake.");
        }
    }
}
