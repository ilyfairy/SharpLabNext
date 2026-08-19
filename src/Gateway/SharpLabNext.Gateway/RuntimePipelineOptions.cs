namespace SharpLabNext.Gateway;

public sealed class RuntimePipelineOptions
{
    public const string SectionName = "RuntimePipeline";

    public TimeSpan MaximumDuration { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan ControlRequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan CancellationGracePeriod { get; set; } = TimeSpan.FromSeconds(5);

    public int MaximumEventCharacters { get; set; } = 4 * 1024 * 1024;

    public void Validate()
    {
        if (MaximumDuration <= TimeSpan.Zero || MaximumDuration > TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException("RuntimePipeline:MaximumDuration is outside the supported range.");
        }

        if (ControlRequestTimeout <= TimeSpan.Zero || ControlRequestTimeout > TimeSpan.FromMinutes(1))
        {
            throw new InvalidOperationException("RuntimePipeline:ControlRequestTimeout is outside the supported range.");
        }

        if (CancellationGracePeriod <= TimeSpan.Zero || CancellationGracePeriod > TimeSpan.FromMinutes(1))
        {
            throw new InvalidOperationException("RuntimePipeline:CancellationGracePeriod is outside the supported range.");
        }

        if (MaximumEventCharacters is < 1024 or > 64 * 1024 * 1024)
        {
            throw new InvalidOperationException("RuntimePipeline:MaximumEventCharacters is outside the supported range.");
        }
    }
}
