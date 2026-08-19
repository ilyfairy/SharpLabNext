namespace SharpLabNext.Gateway;

public sealed class ArtifactPipelineOptions
{
    public const string SectionName = "ArtifactPipeline";

    public TimeSpan MaximumDuration { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan ControlRequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(25);

    public TimeSpan CancellationGracePeriod { get; set; } = TimeSpan.FromSeconds(5);

    public int MaximumEventsPerPoll { get; set; } = 2_048;

    public long MaximumPublicContentBytes { get; set; } = 16L * 1024 * 1024;

    public void Validate()
    {
        ValidateDuration(MaximumDuration, nameof(MaximumDuration), TimeSpan.FromMinutes(5));
        ValidateDuration(ControlRequestTimeout, nameof(ControlRequestTimeout), TimeSpan.FromMinutes(1));
        ValidateDuration(PollInterval, nameof(PollInterval), TimeSpan.FromSeconds(5));
        ValidateDuration(CancellationGracePeriod, nameof(CancellationGracePeriod), TimeSpan.FromMinutes(1));
        if (MaximumEventsPerPoll is <= 0 or > 100_000)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(MaximumEventsPerPoll)} is outside the supported range.");
        }
        if (MaximumPublicContentBytes is <= 0 or > 128L * 1024 * 1024)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(MaximumPublicContentBytes)} is outside the supported range.");
        }
    }

    private static void ValidateDuration(TimeSpan value, string name, TimeSpan maximum)
    {
        if (value <= TimeSpan.Zero || value > maximum)
            throw new InvalidOperationException($"{SectionName}:{name} is outside the supported range.");
    }
}
