using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.Roslyn.Stable.Tests;

public sealed class LspWebSocketEndpointTests : IClassFixture<RoslynStableWorkerFactory>
{
    private readonly RoslynStableWorkerFactory _factory;

    public LspWebSocketEndpointTests(RoslynStableWorkerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task EndpointUsesStandardJsonRpcAndPublishesVersionedDiagnostics()
    {
        using var http = _factory.CreateClient();
        using var openResponse = await http.PostAsJsonAsync(
            "/api/v1/language-sessions",
            LanguageSessionTests.CreateOpenRequest("websocket", "int value = 1;"),
            ContractJson.CreateSerializerOptions(),
            TestContext.Current.CancellationToken);
        openResponse.EnsureSuccessStatusCode();
        var session = await openResponse.Content.ReadFromJsonAsync<LanguageSession>(
            ContractJson.CreateSerializerOptions(),
            TestContext.Current.CancellationToken);
        Assert.NotNull(session);

        var webSocketClient = _factory.Server.CreateWebSocketClient();
        using var socket = await webSocketClient.ConnectAsync(
            new Uri($"ws://localhost/api/v1/language-sessions/{session.SessionId}/lsp"),
            TestContext.Current.CancellationToken);
        await SendAsync(socket, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                processId = (int?)null,
                capabilities = new { },
                rootUri = (string?)null
            }
        });

        using var initialize = await ReceiveAsync(socket);
        Assert.Equal(1, initialize.RootElement.GetProperty("id").GetInt32());
        var capabilities = initialize.RootElement.GetProperty("result").GetProperty("capabilities");
        Assert.True(capabilities.GetProperty("hoverProvider").GetBoolean());
        Assert.True(capabilities.GetProperty("completionProvider").GetProperty("resolveProvider").GetBoolean());

        await SendAsync(socket, new
        {
            jsonrpc = "2.0",
            method = "textDocument/didOpen",
            @params = new
            {
                textDocument = new
                {
                    uri = "file:///Program.cs",
                    languageId = "csharp",
                    version = 2,
                    text = "int value = \"bad\";"
                }
            }
        });

        using var diagnostics = await ReceiveUntilMethodAsync(socket, "textDocument/publishDiagnostics");
        var diagnosticParams = diagnostics.RootElement.GetProperty("params");
        Assert.Equal(2, diagnosticParams.GetProperty("version").GetInt64());
        Assert.Contains(
            diagnosticParams.GetProperty("diagnostics").EnumerateArray(),
            static diagnostic => diagnostic.GetProperty("code").GetString() == "CS0029");
        var data = diagnosticParams.GetProperty("diagnostics")[0].GetProperty("data");
        Assert.Equal(2, data.GetProperty("documentVersion").GetInt64());

