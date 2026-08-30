using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace SharpLabNext.Observability;

public static class SharpLabNextTelemetry
{
    public const string ActivitySourceName = "SharpLabNext.Observability";

    public const string MeterName = "SharpLabNext.Observability";

    private static readonly string InstrumentationVersion =
        typeof(SharpLabNextTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName, InstrumentationVersion);

    public static Meter Meter { get; } = new(MeterName, InstrumentationVersion);

    public static SharpLabNextMetrics Metrics { get; } = new();
}

public enum SharpLabNextTelemetryOutcome
{
    Succeeded,
    Failed,
    Cancelled,
    TimedOut,
    Overloaded,
    OutOfMemory,
    Crashed
}

public enum SharpLabNextRuntimeOperation
{
    Run,
    Jit
}

public enum SharpLabNextContainerPhase
{
    Create,
    Start
}
