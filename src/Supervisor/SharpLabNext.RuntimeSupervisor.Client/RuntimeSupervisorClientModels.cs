using SharpLabNext.Contracts;

namespace SharpLabNext.RuntimeSupervisor.Client;

public interface IRuntimeSupervisorClient
{
    Task<OperationHandle> StartRunAsync(RunRequest request, CancellationToken cancellationToken = default);

    Task<OperationHandle> StartRunAsync(RunRequest request, string? runtimeSessionId, CancellationToken cancellationToken = default) => StartRunAsync(request, cancellationToken);

    Task<OperationHandle> StartJitAsync(JitRequest request, CancellationToken cancellationToken = default);

    Task<OperationHandle> StartJitAsync(JitRequest request, string? runtimeSessionId, CancellationToken cancellationToken = default) => StartJitAsync(request, cancellationToken);

    Task<OperationState?> GetOperationAsync(string operationId, CancellationToken cancellationToken = default);

    IAsyncEnumerable<OperationEvent> WatchEventsAsync(string operationId, long fromSequence = 0, CancellationToken cancellationToken = default);

    Task<CancelResult> CancelAsync(string operationId, string? reason = null, CancellationToken cancellationToken = default);

    Task ReleaseSessionAsync(string runtimeSessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed record RuntimeSupervisorClientSettings(TimeSpan ControlRequestTimeout, int MaximumEventCharacters = 4 * 1024 * 1024)
{
    public void Validate()
    {
        if (ControlRequestTimeout <= TimeSpan.Zero || ControlRequestTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(ControlRequestTimeout), "The runtime supervisor control request timeout is outside the supported range.");
        }

        if (MaximumEventCharacters is < 1024 or > 64 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumEventCharacters), "The runtime supervisor event size limit is outside the supported range.");
        }
    }
}

public sealed class RuntimeSupervisorClientException : Exception
{
    public RuntimeSupervisorClientException(WorkerError error, int? statusCode = null, Exception? innerException = null) : base(error.PublicMessage, innerException)
    {
        Error = error;
        StatusCode = statusCode;
    }

    public WorkerError Error { get; }

    public int? StatusCode { get; }
}
