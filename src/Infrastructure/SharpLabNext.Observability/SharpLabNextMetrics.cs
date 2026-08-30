using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SharpLabNext.Observability;

public sealed class SharpLabNextMetrics
{
    private readonly Gauge<long> _queueDepth = SharpLabNextTelemetry.Meter.CreateGauge<long>("sharplabnext.queue.depth", "{request}", "Current requests waiting in a bounded queue.");
    private readonly Histogram<double> _queueWaitDuration = SharpLabNextTelemetry.Meter.CreateHistogram<double>("sharplabnext.queue.wait.duration", "s", "Time spent waiting for dispatch from a bounded queue.");
    private readonly Counter<long> _queueRejected = SharpLabNextTelemetry.Meter.CreateCounter<long>("sharplabnext.queue.rejected", "{request}", "Requests rejected before queue admission.");
    private readonly UpDownCounter<long> _activeSessions = SharpLabNextTelemetry.Meter.CreateUpDownCounter<long>("sharplabnext.session.active", "{session}", "Currently active language sessions.");
    private readonly Counter<long> _endedSessions = SharpLabNextTelemetry.Meter.CreateCounter<long>("sharplabnext.session.ended", "{session}", "Language sessions ended by outcome.");
    private readonly Histogram<double> _buildDuration = SharpLabNextTelemetry.Meter.CreateHistogram<double>("sharplabnext.build.duration", "s", "Immutable build request duration.");
    private readonly Histogram<double> _runtimeDuration = SharpLabNextTelemetry.Meter.CreateHistogram<double>("sharplabnext.runtime.duration", "s", "Run or JIT request duration.");
    private readonly Histogram<double> _containerPhaseDuration = SharpLabNextTelemetry.Meter.CreateHistogram<double>("sharplabnext.runtime.container.duration", "s", "Runtime container create or start duration.");
    private readonly Histogram<double> _reaperDuration = SharpLabNextTelemetry.Meter.CreateHistogram<double>("sharplabnext.reaper.duration", "s", "Runtime container reaper pass duration.");
    private readonly Counter<long> _reaperRemoved = SharpLabNextTelemetry.Meter.CreateCounter<long>("sharplabnext.reaper.removed", "{container}", "Stale runtime containers removed by the reaper.");
    private readonly Counter<long> _reaperFailures = SharpLabNextTelemetry.Meter.CreateCounter<long>("sharplabnext.reaper.failures", "{failure}", "Runtime container reaper failures.");

    internal SharpLabNextMetrics() { }

