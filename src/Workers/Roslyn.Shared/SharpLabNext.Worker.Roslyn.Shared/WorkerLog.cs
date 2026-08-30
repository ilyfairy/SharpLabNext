using Microsoft.Extensions.Logging;

namespace SharpLabNext.Worker.Roslyn;

internal static partial class WorkerLog
{
    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Roslyn reference set preflight succeeded for {ReferenceSetCount} set(s).")]
    public static partial void ReferenceSetPreflightSucceeded(ILogger logger, int referenceSetCount);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Error, Message = "Roslyn reference set preflight failed: {PublicMessage}")]
    public static partial void ReferenceSetPreflightFailed(ILogger logger, string publicMessage);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Error, Message = "Roslyn build request {RequestId} failed with an internal worker error. TraceId: {TraceId}")]
    public static partial void InternalBuildFailure(ILogger logger, Exception exception, string requestId, string traceId);
}
