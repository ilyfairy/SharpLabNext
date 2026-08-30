using Microsoft.Extensions.Logging.Abstractions;
using SharpLabNext.Contracts;
using SharpLabNext.Gateway;
using SharpLabNext.Operations;
using SharpLabNext.Worker.Client;

namespace SharpLabNext.IntegrationTests;

public sealed class BuildOperationExecutorTests
{
    [Fact]
    public async Task CancellationReachesWorkerAndProducesCancelledTerminalEvent()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new DelegateWorkerClient(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        });
        var operations = new OperationStore();
        await using var scheduler = CreateScheduler(operations);
        var operation = operations.Start("cancel-request", "cancel-key", OperationKind.Build, "trace", DateTimeOffset.UtcNow);
        CreateExecutor(operations, scheduler, worker).QueueBuild(operation, CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1)), "roslyn-stable");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var cancellation = operations.Cancel(operation.Handle.OperationId, "test", DateTimeOffset.UtcNow);
        Assert.Equal(CancelDisposition.Accepted, cancellation.Disposition);
        var state = await WaitForTerminalAsync(operations, operation.Handle.OperationId);
        Assert.Equal(OperationStatus.Cancelled, state.Status);
        Assert.True(worker.ObservedCancellation);
    }

    [Fact]
    public async Task ExpiredDeadlineFailsWithoutCallingWorker()
    {
        var worker = new DelegateWorkerClient((_, _) => throw new InvalidOperationException("Worker must not be called."));
        var operations = new OperationStore();
        await using var scheduler = CreateScheduler(operations);
        var operation = operations.Start("deadline-request", "deadline-key", OperationKind.Build, "trace", DateTimeOffset.UtcNow);
        CreateExecutor(operations, scheduler, worker).QueueBuild(operation, CreateRequest(DateTimeOffset.UtcNow.AddSeconds(-1)), "roslyn-stable");

        var state = await WaitForTerminalAsync(operations, operation.Handle.OperationId);
        Assert.Equal(OperationStatus.Failed, state.Status);
        Assert.Equal(WorkerErrorCategory.DeadlineExceeded, state.Error?.Category);
        Assert.Equal(0, worker.CallCount);
    }

    [Fact]
    public async Task WorkerFailureIsPreservedAsStructuredOperationError()
    {
        var expected = new WorkerError("worker-unavailable", WorkerErrorCategory.Unavailable, "The toolchain worker is unavailable.", true, true, "worker-trace", "roslyn-stable", "test-worker-image");
        var worker = new DelegateWorkerClient((_, _) => throw new ToolchainWorkerException(expected, 503));
        var operations = new OperationStore();
        await using var scheduler = CreateScheduler(operations);
        var operation = operations.Start("failure-request", "failure-key", OperationKind.Build, "trace", DateTimeOffset.UtcNow);
        CreateExecutor(operations, scheduler, worker).QueueBuild(operation, CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1)), "roslyn-stable");

        var state = await WaitForTerminalAsync(operations, operation.Handle.OperationId);
        Assert.Equal(OperationStatus.Failed, state.Status);
        Assert.Equal(expected, state.Error);
    }

    [Fact]
    public async Task FullSharedQueueFailsTheCreatedOperationAndPreservesIdempotency()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new WorkerError("test-worker-failure", WorkerErrorCategory.Unavailable, "Test worker failure.", true, true, "test-trace", "roslyn-stable", "test-image");
        var worker = new DelegateWorkerClient(async (_, cancellationToken) =>
        {
            firstStarted.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            throw new ToolchainWorkerException(failure, 503);
        });
        var operations = new OperationStore();
        await using var scheduler = CreateScheduler(operations, queueCapacity: 1, workerConcurrency: 1);
        var executor = CreateExecutor(operations, scheduler, worker);
        var first = operations.Start("first", "first-key", OperationKind.Build, "trace-1", DateTimeOffset.UtcNow);
        executor.QueueBuild(first, CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1)), "roslyn-stable");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var second = operations.Start("second", "second-key", OperationKind.Build, "trace-2", DateTimeOffset.UtcNow);
        executor.QueueBuild(second, CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1)), "roslyn-stable");
        var rejected = operations.Start("third", "third-key", OperationKind.Build, "trace-3", DateTimeOffset.UtcNow);

        executor.QueueBuild(rejected, CreateRequest(DateTimeOffset.UtcNow.AddMinutes(1)), "roslyn-stable");

        var state = operations.Get(rejected.Handle.OperationId);
        var replay = operations.Start("third-retry", "third-key", OperationKind.Run, "trace-4", DateTimeOffset.UtcNow);
        Assert.Equal(OperationStatus.Failed, state?.Status);
        Assert.Equal(WorkerErrorCategory.ResourceExhausted, state?.Error?.Category);
        Assert.True(state?.Error?.Retryable);
        Assert.True(state?.Error?.SafeToRetry);
        Assert.True(replay.Handle.IsExisting);
        Assert.Equal(rejected.Handle.OperationId, replay.Handle.OperationId);
        release.TrySetResult();
    }

    private static BuildOperationExecutor CreateExecutor(OperationStore operations, BoundedOperationScheduler scheduler, IToolchainWorkerClient worker) => new(operations, scheduler, new SingleWorkerFactory(worker), new RejectingPublisher(), new BuildPipelineOptions(), NullLogger<BuildOperationExecutor>.Instance);

    private static BoundedOperationScheduler CreateScheduler(OperationStore operations, int queueCapacity = 8, int workerConcurrency = 2) => new(
            operations,
            new OperationExecutionOptions { QueueCapacity = queueCapacity, WorkerConcurrency = workerConcurrency, ShutdownTimeout = TimeSpan.FromSeconds(5), ExecutorId = "gateway-test" });

    private static BuildRequest CreateRequest(DateTimeOffset deadline) => new("executor-request", "executor-key", "executor-pipeline", "roslyn-stable", "net10-ref", new WorkspaceSnapshot(ContractSchemaVersions.WorkspaceSnapshot, 1, 1, "csharp", [new WorkspaceFile("Program.cs", 1, "System.Console.WriteLine(42);")], "Program.cs", ["Program.cs"], "net10-ref", new BuildOptions(BuildConfiguration.Release, true, BuildOutputKind.Console, false, true)), deadline, Target: BuildTarget.CompileCheck);

    private static async Task<OperationState> WaitForTerminalAsync(OperationStore operations, string operationId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var state = operations.Get(operationId) ?? throw new InvalidOperationException("Operation disappeared.");
            if (state.Status is OperationStatus.Completed or OperationStatus.Cancelled or OperationStatus.Failed)
                return state;

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("Operation did not reach a terminal state.");
    }

    private sealed class DelegateWorkerClient(Func<BuildRequest, CancellationToken, Task<ToolchainBuildResponse>> build) : IToolchainWorkerClient
    {
        public int CallCount { get; private set; }

        public bool ObservedCancellation { get; private set; }

        public Task<WorkerDescriptor> DescribeAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<ToolchainBuildResponse> BuildAsync(BuildRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            try
            {
                return await build(request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ObservedCancellation = true;
                throw;
            }
        }

        public Task<ToolchainExplainResponse> ExplainAsync(ExplainRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class SingleWorkerFactory(IToolchainWorkerClient worker) : IToolchainWorkerClientFactory
    {
        public IToolchainWorkerClient Create(string workerId) => worker;
    }

    private sealed class RejectingPublisher : IBuildArtifactPublisher
    {
        public Task<PublishedBuildArtifact> PublishAsync(WorkerArtifactEnvelope envelope, CancellationToken cancellationToken) => throw new InvalidOperationException("Artifact publisher must not be called.");

        public Task<PublishedBuildArtifact> AcceptPublishedAsync(ArtifactRef artifactRef, BuildIdentity identity, CancellationToken cancellationToken) => throw new InvalidOperationException("Artifact publisher must not be called.");
    }
}