        await SendAsync(socket, new
        {
            jsonrpc = "2.0",
            method = "textDocument/didChange",
            @params = new
            {
                textDocument = new { uri = "file:///Program.cs", version = 3 },
                contentChanges = new[] { new { text = "System.Console." } }
            }
        });
        await SendAsync(socket, new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "textDocument/completion",
            @params = new
            {
                textDocument = new { uri = "file:///Program.cs" },
                position = new { line = 0, character = 15 },
                context = new { triggerKind = 1, triggerCharacter = (string?)null }
            }
        });

        using var completion = await ReceiveUntilIdAsync(socket, 3);
        Assert.Contains(
            completion.RootElement.GetProperty("result").GetProperty("items").EnumerateArray(),
            static item => item.GetProperty("label").GetString() == "WriteLine");

        await SendAsync(socket, new { jsonrpc = "2.0", id = 4, method = "shutdown", @params = new { } });
        using var shutdown = await ReceiveUntilIdAsync(socket, 4);
        Assert.Equal(JsonValueKind.Null, shutdown.RootElement.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task VisualBasicEndpointPublishesDiagnosticsCompletesAndClosesCleanly()
    {
        using var http = _factory.CreateClient();
        using var openResponse = await http.PostAsJsonAsync(
            "/api/v1/language-sessions",
            LanguageSessionTests.CreateVisualBasicOpenRequest(
                "websocket-vb",
                "Module Program\n    Sub Main()\n    End Sub\nEnd Module"),
            ContractJson.CreateSerializerOptions(),
            TestContext.Current.CancellationToken);
        openResponse.EnsureSuccessStatusCode();
        var session = await openResponse.Content.ReadFromJsonAsync<LanguageSession>(
            ContractJson.CreateSerializerOptions(),
            TestContext.Current.CancellationToken);
        Assert.NotNull(session);
        Assert.Equal("visual-basic", session.LanguageId);

        var webSocketClient = _factory.Server.CreateWebSocketClient();
        using var socket = await webSocketClient.ConnectAsync(
            new Uri($"ws://localhost/api/v1/language-sessions/{session.SessionId}/lsp"),
            TestContext.Current.CancellationToken);
        await SendAsync(socket, new
        {
            jsonrpc = "2.0",
            id = 11,
            method = "initialize",
            @params = new
            {
                processId = (int?)null,
                capabilities = new { },
                rootUri = (string?)null
            }
        });
        using var initialize = await ReceiveUntilIdAsync(socket, 11);
        Assert.True(initialize.RootElement
            .GetProperty("result")
            .GetProperty("capabilities")
            .GetProperty("completionProvider")
            .GetProperty("resolveProvider")
            .GetBoolean());

        await SendAsync(socket, new
        {
            jsonrpc = "2.0",
            method = "textDocument/didOpen",
            @params = new
            {
                textDocument = new
                {
                    uri = "file:///Program.vb",
                    languageId = "visual-basic",
                    version = 2,
                    text = "Module Program\n    Sub Main()\n        Dim value As Integer = \"bad\"\n    End Sub\nEnd Module"
                }
            }
        });
        using var diagnostics = await ReceiveUntilMethodAsync(socket, "textDocument/publishDiagnostics");
        var diagnosticParams = diagnostics.RootElement.GetProperty("params");
        Assert.Equal(2, diagnosticParams.GetProperty("version").GetInt64());
        Assert.Contains(
            diagnosticParams.GetProperty("diagnostics").EnumerateArray(),
            static diagnostic => diagnostic.GetProperty("code").GetString()!.StartsWith("BC", StringComparison.Ordinal));

        await SendAsync(socket, new
        {
            jsonrpc = "2.0",
            method = "textDocument/didChange",
            @params = new
            {
                textDocument = new { uri = "file:///Program.vb", version = 3 },
                contentChanges = new[]
                {
                    new { text = "Imports System\nPublic Class Demo\n    Public Sub Run()\n        Console.\n" }
                }
            }
        });
        await SendAsync(socket, new
        {
            jsonrpc = "2.0",
            id = 13,
            method = "textDocument/completion",
            @params = new
            {
                textDocument = new { uri = "file:///Program.vb" },
                position = new { line = 3, character = 16 },
                context = new { triggerKind = 1, triggerCharacter = (string?)null }
            }
        });
        using var completion = await ReceiveUntilIdAsync(socket, 13);
        Assert.Contains(
            completion.RootElement.GetProperty("result").GetProperty("items").EnumerateArray(),
            static item => item.GetProperty("label").GetString() == "WriteLine");

        await SendAsync(socket, new { jsonrpc = "2.0", id = 14, method = "shutdown", @params = new { } });
        using var shutdown = await ReceiveUntilIdAsync(socket, 14);
        Assert.Equal(JsonValueKind.Null, shutdown.RootElement.GetProperty("result").ValueKind);
        await SendAsync(socket, new { jsonrpc = "2.0", method = "exit", @params = new { } });
        await ReceiveCloseAsync(socket);

        using var closeResponse = await http.DeleteAsync(
            $"/api/v1/language-sessions/{session.SessionId}",
            TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, closeResponse.StatusCode);
    }

    private static async Task SendAsync(WebSocket socket, object message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, ContractJson.CreateLspSerializerOptions());
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonDocument> ReceiveUntilMethodAsync(WebSocket socket, string method)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var message = await ReceiveAsync(socket);
            if (message.RootElement.TryGetProperty("method", out var actualMethod) && actualMethod.GetString() == method)
                return message;
            message.Dispose();
        }

        throw new InvalidOperationException($"JSON-RPC notification '{method}' was not received.");
    }

    private static async Task<JsonDocument> ReceiveUntilIdAsync(WebSocket socket, int id)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var message = await ReceiveAsync(socket);
            if (message.RootElement.TryGetProperty("id", out var actualId) &&
                actualId.ValueKind == JsonValueKind.Number &&
                actualId.GetInt32() == id)
            {
                return message;
            }

            message.Dispose();
        }

        throw new InvalidOperationException($"JSON-RPC response '{id}' was not received.");
    }

    private static async Task<JsonDocument> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[16 * 1024];
        using var content = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, TestContext.Current.CancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("The LSP socket closed before a JSON-RPC message was received.");
            content.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return JsonDocument.Parse(content.ToArray());
        }
    }

    private static async Task ReceiveCloseAsync(WebSocket socket)
    {
        var buffer = new byte[256];
        try
        {
            var result = await socket.ReceiveAsync(buffer, TestContext.Current.CancellationToken);
            Assert.Equal(WebSocketMessageType.Close, result.MessageType);
            Assert.Equal(WebSocketCloseStatus.NormalClosure, result.CloseStatus);
            if (socket.State == WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Test completed.",
                    TestContext.Current.CancellationToken);
            }
        }
        catch (IOException)
        {
            // TestServer disposes its in-memory socket after the server close handshake.
        }
    }
}
