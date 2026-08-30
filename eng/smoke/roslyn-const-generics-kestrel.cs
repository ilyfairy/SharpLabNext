#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property EnableTrimAnalyzer=false
#:property EnableAotAnalyzer=false

using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

var baseAddress = args.Length > 0 ? new Uri(args[0], UriKind.Absolute) : new Uri("http://127.0.0.1:18084");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
using var http = new HttpClient { BaseAddress = baseAddress };
var jsonOptions = CreateBusinessJsonOptions();
var lspJsonOptions = CreateLspJsonOptions();

await EnsureSuccessAsync(await http.GetAsync("/health/ready", timeout.Token), "health");
var describe = await http.GetFromJsonAsync<JsonElement>("/api/v1/worker/describe", jsonOptions, timeout.Token);
Require(describe.GetProperty("Service").GetProperty("Id").GetString() == "roslyn-const-generics", "Worker identity is incorrect.");
var expectedCompilerCommit = describe.GetProperty("CompilerIdentity").GetProperty("CompilerCommit").GetString() ?? throw new InvalidOperationException("Worker describe returned no loaded compiler commit.");
Require(describe.GetProperty("Capabilities").EnumerateArray().Any(static capability => capability.GetProperty("Id").GetString() == "completion" && capability.GetProperty("Available").GetBoolean()), "Completion is not available.");

const string featureSource = """
    using System;

    public static class FixedValue<int Value>
    {
        public static int GetValue() => Value;
    }

    public static class Program
    {
        public static void Main() => Console.WriteLine(FixedValue<42>.GetValue());
    }
    """;
var options = new { configuration = "release", optimize = true, outputKind = "console", allowUnsafe = false, emitPortablePdb = true, nullableContext = "enable", languageVersion = "preview", preprocessorSymbols = Array.Empty<string>(), checkOverflow = false };
var workspace = CreateWorkspace(featureSource, options);

using (var compileCheck = await BuildAsync("compile-check", workspace, options))
{
    var result = compileCheck.RootElement.GetProperty("Result");
    Require(result.GetProperty("CompilationSucceeded").GetBoolean(), "Const-generics compile check failed.");
    Require(result.GetProperty("Identity").GetProperty("CompilerCommit").GetString() == expectedCompilerCommit, "Compile check did not use the compiler reported by worker describe.");
}

using (var artifact = await BuildAsync("artifact", workspace, options))
{
    var root = artifact.RootElement;
    Require(root.GetProperty("Result").GetProperty("Outcome").GetString() == "succeeded", "Artifact build failed.");
    var envelope = root.GetProperty("DevelopmentArtifact");
    var pe = Convert.FromBase64String(envelope.GetProperty("PeImageBase64").GetString()!);
    Require(pe.Length > 2 && pe[0] == (byte)'M' && pe[1] == (byte)'Z', "Artifact response did not contain a PE image.");
    var manifest = envelope.GetProperty("Manifest");
    Require(manifest.GetProperty("RuntimeRequirement").GetProperty("Family").GetString() == "coreclr-const-generics", "Artifact runtime family is incorrect.");
    Require(manifest.GetProperty("MetadataFeatureTags").EnumerateArray().Any(static tag => tag.GetString() == "metadata.const-generics.v1"), "Artifact metadata feature tag is missing.");
}

using (var ast = await BuildAsync("ast", workspace, options))
{
    var resultText = ast.RootElement.GetProperty("Result").GetRawText();
    Require(resultText.Contains("LiteralTypeArgument", StringComparison.Ordinal), "Const-generics AST did not expose LiteralTypeArgument.");
}

const string completionSource = "using System; class Demo { void Run() { Console. } }";
var completionWorkspace = CreateWorkspace(completionSource, options);
var openRequest = new { requestId = $"const-lsp-{Guid.NewGuid():N}", pipelineResolutionId = "const-smoke-pipeline", languageId = "csharp", toolchainId = "roslyn-const-generics", referenceSetId = "const-generics-ref", workspace = completionWorkspace };
var opened = await http.PostAsJsonAsync("/api/v1/language-sessions", openRequest, jsonOptions, timeout.Token);
await EnsureSuccessAsync(opened, "open language session");
using var openedJson = JsonDocument.Parse(await opened.Content.ReadAsByteArrayAsync(timeout.Token));
var sessionId = openedJson.RootElement.GetProperty("SessionId").GetString() ?? throw new InvalidOperationException("Language session returned no ID.");

