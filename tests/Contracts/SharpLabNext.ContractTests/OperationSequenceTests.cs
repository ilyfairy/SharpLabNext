using SharpLabNext.Contracts;

namespace SharpLabNext.ContractTests;

public sealed class OperationSequenceTests
{
    [Fact]
    public void ResumedEventStreamMayStartAfterSequenceOne()
    {
        var events = new[]
        {
            Event(8, new ProgressOperationEventPayload("emit", null, 0.5)),
            Event(12, new CompletedOperationEventPayload(OperationCompletionStatus.Completed, TimeSpan.FromSeconds(1)))
        };

        OperationEventStreamContract.Validate(events);
    }

    [Fact]
    public void SequenceMustBeStrictlyIncreasing()
    {
        var events = new[]
        {
            Event(2, new ProgressOperationEventPayload("parse", null, null)),
            Event(2, new ProgressOperationEventPayload("emit", null, null))
        };

        Assert.Throws<InvalidOperationException>(() => OperationEventStreamContract.Validate(events));
    }

    [Fact]
    public void TerminalEventMustBeLast()
    {
        var events = new[]
        {
            Event(1, new CompletedOperationEventPayload(OperationCompletionStatus.Completed, TimeSpan.Zero)),
            Event(2, new ProgressOperationEventPayload("late", null, null))
        };

        Assert.Throws<InvalidOperationException>(() => OperationEventStreamContract.Validate(events));
    }

    [Fact]
    public void StreamCannotMixOperationIds()
    {
        var events = new[]
        {
            Event(1, new AcceptedOperationEventPayload("req-1", OperationKind.Build)),
            Event(2, new ProgressOperationEventPayload("emit", null, null)) with { OperationId = "op-2" }
        };

        Assert.Throws<InvalidOperationException>(() => OperationEventStreamContract.Validate(events));
    }

    private static OperationEvent Event(long sequence, OperationEventPayload payload) =>
        new("op-1", sequence, DateTimeOffset.UnixEpoch.AddSeconds(sequence), "trace-1", payload);
}