    public void RecordQueueDepth(string queueName, long depth)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(depth);
        var tags = new TagList { { "sharplabnext.queue.name", Dimension(queueName, nameof(queueName)) } };
        _queueDepth.Record(depth, tags);
    }

    public void RecordQueueWait(string queueName, TimeSpan duration, SharpLabNextTelemetryOutcome outcome)
    {
        var tags = new TagList { { "sharplabnext.queue.name", Dimension(queueName, nameof(queueName)) }, { "sharplabnext.outcome", Outcome(outcome) } };
        _queueWaitDuration.Record(DurationSeconds(duration, nameof(duration)), tags);
    }

    public void RecordQueueRejection(string queueName)
    {
        var tags = new TagList { { "sharplabnext.queue.name", Dimension(queueName, nameof(queueName)) } };
        _queueRejected.Add(1, tags);
    }

    public void SessionStarted(string languageId, string toolchainId)
    {
        var tags = SessionTags(languageId, toolchainId);
        _activeSessions.Add(1, tags);
    }

    public void SessionEnded(string languageId, string toolchainId, SharpLabNextTelemetryOutcome outcome)
    {
        var activeTags = SessionTags(languageId, toolchainId);
        _activeSessions.Add(-1, activeTags);
        var endedTags = activeTags;
        endedTags.Add("sharplabnext.outcome", Outcome(outcome));
        _endedSessions.Add(1, endedTags);
    }

    public void RecordBuild(string languageId, string toolchainId, TimeSpan duration, SharpLabNextTelemetryOutcome outcome, bool cacheHit)
    {
        var tags = SessionTags(languageId, toolchainId);
        tags.Add("sharplabnext.outcome", Outcome(outcome));
        tags.Add("sharplabnext.cache.hit", cacheHit);
        _buildDuration.Record(DurationSeconds(duration, nameof(duration)), tags);
    }

    public void RecordRuntime(SharpLabNextRuntimeOperation operation, string runtimeId, TimeSpan duration, SharpLabNextTelemetryOutcome outcome)
    {
        var tags = new TagList { { "sharplabnext.runtime.id", Dimension(runtimeId, nameof(runtimeId)) }, { "sharplabnext.operation.type", RuntimeOperation(operation) }, { "sharplabnext.outcome", Outcome(outcome) } };
        _runtimeDuration.Record(DurationSeconds(duration, nameof(duration)), tags);
    }

    public void RecordContainerPhase(SharpLabNextContainerPhase phase, string runtimeId, TimeSpan duration, SharpLabNextTelemetryOutcome outcome)
    {
        var tags = new TagList { { "sharplabnext.runtime.id", Dimension(runtimeId, nameof(runtimeId)) }, { "sharplabnext.container.phase", ContainerPhase(phase) }, { "sharplabnext.outcome", Outcome(outcome) } };
        _containerPhaseDuration.Record(DurationSeconds(duration, nameof(duration)), tags);
    }

    public void RecordReaperPass(string resourceScope, TimeSpan duration, int removedContainers, int failures)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(removedContainers);
        ArgumentOutOfRangeException.ThrowIfNegative(failures);
        var tags = new TagList { { "sharplabnext.resource.scope", Dimension(resourceScope, nameof(resourceScope)) } };
        _reaperDuration.Record(DurationSeconds(duration, nameof(duration)), tags);
        if (removedContainers > 0)
        {
            _reaperRemoved.Add(removedContainers, tags);
        }
        if (failures > 0)
        {
            _reaperFailures.Add(failures, tags);
        }
    }

    private static TagList SessionTags(string languageId, string toolchainId) =>
        new()
        {
            { "sharplabnext.language.id", Dimension(languageId, nameof(languageId)) },
            { "sharplabnext.toolchain.id", Dimension(toolchainId, nameof(toolchainId)) }
        };

    private static double DurationSeconds(TimeSpan duration, string parameterName)
    {
        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(parameterName, "Telemetry durations cannot be negative.");
        return duration.TotalSeconds;
    }

    private static string Dimension(string value, string parameterName)
    {
        if (!SharpLabNextObservabilityExtensions.IsStableIdentity(value))
        {
            throw new ArgumentException("Metric dimensions must be stable label values of at most 128 characters.", parameterName);
        }
        return value;
    }

    private static string Outcome(SharpLabNextTelemetryOutcome outcome) => outcome switch
    {
        SharpLabNextTelemetryOutcome.Succeeded => "succeeded",
        SharpLabNextTelemetryOutcome.Failed => "failed",
        SharpLabNextTelemetryOutcome.Cancelled => "cancelled",
        SharpLabNextTelemetryOutcome.TimedOut => "timed-out",
        SharpLabNextTelemetryOutcome.Overloaded => "overloaded",
        SharpLabNextTelemetryOutcome.OutOfMemory => "out-of-memory",
        SharpLabNextTelemetryOutcome.Crashed => "crashed",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    private static string RuntimeOperation(SharpLabNextRuntimeOperation operation) => operation switch
    {
        SharpLabNextRuntimeOperation.Run => "run",
        SharpLabNextRuntimeOperation.Jit => "jit",
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static string ContainerPhase(SharpLabNextContainerPhase phase) => phase switch
    {
        SharpLabNextContainerPhase.Create => "create",
        SharpLabNextContainerPhase.Start => "start",
        _ => throw new ArgumentOutOfRangeException(nameof(phase))
    };
}
