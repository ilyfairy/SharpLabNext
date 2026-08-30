namespace SharpLabNext.Gateway;

public sealed class GatewayTrafficOptions
{
    public const string SectionName = "GatewayTraffic";

    public long MaximumRequestBodyBytes { get; set; } = 4L * 1024 * 1024;

    public int PublicGlobalPermitLimit { get; set; } = 30_000;

    public int PublicPermitLimit { get; set; } = 6_000;

    public TimeSpan PublicWindow { get; set; } = TimeSpan.FromMinutes(1);

    public int RuntimeGlobalPermitLimit { get; set; } = 600;

    public int RuntimeClientPermitLimit { get; set; } = 120;

    public TimeSpan RuntimeWindow { get; set; } = TimeSpan.FromMinutes(1);

    public void Validate()
    {
        if (MaximumRequestBodyBytes is < 1024 * 1024 or > 64L * 1024 * 1024)
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(MaximumRequestBodyBytes)} must be between 1 MiB and 64 MiB.");
        }

        ValidateLimit(PublicGlobalPermitLimit, nameof(PublicGlobalPermitLimit));
        ValidateLimit(PublicPermitLimit, nameof(PublicPermitLimit));
        ValidateLimit(RuntimeGlobalPermitLimit, nameof(RuntimeGlobalPermitLimit));
        ValidateLimit(RuntimeClientPermitLimit, nameof(RuntimeClientPermitLimit));
        ValidateWindow(PublicWindow, nameof(PublicWindow));
        ValidateWindow(RuntimeWindow, nameof(RuntimeWindow));
        if (PublicPermitLimit > PublicGlobalPermitLimit)
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(PublicPermitLimit)} cannot exceed {nameof(PublicGlobalPermitLimit)}.");
        }

        if (RuntimeClientPermitLimit > RuntimeGlobalPermitLimit)
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(RuntimeClientPermitLimit)} cannot exceed {nameof(RuntimeGlobalPermitLimit)}.");
        }
    }

    private static void ValidateLimit(int value, string name)
    {
        if (value is < 1 or > 100_000)
            throw new InvalidOperationException($"{SectionName}:{name} must be between 1 and 100000.");
    }

    private static void ValidateWindow(TimeSpan value, string name)
    {
        if (value < TimeSpan.FromSeconds(1) || value > TimeSpan.FromHours(1))
            throw new InvalidOperationException($"{SectionName}:{name} must be between one second and one hour.");
    }
}
