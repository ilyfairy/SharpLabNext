using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SharpLabNext.Catalog;
using SharpLabNext.Contracts;
using SharpLabNext.Gateway;

namespace SharpLabNext.IntegrationTests;

public sealed class GatewayLanguageSessionTests
{
    [Fact]
    public void LanguageWorkerEndpointsAreCatalogBoundAndConfigurationDriven()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:LanguageWorkers:roslyn-stable:BaseAddress"] = "http://roslyn.test:8080",
                ["Services:LanguageWorkers:fsharp-stable:BaseAddress"] = "http://fsharp.test:8080",
                ["Services:LanguageWorkers:gsharp-stable:BaseAddress"] = "http://gsharp.test:8080",
                ["Services:LanguageWorkers:mobius-ilasm-stable:BaseAddress"] = "http://il.test:8080"
            })
            .Build();

        var registry = LanguageWorkerEndpointRegistry.FromConfiguration(configuration, "release-test", ["roslyn-stable", "fsharp-stable", "gsharp-stable", "mobius-ilasm-stable"], defaultServiceToken: "shared-token");

        Assert.True(registry.TryGet("roslyn-stable", out var roslyn));
        Assert.Equal(new Uri("http://roslyn.test:8080"), roslyn!.BaseAddress);
        Assert.Equal("shared-token", roslyn.ServiceToken);
        Assert.True(registry.TryGet("fsharp-stable", out var fsharp));
        Assert.Equal(new Uri("http://fsharp.test:8080"), fsharp!.BaseAddress);
        Assert.True(registry.TryGet("gsharp-stable", out var gsharp));
        Assert.Equal(new Uri("http://gsharp.test:8080"), gsharp!.BaseAddress);
        Assert.True(registry.TryGet("mobius-ilasm-stable", out var il));
        Assert.Equal(new Uri("http://il.test:8080"), il!.BaseAddress);
        Assert.False(registry.TryGet("browser-supplied", out _));
    }

    [Fact]
    public void LanguageWorkerEndpointsRejectWorkersOutsideTheCatalog()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:LanguageWorkers:browser-supplied:BaseAddress"] = "http://169.254.169.254"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() => LanguageWorkerEndpointRegistry.FromConfiguration(configuration, "release-test", ["roslyn-stable"]));

        Assert.Contains("does not match a workerId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealGatewayProxiesCSharpAndVisualBasicAndEnforcesOneShotMessageLimits()
    {
        const string internalServiceToken = "shared-internal-service-token-for-language-tests";
        var catalog = await GatewayTestCatalog.LoadRepositoryAsync(TestContext.Current.CancellationToken);
        var workerEnvironment = new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["RoslynWorker__ReleaseId"] = catalog.ReleaseId
        };
        GatewayTestCatalog.AddRoslynStableReferenceSets(workerEnvironment, catalog);
        await using var worker = await DotNetWebServiceProcess.StartAsync("src/Workers/Roslyn.Stable/SharpLabNext.Worker.Roslyn.Stable/SharpLabNext.Worker.Roslyn.Stable.csproj", "/health/ready", workerEnvironment, TestContext.Current.CancellationToken, configuration: "Release", noBuild: true, internalServiceToken: internalServiceToken);
        await using var gateway = await DotNetWebServiceProcess.StartAsync(
            "src/Gateway/SharpLabNext.Gateway/SharpLabNext.Gateway.csproj",
            "/health/ready",
            new Dictionary<string, string?>
            {
                ["Services__RoslynStableWorker__BaseAddress"] = worker.HttpClient.BaseAddress!.AbsoluteUri,
                ["Services__LanguageWorkers__roslyn-stable__BaseAddress"] = worker.HttpClient.BaseAddress!.AbsoluteUri,
                ["DependencyHealth__Enabled"] = "false"
            },
            TestContext.Current.CancellationToken,
            configuration: "Release",
            noBuild: true,
            internalServiceToken: internalServiceToken);

        var gatewayCatalog = await GatewayTestCatalog.GetAsync(gateway.HttpClient);
        Assert.Equal(catalog.ReleaseId, gatewayCatalog.ReleaseId);

        await AssertLanguageAsync(gateway, gatewayCatalog, "csharp", "Program.cs", "class Program { static void Main() { } }", "using System;\nclass Demo\n{\n    void Run()\n    {\n        Console.\n    }\n}", completionLine: 5, completionCharacter: 16, expectedDiagnosticPrefix: "CS");
        await AssertLanguageAsync(gateway, gatewayCatalog, "visual-basic", "Program.vb", "Module Program\n    Sub Main()\n    End Sub\nEnd Module", "Imports System\nPublic Class Demo\n    Public Sub Run()\n        Console.\n", completionLine: 3, completionCharacter: 16, expectedDiagnosticPrefix: "BC");

        var limitedSession = await OpenSessionAsync(gateway.HttpClient, gatewayCatalog, "csharp", "Program.cs", "class Program { }");
        using (var socket = await ConnectAsync(gateway, limitedSession.WebSocketUrl))
        {
            await socket.SendAsync(new byte[1024 * 1024 + 1], WebSocketMessageType.Text, endOfMessage: true, TestContext.Current.CancellationToken);
            var result = await socket.ReceiveAsync(new byte[256], TestContext.Current.CancellationToken);
            Assert.Equal(WebSocketMessageType.Close, result.MessageType);
            Assert.Equal(WebSocketCloseStatus.MessageTooBig, result.CloseStatus);
            if (socket.State == WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "Limit verified.", TestContext.Current.CancellationToken);
        }

        await AssertSessionRemovedAsync(gateway.HttpClient, limitedSession.SessionId);

        var binarySession = await OpenSessionAsync(gateway.HttpClient, gatewayCatalog, "csharp", "Program.cs", "class Program { }");
        using (var socket = await ConnectAsync(gateway, binarySession.WebSocketUrl))
        {
            await socket.SendAsync(
                new byte[] { 0x01 },
                WebSocketMessageType.Binary,
                endOfMessage: true,
                TestContext.Current.CancellationToken);
            var result = await socket.ReceiveAsync(new byte[256], TestContext.Current.CancellationToken);
            Assert.Equal(WebSocketMessageType.Close, result.MessageType);
            Assert.Equal(WebSocketCloseStatus.InvalidMessageType, result.CloseStatus);
            if (socket.State == WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidMessageType, "Binary rejection verified.", TestContext.Current.CancellationToken);
        }

        await AssertSessionRemovedAsync(gateway.HttpClient, binarySession.SessionId);
    }

    private static async Task AssertLanguageAsync(DotNetWebServiceProcess gateway, CatalogDocument catalog, string languageId, string fileName, string initialSource, string completionSource, int completionLine, int completionCharacter, string expectedDiagnosticPrefix)
    {
        var session = await OpenSessionAsync(gateway.HttpClient, catalog, languageId, fileName, initialSource);
        Assert.Equal(languageId, session.LanguageId);
        Assert.Equal("roslyn-stable", session.ToolchainId);
        Assert.DoesNotContain("http", session.WebSocketUrl, StringComparison.OrdinalIgnoreCase);

        using var socket = await ConnectAsync(gateway, session.WebSocketUrl);
        await Assert.ThrowsAsync<WebSocketException>(async () =>
        {
            using var duplicate = await ConnectAsync(gateway, session.WebSocketUrl);
        });
        await SendAsync(socket, new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { processId = (int?)null, capabilities = new { }, rootUri = (string?)null } });
        using (var initialize = await ReceiveUntilIdAsync(socket, 1))
        {
            Assert.True(initialize.RootElement.GetProperty("result").GetProperty("capabilities").GetProperty("completionProvider").GetProperty("resolveProvider").GetBoolean());
        }

        var documentUri = $"sharplabnext://gateway-smoke/{fileName}";
        var diagnosticSource = languageId == "csharp"
            ? "class Demo { string Value = 42; }" : "Module Demo\n    Dim Value As Integer = \"bad\"\nEnd Module";
        await SendAsync(socket, new { jsonrpc = "2.0", method = "textDocument/didOpen", @params = new { textDocument = new { uri = documentUri, languageId, version = 2, text = diagnosticSource } } });
        using (var diagnostics = await ReceiveUntilMethodAsync(socket, "textDocument/publishDiagnostics"))
        {
            var parameters = diagnostics.RootElement.GetProperty("params");
            Assert.Equal(2, parameters.GetProperty("version").GetInt64());
            Assert.Contains(parameters.GetProperty("diagnostics").EnumerateArray(), diagnostic => diagnostic.GetProperty("code").GetString()!.StartsWith(expectedDiagnosticPrefix, StringComparison.Ordinal));
        }

        await SendAsync(socket, new { jsonrpc = "2.0", method = "textDocument/didChange", @params = new { textDocument = new { uri = documentUri, version = 3 }, contentChanges = new[] { new { text = completionSource } } } });
        await SendAsync(socket, new { jsonrpc = "2.0", id = 3, method = "textDocument/completion", @params = new { textDocument = new { uri = documentUri }, position = new { line = completionLine, character = completionCharacter }, context = new { triggerKind = 1, triggerCharacter = (string?)null } } });
        using (var completion = await ReceiveUntilIdAsync(socket, 3))
        {
            Assert.Contains(completion.RootElement.GetProperty("result").GetProperty("items").EnumerateArray(), static item => item.GetProperty("label").GetString() == "WriteLine");
        }

        await SendAsync(socket, new { jsonrpc = "2.0", id = 4, method = "shutdown", @params = new { } });
        using (var shutdown = await ReceiveUntilIdAsync(socket, 4))
        {
            Assert.Equal(JsonValueKind.Null, shutdown.RootElement.GetProperty("result").ValueKind);
        }
        await SendAsync(socket, new { jsonrpc = "2.0", method = "exit", @params = new { } });
        await ReceiveCloseAsync(socket);
        await AssertSessionRemovedAsync(gateway.HttpClient, session.SessionId);
    }

    private static async Task<GatewayLanguageSessionResponse> OpenSessionAsync(HttpClient client, CatalogDocument catalog, string languageId, string fileName, string source)
    {
        var resolve = new ResolveSelectionRequest(languageId, "roslyn-stable", "net10-ref", "ast", null, BuildConfiguration.Release, catalog.Revision, WorkspaceRevision: 10);
        using var resolveResponse = await client.PostAsJsonAsync("/api/v1/selections/resolve", resolve, ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        resolveResponse.EnsureSuccessStatusCode();
        var resolution = await resolveResponse.Content.ReadFromJsonAsync<ResolveSelectionResponse>(ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        Assert.NotNull(resolution);

        var workspace = new WorkspaceSnapshot(ContractSchemaVersions.WorkspaceSnapshot, Revision: 10, SelectionRevision: 20, languageId, [new WorkspaceFile(fileName, 1, source)], fileName, [fileName], "net10-ref", new BuildOptions(BuildConfiguration.Release, true, BuildOutputKind.Console, false, true));
        var request = new OpenLanguageSessionRequest($"gateway-lsp-{languageId}-{Guid.NewGuid():N}", resolution.PipelineResolutionId, languageId, "roslyn-stable", "net10-ref", workspace);
        using var response = await client.PostAsJsonAsync("/api/v1/language-sessions", request, ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GatewayLanguageSessionResponse>(ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken) ?? throw new InvalidOperationException("Gateway language session response was empty.");
    }

    private static async Task<ClientWebSocket> ConnectAsync(DotNetWebServiceProcess gateway, string path)
    {
        var uri = new UriBuilder(new Uri(gateway.HttpClient.BaseAddress!, path))
        {
            Scheme = "ws"
        }.Uri;
        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(uri, TestContext.Current.CancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task SendAsync(WebSocket socket, object message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, ContractJson.CreateLspSerializerOptions());
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonDocument> ReceiveUntilMethodAsync(WebSocket socket, string method)
    {
        for (var attempt = 0; attempt < 20; attempt++)
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
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var message = await ReceiveAsync(socket);
            if (message.RootElement.TryGetProperty("id", out var actualId) && actualId.ValueKind == JsonValueKind.Number && actualId.GetInt32() == id)
                return message;

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
                throw new WebSocketException("The Gateway LSP socket closed before a JSON-RPC message was received.");
            content.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return JsonDocument.Parse(content.ToArray());
        }
    }

    private static async Task ReceiveCloseAsync(WebSocket socket)
    {
        var result = await socket.ReceiveAsync(new byte[256], TestContext.Current.CancellationToken);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, result.CloseStatus);
        if (socket.State == WebSocketState.CloseReceived)
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Gateway smoke complete.", TestContext.Current.CancellationToken);
    }

    private static async Task AssertSessionRemovedAsync(HttpClient client, string sessionId)
    {
        await Task.Delay(250, TestContext.Current.CancellationToken);
        using var response = await client.DeleteAsync($"/api/v1/language-sessions/{Uri.EscapeDataString(sessionId)}", TestContext.Current.CancellationToken);
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NoContent, HttpStatusCode.NotFound });
    }

}
