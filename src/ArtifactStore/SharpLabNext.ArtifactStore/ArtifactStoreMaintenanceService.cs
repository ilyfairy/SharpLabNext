namespace SharpLabNext.ArtifactStore;

internal sealed class ArtifactStoreMaintenanceService(LocalArtifactStore store, Microsoft.Extensions.Options.IOptions<ArtifactStoreOptions> options, ILogger<ArtifactStoreMaintenanceService> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> CleanupFailed = LoggerMessage.Define(LogLevel.Error, new EventId(1101, nameof(CleanupFailed)), "Artifact Store background cleanup failed.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.CleanupInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                _ = await store.CollectGarbageAsync(options.Value.CleanupBatchSize, options.Value.CleanupBatchSize * 5, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                CleanupFailed(logger, exception);
            }
        }
    }
}
