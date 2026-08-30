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
using SharpLabNext.Worker.IL.Compiler;

namespace SharpLabNext.Worker.IL.Tests;

public sealed class IlWorkerEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = ContractJson.CreateSerializerOptions();
    private static readonly JsonSerializerOptions LspJsonOptions = ContractJson.CreateLspSerializerOptions();

    [Fact]
    public void LspTestMessagesUseStandardJsonRpcMemberNames()
    {
        using var message = JsonDocument.Parse(SerializeLspMessage(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { capabilities = new { } } }));

        Assert.Equal("2.0", message.RootElement.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, message.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("initialize", message.RootElement.GetProperty("method").GetString());
        Assert.True(message.RootElement.TryGetProperty("params", out _));
        Assert.False(message.RootElement.TryGetProperty("Jsonrpc", out _));
        Assert.False(message.RootElement.TryGetProperty("Params", out _));
    }

    [Fact]
    public async Task DescribeHealthAndBuildExposeThePinnedMobiusToolchainAndNet11ReferenceSelection()
    {
        var root = IlTestSettings.CreateRoot();
        try
        {
            await using var factory = CreateFactory(root);
            using var client = factory.CreateClient();
            Assert.Same(SharpLabNextTelemetry.Metrics, factory.Services.GetRequiredService<SharpLabNextMetrics>());
            using var ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
            var readyText = await ready.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(ready.IsSuccessStatusCode, readyText);
            var descriptor = await client.GetFromJsonAsync<WorkerDescriptor>("/api/v1/worker/describe", JsonOptions, TestContext.Current.CancellationToken);
            Assert.NotNull(descriptor);
            Assert.Equal("mobius-ilasm-stable", descriptor.Service.Id);
            Assert.Contains(descriptor.Capabilities, static item => item.Id == "managed-pe" && item.Available);
            Assert.Contains(descriptor.Capabilities, static item => item.Id == "folding-ranges" && item.Available);
            Assert.Contains(descriptor.Capabilities, static item => item.Id == "code-actions" && item.Available);

            using var response = await client.PostAsJsonAsync("/api/v1/build", IlTestSettings.CreateRequest(BuildTarget.Artifact, IlTestSettings.ValidMultiFileWorkspace(), ["Program.il", "Helper.il"], referenceSetId: "net11-preview-ref"), JsonOptions, TestContext.Current.CancellationToken);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(response.IsSuccessStatusCode, $"{readyText}\n{body}");
            var build = JsonSerializer.Deserialize<IlWorkerBuildHttpResponse>(body, JsonOptions);
            Assert.NotNull(build);
            var result = Assert.IsType<BuildResult>(build.Result);
            Assert.Equal(BuildOutcome.Succeeded, result.Outcome);
            Assert.Equal("net11-preview-ref", result.Identity.ReferenceSetId);
            Assert.NotNull(build.DevelopmentArtifact);
            Assert.Equal("net11.0", build.DevelopmentArtifact.TargetFramework);
            Assert.NotEmpty(Convert.FromBase64String(build.DevelopmentArtifact.PeImageBase64));
        }
        finally
        {
            IlTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task WebSocketImplementsDiagnosticsCompletionHoverSignatureHelpTokensSymbolsAndFolding()
    {
        var root = IlTestSettings.CreateRoot();
        try
        {
            await using var factory = CreateFactory(root);
            using var client = factory.CreateClient();
            const string source = """
                .assembly Demo {}
                .class public Program extends [System.Runtime]System.Object
                {
                  .method public static void Main() cil managed
                  {
                    .maxstack 1
                    ldstr "hello"
                    invalid.opcode
                    call void Program::Pick(int32)
                    br.s missing
                    ret
                  }
                  .method public static void Pick(int32 value) cil managed
                  {
                    ret
                  }
                  .method public static void Pick(string value) cil managed
                  {
                    ret
                  }
                }
                """;
            using var opened = await client.PostAsJsonAsync("/api/v1/language-sessions", CreateOpenRequest(source), JsonOptions, TestContext.Current.CancellationToken);
            var openBody = await opened.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(opened.IsSuccessStatusCode, openBody);
            var session = await opened.Content.ReadFromJsonAsync<LanguageSession>(JsonOptions, TestContext.Current.CancellationToken);
            Assert.NotNull(session);

            var webSocketClient = factory.Server.CreateWebSocketClient();
            using var socket = await webSocketClient.ConnectAsync(new Uri($"ws://localhost/api/v1/language-sessions/{session.SessionId}/lsp"), TestContext.Current.CancellationToken);
            await SendAsync(socket, new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { capabilities = new { } } });
            using var initialized = await ReceiveUntilAsync(socket, static rootElement => HasId(rootElement, 1));
            var capabilities = initialized.RootElement.GetProperty("result").GetProperty("capabilities");
            Assert.True(capabilities.GetProperty("hoverProvider").GetBoolean());
            Assert.True(capabilities.GetProperty("definitionProvider").GetBoolean());
            Assert.True(capabilities.GetProperty("documentSymbolProvider").GetBoolean());
            Assert.True(capabilities.GetProperty("workspaceSymbolProvider").GetBoolean());
            Assert.True(capabilities.GetProperty("foldingRangeProvider").GetBoolean());
            Assert.Equal(["quickfix", "refactor.rewrite"], capabilities.GetProperty("codeActionProvider").GetProperty("codeActionKinds").EnumerateArray().Select(static value => value.GetString()!).ToArray());
            string[] completionTriggerCharacters =
            [
..capabilities.GetProperty("completionProvider").GetProperty("triggerCharacters").EnumerateArray().Select(static value => value.GetString()!)
            ];
            Assert.Equal(IlLanguageService.CompletionTriggerCharacters, completionTriggerCharacters);
            string[] signatureHelpTriggerCharacters =
            [
..capabilities.GetProperty("signatureHelpProvider").GetProperty("triggerCharacters").EnumerateArray().Select(static value => value.GetString()!)
            ];
            Assert.Equal(IlLanguageService.SignatureHelpTriggerCharacters, signatureHelpTriggerCharacters);
            string[] tokenTypes =
            [
..capabilities.GetProperty("semanticTokensProvider").GetProperty("legend").GetProperty("tokenTypes").EnumerateArray().Select(static value => value.GetString()!)
            ];
            Assert.Equal(IlLanguageService.SemanticTokenTypes, tokenTypes);
            Assert.Equal("typeParameter", tokenTypes[^1]);
            string[] tokenModifiers =
            [
..capabilities.GetProperty("semanticTokensProvider").GetProperty("legend").GetProperty("tokenModifiers").EnumerateArray().Select(static value => value.GetString()!)
            ];
            Assert.Equal(IlLanguageService.SemanticTokenModifiers, tokenModifiers);

            const string uri = "sharplabnext:///Program.il";
            await SendAsync(socket, new { jsonrpc = "2.0", method = "initialized", @params = new { } });
            await SendAsync(socket, new { jsonrpc = "2.0", method = "textDocument/didOpen", @params = new { textDocument = new { uri, languageId = "il", version = 1, text = source } } });
            using var diagnostics = await ReceiveUntilAsync(socket, static rootElement => rootElement.TryGetProperty("method", out var method) && method.GetString() == "textDocument/publishDiagnostics");
            Assert.Contains(diagnostics.RootElement.GetProperty("params").GetProperty("diagnostics").EnumerateArray(), static item => item.GetProperty("code").GetString() == "ILPAR202" && item.GetProperty("source").GetString() == "Parse" && item.GetProperty("data").GetProperty("diagnosticKind").GetString() == "Parse");

            await SendAsync(socket, new { jsonrpc = "2.0", id = 2, method = "textDocument/completion", @params = new { textDocument = new { uri }, position = new { line = 6, character = 6 }, context = new { triggerKind = 1, triggerCharacter = (string?)null } } });
            using var completion = await ReceiveUntilAsync(socket, static rootElement => HasId(rootElement, 2));
            var ldstrCompletion = Assert.Single(completion.RootElement.GetProperty("result").GetProperty("items").EnumerateArray(), static item => item.GetProperty("label").GetString() == "ldstr");
            Assert.Equal("ldstr", ldstrCompletion.GetProperty("textEdit").GetProperty("newText").GetString());
            Assert.Equal(1, ldstrCompletion.GetProperty("insertTextFormat").GetInt32());

            var opcodeCharacter = source.Split('\n')[6].IndexOf("ldstr", StringComparison.Ordinal) + 1;
            await SendAsync(socket, new { jsonrpc = "2.0", id = 3, method = "textDocument/hover", @params = new { textDocument = new { uri }, position = new { line = 6, character = opcodeCharacter } } });
            using var hover = await ReceiveUntilAsync(socket, static rootElement => HasId(rootElement, 3));
            var hoverMarkdown = hover.RootElement.GetProperty("result").GetProperty("contents").GetProperty("value").GetString()!;
            Assert.Contains("ECMA-335 opcode 'ldstr'", hoverMarkdown, StringComparison.Ordinal);
            Assert.Contains("Operand", hoverMarkdown, StringComparison.Ordinal);

            await AssertNonEmptyArrayResultAsync(socket, 4, "textDocument/semanticTokens/full", new { textDocument = new { uri } }, "data");
            await AssertNonEmptyArrayResultAsync(socket, 5, "textDocument/documentSymbol", new { textDocument = new { uri } });
            await AssertNonEmptyArrayResultAsync(socket, 6, "textDocument/foldingRange", new { textDocument = new { uri } });

            var signatureLine = source.Split('\n')[8];
            await SendAsync(socket, new { jsonrpc = "2.0", id = 7, method = "textDocument/signatureHelp", @params = new { textDocument = new { uri }, position = new { line = 8, character = signatureLine.IndexOf('(') + 1 } } });
            using var signatureHelp = await ReceiveUntilAsync(socket, static rootElement => HasId(rootElement, 7));
            var signatureResult = signatureHelp.RootElement.GetProperty("result");
            Assert.Equal(0, signatureResult.GetProperty("activeParameter").GetInt32());
            Assert.Contains(signatureResult.GetProperty("signatures").EnumerateArray(), static item => item.GetProperty("label").GetString()!.Contains("::Pick(", StringComparison.Ordinal));

            var typeLine = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')[1];
            await SendAsync(socket, new { jsonrpc = "2.0", id = 8, method = "textDocument/definition", @params = new { textDocument = new { uri }, position = new { line = 1, character = typeLine.IndexOf("Program", StringComparison.Ordinal) + 1 } } });
            using var definition = await ReceiveUntilAsync(socket, static rootElement => HasId(rootElement, 8));
            Assert.Equal(uri, definition.RootElement.GetProperty("result").GetProperty("uri").GetString());
            Assert.Equal(1, definition.RootElement.GetProperty("result").GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());

            await SendAsync(socket, new { jsonrpc = "2.0", id = 9, method = "workspace/symbol", @params = new { query = "Program" } });
            using var workspaceSymbols = await ReceiveUntilAsync(socket, static rootElement => HasId(rootElement, 9));
            var programSymbol = Assert.Single(workspaceSymbols.RootElement.GetProperty("result").EnumerateArray(), static item => item.GetProperty("name").GetString() == "Program");
            Assert.Equal(uri, programSymbol.GetProperty("location").GetProperty("uri").GetString());
            Assert.True(programSymbol.GetProperty("data").GetProperty("workspaceRevision").GetInt64() > 0);

            var sourceLines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            await SendAsync(socket, new { jsonrpc = "2.0", id = 10, method = "textDocument/codeAction", @params = new { textDocument = new { uri }, range = new { start = new { line = 0, character = 0 }, end = new { line = sourceLines.Length - 1, character = sourceLines[^1].Length } }, context = new { diagnostics = Array.Empty<object>() } } });
            using var codeActions = await ReceiveUntilAsync(socket, static rootElement => HasId(rootElement, 10));
            var missingLabel = Assert.Single(codeActions.RootElement.GetProperty("result").EnumerateArray(), static action => action.GetProperty("data").TryGetProperty("diagnostic", out var diagnostic) && diagnostic.GetString() == "ILBIND201");
            Assert.Equal("quickfix", missingLabel.GetProperty("kind").GetString());
            Assert.Equal(1, missingLabel.GetProperty("data").GetProperty("documentVersion").GetInt64());
            Assert.Contains("missing:", Assert.Single(missingLabel.GetProperty("edit").GetProperty("changes").GetProperty(uri).EnumerateArray()).GetProperty("newText").GetString(), StringComparison.Ordinal);

            await SendAsync(socket, new { jsonrpc = "2.0", id = 11, method = "shutdown", @params = new { } });
            using var shutdown = await ReceiveUntilAsync(socket, static rootElement => HasId(rootElement, 11));
            Assert.Equal(JsonValueKind.Null, shutdown.RootElement.GetProperty("result").ValueKind);
            await SendAsync(socket, new { jsonrpc = "2.0", method = "exit", @params = new { } });
        }
        finally
        {
            IlTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task WebSocketUsesTheSessionWorkspaceForHoverAndSemanticTokensWithoutDidOpen()
    {
        var root = IlTestSettings.CreateRoot();
        try
        {
            await using var factory = CreateFactory(root);
            using var client = factory.CreateClient();
            const string source = """
                .assembly Demo {}
                .class public Program extends [System.Runtime]System.Object
                {
                  .method public static void Main() cil managed
                  {
                    ldstr "hello"
                    pop
                    ret
                  }
                }
                """;
            using var opened = await client.PostAsJsonAsync("/api/v1/language-sessions", CreateOpenRequest(source), JsonOptions, TestContext.Current.CancellationToken);
            var openBody = await opened.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(opened.IsSuccessStatusCode, openBody);
            var session = await opened.Content.ReadFromJsonAsync<LanguageSession>(JsonOptions, TestContext.Current.CancellationToken);
            Assert.NotNull(session);

            var webSocketClient = factory.Server.CreateWebSocketClient();
            using var socket = await webSocketClient.ConnectAsync(new Uri($"ws://localhost/api/v1/language-sessions/{session.SessionId}/lsp"), TestContext.Current.CancellationToken);
            await SendAsync(socket, new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { capabilities = new { } } });
            using var initialized = await ReceiveUntilAsync(socket, static rootElement => HasId(rootElement, 1));
            Assert.False(initialized.RootElement.TryGetProperty("error", out _));
            await SendAsync(socket, new { jsonrpc = "2.0", method = "initialized", @params = new { } });
            await Task.Delay(25, TestContext.Current.CancellationToken);

            const string uri = "sharplabnext:///Program.il";
            var sourceLines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var opcodeLine = Array.FindIndex(sourceLines, static line => line.Contains("ldstr", StringComparison.Ordinal));
            Assert.True(opcodeLine >= 0);
            var opcodeCharacter = sourceLines[opcodeLine].IndexOf("ldstr", StringComparison.Ordinal) + 1;
            await SendAsync(socket, new { jsonrpc = "2.0", id = 2, method = "textDocument/hover", @params = new { textDocument = new { uri }, position = new { line = opcodeLine, character = opcodeCharacter } } });
            using var hover = await ReceiveUntilWithoutDiagnosticsAsync(socket, static rootElement => HasId(rootElement, 2));
            Assert.Contains("ECMA-335 opcode 'ldstr'", hover.RootElement.GetProperty("result").GetProperty("contents").GetProperty("value").GetString(), StringComparison.Ordinal);

            await SendAsync(socket, new { jsonrpc = "2.0", id = 3, method = "textDocument/semanticTokens/full", @params = new { textDocument = new { uri } } });
            using var semanticTokens = await ReceiveUntilWithoutDiagnosticsAsync(socket, static rootElement => HasId(rootElement, 3));
            Assert.NotEmpty(semanticTokens.RootElement.GetProperty("result").GetProperty("data").EnumerateArray());

            await SendAsync(socket, new { jsonrpc = "2.0", id = 4, method = "shutdown", @params = new { } });
            using var shutdown = await ReceiveUntilAsync(socket, static rootElement => HasId(rootElement, 4));
            Assert.Equal(JsonValueKind.Null, shutdown.RootElement.GetProperty("result").ValueKind);
            await SendAsync(socket, new { jsonrpc = "2.0", method = "exit", @params = new { } });
        }
        finally
        {
            IlTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task WebSocketReadsCancellationBehindAWorkspaceChangeWhileLanguageRequestsAreInFlight()
    {
        var root = IlTestSettings.CreateRoot();
        try
        {
            await using var factory = new IlWorkerFactory(root, diagnosticsDebounceMilliseconds: 10_000);
            using var client = factory.CreateClient();
            var source = string.Concat(".assembly Cancel {}\n.class public C\n{\n  .method public static void M() cil managed\n  {\n    .maxstack 0\n", string.Concat(Enumerable.Repeat("    nop\n", 20_000)), "    ret\n  }\n}\n");
            using var opened = await client.PostAsJsonAsync("/api/v1/language-sessions", CreateOpenRequest(source), JsonOptions, TestContext.Current.CancellationToken);
            var session = await opened.Content.ReadFromJsonAsync<LanguageSession>(JsonOptions, TestContext.Current.CancellationToken);
            Assert.NotNull(session);

            using var socket = await factory.Server.CreateWebSocketClient().ConnectAsync(new Uri($"ws://localhost/api/v1/language-sessions/{session.SessionId}/lsp"), TestContext.Current.CancellationToken);
            await InitializeAsync(socket);
            const string uri = "sharplabnext:///Program.il";
            await SendAsync(socket, new { jsonrpc = "2.0", method = "textDocument/didOpen", @params = new { textDocument = new { uri, languageId = "il", version = 1, text = source } } });
            for (var id = 10; id <= 17; id++)
            {
                await SendAsync(socket, new { jsonrpc = "2.0", id, method = "textDocument/documentSymbol", @params = new { textDocument = new { uri } } });
            }
            await SendDidChangeAsync(socket, uri, 2, source + "\n");
            await SendAsync(socket, new { jsonrpc = "2.0", method = "$/cancelRequest", @params = (object?)null });
            await SendAsync(socket, new { jsonrpc = "2.0", method = "$/cancelRequest", @params = new { id = 17 } });

            using var cancelled = await ReceiveUntilAsync(socket, static message => HasId(message, 17) && message.TryGetProperty("error", out var error) && error.GetProperty("code").GetInt32() == -32800).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal("Request cancelled.", cancelled.RootElement.GetProperty("error").GetProperty("message").GetString());

            for (var id = 10; id < 17; id++)
                await SendAsync(socket, new { jsonrpc = "2.0", method = "$/cancelRequest", @params = new { id } });
        }
        finally
        {
            IlTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task WebSocketCoalescesRapidChangesAndPublishesOnlyTheLatestDiagnosticsVersion()
    {
        var root = IlTestSettings.CreateRoot();
        try
        {
            await using var factory = new IlWorkerFactory(root, diagnosticsDebounceMilliseconds: 100);
            using var client = factory.CreateClient();
            const string initial = ".assembly Demo {}\n.method public static void Main() cil managed\n{\n  initial.invalid\n}\n";
            using var opened = await client.PostAsJsonAsync("/api/v1/language-sessions", CreateOpenRequest(initial), JsonOptions, TestContext.Current.CancellationToken);
            var session = await opened.Content.ReadFromJsonAsync<LanguageSession>(JsonOptions, TestContext.Current.CancellationToken);
            Assert.NotNull(session);

            using var socket = await factory.Server.CreateWebSocketClient().ConnectAsync(new Uri($"ws://localhost/api/v1/language-sessions/{session.SessionId}/lsp"), TestContext.Current.CancellationToken);
            await InitializeAsync(socket);
            const string uri = "sharplabnext:///Program.il";
            await SendAsync(socket, new { jsonrpc = "2.0", method = "textDocument/didOpen", @params = new { textDocument = new { uri, languageId = "il", version = 1, text = initial } } });
            const string version2 = ".assembly Demo {}\n.method public static void Main() cil managed\n{\n  second.invalid\n}\n";
            const string version3 = ".assembly Demo {}\n.method public static void Main() cil managed\n{\n  final.invalid\n}\n";
            await SendDidChangeAsync(socket, uri, 2, version2);
            await SendDidChangeAsync(socket, uri, 3, version3);

            var publishedVersions = new List<long>();
            JsonElement finalDiagnostics = default;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                using var message = await ReceiveMessageAsync(socket);
                if (!IsDiagnosticsFor(message.RootElement, uri, out var parameters))
                    continue;
                var version = parameters.GetProperty("version").GetInt64();
                publishedVersions.Add(version);
                if (version != 3)
                    continue;
                finalDiagnostics = parameters.GetProperty("diagnostics").Clone();
                break;
            }

            Assert.NotEqual(JsonValueKind.Undefined, finalDiagnostics.ValueKind);
            Assert.DoesNotContain(2, publishedVersions);
            Assert.Contains(finalDiagnostics.EnumerateArray(), static item => item.GetProperty("code").GetString() == "ILPAR202" && item.GetProperty("data").GetProperty("documentVersion").GetInt64() == 3);
        }
        finally
        {
            IlTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task WebSocketRefreshesEveryOpenDocumentAndClearsClosedDiagnostics()
    {
        var root = IlTestSettings.CreateRoot();
        try
        {
            await using var factory = new IlWorkerFactory(root, diagnosticsDebounceMilliseconds: 20);
            using var client = factory.CreateClient();
            const string program = ".assembly Demo {}\n.method public static void Main() cil managed\n{\n  program.invalid\n}\n";
            const string helper = ".class public Helper {}\n";
            var files = new[]
            {
                new WorkspaceFile("Program.il", 1, program),
                new WorkspaceFile("Helper.il", 1, helper)
            };
            using var opened = await client.PostAsJsonAsync("/api/v1/language-sessions", CreateOpenRequest(files, ["Program.il", "Helper.il"]), JsonOptions, TestContext.Current.CancellationToken);
            var session = await opened.Content.ReadFromJsonAsync<LanguageSession>(JsonOptions, TestContext.Current.CancellationToken);
            Assert.NotNull(session);

            using var socket = await factory.Server.CreateWebSocketClient().ConnectAsync(new Uri($"ws://localhost/api/v1/language-sessions/{session.SessionId}/lsp"), TestContext.Current.CancellationToken);
            await InitializeAsync(socket);
            const string programUri = "sharplabnext:///Program.il";
            const string helperUri = "sharplabnext:///Helper.il";
            await SendDidOpenAsync(socket, programUri, program);
            using var initialReport = await ReceiveUntilAsync(socket, static message => IsDiagnosticsFor(message, programUri, out _));
            var initialRevision = initialReport.RootElement.GetProperty("params").GetProperty("diagnostics")[0].GetProperty("data").GetProperty("workspaceRevision").GetInt64();

            await SendDidOpenAsync(socket, helperUri, helper);
            var sawHelper = false;
            var refreshedProgramRevision = initialRevision;
            for (var attempt = 0; attempt < 20 && (!sawHelper || refreshedProgramRevision <= initialRevision); attempt++)
            {
                using var message = await ReceiveMessageAsync(socket);
                if (IsDiagnosticsFor(message.RootElement, helperUri, out _))
                {
                    sawHelper = true;
                    continue;
                }
                if (!IsDiagnosticsFor(message.RootElement, programUri, out var parameters))
                    continue;
                refreshedProgramRevision = parameters.GetProperty("diagnostics")[0].GetProperty("data").GetProperty("workspaceRevision").GetInt64();
            }
            Assert.True(sawHelper);
            Assert.True(refreshedProgramRevision > initialRevision);

            await SendAsync(socket, new { jsonrpc = "2.0", method = "textDocument/didClose", @params = new { textDocument = new { uri = programUri } } });
            using var cleared = await ReceiveUntilAsync(socket, static message => IsDiagnosticsFor(message, programUri, out var parameters) && !parameters.GetProperty("diagnostics").EnumerateArray().Any());
            Assert.Equal(1, cleared.RootElement.GetProperty("params").GetProperty("version").GetInt64());
        }
        finally
        {
            IlTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task WebSocketClosesMessagesThatExceedTheConfiguredUtf8ByteLimit()
    {
        var root = IlTestSettings.CreateRoot();
        try
        {
            await using var factory = new IlWorkerFactory(root, maxMessageBytes: 256);
            using var client = factory.CreateClient();
            const string source = ".assembly Limits {}";
            using var opened = await client.PostAsJsonAsync("/api/v1/language-sessions", CreateOpenRequest(source), JsonOptions, TestContext.Current.CancellationToken);
            var session = await opened.Content.ReadFromJsonAsync<LanguageSession>(JsonOptions, TestContext.Current.CancellationToken);
            Assert.NotNull(session);
            var webSocketClient = factory.Server.CreateWebSocketClient();
            using var socket = await webSocketClient.ConnectAsync(new Uri($"ws://localhost/api/v1/language-sessions/{session.SessionId}/lsp"), TestContext.Current.CancellationToken);
            var oversized = new byte[257];
            Array.Fill(oversized, (byte)'x');
            await socket.SendAsync(new ArraySegment<byte>(oversized), WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);
            var receive = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[128]), TestContext.Current.CancellationToken);
            Assert.Equal(WebSocketMessageType.Close, receive.MessageType);
            Assert.Equal(WebSocketCloseStatus.MessageTooBig, receive.CloseStatus);
        }
        finally
        {
            IlTestSettings.DeleteRoot(root);
        }
    }

    private static async Task AssertNonEmptyArrayResultAsync(WebSocket socket, int id, string method, object parameters, string? property = null)
    {
        await SendAsync(socket, new { jsonrpc = "2.0", id, method, @params = parameters });
        using var response = await ReceiveUntilAsync(socket, rootElement => HasId(rootElement, id));
        var result = response.RootElement.GetProperty("result");
        var array = property is null ? result : result.GetProperty(property);
        Assert.NotEmpty(array.EnumerateArray());
    }

    private static WebApplicationFactory<Program> CreateFactory(string root) => new IlWorkerFactory(root);

    private sealed class IlWorkerFactory(string root, int? maxMessageBytes = null, int? diagnosticsDebounceMilliseconds = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["IlWorker:ReleaseId"] = "test-release",
                    ["IlWorker:WorkerImageId"] = $"sha256:{new string('a', 64)}",
                    ["IlWorker:CompilerVersion"] = IlCompilerProtocol.PackageVersion,
                    ["IlWorker:WorkRoot"] = Path.Combine(root, "work"),
                    ["IlWorker:DevelopmentArtifactEnvelope:Enabled"] = "true",
                    ["IlWorker:DevelopmentArtifactEnvelope:MaxBytes"] = (16 * 1024 * 1024).ToString(CultureInfo.InvariantCulture),
                    ["IlWorker:LspLimits:DiagnosticsDebounceMilliseconds"] =
                        (diagnosticsDebounceMilliseconds ?? 1).ToString(CultureInfo.InvariantCulture),
                    ["ReferenceSets:net10-ref:Path"] = Path.Combine(root, "reference-sets", "net10-ref"),
                    ["ReferenceSets:net10-ref:TargetFramework"] = "net10.0",
                    ["ReferenceSets:net10-ref:FrameworkVersion"] = "10.0.9",
                    ["ReferenceSets:net11-preview-ref:Path"] = Path.Combine(root, "reference-sets", "net11-preview-ref"),
                    ["ReferenceSets:net11-preview-ref:TargetFramework"] = "net11.0",
                    ["ReferenceSets:net11-preview-ref:FrameworkVersion"] = "11.0.0-preview.5.26302.115"
                }));
            builder.ConfigureTestServices(services =>
            {
                var settings = IlTestSettings.Create(root);
                settings = settings with { DevelopmentArtifactEnvelope = new IlDevelopmentArtifactEnvelopeOptions(true, 16 * 1024 * 1024), LspLimits = settings.LspLimits with { DiagnosticsDebounceMilliseconds = diagnosticsDebounceMilliseconds ?? 1, MaxMessageBytes = maxMessageBytes ?? settings.LspLimits.MaxMessageBytes } };
                services.RemoveAll<IlWorkerSettings>();
                services.RemoveAll<IlWorkerIdentity>();
                services.RemoveAll<IlCompilationLimits>();
                services.RemoveAll<IlLspLimits>();
                services.RemoveAll<IlDevelopmentArtifactEnvelopeOptions>();
                services.RemoveAll<IlReferenceSetProvider>();
                services.AddSingleton(settings);
                services.AddSingleton(settings.Identity);
                services.AddSingleton(settings.CompilationLimits);
                services.AddSingleton(settings.LspLimits);
                services.AddSingleton(settings.DevelopmentArtifactEnvelope);
                services.AddSingleton(new IlReferenceSetProvider(settings.ReferenceSets));
            });
        }
    }

    private static OpenLanguageSessionRequest CreateOpenRequest(string source) => CreateOpenRequest([new WorkspaceFile("Program.il", 1, source)], ["Program.il"]);

    private static OpenLanguageSessionRequest CreateOpenRequest(IReadOnlyList<WorkspaceFile> files, IReadOnlyList<string> sourceOrder)
    {
        var request = IlTestSettings.CreateRequest(BuildTarget.CompileCheck, files, sourceOrder);
        return new OpenLanguageSessionRequest("request-http-lsp", "pipeline-http-lsp", "il", "mobius-ilasm-stable", "net10-ref", request.Workspace);
    }

    private static bool HasId(JsonElement root, int expected) => root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == expected;

    private static Task SendAsync(WebSocket socket, object payload)
    {
        var bytes = SerializeLspMessage(payload);
        return socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);
    }

    private static byte[] SerializeLspMessage(object payload) => JsonSerializer.SerializeToUtf8Bytes(payload, LspJsonOptions);

    private static async Task InitializeAsync(WebSocket socket)
    {
        await SendAsync(socket, new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { capabilities = new { } } });
        using var response = await ReceiveUntilAsync(socket, static message => HasId(message, 1));
        Assert.True(response.RootElement.TryGetProperty("result", out _));
        await SendAsync(socket, new { jsonrpc = "2.0", method = "initialized", @params = new { } });
    }

    private static Task SendDidOpenAsync(WebSocket socket, string uri, string text) =>
        SendAsync(socket, new { jsonrpc = "2.0", method = "textDocument/didOpen", @params = new { textDocument = new { uri, languageId = "il", version = 1, text } } });

    private static Task SendDidChangeAsync(WebSocket socket, string uri, long version, string text) =>
        SendAsync(socket, new { jsonrpc = "2.0", method = "textDocument/didChange", @params = new { textDocument = new { uri, version }, contentChanges = new[] { new { text } } } });

    private static bool IsDiagnosticsFor(JsonElement message, string uri, out JsonElement parameters)
    {
        if (message.TryGetProperty("method", out var method) && method.GetString() == "textDocument/publishDiagnostics" && message.TryGetProperty("params", out parameters) && parameters.GetProperty("uri").GetString() == uri)
        {
            return true;
        }
        parameters = default;
        return false;
    }

    private static async Task<JsonDocument> ReceiveUntilAsync(WebSocket socket, Func<JsonElement, bool> predicate)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var document = await ReceiveMessageAsync(socket);
            if (predicate(document.RootElement))
                return document;
            document.Dispose();
        }
        throw new TimeoutException("The expected IL LSP message was not received.");
    }

    private static async Task<JsonDocument> ReceiveUntilWithoutDiagnosticsAsync(WebSocket socket, Func<JsonElement, bool> predicate)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var document = await ReceiveMessageAsync(socket);
            Assert.False(document.RootElement.TryGetProperty("method", out var method) && method.GetString() == "textDocument/publishDiagnostics", "A read-only IL result session published diagnostics without didOpen.");
            if (predicate(document.RootElement))
                return document;
            document.Dispose();
        }
        throw new TimeoutException("The expected IL LSP message was not received.");
    }

    private static async Task<JsonDocument> ReceiveMessageAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), TestContext.Current.CancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("The IL LSP WebSocket closed before the expected response.");
            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return JsonDocument.Parse(stream.ToArray());
    }
}
