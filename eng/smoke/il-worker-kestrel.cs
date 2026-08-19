#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property EnableTrimAnalyzer=false
#:property EnableAotAnalyzer=false

using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

var arguments = args.ToList();
var languageOnly = arguments.Remove("--language-only");
string? internalServiceToken = null;
var tokenFileIndex = arguments.IndexOf("--token-file");
if (tokenFileIndex >= 0)
{
    if (tokenFileIndex + 1 >= arguments.Count)
        throw new ArgumentException("--token-file requires a path.");
    internalServiceToken = File.ReadAllText(arguments[tokenFileIndex + 1]).TrimEnd('\r', '\n');
    arguments.RemoveRange(tokenFileIndex, 2);
}
var baseAddress = arguments.Count > 0 ? new Uri(arguments[0], UriKind.Absolute) : new Uri("http://127.0.0.1:5187");
if (baseAddress.Scheme != Uri.UriSchemeHttp && baseAddress.Scheme != Uri.UriSchemeHttps)
    throw new ArgumentException("The smoke endpoint must use HTTP or HTTPS.");
if (internalServiceToken is not null && baseAddress.Scheme != Uri.UriSchemeHttps && !baseAddress.IsLoopback)
    throw new ArgumentException("--token-file requires HTTPS unless the smoke endpoint is loopback.");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
using var http = new HttpClient { BaseAddress = baseAddress };
if (internalServiceToken is not null)
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", internalServiceToken);
var jsonOptions = CreateBusinessJsonOptions();
var lspJsonOptions = CreateLspJsonOptions();
using var health = await http.GetAsync("/health/ready", timeout.Token);
await EnsureSuccessAsync(health, "health");

const string source = """
    .assembly SharpLabNextSmoke {}
    .module SharpLabNextSmoke.dll
    .class public auto ansi Program extends [System.Runtime]System.Object
    {
      .method public hidebysig static void Main() cil managed
      {
        .entrypoint
        .maxstack 1
        ldstr "il-kestrel-smoke"
        call void [System.Console]System.Console::WriteLine(string)
        ret
      }
    }
    """;
var options = new
{
    configuration = "release",
    optimize = true,
    outputKind = "console",
    allowUnsafe = false,
    emitPortablePdb = true,
    nullableContext = "disable",
    languageVersion = "ecma-335",
    preprocessorSymbols = Array.Empty<string>(),
    checkOverflow = false
};
var workspace = new
{
    schemaVersion = 1,
    revision = 7,
    selectionRevision = 3,
    languageId = "il",
    files = new[] { new { path = "Program.il", version = 1, text = source } },
    activeFile = "Program.il",
    sourceOrder = new[] { "Program.il" },
    referenceSetId = "net11-preview-ref",
    buildOptions = options
};
var pipelineResolutionId = "smoke-pipeline";
if (languageOnly)
{
    var catalog = await http.GetFromJsonAsync<JsonElement>("/api/v1/catalog", jsonOptions, timeout.Token);
    var selectionRequest = new
    {
        languageId = "il",
        toolchainId = "mobius-ilasm-stable",
        referenceSetId = "net11-preview-ref",
        outputId = "compile-check",
        runtimeId = (string?)null,
        buildMode = "release",
        catalogRevision = catalog.GetProperty("Revision").GetString(),
        workspaceRevision = workspace.revision
    };
    using var selected = await http.PostAsJsonAsync("/api/v1/selections/resolve", selectionRequest, jsonOptions, timeout.Token);
    await EnsureSuccessAsync(selected, "resolve selection");
    using var selectedJson = JsonDocument.Parse(await selected.Content.ReadAsByteArrayAsync(timeout.Token));
    pipelineResolutionId = selectedJson.RootElement.GetProperty("PipelineResolutionId").GetString()
        ?? throw new InvalidOperationException("Selection resolution returned no ID.");
}
if (!languageOnly)
{
    var buildRequest = new
    {
        requestId = $"smoke-build-{Guid.NewGuid():N}",
        idempotencyKey = $"smoke-idempotency-{Guid.NewGuid():N}",
        pipelineResolutionId,
        toolchainId = "mobius-ilasm-stable",
        referenceSetId = "net11-preview-ref",
        workspace,
        deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(20),
        options,
        target = "artifact"
    };
    using var build = await http.PostAsJsonAsync("/api/v1/build", buildRequest, jsonOptions, timeout.Token);
    await EnsureSuccessAsync(build, "build");
    using var buildJson = JsonDocument.Parse(await build.Content.ReadAsByteArrayAsync(timeout.Token));
    var root = buildJson.RootElement;
    Require(root.GetProperty("Result").GetProperty("Outcome").GetString() == "succeeded", "Build did not succeed.");
    Require(root.GetProperty("Result").GetProperty("Identity").GetProperty("ReferenceSetId").GetString() == "net11-preview-ref", "Build used the wrong reference set.");
    Require(root.GetProperty("DevelopmentArtifact").GetProperty("TargetFramework").GetString() == "net11.0", "Artifact target framework is incorrect.");
    Require(root.GetProperty("DevelopmentArtifact").GetProperty("PeImageBase64").GetString()!.Length > 100, "Build returned no PE payload.");
}

