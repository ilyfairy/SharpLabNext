using System.Buffers;
using System.Net.WebSockets;
using SharpLabNext.Contracts;

namespace SharpLabNext.Gateway;

public sealed class LanguageWebSocketProxy(LanguageSessionGatewayService sessions, LanguageSessionGatewayOptions options)
{
    public async Task RunAsync(string sessionId, HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            return;
        }

        GatewayLanguageSessionConnection connection;
        try
        {
            connection = sessions.Attach(sessionId);
        }
        catch (GatewayLanguageSessionException exception)
        {
            context.Response.StatusCode = exception.StatusCode;
            await context.Response.WriteAsJsonAsync(
                new { Error = exception.Code, Message = exception.Message },
                ContractJson.CreateSerializerOptions(),
                context.RequestAborted);
            return;
        }

        try
        {
            using (connection)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, connection.State.Closed);
                ClientWebSocket upstream;
                try
                {
                    upstream = await sessions.ConnectUpstreamAsync(connection.State, linked.Token).ConfigureAwait(false);
                }
                catch (GatewayLanguageSessionException exception)
                {
                    context.Response.StatusCode = exception.StatusCode;
                    await context.Response.WriteAsJsonAsync(
                        new { Error = exception.Code, Message = exception.Message },
                        ContractJson.CreateSerializerOptions(),
                        context.RequestAborted);
                    return;
                }

                using (upstream)
                {
                    using var browser = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
                    Task<PumpResult>? browserToWorker = null;
                    Task<PumpResult>? workerToBrowser = null;
                    try
                    {
                        browserToWorker = PumpAsync(browser, upstream, linked.Token);
                        workerToBrowser = PumpAsync(upstream, browser, linked.Token);
                        var completed = await Task.WhenAny(browserToWorker, workerToBrowser).ConfigureAwait(false);
                        var result = await completed.ConfigureAwait(false);
                        await ClosePairAsync(browser, upstream, result.Status, result.Description).ConfigureAwait(false);
                        await DrainPumpsAsync(linked, browserToWorker, workerToBrowser).ConfigureAwait(false);
                    }
                    catch (LanguageWebSocketProxyException exception)
                    {
                        await ClosePairAsync(browser, upstream, exception.Status, exception.Message).ConfigureAwait(false);
                        if (browserToWorker is not null && workerToBrowser is not null)
                            await DrainPumpsAsync(linked, browserToWorker, workerToBrowser).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested)
                    {
                        await ClosePairAsync(browser, upstream, WebSocketCloseStatus.NormalClosure, "Language session closed.").ConfigureAwait(false);
                    }
                    catch (WebSocketException)
                    {
                        await ClosePairAsync(browser, upstream, WebSocketCloseStatus.EndpointUnavailable, "Language connection ended.").ConfigureAwait(false);
                        if (browserToWorker is not null && workerToBrowser is not null)
                            await DrainPumpsAsync(linked, browserToWorker, workerToBrowser).ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            await sessions.CloseAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<PumpResult> PumpAsync(WebSocket source, WebSocket destination, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var content = new MemoryStream();
            while (!cancellationToken.IsCancellationRequested && source.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                var result = await source.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return new PumpResult(source.CloseStatus ?? WebSocketCloseStatus.NormalClosure, LimitDescription(source.CloseStatusDescription));
                }
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new LanguageWebSocketProxyException(WebSocketCloseStatus.InvalidMessageType, "LSP requires JSON text messages.");
                }
                if (content.Length + result.Count > options.MaxMessageBytes)
                {
                    throw new LanguageWebSocketProxyException(WebSocketCloseStatus.MessageTooBig, "LSP message exceeds the Gateway limit.");
                }

                content.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;

                if (destination.State != WebSocketState.Open)
                    return new PumpResult(WebSocketCloseStatus.EndpointUnavailable, "Language connection ended.");
                await destination.SendAsync(content.GetBuffer().AsMemory(0, checked((int)content.Length)), WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
                content.SetLength(0);
            }
            return new PumpResult(WebSocketCloseStatus.NormalClosure, "Language session closed.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private Task ClosePairAsync(WebSocket browser, WebSocket upstream, WebSocketCloseStatus status, string? description) =>
        Task.WhenAll(CloseAsync(browser, status, description), CloseAsync(upstream, status, description));

    private static async Task IgnoreFailuresAsync(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or LanguageWebSocketProxyException) { }
    }

    private async Task DrainPumpsAsync(CancellationTokenSource linked, params Task[] tasks)
    {
        var drain = IgnoreFailuresAsync(tasks);
        var timeout = Task.Delay(options.CloseTimeout);
        if (await Task.WhenAny(drain, timeout).ConfigureAwait(false) != drain)
            linked.Cancel();
        await drain.ConfigureAwait(false);
    }

    private async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string? description)
    {
        using var timeout = new CancellationTokenSource(options.CloseTimeout);
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(status, description, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
        {
            socket.Abort();
        }
    }

    private static string? LimitDescription(string? description) =>
        description is { Length: > 120 } ? description[..120] : description;

    private sealed record PumpResult(WebSocketCloseStatus Status, string? Description);

    private sealed class LanguageWebSocketProxyException(WebSocketCloseStatus status, string message) : Exception(message)
    {
        public WebSocketCloseStatus Status { get; } = status;
    }
}
