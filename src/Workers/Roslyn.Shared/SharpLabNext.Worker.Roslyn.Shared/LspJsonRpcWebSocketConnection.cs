using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.Roslyn;

internal sealed class LspJsonRpcWebSocketConnection(
    WebSocket socket,
    RoslynLanguageSession session,
    LspLimits limits,
    CancellationToken requestAborted) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = ContractJson.CreateLspSerializerOptions();

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _requests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _requestTasks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _diagnostics = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, Task> _diagnosticTasks = new();
    private readonly CancellationTokenSource _connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
    private int _initialized;
    private int _shutdown;
    private long _diagnosticSequence;

    public async Task RunAsync()
    {
        using var connectionLease = session.AttachConnection();
        try
        {
            while (!_connectionCancellation.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var message = await ReceiveMessageAsync(_connectionCancellation.Token).ConfigureAwait(false);
                if (message is null)
                    break;

                await DispatchMessageAsync(message, _connectionCancellation.Token).ConfigureAwait(false);
            }
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            _connectionCancellation.Cancel();

            await Task.WhenAll(_requestTasks.Values.Concat(_diagnosticTasks.Values)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            DisposeCancellations(_requests);
            DisposeCancellations(_diagnostics);
            await CompleteCloseHandshakeAsync().ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        _connectionCancellation.Cancel();
        _sendLock.Dispose();
        _connectionCancellation.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task DispatchMessageAsync(byte[] message, CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(message);
        }
        catch (JsonException)
        {
            await SendErrorAsync(null, -32700, "Parse error", cancellationToken).ConfigureAwait(false);
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("jsonrpc", out var jsonRpc) ||
                jsonRpc.GetString() != "2.0" ||
                !root.TryGetProperty("method", out var methodElement) ||
                methodElement.ValueKind != JsonValueKind.String)
            {
                await SendErrorAsync(GetOptionalId(root), -32600, "Invalid Request", cancellationToken).ConfigureAwait(false);
                return;
            }

            var method = methodElement.GetString()!;
            var messageClone = root.Clone();
            if (method == "$/cancelRequest")
            {
                CancelRequest(messageClone);
                return;
            }

            if (!root.TryGetProperty("id", out var id))
            {
                await HandleNotificationAsync(method, messageClone, cancellationToken).ConfigureAwait(false);
                return;
            }

            var idClone = id.Clone();
            var idKey = idClone.GetRawText();
            if (_requests.Count >= limits.MaxConcurrentRequestsPerConnection)
            {
                await SendErrorAsync(idClone, -32000, "Too many concurrent LSP requests.", cancellationToken).ConfigureAwait(false);
                return;
            }

            var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(_connectionCancellation.Token);
            if (!_requests.TryAdd(idKey, requestCancellation))
            {
                requestCancellation.Dispose();
                await SendErrorAsync(idClone, -32600, "A request with the same id is already active.", cancellationToken).ConfigureAwait(false);
                return;
            }

            var task = ProcessRequestAsync(idClone, method, messageClone, requestCancellation);
            _requestTasks[idKey] = task;
            _ = CompleteRequestAsync(idKey, task);
        }
    }

    private async Task CompleteRequestAsync(string idKey, Task task)
    {
        await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _requestTasks.TryRemove(idKey, out _);
        if (_requests.TryRemove(idKey, out var cancellation))
            cancellation.Dispose();
    }

    private async Task ProcessRequestAsync(
        JsonElement id,
        string method,
        JsonElement message,
        CancellationTokenSource requestCancellation)
    {
        try
        {
            if (Volatile.Read(ref _initialized) == 0 && method != "initialize")
                throw new LspSessionUnavailableException("The LSP connection has not been initialized.");
            if (Volatile.Read(ref _shutdown) != 0 && method != "exit")
                throw new LspSessionUnavailableException("The LSP connection has been shut down.");

            object? result = method switch
            {
                "initialize" => Initialize(),
                "shutdown" => Shutdown(),
                "textDocument/completion" => await session.GetCompletionsAsync(
                    DeserializeParams<LspCompletionParams>(message),
                    requestCancellation.Token).ConfigureAwait(false),
                "completionItem/resolve" => await session.ResolveCompletionAsync(
                    DeserializeParams<LspCompletionItem>(message),
                    requestCancellation.Token).ConfigureAwait(false),
                "textDocument/hover" => await session.GetHoverAsync(
                    DeserializeParams<LspTextDocumentPositionParams>(message),
                    requestCancellation.Token).ConfigureAwait(false),
                "textDocument/signatureHelp" => await session.GetSignatureHelpAsync(
                    DeserializeParams<LspSignatureHelpParams>(message),
                    requestCancellation.Token).ConfigureAwait(false),
                "textDocument/semanticTokens/full" => await session.GetSemanticTokensAsync(
                    DeserializeParams<LspSemanticTokensParams>(message),
                    requestCancellation.Token).ConfigureAwait(false),
                "textDocument/documentSymbol" => await session.GetDocumentSymbolsAsync(
                    DeserializeParams<LspDocumentSymbolParams>(message),
                    requestCancellation.Token).ConfigureAwait(false),
                "textDocument/codeAction" => await session.GetCodeActionsAsync(
                    DeserializeParams<LspCodeActionParams>(message),
                    requestCancellation.Token).ConfigureAwait(false),
                _ => throw new LspMethodNotFoundException($"LSP method '{method}' is not supported.")
            };
            await SendResultAsync(id, result, requestCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await SendErrorIgnoringCancellationAsync(id, -32800, "Request cancelled.").ConfigureAwait(false);
        }
        catch (LspMethodNotFoundException exception)
        {
            await SendErrorIgnoringCancellationAsync(id, -32601, exception.Message).ConfigureAwait(false);
        }
        catch (LspInvalidParamsException exception)
        {
            await SendErrorIgnoringCancellationAsync(id, -32602, exception.Message).ConfigureAwait(false);
        }
        catch (LspContentModifiedException exception)
        {
            await SendErrorIgnoringCancellationAsync(id, -32801, exception.Message).ConfigureAwait(false);
        }
        catch (LspLimitExceededException exception)
        {
            await SendErrorIgnoringCancellationAsync(id, -32000, exception.Message).ConfigureAwait(false);
        }
        catch (LspSessionUnavailableException exception)
        {
            await SendErrorIgnoringCancellationAsync(id, -32002, exception.Message).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            await SendErrorIgnoringCancellationAsync(id, -32602, $"Invalid params: {exception.Message}").ConfigureAwait(false);
        }
        catch (Exception)
        {
            await SendErrorIgnoringCancellationAsync(id, -32603, "Internal error.").ConfigureAwait(false);
        }
    }

    private async Task HandleNotificationAsync(
        string method,
        JsonElement message,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _initialized) == 0 && method != "exit")
            return;
        if (Volatile.Read(ref _shutdown) != 0 && method != "exit")
            return;

        switch (method)
        {
            case "initialized":
                return;
            case "exit":
                _connectionCancellation.Cancel();
                return;
            case "textDocument/didOpen":
                {
                    var state = await session.DidOpenAsync(
                        DeserializeParams<LspDidOpenTextDocumentParams>(message),
                        cancellationToken).ConfigureAwait(false);
                    ScheduleDiagnostics(state);
                    return;
                }
            case "textDocument/didChange":
                {
                    var state = await session.DidChangeAsync(
                        DeserializeParams<LspDidChangeTextDocumentParams>(message),
                        cancellationToken).ConfigureAwait(false);
                    ScheduleDiagnostics(state);
                    return;
                }
            case "textDocument/didClose":
                {
                    var state = await session.DidCloseAsync(
                        DeserializeParams<LspDidCloseTextDocumentParams>(message),
                        cancellationToken).ConfigureAwait(false);
                    CancelDiagnostics(state.Uri);
                    await SendNotificationAsync(
                        "textDocument/publishDiagnostics",
                        new { uri = state.Uri, version = state.Version, diagnostics = Array.Empty<LspDiagnostic>() },
                        cancellationToken).ConfigureAwait(false);
                    return;
                }
            default:
                return;
        }
    }

    private object Initialize()
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
            throw new LspInvalidParamsException("initialize can only be called once per connection.");
        return new
        {
            capabilities = new
            {
                positionEncoding = ContractConventions.TextCoordinateEncoding,
                textDocumentSync = new { openClose = true, change = 2, save = false },
                completionProvider = new { resolveProvider = true, triggerCharacters = new[] { ".", ":", "<" } },
                hoverProvider = true,
                signatureHelpProvider = new
                {
                    triggerCharacters = new[] { "(", ",", "<" },
                    retriggerCharacters = new[] { ",", ")" }
                },
                semanticTokensProvider = new
                {
                    legend = new
                    {
                        tokenTypes = RoslynLspFeatureService.SemanticTokenTypes,
                        tokenModifiers = RoslynLspFeatureService.SemanticTokenModifiers
                    },
                    range = false,
                    full = true
                },
                documentSymbolProvider = true,
                codeActionProvider = new
                {
                    codeActionKinds = new[] { "quickfix", "source.organizeImports", "source.formatDocument" }
                }
            },
            serverInfo = new { name = "SharpLabNext Roslyn Stable", version = CSharpBuildService.GetLoadedCompilerVersion() }
        };
    }

    private object? Shutdown()
    {
        Interlocked.Exchange(ref _shutdown, 1);
        return null;
    }

    private void ScheduleDiagnostics(LspDocumentState state)
    {
        CancelDiagnostics(state.Uri);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_connectionCancellation.Token);
        _diagnostics[state.Uri] = cancellation;
        var task = PublishDiagnosticsAsync(state, cancellation);
        var sequence = Interlocked.Increment(ref _diagnosticSequence);
        _diagnosticTasks[sequence] = task;
        _ = CompleteDiagnosticsAsync(sequence, task);
    }

    private async Task CompleteDiagnosticsAsync(long sequence, Task task)
    {
        await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _diagnosticTasks.TryRemove(sequence, out _);
    }

    private async Task PublishDiagnosticsAsync(
        LspDocumentState state,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(limits.DiagnosticsDebounceMilliseconds, cancellation.Token).ConfigureAwait(false);
            var report = await session
                .GetDiagnosticsAsync(state.Uri, state.Version, cancellation.Token)
                .ConfigureAwait(false);
            if (report is null)
                return;
            await SendNotificationAsync(
                "textDocument/publishDiagnostics",
                new { uri = report.Uri, version = report.Version, diagnostics = report.Diagnostics },
                cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_diagnostics.TryGetValue(state.Uri, out var current) && ReferenceEquals(current, cancellation))
                _diagnostics.TryRemove(state.Uri, out _);
            cancellation.Dispose();
        }
    }

    private void CancelDiagnostics(string uri)
    {
        if (_diagnostics.TryRemove(uri, out var previous))
            CancelIgnoringDisposal(previous);
    }

    private void CancelRequest(JsonElement message)
    {
        if (!message.TryGetProperty("params", out var parameters) ||
            !parameters.TryGetProperty("id", out var id))
        {
            return;
        }

        if (_requests.TryGetValue(id.GetRawText(), out var cancellation))
            CancelIgnoringDisposal(cancellation);
    }

    private static T DeserializeParams<T>(JsonElement message)
    {
        if (!message.TryGetProperty("params", out var parameters))
            throw new LspInvalidParamsException("JSON-RPC params are required.");
        return parameters.Deserialize<T>(JsonOptions)
            ?? throw new LspInvalidParamsException("JSON-RPC params could not be deserialized.");
    }

    private async Task<byte[]?> ReceiveMessageAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var content = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            if (result.MessageType != WebSocketMessageType.Text)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.InvalidMessageType,
                    "LSP requires JSON text messages.",
                    cancellationToken).ConfigureAwait(false);
                return null;
            }

            if (content.Length + result.Count > limits.MaxMessageBytes)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.MessageTooBig,
                    "LSP message exceeds the configured limit.",
                    cancellationToken).ConfigureAwait(false);
                return null;
            }

            content.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return content.ToArray();
        }
    }

    private Task SendResultAsync(JsonElement id, object? result, CancellationToken cancellationToken) =>
        SendEnvelopeAsync(new JsonRpcResult("2.0", id, result), cancellationToken);

    private Task SendErrorAsync(JsonElement? id, int code, string message, CancellationToken cancellationToken) =>
        SendEnvelopeAsync(new JsonRpcErrorResult("2.0", id, new JsonRpcError(code, message)), cancellationToken);

    private async Task SendErrorIgnoringCancellationAsync(JsonElement id, int code, string message)
    {
        try
        {
            await SendErrorAsync(id, code, message, _connectionCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Task SendNotificationAsync(string method, object parameters, CancellationToken cancellationToken) =>
        SendEnvelopeAsync(new JsonRpcNotification("2.0", method, parameters), cancellationToken);

    private async Task SendEnvelopeAsync(object envelope, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        if (bytes.Length > limits.MaxMessageBytes)
            throw new LspLimitExceededException("LSP response exceeds the configured message limit.");

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static JsonElement? GetOptionalId(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var id)
            ? id.Clone()
            : null;

    private static void DisposeCancellations(ConcurrentDictionary<string, CancellationTokenSource> cancellations)
    {
        foreach (var key in cancellations.Keys)
        {
            if (cancellations.TryRemove(key, out var cancellation))
                cancellation.Dispose();
        }
    }

    internal static void CancelIgnoringDisposal(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task CompleteCloseHandshakeAsync()
    {
        try
        {
            if (socket.State == WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "LSP connection closed.",
                    CancellationToken.None).ConfigureAwait(false);
            }
            else if (socket.State == WebSocketState.Open)
            {
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "LSP connection ended.",
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (WebSocketException)
        {
        }
    }

    private sealed record JsonRpcResult(
        string Jsonrpc,
        JsonElement Id,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] object? Result);

    private sealed record JsonRpcErrorResult(
        string Jsonrpc,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] JsonElement? Id,
        JsonRpcError Error);

    private sealed record JsonRpcError(int Code, string Message);

    private sealed record JsonRpcNotification(string Jsonrpc, string Method, object Params);
}
