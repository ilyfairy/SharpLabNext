using SharpLabNext.Contracts;
using SharpLabNext.Operations;

namespace SharpLabNext.UnitTests;

public sealed class OperationStoreTests
{
    [Fact]
    public void StartIsIdempotentForTheSameKey()
    {
        var store = new OperationStore();

        var first = store.Start("req-1", "key-1", OperationKind.Build, "trace-1", DateTimeOffset.UnixEpoch);
        var second = store.Start("req-2", "key-1", OperationKind.Run, "trace-2", DateTimeOffset.UnixEpoch.AddSeconds(1));

        Assert.False(first.Handle.IsExisting);
        Assert.True(second.Handle.IsExisting);
        Assert.Equal(first.Handle.OperationId, second.Handle.OperationId);
        Assert.Equal("req-1", second.Handle.RequestId);
    }

    [Fact]
    public async Task WatchCanResumeFromSequenceAndStopsAtTerminalEvent()
    {
        var store = new OperationStore();
        var start = store.Start("req-1", "key-1", OperationKind.Build, "trace-1", DateTimeOffset.UnixEpoch);
        store.Append(start.Handle.OperationId, new ProgressOperationEventPayload("compile", null, 0.5), DateTimeOffset.UnixEpoch.AddSeconds(1));
        store.Append(start.Handle.OperationId, new CompletedOperationEventPayload(OperationCompletionStatus.Completed, TimeSpan.FromSeconds(2)), DateTimeOffset.UnixEpoch.AddSeconds(2));

        var events = new List<OperationEvent>();
        await foreach (var operationEvent in store.WatchAsync(start.Handle.OperationId, 1, TestContext.Current.CancellationToken))
            events.Add(operationEvent);

        Assert.Equal([2L, 3L], events.Select(static item => item.Sequence));
        Assert.IsType<CompletedOperationEventPayload>(events[^1].Payload);
    }

