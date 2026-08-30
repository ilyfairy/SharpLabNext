namespace SharpLabNext.Worker.FSharp;

internal static partial class FSharpWorkerLog
{
    [LoggerMessage(EventId = 7101, Level = LogLevel.Warning, Message = "F# reference set preflight failed: {Message}")]
    public static partial void ReferenceSetPreflightFailed(ILogger logger, string message);

    [LoggerMessage(EventId = 7102, Level = LogLevel.Error, Message = "F# build failed for request {RequestId}.")]
    public static partial void BuildFailed(ILogger logger, Exception exception, string requestId);
}
