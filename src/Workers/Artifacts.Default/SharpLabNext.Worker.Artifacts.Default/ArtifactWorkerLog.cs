namespace SharpLabNext.ArtifactWorker;

internal static partial class ArtifactWorkerLog
{
    [LoggerMessage(
        EventId = 4100,
        Level = LogLevel.Error,
        Message = "Artifact operation {OperationId} failed. TraceId {TraceId}.")]
    public static partial void OperationFailed(
        ILogger logger,
        Exception exception,
        string operationId,
        string traceId);
}
