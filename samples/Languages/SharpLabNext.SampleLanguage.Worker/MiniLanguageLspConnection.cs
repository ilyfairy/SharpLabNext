using System.Net.WebSockets;
using System.Text.Json;
using SharpLabNext.Contracts;

namespace SharpLabNext.SampleLanguage.Worker;

internal sealed class MiniLanguageLspConnection(
    WebSocket socket,
    MiniLanguageSessionState session,
    int maximumMessageBytes)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, MiniLanguageDocument> _documents = CreateInitialDocuments(session.Workspace);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var message = await ReceiveMessageAsync(cancellationToken).ConfigureAwait(false);
                if (message is null)
                    break;
                using var document = await ParseMessageAsync(message, cancellationToken).ConfigureAwait(false);
                if (document is null)
                    continue;
                if (!await HandleMessageAsync(document.RootElement, cancellationToken).ConfigureAwait(false))
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "LSP connection closed.", CancellationToken.None).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                }
            }
        }
    }

    private async Task<bool> HandleMessageAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
        {
            await SendErrorAsync(root, -32600, "Invalid JSON-RPC request.", cancellationToken).ConfigureAwait(false);
            return true;
        }

        var method = methodElement.GetString();
        switch (method)
        {
            case "initialize":
                await SendResultAsync(root, new
                {
                    capabilities = new
                    {
                        textDocumentSync = new { openClose = true, change = 1 },
                        completionProvider = new { resolveProvider = false, triggerCharacters = Array.Empty<string>() }
                    },
                    serverInfo = new { name = "SharpLabNext MiniLang", version = MiniLanguageCompiler.Version }
                }, cancellationToken).ConfigureAwait(false);
                break;
            case "initialized":
                break;
            case "textDocument/didOpen":
                await HandleDidOpenAsync(root, cancellationToken).ConfigureAwait(false);
                break;
            case "textDocument/didChange":
                await HandleDidChangeAsync(root, cancellationToken).ConfigureAwait(false);
                break;
            case "textDocument/completion":
                await SendResultAsync(root, new
                {
                    isIncomplete = false,
                    items = new[]
                    {
                        new
                        {
                            label = "print",
                            kind = 14,
                            detail = "Write a line to standard output",
                            insertText = "print \"$1\"",
                            insertTextFormat = 2
                        }
                    }
                }, cancellationToken).ConfigureAwait(false);
                break;
            case "shutdown":
                await SendResultAsync(root, null, cancellationToken).ConfigureAwait(false);
                break;
            case "exit":
                return false;
            default:
                if (root.TryGetProperty("id", out _))
                    await SendErrorAsync(root, -32601, $"Method '{method}' is not supported.", cancellationToken).ConfigureAwait(false);
                break;
        }
        return true;
    }

    private async Task HandleDidOpenAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!TryGetParameters(root, out var parameters) ||
            !parameters.TryGetProperty("textDocument", out var textDocument) ||
            !TryGetString(textDocument, "uri", out var uri) ||
            !TryGetString(textDocument, "text", out var text) ||
            !textDocument.TryGetProperty("version", out var versionElement) ||
            !versionElement.TryGetInt64(out var version))
        {
            await SendErrorAsync(root, -32602, "didOpen requires uri, version, and text.", cancellationToken).ConfigureAwait(false);
            return;
        }
        _documents[uri] = new MiniLanguageDocument(UriToPath(uri), version, text);
        await PublishDiagnosticsAsync(uri, _documents[uri], cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleDidChangeAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!TryGetParameters(root, out var parameters) ||
            !parameters.TryGetProperty("textDocument", out var textDocument) ||
            !TryGetString(textDocument, "uri", out var uri) ||
            !textDocument.TryGetProperty("version", out var versionElement) ||
            !versionElement.TryGetInt64(out var version) ||
            !parameters.TryGetProperty("contentChanges", out var changes) ||
            changes.ValueKind != JsonValueKind.Array ||
            changes.GetArrayLength() == 0 ||
            !TryGetString(changes[changes.GetArrayLength() - 1], "text", out var text))
        {
            await SendErrorAsync(root, -32602, "didChange requires a full-document content change.", cancellationToken).ConfigureAwait(false);
            return;
        }
        _documents[uri] = new MiniLanguageDocument(UriToPath(uri), version, text);
        await PublishDiagnosticsAsync(uri, _documents[uri], cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishDiagnosticsAsync(
        string uri,
        MiniLanguageDocument document,
        CancellationToken cancellationToken)
    {
        var diagnostics = MiniLanguageCompiler.GetDiagnostics(
            document.Path,
            document.Text,
            document.Version,
            session.Session.SelectionRevision)
            .Select(static diagnostic => new
            {
                range = new
                {
                    start = new
                    {
                        line = diagnostic.Range?.StartLine ?? 0,
                        character = diagnostic.Range?.StartCharacter ?? 0
                    },
                    end = new
                    {
                        line = diagnostic.Range?.EndLine ?? 0,
                        character = diagnostic.Range?.EndCharacter ?? 0
                    }
                },
                severity = diagnostic.Severity == DiagnosticSeverity.Error ? 1 : 2,
                code = diagnostic.Code,
                source = diagnostic.Source,
                message = diagnostic.Message,
                data = new
                {
                    workspaceRevision = diagnostic.WorkspaceRevision,
                    selectionRevision = diagnostic.SelectionRevision
                }
            })
            .ToArray();
        await SendAsync(new
        {
            jsonrpc = "2.0",
            method = "textDocument/publishDiagnostics",
            @params = new { uri, version = document.Version, diagnostics }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument?> ParseMessageAsync(
        byte[] message,
        CancellationToken cancellationToken)
    {
        try
        {
            return JsonDocument.Parse(message);
        }
        catch (JsonException)
        {
            await SendAsync(new
            {
                jsonrpc = "2.0",
                id = (object?)null,
                error = new { code = -32700, message = "Parse error." }
            }, cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    private async Task<byte[]?> ReceiveMessageAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Min(maximumMessageBytes, 16 * 1024)];
        using var content = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            if (result.MessageType != WebSocketMessageType.Text)
            {
                await socket.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "LSP frames must be UTF-8 text.", CancellationToken.None).ConfigureAwait(false);
                return null;
            }
            if (content.Length + result.Count > maximumMessageBytes)
            {
                await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "LSP message exceeds the configured limit.", CancellationToken.None).ConfigureAwait(false);
                return null;
            }
            content.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return content.ToArray();
    }

    private Task SendResultAsync(JsonElement root, object? result, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("id", out var id))
            return Task.CompletedTask;
        return SendAsync(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.Clone(),
            ["result"] = result
        }, cancellationToken);
    }

    private Task SendErrorAsync(
        JsonElement root,
        int code,
        string message,
        CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("id", out var id))
            return Task.CompletedTask;
        return SendAsync(new
        {
            jsonrpc = "2.0",
            id = id.Clone(),
            error = new { code, message }
        }, cancellationToken);
    }

    private async Task SendAsync(object message, CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open)
            return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, MiniLanguageDocument> CreateInitialDocuments(WorkspaceSnapshot workspace)
    {
        var documents = new Dictionary<string, MiniLanguageDocument>(StringComparer.Ordinal);
        foreach (var file in workspace.Files)
        {
            var uri = $"sharplabnext:///{Uri.EscapeDataString(file.Path).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)}";
            documents[uri] = new MiniLanguageDocument(file.Path, file.Version, file.Text);
        }
        return documents;
    }

    private static bool TryGetParameters(JsonElement root, out JsonElement parameters) =>
        root.TryGetProperty("params", out parameters) && parameters.ValueKind == JsonValueKind.Object;

    private static bool TryGetString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        return root.TryGetProperty(name, out var element) &&
            element.ValueKind == JsonValueKind.String &&
            (value = element.GetString() ?? string.Empty).Length > 0;
    }

    private static string UriToPath(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            return MiniLanguageCompiler.DefaultFileName;
        var path = Uri.UnescapeDataString(parsed.AbsolutePath.TrimStart('/'));
        return string.IsNullOrEmpty(path) ? MiniLanguageCompiler.DefaultFileName : path;
    }
}

internal sealed record MiniLanguageDocument(string Path, long Version, string Text);