    [Fact]
    public void TerminalOperationRejectsFurtherEvents()
    {
        var store = new OperationStore();
        var start = store.Start("req-1", "key-1", OperationKind.Build, "trace-1", DateTimeOffset.UnixEpoch);
        store.Append(start.Handle.OperationId, new CompletedOperationEventPayload(OperationCompletionStatus.Completed, TimeSpan.Zero), DateTimeOffset.UnixEpoch);

        Assert.Throws<InvalidOperationException>(() => store.Append(start.Handle.OperationId, new ProgressOperationEventPayload("late", null, null), DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void EventLimitReservesTheFinalSlotForATerminalFailure()
    {
        var store = new OperationStore(new OperationStoreOptions { MaximumEventsPerOperation = 3 });
        var start = store.Start("req-1", "key-1", OperationKind.Build, "trace-1", DateTimeOffset.UnixEpoch);
        store.Append(start.Handle.OperationId, new ProgressOperationEventPayload("first", null, 0.25), DateTimeOffset.UnixEpoch.AddSeconds(1));

        var terminal = store.Append(start.Handle.OperationId, new ProgressOperationEventPayload("overflow", null, 0.5), DateTimeOffset.UnixEpoch.AddSeconds(2));

        var failure = Assert.IsType<FailedOperationEventPayload>(terminal.Payload);
        Assert.Equal("operation-event-limit-exceeded", failure.Error.Code);
        Assert.Equal(WorkerErrorCategory.ResourceExhausted, failure.Error.Category);
        Assert.Equal(OperationStatus.Failed, store.Get(start.Handle.OperationId)?.Status);
        Assert.Equal(3, store.GetEvents(start.Handle.OperationId)?.Count);
    }

    [Fact]
    public void CancellationPropagatesToOperationToken()
    {
        var store = new OperationStore();
        var start = store.Start("req-1", "key-1", OperationKind.Run, "trace-1", DateTimeOffset.UnixEpoch);

        var result = store.Cancel(start.Handle.OperationId, "user", DateTimeOffset.UnixEpoch.AddSeconds(1));

        Assert.Equal(CancelDisposition.Accepted, result.Disposition);
        Assert.True(start.CancellationToken.IsCancellationRequested);
        Assert.Equal(OperationStatus.Cancelling, store.Get(start.Handle.OperationId)?.Status);
    }

    [Fact]
    public void CapacityExhaustionDoesNotEvictOrLeakAnActiveOperation()
    {
        var store = new OperationStore(new OperationStoreOptions { MaximumOperations = 1, TerminalOperationTimeToLive = TimeSpan.FromHours(1) });
        var active = store.Start("active-request", "active-key", OperationKind.Build, "active-trace", DateTimeOffset.UnixEpoch);

        var exception = Assert.Throws<OperationCapacityExceededException>(() => store.Start("rejected-request", "rejected-key", OperationKind.Run, "rejected-trace", DateTimeOffset.UnixEpoch.AddDays(1)));

        Assert.Equal(1, exception.MaximumOperations);
        Assert.Equal(OperationStatus.Accepted, store.Get(active.Handle.OperationId)?.Status);

        store.Append(active.Handle.OperationId, new CompletedOperationEventPayload(OperationCompletionStatus.Completed, TimeSpan.Zero), DateTimeOffset.UnixEpoch.AddDays(1));
        Assert.Equal(1, store.RemoveExpiredTerminalOperations(DateTimeOffset.UnixEpoch.AddDays(2)));

        var retried = store.Start("retried-request", "rejected-key", OperationKind.Run, "retried-trace", DateTimeOffset.UnixEpoch.AddDays(2));
        Assert.False(retried.Handle.IsExisting);
    }

    [Fact]
    public void CleanupRemovesOnlyExpiredTerminalOperationsAndTheirIdempotencyKeys()
    {
        var store = new OperationStore(new OperationStoreOptions { MaximumOperations = 4, TerminalOperationTimeToLive = TimeSpan.FromHours(1) });
        var expired = store.Start("expired", "expired-key", OperationKind.Build, "trace-1", DateTimeOffset.UnixEpoch);
        var active = store.Start("active", "active-key", OperationKind.Run, "trace-2", DateTimeOffset.UnixEpoch);
        var recent = store.Start("recent", "recent-key", OperationKind.Jit, "trace-3", DateTimeOffset.UnixEpoch);
        store.Append(expired.Handle.OperationId, new CompletedOperationEventPayload(OperationCompletionStatus.Completed, TimeSpan.Zero), DateTimeOffset.UnixEpoch);
        store.Append(recent.Handle.OperationId, new FailedOperationEventPayload(new WorkerError("recent-failure", WorkerErrorCategory.Internal, "Recent failure.", false, false, "trace-3", "test", "test")), DateTimeOffset.UnixEpoch.AddMinutes(90));

        var removed = store.RemoveExpiredTerminalOperations(DateTimeOffset.UnixEpoch.AddHours(2));

        Assert.Equal(1, removed);
        Assert.Null(store.Get(expired.Handle.OperationId));
        Assert.Equal(OperationStatus.Accepted, store.Get(active.Handle.OperationId)?.Status);
        Assert.Equal(OperationStatus.Failed, store.Get(recent.Handle.OperationId)?.Status);
        var replacement = store.Start("replacement", "expired-key", OperationKind.Build, "trace-4", DateTimeOffset.UnixEpoch.AddHours(2));
        var recentReplay = store.Start("recent-replay", "recent-key", OperationKind.Build, "trace-5", DateTimeOffset.UnixEpoch.AddHours(2));
        Assert.False(replacement.Handle.IsExisting);
        Assert.True(recentReplay.Handle.IsExisting);
        Assert.Equal(recent.Handle.OperationId, recentReplay.Handle.OperationId);
    }

    [Fact]
    public void QueueOverloadFailuresUseTheShortRetentionWindow()
    {
        var store = new OperationStore(new OperationStoreOptions { MaximumOperations = 3, TerminalOperationTimeToLive = TimeSpan.FromHours(1), OverloadOperationTimeToLive = TimeSpan.FromMinutes(1) });
        var overloaded = store.Start("overloaded", "overloaded-key", OperationKind.Build, "trace-1", DateTimeOffset.UnixEpoch);
        var ordinaryFailure = store.Start("ordinary", "ordinary-key", OperationKind.Build, "trace-2", DateTimeOffset.UnixEpoch);
        var active = store.Start("active", "active-key", OperationKind.Build, "trace-3", DateTimeOffset.UnixEpoch);
        store.Append(overloaded.Handle.OperationId, new FailedOperationEventPayload(new WorkerError("operation-queue-full", WorkerErrorCategory.ResourceExhausted, "Queue full.", true, true, "trace-1", "gateway", "unknown")), DateTimeOffset.UnixEpoch);
        store.Append(ordinaryFailure.Handle.OperationId, new FailedOperationEventPayload(new WorkerError("ordinary-failure", WorkerErrorCategory.Internal, "Ordinary failure.", false, false, "trace-2", "gateway", "unknown")), DateTimeOffset.UnixEpoch);

        var removed = store.RemoveExpiredTerminalOperations(DateTimeOffset.UnixEpoch.AddMinutes(2));

        Assert.Equal(1, removed);
        Assert.Null(store.Get(overloaded.Handle.OperationId));
        Assert.Equal(OperationStatus.Failed, store.Get(ordinaryFailure.Handle.OperationId)?.Status);
        Assert.Equal(OperationStatus.Accepted, store.Get(active.Handle.OperationId)?.Status);
        Assert.False(store.Start("replacement", "overloaded-key", OperationKind.Build, "trace-4", DateTimeOffset.UnixEpoch.AddMinutes(2)).Handle.IsExisting);
    }

    [Fact]
    public void OverloadRetentionCannotExceedTerminalRetention()
    {
        Assert.Throws<InvalidOperationException>(() => new OperationStore(new OperationStoreOptions { TerminalOperationTimeToLive = TimeSpan.FromMinutes(1), OverloadOperationTimeToLive = TimeSpan.FromMinutes(2) }));
    }
}