var openRequest = new
{
    requestId = $"smoke-lsp-{Guid.NewGuid():N}",
    pipelineResolutionId,
    languageId = "il",
    toolchainId = "mobius-ilasm-stable",
    referenceSetId = "net11-preview-ref",
    workspace,
    lspVersion = "3.17"
};
using var opened = await http.PostAsJsonAsync("/api/v1/language-sessions", openRequest, jsonOptions, timeout.Token);
await EnsureSuccessAsync(opened, "open language session");
using var openedJson = JsonDocument.Parse(await opened.Content.ReadAsByteArrayAsync(timeout.Token));
var sessionId = openedJson.RootElement.GetProperty("SessionId").GetString()
    ?? throw new InvalidOperationException("Language session returned no ID.");

try
{
    using var socket = new ClientWebSocket();
    if (internalServiceToken is not null)
        socket.Options.SetRequestHeader("Authorization", $"Bearer {internalServiceToken}");
    var socketUri = new UriBuilder(baseAddress)
    {
        Scheme = baseAddress.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        Path = $"/api/v1/language-sessions/{sessionId}/lsp"
    }.Uri;
    await socket.ConnectAsync(socketUri, timeout.Token);
    await SendAsync(socket, new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { capabilities = new { } } }, lspJsonOptions, timeout.Token);
    using (var initialized = await ReceiveUntilAsync(socket, static root => HasId(root, 1), timeout.Token))
    {
        var capabilities = initialized.RootElement.GetProperty("result").GetProperty("capabilities");
        Require(capabilities.GetProperty("semanticTokensProvider").GetProperty("full").GetBoolean(), "Semantic tokens are not advertised.");
        Require(capabilities.GetProperty("foldingRangeProvider").GetBoolean(), "Folding ranges are not advertised.");
    }

    const string uri = "sharplabnext:///Program.il";
    await SendAsync(socket, new { jsonrpc = "2.0", method = "initialized", @params = new { } }, lspJsonOptions, timeout.Token);
    await SendAsync(socket, new
    {
        jsonrpc = "2.0",
        method = "textDocument/didOpen",
        @params = new { textDocument = new { uri, languageId = "il", version = 1, text = source } }
    }, lspJsonOptions, timeout.Token);
    using (var diagnostics = await ReceiveUntilAsync(socket, static root =>
        root.TryGetProperty("method", out var method) && method.GetString() == "textDocument/publishDiagnostics", timeout.Token))
    {
        Require(diagnostics.RootElement.GetProperty("params").GetProperty("diagnostics").GetArrayLength() == 0, "Valid IL produced live diagnostics.");
    }

    const string completionSource = """
        .assembly SharpLabNextCompletionSmoke {}
        .class public auto ansi Program
        {
          .method public static void Main() cil managed
          {
            .maxstack 1
            call void [System.R
          }
        }
        """;
    var completionLines = completionSource.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    const int completionLine = 6;
    await SendAsync(socket, new
    {
        jsonrpc = "2.0",
        method = "textDocument/didChange",
        @params = new
        {
            textDocument = new { uri, version = 2 },
            contentChanges = new[] { new { text = completionSource } }
        }
    }, lspJsonOptions, timeout.Token);
    using (await ReceiveUntilAsync(socket, static root =>
        root.TryGetProperty("method", out var method) && method.GetString() == "textDocument/publishDiagnostics", timeout.Token))
    {
    }
    await SendAsync(socket, new
    {
        jsonrpc = "2.0",
        id = 2,
        method = "textDocument/completion",
        @params = new
        {
            textDocument = new { uri },
            position = new { line = completionLine, character = completionLines[completionLine].Length },
            context = new { triggerKind = 1, triggerCharacter = (string?)null }
        }
    }, lspJsonOptions, timeout.Token);
    using (var completion = await ReceiveUntilAsync(socket, static root => HasId(root, 2), timeout.Token))
    {
        var items = completion.RootElement.GetProperty("result").GetProperty("items").EnumerateArray().ToArray();
        var importedRuntime = items.Any(static item =>
            item.GetProperty("label").GetString() == "System.Runtime" &&
            item.GetProperty("data").GetProperty("origin").GetString() == "ImportedAssembly" &&
            item.GetProperty("textEdit").GetProperty("newText").GetString() == "System.Runtime]");
        var candidates = items.Select(static item =>
            $"{item.GetProperty("label").GetString()} ({item.GetProperty("data").GetProperty("origin").GetString()})");
        Require(importedRuntime,
            $"Imported reference-set assembly completion is missing. Available candidates: {string.Join(", ", candidates)}");
    }

    await SendAsync(socket, new { jsonrpc = "2.0", id = 3, method = "shutdown", @params = new { } }, lspJsonOptions, timeout.Token);
    using (await ReceiveUntilAsync(socket, static root => HasId(root, 3), timeout.Token))
    {
    }
    await SendAsync(socket, new { jsonrpc = "2.0", method = "exit", @params = new { } }, lspJsonOptions, timeout.Token);
}
finally
{
    await TryCloseSessionAsync(http, sessionId);
}
Console.WriteLine(languageOnly
    ? "IL gateway smoke passed: health, WebSocket LSP, diagnostics and reference completion."
    : "IL Kestrel smoke passed: health, net11 Build/PE, WebSocket LSP, diagnostics and reference completion.");