using var socket = new ClientWebSocket();
var socketUri = new UriBuilder(baseAddress)
{
    Scheme = baseAddress.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
    Path = $"/api/v1/language-sessions/{sessionId}/lsp"
}.Uri;
await socket.ConnectAsync(socketUri, timeout.Token);
await SendAsync(socket, new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { processId = (int?)null, capabilities = new { }, rootUri = (string?)null } }, timeout.Token);
using (var initialized = await ReceiveUntilAsync(socket, static root => HasId(root, 1), timeout.Token))
{
    Require(initialized.RootElement.GetProperty("result").GetProperty("capabilities").GetProperty("completionProvider").GetProperty("resolveProvider").GetBoolean(), "LSP completion was not advertised.");
}

const string uri = "file:///Program.cs";
await SendAsync(socket, new { jsonrpc = "2.0", method = "textDocument/didOpen", @params = new { textDocument = new { uri, languageId = "csharp", version = 2, text = completionSource } } }, timeout.Token);
using (await ReceiveUntilAsync(socket, static root => root.TryGetProperty("method", out var method) && method.GetString() == "textDocument/publishDiagnostics", timeout.Token)) { }
await SendAsync(socket, new { jsonrpc = "2.0", id = 2, method = "textDocument/completion", @params = new { textDocument = new { uri }, position = new { line = 0, character = 48 }, context = new { triggerKind = 1, triggerCharacter = (string?)null } } }, timeout.Token);
using (var completion = await ReceiveUntilAsync(socket, static root => HasId(root, 2), timeout.Token))
{
    Require(completion.RootElement.GetProperty("result").GetProperty("items").EnumerateArray().Any(static item => item.GetProperty("label").GetString() == "WriteLine"), "Const-generics LSP completion did not return WriteLine.");
}

await SendAsync(socket, new { jsonrpc = "2.0", id = 3, method = "shutdown", @params = new { } }, timeout.Token);
using (await ReceiveUntilAsync(socket, static root => HasId(root, 3), timeout.Token)) { }
await SendAsync(socket, new { jsonrpc = "2.0", method = "exit", @params = new { } }, timeout.Token);
Console.WriteLine("ConstGenerics Roslyn Kestrel smoke passed: health/describe, compile-check, PE artifact, fork AST and LSP completion.");

async Task<JsonDocument> BuildAsync(string target, object buildWorkspace, object buildOptions)
{
    var request = new { requestId = $"const-{target}-{Guid.NewGuid():N}", idempotencyKey = $"const-{target}-idempotency-{Guid.NewGuid():N}", pipelineResolutionId = "const-smoke-pipeline", toolchainId = "roslyn-const-generics", referenceSetId = "const-generics-ref", workspace = buildWorkspace, deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(45), options = buildOptions, target };
    var response = await http.PostAsJsonAsync("/api/v1/build", request, jsonOptions, timeout.Token);
    await EnsureSuccessAsync(response, target);
    return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(timeout.Token));
}

static object CreateWorkspace(string source, object options) => new { schemaVersion = 1, revision = 7, selectionRevision = 3, languageId = "csharp", files = new[] { new { path = "Program.cs", version = 1, text = source } }, activeFile = "Program.cs", sourceOrder = new[] { "Program.cs" }, referenceSetId = "const-generics-ref", buildOptions = options };

static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
{
    if (response.IsSuccessStatusCode)
        return;
    var body = await response.Content.ReadAsStringAsync();
    throw new InvalidOperationException($"{operation} failed with {(int)response.StatusCode}: {body}");
}

static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

static bool HasId(JsonElement root, int expected) => root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == expected;

static async Task SendAsync(ClientWebSocket socket, object payload, CancellationToken cancellationToken)
{
    var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, CreateLspJsonOptions());
    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
}

static JsonSerializerOptions CreateBusinessJsonOptions() => new(JsonSerializerDefaults.Web)
{
    PropertyNamingPolicy = PascalCaseJsonNamingPolicy.Instance,
    DictionaryKeyPolicy = null,
    TypeInfoResolver = new DefaultJsonTypeInfoResolver()
};

static JsonSerializerOptions CreateLspJsonOptions() => new(JsonSerializerDefaults.Web)
{
    TypeInfoResolver = new DefaultJsonTypeInfoResolver()
};

static async Task<JsonDocument> ReceiveUntilAsync(ClientWebSocket socket, Func<JsonElement, bool> predicate, CancellationToken cancellationToken)
{
    for (var attempt = 0; attempt < 20; attempt++)
    {
        var buffer = new byte[64 * 1024];
        using var stream = new MemoryStream();
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
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
    throw new TimeoutException("The expected LSP response was not received.");
}

sealed class PascalCaseJsonNamingPolicy : JsonNamingPolicy
{
    public static readonly PascalCaseJsonNamingPolicy Instance = new();

    public override string ConvertName(string name) =>
        name.Length == 0 || char.IsUpper(name[0])
            ? name : char.ToUpperInvariant(name[0]) + name[1..];
}
