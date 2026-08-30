using SharpLabNext.Contracts;
using SharpLabNext.Operations;

namespace SharpLabNext.UnitTests;

public sealed class BoundedOperationSchedulerTests
{
    [Fact]
    public async Task WorkerConcurrencyIsNeverExceeded()
    {
        var operations = new OperationStore();
        await using var scheduler = CreateScheduler(operations, queueCapacity: 4, workerConcurrency: 2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothWorkersEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = 0;
        var maximum = 0;
        var starts = Enumerable.Range(0, 4).Select(index => Start(operations, index)).ToArray();

        foreach (var operation in starts)
        {
            Assert.True(scheduler.TryQueue(operation, async () =>
            {
                var concurrent = Interlocked.Increment(ref current);
                UpdateMaximum(ref maximum, concurrent);
                if (concurrent == 2)
                {
                    bothWorkersEntered.TrySetResult();
                }

                await release.Task.WaitAsync(operation.CancellationToken);
                Interlocked.Decrement(ref current);
                Complete(operations, operation);
            }));
        }

        await bothWorkersEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(2, Volatile.Read(ref maximum));
        release.TrySetResult();
        await WaitForAllTerminalAsync(operations, starts);
        Assert.Equal(2, Volatile.Read(ref maximum));
    }

    [Fact]
    public async Task CancellationBeforeDispatchSkipsTheCallback()
    {
        var operations = new OperationStore();
        await using var scheduler = CreateScheduler(operations, queueCapacity: 1, workerConcurrency: 1);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Start(operations, 1);
        var queued = Start(operations, 2);
        var queuedCallbackCalled = false;
        Assert.True(scheduler.TryQueue(first, async () =>
        {
            firstStarted.TrySetResult();
            await releaseFirst.Task.WaitAsync(first.CancellationToken);
            Complete(operations, first);
        }));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(scheduler.TryQueue(queued, () =>
        {
            queuedCallbackCalled = true;
            Complete(operations, queued);
            return Task.CompletedTask;
        }));

        operations.Cancel(queued.Handle.OperationId, "test", DateTimeOffset.UtcNow);
        releaseFirst.TrySetResult();
        await WaitForTerminalAsync(operations, queued.Handle.OperationId);

        Assert.False(queuedCallbackCalled);
        Assert.Equal(OperationStatus.Cancelled, operations.Get(queued.Handle.OperationId)?.Status);
    }

    [Fact]
    public async Task CallbackFailureDoesNotStopTheConsumerLoop()
    {
        var operations = new OperationStore();
        await using var scheduler = CreateScheduler(operations, queueCapacity: 2, workerConcurrency: 1);
        var first = Start(operations, 1);
        var second = Start(operations, 2);
        var secondRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(scheduler.TryQueue(first, () => throw new InvalidOperationException("expected")));
        Assert.True(scheduler.TryQueue(second, () =>
        {
            Complete(operations, second);
            secondRan.TrySetResult();
            return Task.CompletedTask;
        }));

        await secondRan.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var firstState = await WaitForTerminalAsync(operations, first.Handle.OperationId);

        Assert.Equal(OperationStatus.Failed, firstState.Status);
        Assert.Equal("operation-executor-failed", firstState.Error?.Code);
        Assert.Equal(OperationStatus.Completed, operations.Get(second.Handle.OperationId)?.Status);
    }

    [Fact]
    public async Task DisposeCancelsRunningWorkAndTerminatesQueuedWork()
    {
        var operations = new OperationStore();
        var scheduler = CreateScheduler(operations, queueCapacity: 1, workerConcurrency: 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = Start(operations, 1);
        var queued = Start(operations, 2);
        Assert.True(scheduler.TryQueue(running, async () =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, running.CancellationToken);
        }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(scheduler.TryQueue(queued, () => throw new InvalidOperationException("Must not run.")));

        await scheduler.DisposeAsync();

        Assert.Equal(OperationStatus.Cancelled, operations.Get(running.Handle.OperationId)?.Status);
        Assert.Equal(OperationStatus.Cancelled, operations.Get(queued.Handle.OperationId)?.Status);
    }

    private static BoundedOperationScheduler CreateScheduler(OperationStore operations, int queueCapacity, int workerConcurrency) => new(
            operations,
            new OperationExecutionOptions { QueueCapacity = queueCapacity, WorkerConcurrency = workerConcurrency, ShutdownTimeout = TimeSpan.FromSeconds(5), ExecutorId = "test-executor" });

    private static OperationStart Start(OperationStore operations, int index) => operations.Start($"request-{index}", $"key-{index}", OperationKind.Build, $"trace-{index}", DateTimeOffset.UtcNow);

    private static void Complete(OperationStore operations, OperationStart operation) => operations.Append(operation.Handle.OperationId, new CompletedOperationEventPayload(OperationCompletionStatus.Completed, TimeSpan.Zero), DateTimeOffset.UtcNow);

    private static async Task WaitForAllTerminalAsync(OperationStore operations, IEnumerable<OperationStart> starts)
    {
        foreach (var start in starts) await WaitForTerminalAsync(operations, start.Handle.OperationId);
    }

    private static async Task<OperationState> WaitForTerminalAsync(OperationStore operations, string operationId)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var state = operations.Get(operationId) ?? throw new InvalidOperationException("Operation disappeared.");
            if (state.Status is OperationStatus.Completed or OperationStatus.Cancelled or OperationStatus.Failed)
            {
                return state;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("Operation did not reach a terminal state.");
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        var observed = Volatile.Read(ref maximum);
        while (candidate > observed)
        {
            var previous = Interlocked.CompareExchange(ref maximum, candidate, observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }
}
