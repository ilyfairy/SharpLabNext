using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpLabNext.Catalog;
using SharpLabNext.Contracts;
using SharpLabNext.Gateway;
using SharpLabNext.Operations;
using SharpLabNext.Worker.Client;

namespace SharpLabNext.IntegrationTests;

public sealed class GatewayTests : IClassFixture<GatewayTestFactory>
{
    private readonly HttpClient _client;
    private readonly GatewayTestFactory _factory;

    public GatewayTests(GatewayTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task HealthCatalogAndFrontendAreServed()
    {
        var health = await _client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        var catalog = await _client.GetAsync("/api/v1/catalog", TestContext.Current.CancellationToken);
        var frontend = await _client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        Assert.Equal(HttpStatusCode.OK, frontend.StatusCode);
        Assert.Equal("text/html", frontend.Content.Headers.ContentType?.MediaType);
        Assert.Contains("SharpLabNext", await frontend.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);

        using var catalogJson = JsonDocument.Parse(await catalog.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
        var firstCompatibilityRule = catalogJson.RootElement.GetProperty("Compatibility").EnumerateArray().First();
        Assert.Equal(JsonValueKind.String, firstCompatibilityRule.GetProperty("Kind").ValueKind);
        Assert.Equal("toolchain-reference-set", firstCompatibilityRule.GetProperty("Kind").GetString());
    }

    [Fact]
    public async Task SelectionCanBeResolvedAndAstBuildObserved()
    {
        var catalog = await _client.GetFromJsonAsync<CatalogDocument>("/api/v1/catalog", ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        Assert.NotNull(catalog);
        var request = new ResolveSelectionRequest("csharp", "roslyn-stable", "net10-ref", "ast", null, BuildConfiguration.Release, catalog.Revision, 1);
        var resolveResponse = await _client.PostAsJsonAsync("/api/v1/selections/resolve", request, ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        resolveResponse.EnsureSuccessStatusCode();
        var resolution = await resolveResponse.Content.ReadFromJsonAsync<ResolveSelectionResponse>(ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        Assert.NotNull(resolution);

        var workspace = new WorkspaceSnapshot(1, 1, 1, "csharp", [new WorkspaceFile("Program.cs", 1, "System.Console.WriteLine(42);")], "Program.cs", ["Program.cs"], "net10-ref", new BuildOptions(BuildConfiguration.Release, true, BuildOutputKind.Console, false, true));
        var buildRequest = new BuildRequest("req-integration-build", "key-integration-build", resolution.PipelineResolutionId, "roslyn-stable", "net10-ref", workspace, DateTimeOffset.UtcNow.AddSeconds(10), Target: BuildTarget.Ast);
        var buildResponse = await _client.PostAsJsonAsync("/api/v1/builds", buildRequest, ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, buildResponse.StatusCode);
        var handle = await buildResponse.Content.ReadFromJsonAsync<OperationHandle>(ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        Assert.NotNull(handle);

        OperationState? state = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            state = await _client.GetFromJsonAsync<OperationState>($"/api/v1/operations/{handle.OperationId}", ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
            if (state?.Status == OperationStatus.Completed)
                break;

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.Equal(OperationStatus.Completed, state?.Status);
        Assert.Equal(4, state?.LastSequence);
    }

    [Fact]
    public async Task OperationEventsWebSocketResumesFromSequenceAndClosesAtTerminalEvent()
    {
        var operationId = CreateCompletedOperation();
        var webSocketClient = _factory.Server.CreateWebSocketClient();
        using var socket = await webSocketClient.ConnectAsync(
            new Uri($"ws://localhost/api/v1/operations/{operationId}/events?FromSequence=1"),
            TestContext.Current.CancellationToken);

        var progress = await ReceiveOperationEventAsync(socket);
        var completed = await ReceiveOperationEventAsync(socket);

        Assert.Equal(2, progress.Sequence);
        Assert.IsType<ProgressOperationEventPayload>(progress.Payload);
        Assert.Equal(3, completed.Sequence);
        Assert.IsType<CompletedOperationEventPayload>(completed.Payload);

        var close = await socket.ReceiveAsync(new byte[256], TestContext.Current.CancellationToken);
        Assert.Equal(WebSocketMessageType.Close, close.MessageType);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, close.CloseStatus);
        if (socket.State == WebSocketState.CloseReceived)
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Operation event stream verified.", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OperationCommandWebSocketControlsAndStreamsAnOperationOnOneSession()
    {
        var catalog = await _client.GetFromJsonAsync<CatalogDocument>("/api/v1/catalog", ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        Assert.NotNull(catalog);
        var selectionRequest = new ResolveSelectionRequest("csharp", "roslyn-stable", "net10-ref", "ast", null, BuildConfiguration.Release, catalog.Revision, 73);

        var webSocketClient = _factory.Server.CreateWebSocketClient();
        using var socket = await webSocketClient.ConnectAsync(
            new Uri("ws://localhost/api/v1/operations/ws"),
            TestContext.Current.CancellationToken);
        await SendWebSocketJsonAsync(socket, new { type = "resolve-selection", commandId = "resolve-1", request = selectionRequest });
        using var resolved = await ReceiveWebSocketJsonAsync(socket);
        Assert.Equal("resolve-1", resolved.RootElement.GetProperty("CommandId").GetString());
        Assert.True(resolved.RootElement.GetProperty("Ok").GetBoolean());
        Assert.Equal(StatusCodes.Status200OK, resolved.RootElement.GetProperty("Status").GetInt32());
        var resolution = resolved.RootElement.GetProperty("Payload").Deserialize<ResolveSelectionResponse>(ContractJson.CreateSerializerOptions());
        Assert.NotNull(resolution);

        var request = new BuildRequest($"ws-start-{Guid.NewGuid():N}", $"ws-start-key-{Guid.NewGuid():N}", resolution.PipelineResolutionId, "roslyn-stable", "net10-ref", new WorkspaceSnapshot(ContractSchemaVersions.WorkspaceSnapshot, 73, 1, "csharp", [new WorkspaceFile("Program.cs", 1, "System.Console.WriteLine(42);")], "Program.cs", ["Program.cs"], "net10-ref", new BuildOptions(BuildConfiguration.Release, true, BuildOutputKind.Console, false, true)), DateTimeOffset.UtcNow.AddSeconds(10), Target: BuildTarget.Ast);

        await SendWebSocketJsonAsync(socket, new { type = "start", commandId = "start-1", operation = "build", request });
        using var start = await ReceiveWebSocketJsonAsync(socket);
        Assert.Equal("response", start.RootElement.GetProperty("Type").GetString());
        Assert.Equal("start-1", start.RootElement.GetProperty("CommandId").GetString());
        Assert.True(start.RootElement.GetProperty("Ok").GetBoolean());
        Assert.Equal(StatusCodes.Status202Accepted, start.RootElement.GetProperty("Status").GetInt32());
        var handle = start.RootElement.GetProperty("Payload").Deserialize<OperationHandle>(ContractJson.CreateSerializerOptions());
        Assert.NotNull(handle);

        await SendWebSocketJsonAsync(socket, new { type = "state", commandId = "state-1", operationId = handle.OperationId });
        using var state = await ReceiveWebSocketJsonAsync(socket);
        Assert.Equal("state-1", state.RootElement.GetProperty("CommandId").GetString());
        Assert.Equal(handle.OperationId, state.RootElement.GetProperty("Payload").GetProperty("OperationId").GetString());

        await SendWebSocketJsonAsync(socket, new { type = "subscribe", commandId = "subscribe-1", operationId = handle.OperationId, fromSequence = 0 });
        using var subscribed = await ReceiveWebSocketJsonAsync(socket);
        Assert.Equal("subscribe-1", subscribed.RootElement.GetProperty("CommandId").GetString());
        Assert.True(subscribed.RootElement.GetProperty("Ok").GetBoolean());

        var sequences = new List<long>();
        while (true)
        {
            using var message = await ReceiveWebSocketJsonAsync(socket);
            Assert.Equal("event", message.RootElement.GetProperty("Type").GetString());
            var operationEvent = message.RootElement.GetProperty("Event").Deserialize<OperationEvent>(ContractJson.CreateSerializerOptions());
            Assert.NotNull(operationEvent);
            sequences.Add(operationEvent.Sequence);
            if (operationEvent.Payload.IsTerminal)
                break;
        }
        Assert.Equal(Enumerable.Range(1, sequences.Count).Select(static value => (long)value), sequences);

        await SendWebSocketJsonAsync(socket, new { type = "cancel", commandId = "cancel-1", operationId = handle.OperationId, reason = "test" });
        using var cancel = await ReceiveWebSocketJsonAsync(socket);
        Assert.Equal("cancel-1", cancel.RootElement.GetProperty("CommandId").GetString());
        Assert.Equal("already-terminal", cancel.RootElement.GetProperty("Payload").GetProperty("Disposition").GetString());
    }

    [Fact]
    public async Task OperationEventsRetainSseCompatibilityAndResumeSemantics()
    {
        var operationId = CreateCompletedOperation();

        using var response = await _client.GetAsync($"/api/v1/operations/{operationId}/events?FromSequence=1", HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var events = body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(static line => line.StartsWith("data: ", StringComparison.Ordinal))
            .Select(static line => JsonSerializer.Deserialize<OperationEvent>(line.AsSpan("data: ".Length), ContractJson.CreateSerializerOptions()) ?? throw new InvalidOperationException("Operation event was empty.")).ToArray();

        Assert.Equal([2L, 3L], events.Select(static operationEvent => operationEvent.Sequence));
        Assert.IsType<CompletedOperationEventPayload>(events[^1].Payload);
    }

    [Theory]
    [InlineData("csharp", "roslyn-main", "net11-preview-ref", "ast", BuildTarget.Ast, "roslyn-main", "Program.cs", "System.Console.WriteLine(42);")]
    [InlineData("visual-basic", "roslyn-main", "net11-preview-ref", "ast", BuildTarget.Ast, "roslyn-main", "Program.vb", "Module Program\nEnd Module")]
    [InlineData("fsharp", "fsharp-stable", "net10-ref", "compile-check", BuildTarget.CompileCheck, "fsharp-stable", "Program.fs", "printfn \"hello\"")]
    [InlineData("gsharp", "gsharp-stable", "net10-ref", "compile-check", BuildTarget.CompileCheck, "gsharp-stable", "Program.gs", "package Test\n\nlet answer = 42")]
    [InlineData("il", "mobius-ilasm-stable", "net10-ref", "compile-check", BuildTarget.CompileCheck, "mobius-ilasm-stable", "Program.il", ".assembly Test {}")]
    public async Task BuildUsesCompilerWorkerSelectedByServerPipeline(string languageId, string toolchainId, string referenceSetId, string outputId, BuildTarget target, string expectedWorkerId, string fileName, string source)
    {
        _factory.WorkerFactory.Clear();
        var catalog = await _client.GetFromJsonAsync<CatalogDocument>("/api/v1/catalog", ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        Assert.NotNull(catalog);
        using var resolveResponse = await _client.PostAsJsonAsync("/api/v1/selections/resolve", new ResolveSelectionRequest(languageId, toolchainId, referenceSetId, outputId, null, BuildConfiguration.Release, catalog.Revision, 40), ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        resolveResponse.EnsureSuccessStatusCode();
        var resolution = await resolveResponse.Content.ReadFromJsonAsync<ResolveSelectionResponse>(ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        Assert.NotNull(resolution);
        var options = new BuildOptions(BuildConfiguration.Release, true, BuildOutputKind.Console, false, true);
        var workspace = new WorkspaceSnapshot(ContractSchemaVersions.WorkspaceSnapshot, 40, 4, languageId, [new WorkspaceFile(fileName, 1, source)], fileName, [fileName], referenceSetId, options);
        using var start = await _client.PostAsJsonAsync("/api/v1/builds", new BuildRequest($"route-{languageId}-{Guid.NewGuid():N}", $"route-key-{Guid.NewGuid():N}", resolution.PipelineResolutionId, toolchainId, referenceSetId, workspace, DateTimeOffset.UtcNow.AddSeconds(10), Target: target), ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var handle = await start.Content.ReadFromJsonAsync<OperationHandle>(ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        Assert.NotNull(handle);

        OperationState? terminalState = null;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var state = await _client.GetFromJsonAsync<OperationState>($"/api/v1/operations/{handle.OperationId}", ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
            if (state?.Status is OperationStatus.Completed or OperationStatus.Failed)
            {
                terminalState = state;
                break;
            }
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.Equal(OperationStatus.Completed, terminalState?.Status);
        Assert.Contains(expectedWorkerId, _factory.WorkerFactory.CreatedWorkerIds);
    }

    [Fact]
    public async Task BrowserCannotSupplyAnUpstreamLanguageWorkerAddress()
    {
        using var content = new StringContent(
            """{"requestId":"request","upstreamUrl":"http://169.254.169.254"}""",
            Encoding.UTF8,
            "application/json");

        using var response = await _client.PostAsync("/api/v1/language-sessions", content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GatewayRejectsOversizedLanguageWorkspacesBeforeResolutionOrWorkerAccess()
    {
        var files = Enumerable.Range(0, 33).Select(index => new WorkspaceFile($"File{index}.cs", 1, "class C { }")).ToArray();
        var request = new OpenLanguageSessionRequest("oversized-language-session", "browser-controlled-resolution", "csharp", "roslyn-stable", "net10-ref", new WorkspaceSnapshot(ContractSchemaVersions.WorkspaceSnapshot, 1, 1, "csharp", files, files[0].Path, files.Select(static file => file.Path).ToArray(), "net10-ref", new BuildOptions(BuildConfiguration.Release, true, BuildOutputKind.Console, false, true)));

        using var response = await _client.PostAsJsonAsync("/api/v1/language-sessions", request, ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        Assert.Equal("language-session-source-limit", problem.GetProperty("Error").GetString());
    }

    [Fact]
    public async Task ExplainSelectionRunsAsAStandaloneStructuredOperation()
    {
        var catalog = await _client.GetFromJsonAsync<CatalogDocument>("/api/v1/catalog", ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        Assert.NotNull(catalog);
        var resolutionResponse = await _client.PostAsJsonAsync("/api/v1/selections/resolve", new ResolveSelectionRequest("csharp", "roslyn-stable", "net10-ref", "explain", null, BuildConfiguration.Release, catalog.Revision, 21), ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        resolutionResponse.EnsureSuccessStatusCode();
        var resolution = await resolutionResponse.Content.ReadFromJsonAsync<ResolveSelectionResponse>(ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        Assert.NotNull(resolution);
        var workspace = new WorkspaceSnapshot(ContractSchemaVersions.WorkspaceSnapshot, 21, 8, "csharp", [new WorkspaceFile("Program.cs", 1, "class Program { }")], "Program.cs", ["Program.cs"], "net10-ref", new BuildOptions(BuildConfiguration.Release, true, BuildOutputKind.Console, false, true));
        using var start = await _client.PostAsJsonAsync("/api/v1/explanations", new ExplainRequest("gateway-explain", "gateway-explain-key", resolution.PipelineResolutionId, workspace, DateTimeOffset.UtcNow.AddSeconds(10)), ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var handle = await start.Content.ReadFromJsonAsync<OperationHandle>(ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        Assert.NotNull(handle);

        OperationState? state = null;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            state = await _client.GetFromJsonAsync<OperationState>($"/api/v1/operations/{handle.OperationId}", ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
            if (state?.Status is OperationStatus.Completed or OperationStatus.Failed)
                break;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        Assert.Equal(OperationStatus.Completed, state?.Status);
    }

    private string CreateCompletedOperation()
    {
        var store = _factory.Services.GetRequiredService<OperationStore>();
        var suffix = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var operation = store.Start($"gateway-events-{suffix}", $"gateway-events-key-{suffix}", OperationKind.Build, $"gateway-events-trace-{suffix}", now);
        store.Append(operation.Handle.OperationId, new ProgressOperationEventPayload("compile", "Compiling", 0.5), now.AddMilliseconds(1));
        store.Append(operation.Handle.OperationId, new CompletedOperationEventPayload(OperationCompletionStatus.Completed, TimeSpan.FromMilliseconds(2)), now.AddMilliseconds(2));
        return operation.Handle.OperationId;
    }

    private static async Task<OperationEvent> ReceiveOperationEventAsync(WebSocket socket)
    {
        var buffer = new byte[4 * 1024];
        using var content = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, TestContext.Current.CancellationToken);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            content.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
                continue;

            return JsonSerializer.Deserialize<OperationEvent>(content.ToArray(), ContractJson.CreateSerializerOptions()) ?? throw new InvalidOperationException("Operation WebSocket event was empty.");
        }
    }

    private static async Task SendWebSocketJsonAsync<T>(WebSocket socket, T value)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, ContractJson.CreateSerializerOptions());
        await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonDocument> ReceiveWebSocketJsonAsync(WebSocket socket)
    {
        using var content = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, TestContext.Current.CancellationToken);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            content.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return JsonDocument.Parse(content.ToArray());
        }
    }
}

public sealed class GatewayTestFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IToolchainWorkerClient>();
            services.RemoveAll<IBuildArtifactPublisher>();
            services.RemoveAll<IToolchainWorkerClientFactory>();
            services.AddSingleton<IToolchainWorkerClient, GatewayFakeWorkerClient>();
            services.AddSingleton<GatewayFakeWorkerClientFactory>();
            services.AddSingleton<IToolchainWorkerClientFactory>(services => services.GetRequiredService<GatewayFakeWorkerClientFactory>());
            services.AddSingleton<IBuildArtifactPublisher, GatewayRejectingArtifactPublisher>();
        });
    }

    internal GatewayFakeWorkerClientFactory WorkerFactory => Services.GetRequiredService<GatewayFakeWorkerClientFactory>();
}

internal sealed class GatewayFakeWorkerClientFactory(IToolchainWorkerClient worker) : IToolchainWorkerClientFactory
{
    private readonly ConcurrentQueue<string> _createdWorkerIds = new();

    public IReadOnlyCollection<string> CreatedWorkerIds => _createdWorkerIds.ToArray();

    public IToolchainWorkerClient Create(string workerId)
    {
        _createdWorkerIds.Enqueue(workerId);
        return worker;
    }

    public void Clear()
    {
        while (_createdWorkerIds.TryDequeue(out _)) { }
    }
}

internal sealed class GatewayFakeWorkerClient : IToolchainWorkerClient
{
    public Task<WorkerDescriptor> DescribeAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<ToolchainBuildResponse> BuildAsync(BuildRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OperationResult result = request.Target switch
        {
            BuildTarget.Ast => new AstResult(new AstDocument(request.Workspace.LanguageId, request.ToolchainId, request.Workspace.Revision, new AstNode("CompilationUnit", new TextRange(0, 0, 0, 0), null, new Dictionary<string, string?>(), []), false)),
            BuildTarget.CompileCheck => new CompilationCheckResult(true, [], Identity(request), request.Workspace.Revision, request.Workspace.SelectionRevision),
            _ => throw new NotSupportedException()
        };
        return Task.FromResult(new ToolchainBuildResponse(request.RequestId, result, null));
    }

    public Task<ToolchainExplainResponse> ExplainAsync(ExplainRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ToolchainExplainResponse(request.RequestId, new ExplainResult(new ExplanationDocument(request.Workspace.LanguageId, "roslyn-stable", request.Workspace.Revision, request.Workspace.SelectionRevision, request.Workspace.Files.Select(file => new ExplanationFile(file.Path, [new ExplanationNode("CompilationUnit", "Compilation unit", "The root syntax node for this C# source file.", new TextRange(0, 0, 0, Math.Min(file.Text.Length, 1)), 0)])).ToArray(), false))));
    }

    private static BuildIdentity Identity(BuildRequest request) => new("content", request.Workspace.LanguageId, request.ToolchainId, "test", null, request.ReferenceSetId, "test-worker");
}

internal sealed class GatewayRejectingArtifactPublisher : IBuildArtifactPublisher
{
    public Task<PublishedBuildArtifact> PublishAsync(WorkerArtifactEnvelope envelope, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<PublishedBuildArtifact> AcceptPublishedAsync(ArtifactRef artifactRef, BuildIdentity identity, CancellationToken cancellationToken) => throw new NotSupportedException();
}
