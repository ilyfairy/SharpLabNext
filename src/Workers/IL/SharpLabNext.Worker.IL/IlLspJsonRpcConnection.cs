using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.IL;

internal sealed class IlLspJsonRpcConnection(
    WebSocket socket,
    IlLanguageSession session,
    IlLspLimits limits,
    CancellationToken requestAborted) : IAsyncDisposable
{
    private const int MaxConcurrentRequests = 8;
    private const int MaxPendingWorkspaceNotifications = 128;
    private static readonly JsonSerializerOptions JsonOptions = ContractJson.CreateLspSerializerOptions();

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _diagnosticsSignal = new(0, 1);
    private readonly ConcurrentDictionary<string, InFlightRequest> _requests = new(StringComparer.Ordinal);
    private readonly Channel<WorkspaceNotification> _workspaceNotifications = Channel.CreateBounded<WorkspaceNotification>(
        new BoundedChannelOptions(MaxPendingWorkspaceNotifications)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly object _diagnosticsLock = new();
    private readonly CancellationTokenSource _connectionCancellation =
        CancellationTokenSource.CreateLinkedTokenSource(requestAborted, session.LifetimeToken);
    private CancellationTokenSource? _activeDiagnostics;
    private Task? _diagnosticsTask;
    private Task? _workspaceTask;
    private Task _workspaceBarrier = Task.CompletedTask;
    private int _diagnosticsRequested;
    private int _initialized;
    private int _shutdown;

    public async Task RunAsync()
    {
        using var connectionLease = session.AttachConnection();
        _workspaceTask = RunWorkspaceLoopAsync();
        _diagnosticsTask = RunDiagnosticsLoopAsync();
        try
        {
            while (!_connectionCancellation.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var message = await ReceiveAsync(_connectionCancellation.Token).ConfigureAwait(false);
                if (message is null)
                    break;
                await DispatchAsync(message.RootElement, _connectionCancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_connectionCancellation.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            CancelIgnoringDisposal(_connectionCancellation);
            _workspaceNotifications.Writer.TryComplete();
            CancelDiagnosticsComputation();
            foreach (var request in _requests.Values)
                CancelIgnoringDisposal(request.Cancellation);

            var tasks = _requests.Values.Select(static request => request.Task).ToList();
            if (_diagnosticsTask is not null)
                tasks.Add(_diagnosticsTask);
            if (_workspaceTask is not null)
                tasks.Add(_workspaceTask);
            await Task.WhenAll(tasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            DisposeRequests();
            await CompleteCloseHandshakeAsync().ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        CancelIgnoringDisposal(_connectionCancellation);
        _workspaceNotifications.Writer.TryComplete();
        CancelDiagnosticsComputation();
        foreach (var request in _requests.Values)
            CancelIgnoringDisposal(request.Cancellation);
        _diagnosticsSignal.Dispose();
        _sendLock.Dispose();
        _connectionCancellation.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task DispatchAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("jsonrpc", out var jsonRpc) || jsonRpc.GetString() != "2.0" ||
            !root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
        {
            await SendErrorAsync(GetOptionalId(root), -32600, "Invalid Request", cancellationToken).ConfigureAwait(false);
            return;
        }

        var method = methodElement.GetString()!;
        var message = root.Clone();
        if (method == "$/cancelRequest")
        {
            CancelRequest(message);
            return;
        }

        if (!root.TryGetProperty("id", out var id))
        {
            await HandleNotificationAsync(method, message, cancellationToken).ConfigureAwait(false);
            return;
        }

        var idClone = id.Clone();
        if (idClone.ValueKind is not JsonValueKind.String and not JsonValueKind.Number)
        {
            await SendErrorAsync(idClone, -32600, "Request id must be a string or number.", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (Volatile.Read(ref _initialized) == 0 && method != "initialize")
        {
            await SendErrorAsync(
                idClone,
                -32002,
                "The IL LSP connection has not been initialized.",
                cancellationToken).ConfigureAwait(false);
            return;
        }
        if (Volatile.Read(ref _shutdown) != 0)
        {
            await SendErrorAsync(
                idClone,
                -32002,
                "The IL LSP connection has been shut down.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_requests.Count >= MaxConcurrentRequests)
        {
            await SendErrorAsync(idClone, -32001, "Too many concurrent IL LSP requests.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var idKey = idClone.GetRawText();
        var request = new InFlightRequest(
            CancellationTokenSource.CreateLinkedTokenSource(_connectionCancellation.Token));
        if (!_requests.TryAdd(idKey, request))
        {
            request.Cancellation.Dispose();
            await SendErrorAsync(idClone, -32600, "A request with the same id is already active.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var workspaceBarrier = _workspaceBarrier;
        if (method == "shutdown")
            Shutdown();
        request.Task = method is "initialize" or "shutdown"
            ? ProcessRequestAsync(idClone, method, message, workspaceBarrier, request.Cancellation.Token)
            : Task.Run(
                () => ProcessRequestAsync(idClone, method, message, workspaceBarrier, request.Cancellation.Token),
                CancellationToken.None);
        _ = CompleteRequestAsync(idKey, request);
    }

    private async Task CompleteRequestAsync(string idKey, InFlightRequest request)
    {
        await request.Task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (_requests.TryRemove(idKey, out var completed))
            completed.Cancellation.Dispose();
    }

    private async Task ProcessRequestAsync(
        JsonElement id,
        string method,
        JsonElement root,
        Task workspaceBarrier,
        CancellationToken cancellationToken)
    {
        try
        {
            if (method != "initialize")
                await workspaceBarrier.WaitAsync(cancellationToken).ConfigureAwait(false);
            object? result = method switch
            {
                "initialize" => Initialize(),
                "shutdown" => Shutdown(),
                "textDocument/completion" => await session.GetCompletionsAsync(
                    RequiredParams<IlLspCompletionParams>(root), cancellationToken).ConfigureAwait(false),
                "textDocument/hover" => await session.GetHoverAsync(
                    RequiredParams<IlLspTextDocumentPositionParams>(root), cancellationToken).ConfigureAwait(false),
                "textDocument/definition" => await session.GetDefinitionAsync(
                    RequiredParams<IlLspTextDocumentPositionParams>(root), cancellationToken).ConfigureAwait(false),
                "workspace/symbol" => await session.GetWorkspaceSymbolsAsync(
                    RequiredParams<IlLspWorkspaceSymbolParams>(root),
                    limits.MaxDocumentSymbols,
                    cancellationToken).ConfigureAwait(false),
                "textDocument/signatureHelp" => await session.GetSignatureHelpAsync(
                    RequiredParams<IlLspTextDocumentPositionParams>(root), cancellationToken).ConfigureAwait(false),
                "textDocument/semanticTokens/full" => await session.GetSemanticTokensAsync(
                    RequiredParams<IlLspSemanticTokensParams>(root), cancellationToken).ConfigureAwait(false),
                "textDocument/documentSymbol" => await session.GetDocumentSymbolsAsync(
                    RequiredParams<IlLspDocumentSymbolParams>(root), cancellationToken).ConfigureAwait(false),
                "textDocument/codeAction" => await session.GetCodeActionsAsync(
                    RequiredParams<IlLspCodeActionParams>(root),
                    limits.MaxCodeActions,
                    cancellationToken).ConfigureAwait(false),
                "textDocument/foldingRange" => await session.GetFoldingRangesAsync(
                    RequiredParams<IlLspFoldingRangeParams>(root), cancellationToken).ConfigureAwait(false),
                _ => throw new IlLspMethodNotFoundException($"LSP method '{method}' is not supported.")
            };
            await SendResultAsync(id, result, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await SendErrorIgnoringCancellationAsync(id, -32800, "Request cancelled.").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var (code, message) = exception switch
            {
                IlLspMethodNotFoundException => (-32601, exception.Message),
                IlLspInvalidParamsException => (-32602, exception.Message),
                IlLspContentModifiedException => (-32801, exception.Message),
                IlLspLimitExceededException => (-32001, exception.Message),
                IlLspSessionUnavailableException => (-32002, exception.Message),
                JsonException => (-32602, "Invalid request parameters."),
                _ => (-32603, "Internal IL LSP error.")
            };
            await SendErrorIgnoringCancellationAsync(id, code, message).ConfigureAwait(false);
        }
    }

    private async Task HandleNotificationAsync(
        string method,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        if (method == "exit")
        {
            CancelIgnoringDisposal(_connectionCancellation);
            return;
        }
        if (Volatile.Read(ref _initialized) == 0 || Volatile.Read(ref _shutdown) != 0)
            return;
        switch (method)
        {
            case "initialized":
                return;
            case "textDocument/didOpen":
                await QueueWorkspaceNotificationAsync(
                    new WorkspaceNotification(
                        WorkspaceNotificationKind.DidOpen,
                        RequiredParams<IlLspDidOpenParams>(root),
                        NewWorkspaceCompletion()),
                    cancellationToken).ConfigureAwait(false);
                return;
            case "textDocument/didChange":
                await QueueWorkspaceNotificationAsync(
                    new WorkspaceNotification(
                        WorkspaceNotificationKind.DidChange,
                        RequiredParams<IlLspDidChangeParams>(root),
                        NewWorkspaceCompletion()),
                    cancellationToken).ConfigureAwait(false);
                return;
            case "textDocument/didClose":
                await QueueWorkspaceNotificationAsync(
                    new WorkspaceNotification(
                        WorkspaceNotificationKind.DidClose,
                        RequiredParams<IlLspDidCloseParams>(root),
                        NewWorkspaceCompletion()),
                    cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    private TaskCompletionSource NewWorkspaceCompletion()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _workspaceBarrier = completion.Task;
        return completion;
    }

    private async Task QueueWorkspaceNotificationAsync(
        WorkspaceNotification notification,
        CancellationToken cancellationToken)
    {
        if (_workspaceNotifications.Writer.TryWrite(notification))
            return;

        notification.Completion.TrySetException(
            new IlLspLimitExceededException("Too many pending IL LSP workspace notifications."));
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    "Too many pending IL LSP workspace notifications.",
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            CancelIgnoringDisposal(_connectionCancellation);
        }
    }

    private async Task RunWorkspaceLoopAsync()
    {
        try
        {
            await foreach (var notification in _workspaceNotifications.Reader.ReadAllAsync(_connectionCancellation.Token))
            {
                try
                {
                    await ApplyWorkspaceNotificationAsync(notification, _connectionCancellation.Token).ConfigureAwait(false);
                    notification.Completion.TrySetResult();
                }
                catch (OperationCanceledException) when (_connectionCancellation.IsCancellationRequested)
                {
                    notification.Completion.TrySetCanceled(_connectionCancellation.Token);
                    break;
                }
                catch (Exception exception)
                {
                    notification.Completion.TrySetException(exception);
                    CancelIgnoringDisposal(_connectionCancellation);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (_connectionCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            while (_workspaceNotifications.Reader.TryRead(out var pending))
                pending.Completion.TrySetCanceled(_connectionCancellation.Token);
        }
    }

    private async Task ApplyWorkspaceNotificationAsync(
        WorkspaceNotification notification,
        CancellationToken cancellationToken)
    {
        CancelDiagnosticsComputation();
        switch (notification.Kind)
        {
            case WorkspaceNotificationKind.DidOpen:
                await session.DidOpenAsync(
                    (IlLspDidOpenParams)notification.Parameters,
                    cancellationToken).ConfigureAwait(false);
                break;
            case WorkspaceNotificationKind.DidChange:
                await session.DidChangeAsync(
                    (IlLspDidChangeParams)notification.Parameters,
                    cancellationToken).ConfigureAwait(false);
                break;
            case WorkspaceNotificationKind.DidClose:
            {
                var state = await session.DidCloseAsync(
                    (IlLspDidCloseParams)notification.Parameters,
                    cancellationToken).ConfigureAwait(false);
                await SendNotificationAsync(
                    "textDocument/publishDiagnostics",
                    new { uri = state.Uri, version = state.Version, diagnostics = Array.Empty<IlLspDiagnostic>() },
                    cancellationToken).ConfigureAwait(false);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(notification), notification.Kind, "Unknown workspace notification kind.");
        }
        RequestWorkspaceDiagnostics();
    }

    private object Initialize()
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
            throw new IlLspInvalidParamsException("initialize can only be called once per connection.");
        return new
        {
            capabilities = new
            {
                positionEncoding = ContractConventions.TextCoordinateEncoding,
                textDocumentSync = new { openClose = true, change = 2, save = false },
                completionProvider = new
                {
                    resolveProvider = false,
                    triggerCharacters = IlLanguageService.CompletionTriggerCharacters
                },
                hoverProvider = true,
                definitionProvider = true,
                signatureHelpProvider = new
                {
                    triggerCharacters = IlLanguageService.SignatureHelpTriggerCharacters
                },
                semanticTokensProvider = new
                {
                    legend = new
                    {
                        tokenTypes = IlLanguageService.SemanticTokenTypes,
                        tokenModifiers = IlLanguageService.SemanticTokenModifiers
                    },
                    range = false,
                    full = true
                },
                documentSymbolProvider = true,
                workspaceSymbolProvider = true,
                codeActionProvider = new
                {
                    codeActionKinds = new[] { "quickfix", "refactor.rewrite" }
                },
                foldingRangeProvider = true
            },
            serverInfo = new { name = "SharpLabNext IL language server", version = "1.0" }
        };
    }

    private object? Shutdown()
    {
        Interlocked.Exchange(ref _shutdown, 1);
        return null;
    }

    private void RequestWorkspaceDiagnostics()
    {
        Interlocked.Exchange(ref _diagnosticsRequested, 1);
        try
        {
            if (_diagnosticsSignal.CurrentCount == 0)
                _diagnosticsSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private async Task RunDiagnosticsLoopAsync()
    {
        try
        {
            while (true)
            {
                await _diagnosticsSignal.WaitAsync(_connectionCancellation.Token).ConfigureAwait(false);
                while (Interlocked.Exchange(ref _diagnosticsRequested, 0) != 0)
                    await PublishWorkspaceDiagnosticsAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_connectionCancellation.IsCancellationRequested)
        {
        }
    }

    private async Task PublishWorkspaceDiagnosticsAsync()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_connectionCancellation.Token);
        lock (_diagnosticsLock)
            _activeDiagnostics = cancellation;
        try
        {
            await Task.Delay(limits.DiagnosticsDebounceMilliseconds, cancellation.Token).ConfigureAwait(false);
            var reports = await session.GetWorkspaceDiagnosticsAsync(cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _diagnosticsRequested) != 0)
                return;
            foreach (var report in reports)
            {
                await SendNotificationAsync(
                    "textDocument/publishDiagnostics",
                    new { uri = report.Uri, version = report.Version, diagnostics = report.Diagnostics },
                    cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_diagnosticsLock)
            {
                if (ReferenceEquals(_activeDiagnostics, cancellation))
                    _activeDiagnostics = null;
            }
        }
    }

    private void CancelDiagnosticsComputation()
    {
        CancellationTokenSource? cancellation;
        lock (_diagnosticsLock)
            cancellation = _activeDiagnostics;
        if (cancellation is not null)
            CancelIgnoringDisposal(cancellation);
    }

    private void CancelRequest(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var parameters) ||
            parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("id", out var id) ||
            id.ValueKind is not JsonValueKind.String and not JsonValueKind.Number)
        {
            return;
        }
        if (_requests.TryGetValue(id.GetRawText(), out var request))
            CancelIgnoringDisposal(request.Cancellation);
    }

    private static T RequiredParams<T>(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var parameters))
            throw new IlLspInvalidParamsException("Request parameters are required.");
        return parameters.Deserialize<T>(JsonOptions)
            ?? throw new IlLspInvalidParamsException("Request parameters are invalid.");
    }

    private async Task<JsonDocument?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var content = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            if (result.MessageType != WebSocketMessageType.Text)
            {
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.InvalidMessageType,
                    "IL LSP requires UTF-8 JSON text messages.",
                    cancellationToken).ConfigureAwait(false);
                return null;
            }
            if (content.Length + result.Count > limits.MaxMessageBytes)
            {
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.MessageTooBig,
                    "IL LSP message exceeds the configured size limit.",
                    cancellationToken).ConfigureAwait(false);
                return null;
            }
            content.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                break;
        }
        try
        {
            return JsonDocument.Parse(content.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException)
        {
            await SendErrorAsync(null, -32700, "Parse error", cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    private Task SendResultAsync(JsonElement id, object? result, CancellationToken cancellationToken) =>
        SendAsync(new JsonRpcResult("2.0", id, result), cancellationToken);

    private Task SendErrorAsync(JsonElement? id, int code, string message, CancellationToken cancellationToken) =>
        SendAsync(new JsonRpcErrorResult("2.0", id, new JsonRpcError(code, message)), cancellationToken);

    private async Task SendErrorIgnoringCancellationAsync(JsonElement id, int code, string message)
    {
        try
        {
            await SendErrorAsync(id, code, message, _connectionCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
    }

    private Task SendNotificationAsync(string method, object parameters, CancellationToken cancellationToken) =>
        SendAsync(new JsonRpcNotification("2.0", method, parameters), cancellationToken);

    private async Task SendAsync(object payload, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        if (bytes.Length > limits.MaxMessageBytes)
            throw new IlLspLimitExceededException("IL LSP response exceeds the configured message limit.");
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task CompleteCloseHandshakeAsync()
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "IL LSP connection closed.",
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (WebSocketException)
        {
        }
    }

    private void DisposeRequests()
    {
        foreach (var id in _requests.Keys)
        {
            if (_requests.TryRemove(id, out var request))
                request.Cancellation.Dispose();
        }
    }

    private static JsonElement? GetOptionalId(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var id) ? id.Clone() : null;

    private static void CancelIgnoringDisposal(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed class InFlightRequest(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; set; } = Task.CompletedTask;
    }

    private sealed record WorkspaceNotification(
        WorkspaceNotificationKind Kind,
        object Parameters,
        TaskCompletionSource Completion);

    private enum WorkspaceNotificationKind
    {
        DidOpen,
        DidChange,
        DidClose
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
