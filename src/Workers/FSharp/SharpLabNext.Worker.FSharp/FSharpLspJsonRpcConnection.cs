using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.FSharp;

internal sealed class FSharpLspJsonRpcConnection(
    WebSocket socket,
    FSharpLanguageSession session,
    FSharpLspLimits limits,
    CancellationToken requestAborted) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(ContractJson.CreateLspSerializerOptions())
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
    private int _initialized;
    private int _shutdown;

    public async Task RunAsync()
    {
        using var lease = session.AttachConnection();
        while (!_connectionCancellation.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            using var message = await ReceiveAsync(_connectionCancellation.Token).ConfigureAwait(false);
            if (message is null)
                break;
            await DispatchAsync(message.RootElement, _connectionCancellation.Token).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _connectionCancellation.CancelAsync();
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "LSP connection closed.", CancellationToken.None);
            }
            catch (WebSocketException)
            {
            }
        }
        _sendLock.Dispose();
        _connectionCancellation.Dispose();
    }

    private async Task DispatchAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
            return;
        var method = methodElement.GetString()!;
        if (root.TryGetProperty("id", out var id))
        {
            await HandleRequestAsync(id.Clone(), method, root, cancellationToken).ConfigureAwait(false);
            return;
        }
        await HandleNotificationAsync(method, root, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleRequestAsync(
        JsonElement id,
        string method,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        try
        {
            if (Volatile.Read(ref _initialized) == 0 && method != "initialize")
                throw new FSharpLspSessionUnavailableException("The LSP connection has not been initialized.");
            if (Volatile.Read(ref _shutdown) != 0 && method != "shutdown")
                throw new FSharpLspSessionUnavailableException("The LSP connection has already shut down.");
            object? result = method switch
            {
                "initialize" => Initialize(),
                "shutdown" => Shutdown(),
                "textDocument/completion" => await session.GetCompletionsAsync(
                    RequiredParams<FSharpLspCompletionParams>(root), cancellationToken).ConfigureAwait(false),
                "textDocument/hover" => await session.GetHoverAsync(
                    RequiredParams<FSharpLspTextDocumentPositionParams>(root), cancellationToken).ConfigureAwait(false),
                "textDocument/signatureHelp" => await session.GetSignatureHelpAsync(
                    RequiredParams<FSharpLspSignatureHelpParams>(root), cancellationToken).ConfigureAwait(false),
                "textDocument/semanticTokens/full" => await session.GetSemanticTokensAsync(
                    RequiredParams<FSharpLspSemanticTokensParams>(root), cancellationToken).ConfigureAwait(false),
                "textDocument/documentSymbol" => await session.GetDocumentSymbolsAsync(
                    RequiredParams<FSharpLspDocumentSymbolParams>(root), cancellationToken).ConfigureAwait(false),
                "textDocument/codeAction" => await session.GetCodeActionsAsync(
                    RequiredParams<FSharpLspCodeActionParams>(root), cancellationToken).ConfigureAwait(false),
                _ => throw new FSharpLspMethodNotFoundException(method)
            };
            await SendAsync(new { jsonrpc = "2.0", id, result }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var (code, message) = exception switch
            {
                FSharpLspMethodNotFoundException => (-32601, "Method not found."),
                FSharpLspInvalidParamsException => (-32602, exception.Message),
                FSharpLspContentModifiedException => (-32801, exception.Message),
                FSharpLspLimitExceededException => (-32001, exception.Message),
                FSharpLspSessionUnavailableException => (-32002, exception.Message),
                OperationCanceledException => (-32800, "Request cancelled."),
                _ => (-32603, "Internal LSP error.")
            };
            await SendAsync(
                new { jsonrpc = "2.0", id, error = new { code, message } },
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task HandleNotificationAsync(
        string method,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        if (method == "exit")
        {
            await _connectionCancellation.CancelAsync();
            return;
        }
        if (Volatile.Read(ref _initialized) == 0)
            return;
        if (Volatile.Read(ref _shutdown) != 0)
            return;
        switch (method)
        {
            case "initialized":
            case "$/cancelRequest":
                return;
            case "textDocument/didOpen":
                {
                    var state = await session.DidOpenAsync(
                        RequiredParams<FSharpLspDidOpenParams>(root), cancellationToken).ConfigureAwait(false);
                    await PublishDiagnosticsAsync(state.Uri, cancellationToken).ConfigureAwait(false);
                    return;
                }
            case "textDocument/didChange":
                {
                    var state = await session.DidChangeAsync(
                        RequiredParams<FSharpLspDidChangeParams>(root), cancellationToken).ConfigureAwait(false);
                    await PublishDiagnosticsAsync(state.Uri, cancellationToken).ConfigureAwait(false);
                    return;
                }
            case "textDocument/didClose":
                {
                    var state = await session.DidCloseAsync(
                        RequiredParams<FSharpLspDidCloseParams>(root), cancellationToken).ConfigureAwait(false);
                    await SendAsync(new
                    {
                        jsonrpc = "2.0",
                        method = "textDocument/publishDiagnostics",
                        @params = new { uri = state.Uri, version = state.Version, diagnostics = Array.Empty<object>() }
                    }, cancellationToken).ConfigureAwait(false);
                    return;
                }
        }
    }

    private object Initialize()
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
            throw new FSharpLspInvalidParamsException("initialize can only be called once.");
        return new
        {
            capabilities = new
            {
                positionEncoding = "utf-16",
                textDocumentSync = new { openClose = true, change = 2 },
                completionProvider = new { resolveProvider = false, triggerCharacters = new[] { "." } },
                hoverProvider = true,
                signatureHelpProvider = new
                {
                    triggerCharacters = new[] { "(", "," },
                    retriggerCharacters = new[] { "," }
                },
                semanticTokensProvider = new
                {
                    legend = new
                    {
                        tokenTypes = FSharpLanguageSession.SemanticTokenTypes,
                        tokenModifiers = FSharpLanguageSession.SemanticTokenModifiers
                    },
                    range = false,
                    full = true
                },
                documentSymbolProvider = true,
                codeActionProvider = new
                {
                    codeActionKinds = new[] { "quickfix", "source.organizeImports" }
                }
            },
            serverInfo = new
            {
                name = "SharpLabNext F# FCS adapter",
                version = Worker.FSharp.Compiler.FSharpCompilerFacade.CompilerVersion
            }
        };
    }

    private object? Shutdown()
    {
        Interlocked.Exchange(ref _shutdown, 1);
        return null;
    }

    private async Task PublishDiagnosticsAsync(string uri, CancellationToken cancellationToken)
    {
        var report = await session.GetDiagnosticsAsync(uri, cancellationToken).ConfigureAwait(false);
        await SendAsync(new
        {
            jsonrpc = "2.0",
            method = "textDocument/publishDiagnostics",
            @params = new { uri = report.Uri, version = report.Version, diagnostics = report.Diagnostics }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static T RequiredParams<T>(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var parameters))
            throw new FSharpLspInvalidParamsException("Request parameters are required.");
        return parameters.Deserialize<T>(JsonOptions)
            ?? throw new FSharpLspInvalidParamsException("Request parameters are invalid.");
    }

    private async Task<JsonDocument?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var content = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            if (result.MessageType != WebSocketMessageType.Text)
                throw new FSharpLspInvalidParamsException("LSP WebSocket frames must be UTF-8 text.");
            content.Write(buffer, 0, result.Count);
            if (content.Length > limits.MaxMessageBytes)
                throw new FSharpLspLimitExceededException("LSP message exceeds the configured size limit.");
            if (result.EndOfMessage)
                break;
        }
        try
        {
            return JsonDocument.Parse(content.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException exception)
        {
            throw new FSharpLspInvalidParamsException($"Invalid LSP JSON: {exception.Message}");
        }
    }

    private async Task SendAsync<T>(T payload, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        if (bytes.Length > limits.MaxMessageBytes)
            throw new FSharpLspLimitExceededException("LSP response exceeds the configured size limit.");
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

    private sealed class FSharpLspMethodNotFoundException(string method)
        : FSharpWorkerException($"LSP method '{method}' is not implemented.");
}
