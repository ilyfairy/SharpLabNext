namespace SharpLabNext.Gateway;

public sealed class BuildPipelineOptions
{
    public const string SectionName = "BuildPipeline";

    public TimeSpan MaximumDuration { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan ArtifactTimeToLive { get; set; } = TimeSpan.FromHours(1);

    public long MaximumWorkerArtifactBytes { get; set; } = 32L * 1024 * 1024;

    public void Validate()
    {
        if (MaximumDuration <= TimeSpan.Zero || MaximumDuration > TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException("BuildPipeline:MaximumDuration is outside the supported range.");
        }

        if (ArtifactTimeToLive <= TimeSpan.Zero || ArtifactTimeToLive > TimeSpan.FromDays(30))
        {
            throw new InvalidOperationException("BuildPipeline:ArtifactTimeToLive is outside the supported range.");
        }

        if (MaximumWorkerArtifactBytes <= 0 || MaximumWorkerArtifactBytes > 512L * 1024 * 1024)
        {
            throw new InvalidOperationException("BuildPipeline:MaximumWorkerArtifactBytes is outside the supported range.");
        }
    }
}
