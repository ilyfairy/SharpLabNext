namespace SharpLabNext.Worker.IL;

internal static partial class IlWorkerLog
{
    [LoggerMessage(1, LogLevel.Warning, "Failed to remove IL compiler temporary directory {Path}.")]
    public static partial void TemporaryDirectoryCleanupFailed(
        ILogger logger,
        Exception exception,
        string path);

    [LoggerMessage(2, LogLevel.Warning, "The isolated IL assembler is unavailable for request {RequestId}.")]
    public static partial void AssemblerUnavailable(
        ILogger logger,
        Exception exception,
        string requestId);

    [LoggerMessage(3, LogLevel.Error, "The IL worker failed request {RequestId}.")]
    public static partial void BuildFailed(
        ILogger logger,
        Exception exception,
        string requestId);
}
