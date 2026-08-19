using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using SharpLabNext.Contracts;
using SharpLabNext.Operations;
using SharpLabNext.PipelineResolver;
using Resolver = SharpLabNext.PipelineResolver.PipelineResolver;

namespace SharpLabNext.Gateway;

internal sealed class OperationCommandWebSocket(
    OperationControlService control,
    OperationStore operations,
    PipelineResolutionRegistry resolutions,
    GatewayDependencyHealthService dependencyHealth,
    ILogger<OperationCommandWebSocket> logger)
{
    private const int MaximumCommandBytes = 2 * 1024 * 1024;
    private const int RuntimeSessionReleaseAttempts = 2;
    private static readonly JsonSerializerOptions SerializerOptions = ContractJson.CreateSerializerOptions();
    private static readonly Action<ILogger, string, Exception?> LogRuntimeSessionReleaseFailure =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(40, nameof(LogRuntimeSessionReleaseFailure)),
            "Could not release the runtime session for operation WebSocket {TraceId}.");

    public async Task RunAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(
                new { Error = "websocket-required", Message = "Operation control requires a WebSocket request." },
                ContractJson.CreateSerializerOptions(),
                context.RequestAborted);
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        var subscriptions = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        var runtimeSession = new RuntimeCommandSession(CreateRuntimeSessionId());
        var sender = SendAsync(socket, outbound.Reader, sessionCancellation.Token);

        try
        {
            while (socket.State == WebSocketState.Open && !sessionCancellation.IsCancellationRequested)
            {
                var payload = await ReceiveMessageAsync(socket, sessionCancellation.Token).ConfigureAwait(false);
                if (payload is null)
                    break;

                OperationCommand? command;
                try
                {
                    command = JsonSerializer.Deserialize<OperationCommand>(payload, SerializerOptions);
                }
                catch (JsonException)
                {
                    await WriteResponseAsync(
                        outbound.Writer,
                        new OperationCommandResponse(
                            "response",
                            string.Empty,
                            false,
                            StatusCodes.Status400BadRequest,
                            null,
                            new OperationCommandProblem("invalid-command", "The operation command is malformed.")),
                        sessionCancellation.Token).ConfigureAwait(false);
                    continue;
                }

                if (command is null || string.IsNullOrWhiteSpace(command.CommandId))
                {
                    await WriteResponseAsync(
                        outbound.Writer,
                        new OperationCommandResponse(
                            "response",
                            command?.CommandId ?? string.Empty,
                            false,
                            StatusCodes.Status400BadRequest,
                            null,
                            new OperationCommandProblem("invalid-command", "CommandId is required.")),
                        sessionCancellation.Token).ConfigureAwait(false);
                    continue;
                }

                await HandleCommandAsync(
                    command,
                    context.TraceIdentifier,
                    outbound.Writer,
                    subscriptions,
                    runtimeSession,
                    sessionCancellation.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
        {
            // Disconnecting ends this command session; finally releases its reusable runtime container.
        }
        finally
        {
            sessionCancellation.Cancel();
            foreach (var subscription in subscriptions.Values)
                subscription.Cancel();
            CancelStartedRuntimeOperations(runtimeSession, "runtime-session-disconnected");
            await TryReleaseRuntimeSessionAsync(
                runtimeSession.Id,
                context.TraceIdentifier,
                CancellationToken.None).ConfigureAwait(false);
            outbound.Writer.TryComplete();
            try
            {
                await sender.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
            {
            }
        }
    }

    private async Task HandleCommandAsync(
        OperationCommand command,
        string traceId,
        ChannelWriter<byte[]> outbound,
        ConcurrentDictionary<string, CancellationTokenSource> subscriptions,
        RuntimeCommandSession runtimeSession,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (command.Type)
            {
                case "resolve-selection":
                    if (command.Request is null)
                    {
                        await WriteProblemAsync(
                            command.CommandId,
                            "invalid-command",
                            "Resolve selection requires request.",
                            outbound,
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    var selectionRequest = command.Request.Value.Deserialize<ResolveSelectionRequest>(SerializerOptions)
                        ?? throw new JsonException("The selection request is missing.");
                    var snapshot = await dependencyHealth.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
                    var resolution = Resolver.Resolve(snapshot.Catalog, selectionRequest, DateTimeOffset.UtcNow);
                    var fingerprint = CreateRuntimeFingerprint(selectionRequest, resolution);
                    if (runtimeSession.Fingerprint is not null &&
                        !string.Equals(runtimeSession.Fingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        CancelStartedRuntimeOperations(runtimeSession, "runtime-session-pipeline-changed");
                        var releasedSessionId = runtimeSession.Rotate(CreateRuntimeSessionId());
                        await TryReleaseRuntimeSessionAsync(
                            releasedSessionId,
                            traceId,
                            CancellationToken.None).ConfigureAwait(false);
                    }

                    runtimeSession.Fingerprint = fingerprint;
                    runtimeSession.PipelineResolutionId = resolution.PipelineResolutionId;
                    resolutions.Store(resolution);
                    await WriteResponseAsync(
                        outbound,
                        new OperationCommandResponse(
                            "response",
                            command.CommandId,
                            true,
                            StatusCodes.Status200OK,
                            resolution,
                            null),
                        cancellationToken).ConfigureAwait(false);
                    return;

                case "start":
                    if (string.IsNullOrWhiteSpace(command.Operation) || command.Request is null)
                    {
                        await WriteProblemAsync(command.CommandId, "invalid-command", "Start requires operation and request.", outbound, cancellationToken);
                        return;
                    }
                    if (command.Operation is "run" or "jit")
                    {
                        var requestPipelineResolutionId = GetRuntimePipelineResolutionId(
                            command.Operation,
                            command.Request.Value);
                        if (string.IsNullOrWhiteSpace(runtimeSession.PipelineResolutionId) ||
                            !string.Equals(
                                runtimeSession.PipelineResolutionId,
                                requestPipelineResolutionId,
                                StringComparison.Ordinal))
                        {
                            await WriteProblemAsync(
                                command.CommandId,
                                "runtime-session-resolution-mismatch",
                                "Run and JIT must use the last pipeline resolution returned by this operation WebSocket.",
                                outbound,
                                cancellationToken).ConfigureAwait(false);
                            return;
                        }
                    }
                    var start = await control.StartAsync(
                        command.Operation,
                        command.Request.Value,
                        traceId,
                        runtimeSession.Id,
                        cancellationToken).ConfigureAwait(false);
                    if (command.Operation is "run" or "jit" &&
                        start.Payload is OperationHandle { IsExisting: false } handle)
                    {
                        runtimeSession.StartedRuntimeOperationIds.Add(handle.OperationId);
                    }
                    await WriteControlResponseAsync(command.CommandId, start, outbound, cancellationToken).ConfigureAwait(false);
                    return;

                case "state":
                    if (string.IsNullOrWhiteSpace(command.OperationId))
                    {
                        await WriteProblemAsync(command.CommandId, "invalid-command", "State requires operationId.", outbound, cancellationToken);
                        return;
                    }
                    await WriteControlResponseAsync(
                        command.CommandId,
                        control.GetState(command.OperationId),
                        outbound,
                        cancellationToken).ConfigureAwait(false);
                    return;

                case "cancel":
                    if (string.IsNullOrWhiteSpace(command.OperationId))
                    {
                        await WriteProblemAsync(command.CommandId, "invalid-command", "Cancel requires operationId.", outbound, cancellationToken);
                        return;
                    }
                    await WriteControlResponseAsync(
                        command.CommandId,
                        control.Cancel(command.OperationId, command.OperationId, command.Reason),
                        outbound,
                        cancellationToken).ConfigureAwait(false);
                    return;

                case "subscribe":
                    if (string.IsNullOrWhiteSpace(command.OperationId) || command.FromSequence is null or < 0)
                    {
                        await WriteProblemAsync(
                            command.CommandId,
                            "invalid-command",
                            "Subscribe requires OperationId and a non-negative FromSequence.",
                            outbound,
                            cancellationToken);
                        return;
                    }
                    if (operations.Get(command.OperationId) is null)
                    {
                        await WriteControlResponseAsync(
                            command.CommandId,
                            new OperationControlResponse(StatusCodes.Status404NotFound, null),
                            outbound,
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    await WriteResponseAsync(
                        outbound,
                        new OperationCommandResponse(
                            "response",
                            command.CommandId,
                            true,
                            StatusCodes.Status200OK,
                            new OperationSubscription(command.OperationId, command.FromSequence.Value),
                            null),
                        cancellationToken).ConfigureAwait(false);
                    StartSubscription(
                        command.OperationId,
                        command.FromSequence.Value,
                        outbound,
                        subscriptions,
                        cancellationToken);
                    return;

                default:
                    await WriteProblemAsync(command.CommandId, "unsupported-command", "The operation command type is not supported.", outbound, cancellationToken);
                    return;
            }
        }
        catch (JsonException)
        {
            await WriteProblemAsync(command.CommandId, "invalid-command-request", "The operation request is malformed.", outbound, cancellationToken);
        }
        catch (SelectionResolutionException exception)
        {
            await WriteResponseAsync(
                outbound,
                new OperationCommandResponse(
                    "response",
                    command.CommandId,
                    false,
                    StatusCodes.Status400BadRequest,
                    null,
                    new SelectionCommandProblem(
                        exception.Code,
                        exception.Message,
                        exception.Field.ToString(),
                        exception.Value)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCapacityExceededException exception)
        {
            await WriteResponseAsync(
                outbound,
                new OperationCommandResponse(
                    "response",
                    command.CommandId,
                    false,
                    StatusCodes.Status429TooManyRequests,
                    null,
                    new OperationCapacityCommandProblem(
                        "operation-capacity-exhausted",
                        "The service is retaining the maximum number of operations. Retry shortly.",
                        exception.MaximumOperations)),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private void CancelStartedRuntimeOperations(RuntimeCommandSession session, string reason)
    {
        foreach (var operationId in session.StartedRuntimeOperationIds)
            _ = control.Cancel(operationId, operationId, reason);
        session.StartedRuntimeOperationIds.Clear();
    }

    private async Task TryReleaseRuntimeSessionAsync(
        string runtimeSessionId,
        string traceId,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= RuntimeSessionReleaseAttempts; attempt++)
        {
            try
            {
                await control.ReleaseRuntimeSessionAsync(runtimeSessionId, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception)
            {
                lastException = exception;
                if (attempt < RuntimeSessionReleaseAttempts)
                    await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken.None).ConfigureAwait(false);
            }
        }

        LogRuntimeSessionReleaseFailure(logger, traceId, lastException);
    }

    private static string CreateRuntimeSessionId() =>
        $"rs_{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";

    private static string? GetRuntimePipelineResolutionId(string operation, JsonElement request) =>
        operation switch
        {
            "run" => request.Deserialize<RunRequest>(SerializerOptions)?.PipelineResolutionId,
            "jit" => request.Deserialize<JitRequest>(SerializerOptions)?.PipelineResolutionId,
            _ => null
        };

    private static string CreateRuntimeFingerprint(
        ResolveSelectionRequest request,
        ResolveSelectionResponse resolution)
    {
        var selection = resolution.EffectiveSelection;
        var plan = resolution.PipelinePlan;
        var descriptor = new RuntimeFingerprintDescriptor(
            selection.LanguageId,
            selection.ToolchainId,
            selection.ReferenceSetId,
            selection.OutputId,
            selection.RuntimeId,
            request.BuildMode,
            plan.SecurityPolicyId,
            plan.ReleaseId,
            plan.Stages.Select(static stage => new RuntimeFingerprintStage(
                stage.Id,
                stage.Kind,
                stage.ProviderId,
                stage.InputArtifactFormat,
                stage.OutputArtifactFormat)).ToArray());
        return Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(descriptor, SerializerOptions)));
    }

    private void StartSubscription(
        string operationId,
        long fromSequence,
        ChannelWriter<byte[]> outbound,
        ConcurrentDictionary<string, CancellationTokenSource> subscriptions,
        CancellationToken sessionCancellation)
    {
        var subscriptionCancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation);
        if (subscriptions.TryGetValue(operationId, out var previous))
            previous.Cancel();
        subscriptions[operationId] = subscriptionCancellation;

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var operationEvent in operations.WatchAsync(
                                   operationId,
                                   fromSequence,
                                   subscriptionCancellation.Token))
                {
                    var message = JsonSerializer.SerializeToUtf8Bytes(
                        new OperationCommandEvent("event", operationId, operationEvent),
                        SerializerOptions);
                    await outbound.WriteAsync(message, subscriptionCancellation.Token).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or ChannelClosedException)
            {
            }
            finally
            {
                if (subscriptions.TryGetValue(operationId, out var current) && ReferenceEquals(current, subscriptionCancellation))
                    subscriptions.TryRemove(operationId, out _);
                subscriptionCancellation.Dispose();
            }
        }, CancellationToken.None);
    }

    private static ValueTask WriteControlResponseAsync(
        string commandId,
        OperationControlResponse controlResponse,
        ChannelWriter<byte[]> outbound,
        CancellationToken cancellationToken) => WriteResponseAsync(
        outbound,
        new OperationCommandResponse(
            "response",
            commandId,
            controlResponse.StatusCode is >= 200 and < 300,
            controlResponse.StatusCode,
            controlResponse.StatusCode is >= 200 and < 300 ? controlResponse.Payload : null,
            controlResponse.StatusCode is >= 200 and < 300 ? null : controlResponse.Payload),
        cancellationToken);

    private static ValueTask WriteProblemAsync(
        string commandId,
        string error,
        string message,
        ChannelWriter<byte[]> outbound,
        CancellationToken cancellationToken) => WriteResponseAsync(
        outbound,
        new OperationCommandResponse(
            "response",
            commandId,
            false,
            StatusCodes.Status400BadRequest,
            null,
            new OperationCommandProblem(error, message)),
        cancellationToken);

    private static ValueTask WriteResponseAsync(
        ChannelWriter<byte[]> outbound,
        OperationCommandResponse response,
        CancellationToken cancellationToken) => outbound.WriteAsync(
        JsonSerializer.SerializeToUtf8Bytes(response, SerializerOptions),
        cancellationToken);

    private static async Task SendAsync(
        WebSocket socket,
        ChannelReader<byte[]> outbound,
        CancellationToken cancellationToken)
    {
        await foreach (var message in outbound.ReadAllAsync(cancellationToken))
        {
            await socket.SendAsync(
                message.AsMemory(),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<byte[]?> ReceiveMessageAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            if (result.MessageType != WebSocketMessageType.Text)
            {
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.InvalidMessageType,
                    "Operation commands must be UTF-8 text.",
                    cancellationToken).ConfigureAwait(false);
                return null;
            }
            if (stream.Length + result.Count > MaximumCommandBytes)
            {
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.MessageTooBig,
                    "Operation command is too large.",
                    cancellationToken).ConfigureAwait(false);
                return null;
            }
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return stream.ToArray();
        }
    }

    private sealed record OperationCommand(
        string Type,
        string CommandId,
        string? Operation,
        string? OperationId,
        long? FromSequence,
        string? Reason,
        JsonElement? Request);

    private sealed record OperationCommandResponse(
        string Type,
        string CommandId,
        bool Ok,
        int Status,
        object? Payload,
        object? Error);

    private sealed record OperationCommandEvent(string Type, string OperationId, OperationEvent Event);

    private sealed record OperationCommandProblem(string Error, string Message);

    private sealed record SelectionCommandProblem(string Error, string Message, string Field, string? Value);

    private sealed record OperationCapacityCommandProblem(string Error, string Message, int MaximumOperations);

    private sealed record OperationSubscription(string OperationId, long FromSequence);

    private sealed class RuntimeCommandSession(string id)
    {
        public string Id { get; private set; } = id;

        public string? Fingerprint { get; set; }

        public string? PipelineResolutionId { get; set; }

        public HashSet<string> StartedRuntimeOperationIds { get; } = new(StringComparer.Ordinal);

        public string Rotate(string nextId)
        {
            var previousId = Id;
            Id = nextId;
            return previousId;
        }
    }

    private sealed record RuntimeFingerprintDescriptor(
        string LanguageId,
        string ToolchainId,
        string ReferenceSetId,
        string OutputId,
        string? RuntimeId,
        BuildConfiguration BuildMode,
        string SecurityPolicyId,
        string ReleaseId,
        IReadOnlyList<RuntimeFingerprintStage> Stages);

    private sealed record RuntimeFingerprintStage(
        string Id,
        PipelineStageKind Kind,
        string ProviderId,
        string? InputArtifactFormat,
        string? OutputArtifactFormat);
}
