using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.Roslyn.Main.Tests;

public sealed class RoslynMainLspTests
{
    [Theory]
    [InlineData("csharp", "Program.cs", "System.Console.", 0, 15, "WriteLine", null, null)]
    [InlineData("visual-basic", "Program.vb", "Imports System\nModule Program\n Sub Main()\n  Console.\n End Sub\nEnd Module", 3, 10, "WriteLine", null, null)]
    [InlineData("csharp", "Program.cs", "using System;\n\nConsole.WriteLine(\"Hello\");\nwhi", 3, 3, "while", 1, null)]
    [InlineData("csharp", "Program.cs", "class Program\n{\n    void Run()\n    {\n        var task = System.Threading.Tasks.Task.CompletedTask;\n        task.await\n    }\n}", 5, 18, "await", null, "await task")]
    public async Task WebSocketLspCompletesCSharpAndVisualBasicWithMainCompiler(
        string languageId,
        string fileName,
        string source,
        int line,
        int character,
        string expectedLabel,
        int? maxCompletionItems,
        string? expectedPrimaryNewText)
    {
        await using var factory = new RoslynMainWorkerFactory("Development", maxCompletionItems);
        using var http = factory.CreateClient();
        using var openResponse = await http.PostAsJsonAsync(
            "/api/v1/language-sessions",
            CreateOpenRequest(languageId, fileName, source),
            ContractJson.CreateSerializerOptions(),
            TestContext.Current.CancellationToken);
        var openBody = await openResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(openResponse.IsSuccessStatusCode, openBody);
        var session = await openResponse.Content.ReadFromJsonAsync<LanguageSession>(
            ContractJson.CreateSerializerOptions(),
            TestContext.Current.CancellationToken);
        Assert.NotNull(session);
        Assert.Equal("roslyn-main/5.10.0", session.CompilerBuildIdentity);

        var webSocketClient = factory.Server.CreateWebSocketClient();
        using var socket = await webSocketClient.ConnectAsync(
            new Uri($"ws://localhost/api/v1/language-sessions/{session.SessionId}/lsp"),
            TestContext.Current.CancellationToken);
        await SendAsync(socket, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new { processId = (int?)null, capabilities = new { }, rootUri = (string?)null }
        });
        using var initialize = await ReceiveUntilIdAsync(socket, 1);
        Assert.True(initialize.RootElement
            .GetProperty("result")
            .GetProperty("capabilities")
            .GetProperty("completionProvider")
            .GetProperty("resolveProvider")
            .GetBoolean());

        var uri = $"file:///{fileName}";
        await SendAsync(socket, new
        {
            jsonrpc = "2.0",
            method = "textDocument/didOpen",
            @params = new
            {
                textDocument = new { uri, languageId, version = 2, text = source }
            }
        });
        await SendAsync(socket, new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "textDocument/completion",
            @params = new
            {
                textDocument = new { uri },
                position = new { line, character },
                context = new { triggerKind = 1, triggerCharacter = (string?)null }
            }
        });

        using var completion = await ReceiveUntilIdAsync(socket, 2);
        Assert.Contains(
            completion.RootElement.GetProperty("result").GetProperty("items").EnumerateArray(),
            item => item.GetProperty("label").GetString() == expectedLabel);
        if (expectedPrimaryNewText is not null)
        {
            var item = completion.RootElement
                .GetProperty("result")
                .GetProperty("items")
                .EnumerateArray()
                .Single(item => item.GetProperty("label").GetString() == expectedLabel);
            var textEdit = item.GetProperty("textEdit");
            Assert.Equal(expectedPrimaryNewText, textEdit.GetProperty("newText").GetString());
            Assert.Equal(
                character - "task.await".Length,
                textEdit.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
            Assert.Equal(
                character,
                textEdit.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());
            Assert.DoesNotContain(
                item.GetProperty("additionalTextEdits").EnumerateArray(),
                static edit => edit.GetProperty("newText").GetString()?.Contains(
                    "await",
                    StringComparison.Ordinal) == true);
        }

        await SendAsync(socket, new { jsonrpc = "2.0", id = 3, method = "shutdown", @params = new { } });
        using var shutdown = await ReceiveUntilIdAsync(socket, 3);
        Assert.Equal(JsonValueKind.Null, shutdown.RootElement.GetProperty("result").ValueKind);
        await SendAsync(socket, new { jsonrpc = "2.0", method = "exit", @params = new { } });
    }

    private static OpenLanguageSessionRequest CreateOpenRequest(
        string languageId,
        string fileName,
        string source)
    {
        var options = new BuildOptions(
            BuildConfiguration.Release,
            Optimize: true,
            BuildOutputKind.Console,
            AllowUnsafe: false,
            EmitPortablePdb: true,
            languageId == "csharp" ? NullableContextMode.Enable : NullableContextMode.Disable,
            LanguageVersion: languageId == "csharp" ? "preview" : "latest");
        var workspace = new WorkspaceSnapshot(
            ContractSchemaVersions.WorkspaceSnapshot,
            Revision: 5,
            SelectionRevision: 3,
            languageId,
            [new WorkspaceFile(fileName, 1, source)],
            fileName,
            [fileName],
            "net11-preview-ref",
            options);
        return new OpenLanguageSessionRequest(
            $"main-lsp-{Guid.NewGuid():N}",
            "main-lsp-pipeline",
            languageId,
            "roslyn-main",
            "net11-preview-ref",
            workspace);
    }

    private static Task SendAsync(WebSocket socket, object message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, ContractJson.CreateLspSerializerOptions());
        return socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            TestContext.Current.CancellationToken);
    }

    private static async Task<JsonDocument> ReceiveUntilIdAsync(WebSocket socket, int expectedId)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var message = await ReceiveAsync(socket);
            if (message.RootElement.TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.Number &&
                id.GetInt32() == expectedId)
            {
                return message;
            }

            message.Dispose();
        }

        throw new InvalidOperationException($"JSON-RPC response '{expectedId}' was not received.");
    }

    private static async Task<JsonDocument> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                TestContext.Current.CancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("The LSP socket closed before the expected response arrived.");
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return JsonDocument.Parse(stream.ToArray());
        }
    }
}