static async Task TryCloseSessionAsync(HttpClient http, string sessionId)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    try
    {
        using var response = await http.DeleteAsync($"/api/v1/language-sessions/{sessionId}", timeout.Token);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            Console.Error.WriteLine($"Session cleanup returned {(int)response.StatusCode}.");
    }
    catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
    {
        Console.Error.WriteLine($"Session cleanup failed: {exception.Message}");
    }
}

static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
{
    if (response.IsSuccessStatusCode)
        return;
    var body = await response.Content.ReadAsStringAsync();
    throw new InvalidOperationException($"{operation} failed with {(int)response.StatusCode}: {body}");
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static bool HasId(JsonElement root, int expected) =>
    root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == expected;

static async Task SendAsync(
    ClientWebSocket socket,
    object payload,
    JsonSerializerOptions options,
    CancellationToken cancellationToken)
{
    var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, options);
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

static async Task<JsonDocument> ReceiveUntilAsync(
    ClientWebSocket socket,
    Func<JsonElement, bool> predicate,
    CancellationToken cancellationToken)
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
                throw new InvalidOperationException("The IL LSP WebSocket closed before the expected response.");
            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        var document = JsonDocument.Parse(stream.ToArray());
        if (predicate(document.RootElement))
            return document;
        document.Dispose();
    }
    throw new TimeoutException("The expected IL LSP response was not received.");
}

sealed class PascalCaseJsonNamingPolicy : JsonNamingPolicy
{
    public static readonly PascalCaseJsonNamingPolicy Instance = new();

    public override string ConvertName(string name) =>
        name.Length == 0 || !char.IsAsciiLetterLower(name[0])
            ? name
            : char.ToUpperInvariant(name[0]) + name[1..];
}
