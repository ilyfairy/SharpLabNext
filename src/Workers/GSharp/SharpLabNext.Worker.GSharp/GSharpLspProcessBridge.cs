using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using SharpLabNext.LanguageWorker.Sdk;

namespace SharpLabNext.Worker.GSharp;

internal sealed partial class GSharpLspProcessBridge(GSharpWorkerSettings settings, int maximumMessageBytes, ILogger<GSharpLspProcessBridge> logger)
{
    private const int MaximumHeaderBytes = 8192;

    public async Task RunAsync(WebSocket socket, GSharpLanguageSessionState session, CancellationToken cancellationToken)
    {
        if (!File.Exists(session.Toolchain.LanguageServerAssemblyPath))
        {
            throw new LanguageWorkerRequestException("language-server-unavailable", "The fixed G# language server is unavailable.", StatusCodes.Status503ServiceUnavailable);
        }

        var sessionRoot = Path.Combine(settings.WorkRoot, $"lsp-{session.Session.SessionId}");
        Directory.CreateDirectory(sessionRoot);
        await WriteInitialFilesAsync(sessionRoot, session.Workspace, cancellationToken).ConfigureAwait(false);
        var startInfo = GSharpProcessEnvironment.Create(settings, sessionRoot);
        startInfo.ArgumentList.Add(session.Toolchain.LanguageServerAssemblyPath);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Process.Start returned false.");
            LspProcessStarted(logger, session.Toolchain.LanguageServerAssemblyPath, sessionRoot, process.Id);
        }
        catch (Exception exception)
        {
            DeleteSessionRoot(sessionRoot);
            throw new LanguageWorkerRequestException("language-server-unavailable", "The fixed G# language server could not be started.", StatusCodes.Status503ServiceUnavailable, exception);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stderrTask = CaptureStandardErrorAsync(process.StandardError.BaseStream);
        var clientToServer = PumpClientToServerAsync(socket, process.StandardInput.BaseStream, linked.Token);
        var serverToClient = PumpServerToClientAsync(process.StandardOutput.BaseStream, socket, linked.Token);
        var processTask = MonitorProcessAsync(process, linked.Token);
        try
        {
            var completed = await Task.WhenAny(clientToServer, serverToClient, processTask).ConfigureAwait(false);
            LspBridgeTaskCompleted(logger, clientToServer.IsCompleted, clientToServer.IsFaulted, serverToClient.IsCompleted, serverToClient.IsFaulted, processTask.IsCompleted, processTask.IsFaulted);
            await completed.ConfigureAwait(false);
            LspProcessState(logger, process.HasExited, process.HasExited ? process.ExitCode : -1);
            if (completed == processTask && process.ExitCode != 0 && !cancellationToken.IsCancellationRequested)
            {
                var stderr = await stderrTask.ConfigureAwait(false);
                throw new LanguageWorkerRequestException("language-server-crashed", $"The G# language server exited unexpectedly. {GSharpProcessEnvironment.PublicText(stderr)}", StatusCodes.Status503ServiceUnavailable);
            }
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            GSharpProcessEnvironment.Kill(process);
            await Task.WhenAll(clientToServer, serverToClient, processTask, stderrTask).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "G# language server connection closed.", CancellationToken.None).ConfigureAwait(false);
                }
                catch (WebSocketException) { }
            }
            DeleteSessionRoot(sessionRoot);
        }
    }

    private async Task PumpClientToServerAsync(WebSocket socket, Stream serverInput, CancellationToken cancellationToken)
    {
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var payload = await ReceiveWebSocketMessageAsync(socket, cancellationToken).ConfigureAwait(false);
            if (payload is null)
                break;
            var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");
            await serverInput.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await serverInput.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await serverInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PumpServerToClientAsync(Stream serverOutput, WebSocket socket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var payload = await ReadLspMessageAsync(serverOutput, cancellationToken).ConfigureAwait(false);
            if (payload is null)
                break;
            if (socket.State != WebSocketState.Open)
                break;
            await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<byte[]?> ReceiveWebSocketMessageAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Min(16 * 1024, maximumMessageBytes)];
        using var content = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidMessageType, "G# LSP messages must be UTF-8 JSON text.", CancellationToken.None).ConfigureAwait(false);
                return null;
            }
            if (content.Length + result.Count > maximumMessageBytes)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "G# LSP message exceeds the configured limit.", CancellationToken.None).ConfigureAwait(false);
                return null;
            }
            content.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return content.ToArray();
        }
    }

    private async Task<byte[]?> ReadLspMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var header = new MemoryStream();
        var matched = 0;
        byte[] delimiter = [13, 10, 13, 10];
        while (header.Length < MaximumHeaderBytes)
        {
            var value = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
            if (value < 0)
                return header.Length == 0 ? null : throw InvalidFrame("The G# LSP header ended unexpectedly.");
            header.WriteByte(checked((byte)value));
            matched = value == delimiter[matched] ? matched + 1 : value == delimiter[0] ? 1 : 0;
            if (matched == delimiter.Length)
                break;
        }
        if (matched != delimiter.Length)
            throw InvalidFrame("The G# LSP header exceeds its size limit.");

        var headerText = Encoding.ASCII.GetString(header.GetBuffer(), 0, checked((int)header.Length));
        int? contentLength = null;
        foreach (var line in headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
                throw InvalidFrame("The G# LSP header is malformed.");
            if (!line[..separator].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;
            if (contentLength is not null || !int.TryParse(line[(separator + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                throw InvalidFrame("The G# LSP Content-Length header is invalid.");
            }
            contentLength = parsed;
        }
        if (contentLength is null or <= 0 || contentLength > maximumMessageBytes)
            throw InvalidFrame("The G# LSP payload exceeds the configured limit.");

        var payload = new byte[contentLength.Value];
        var offset = 0;
        while (offset < payload.Length)
        {
            var read = await stream.ReadAsync(payload.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw InvalidFrame("The G# LSP payload ended unexpectedly.");
            offset += read;
        }
        return payload;
    }

    private async Task<int> MonitorProcessAsync(Process process, CancellationToken cancellationToken)
    {
        var exitTask = process.WaitForExitAsync(CancellationToken.None);
        while (!exitTask.IsCompleted)
        {
            await Task.WhenAny(exitTask, Task.Delay(100, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (exitTask.IsCompleted)
                break;
            try
            {
                process.Refresh();
                if (process.WorkingSet64 > settings.ProcessLimits.MaximumProcessWorkingSetBytes)
                {
                    GSharpProcessEnvironment.Kill(process);
                    throw new LanguageWorkerRequestException("language-server-memory-limit", "The G# language server exceeded its memory limit.", StatusCodes.Status429TooManyRequests);
                }
            }
            catch (InvalidOperationException) when (process.HasExited) { }
        }
        await exitTask.ConfigureAwait(false);
        return process.ExitCode;
    }

    private async Task<string> CaptureStandardErrorAsync(Stream stream)
    {
        using var result = new MemoryStream(Math.Min(settings.ProcessLimits.MaximumProcessOutputBytes, 16 * 1024));
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
                break;
            if (result.Length < settings.ProcessLimits.MaximumProcessOutputBytes)
            {
                result.Write(buffer, 0, Math.Min(read, settings.ProcessLimits.MaximumProcessOutputBytes - checked((int)result.Length)));
            }
        }
        return Encoding.UTF8.GetString(result.GetBuffer(), 0, checked((int)result.Length));
    }

    private static async Task WriteInitialFilesAsync(string sessionRoot, SharpLabNext.Contracts.WorkspaceSnapshot workspace, CancellationToken cancellationToken)
    {
        foreach (var file in workspace.Files)
        {
            var relativePath = GSharpWorkspaceValidator.NormalizeRelativePath(file.Path);
            var path = Path.Combine(sessionRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, file.Text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<int> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        return read == 0 ? -1 : buffer[0];
    }

    private static LanguageWorkerRequestException InvalidFrame(string message) => new("language-server-protocol-error", message, StatusCodes.Status502BadGateway);

    private void DeleteSessionRoot(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LspSessionCleanupFailed(logger, exception, path);
        }
    }

    [LoggerMessage(EventId = 6102, Level = LogLevel.Warning, Message = "Failed to remove G# LSP session directory {SessionRoot}.")]
    private static partial void LspSessionCleanupFailed(ILogger logger, Exception exception, string sessionRoot);

    [LoggerMessage(EventId = 6103, Level = LogLevel.Warning, Message = "G# LSP process started: {Path} {SessionRoot} (pid {Pid}).")]
    private static partial void LspProcessStarted(ILogger logger, string path, string sessionRoot, int pid);

    [LoggerMessage(EventId = 6104, Level = LogLevel.Warning, Message = "G# LSP bridge task completed: client={ClientCompleted}/{ClientFaulted}, server={ServerCompleted}/{ServerFaulted}, process={ProcessCompleted}/{ProcessFaulted}.")]
    private static partial void LspBridgeTaskCompleted(ILogger logger, bool clientCompleted, bool clientFaulted, bool serverCompleted, bool serverFaulted, bool processCompleted, bool processFaulted);

    [LoggerMessage(EventId = 6105, Level = LogLevel.Warning, Message = "G# LSP process state after bridge task: exited={Exited}, exitCode={ExitCode}.")]
    private static partial void LspProcessState(ILogger logger, bool exited, int exitCode);
}
