namespace SharpLabNext.ArtifactStore;

public sealed class ArtifactStoreOptions
{
    public const string SectionName = "ArtifactStore";

    public string RootPath { get; set; } = "data/artifact-store";

    public long MaxContentBytes { get; set; } = 64L * 1024 * 1024;

    public long MaxArtifactBytes { get; set; } = 256L * 1024 * 1024;

    public int MaxArtifactFiles { get; set; } = 2048;

    public TimeSpan DefaultTimeToLive { get; set; } = TimeSpan.FromHours(24);

    public TimeSpan MaximumTimeToLive { get; set; } = TimeSpan.FromDays(30);

    public TimeSpan MaximumLeaseDuration { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);

    public int CleanupBatchSize { get; set; } = 1000;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RootPath);
        if (MaxContentBytes <= 0 || MaxArtifactBytes <= 0 || MaxArtifactBytes < MaxContentBytes)
        {
            throw new InvalidOperationException("Artifact Store size limits are invalid.");
        }

        if (MaxArtifactFiles <= 0)
        {
            throw new InvalidOperationException("MaxArtifactFiles must be positive.");
        }

        if (DefaultTimeToLive <= TimeSpan.Zero || MaximumTimeToLive < DefaultTimeToLive)
        {
            throw new InvalidOperationException("Artifact Store TTL limits are invalid.");
        }

        if (MaximumLeaseDuration <= TimeSpan.Zero || CleanupInterval <= TimeSpan.Zero || CleanupBatchSize <= 0)
        {
            throw new InvalidOperationException("Artifact Store lease or cleanup limits are invalid.");
        }
    }
}
