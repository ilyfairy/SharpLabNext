using Microsoft.Extensions.Logging.Abstractions;
using SharpLabNext.ArtifactWorker;
using SharpLabNext.Contracts;

namespace SharpLabNext.ArtifactWorker.Tests;

public sealed class OperationRegistryTests
{
    [Fact]
    public async Task IdempotencyReturnsExistingOperationAndProducesValidEventSequence()
    {
        var root = TestSettings.CreateRoot();
        try
        {
            using var registry = new ArtifactOperationRegistry(TestSettings.Create(root), NullLogger<ArtifactOperationRegistry>.Instance);
            var first = registry.Start("request-1", "same-key", OperationKind.RenderArtifact, static (_, _) => Task.FromResult(Success()));
            var second = registry.Start("request-2", "same-key", OperationKind.RenderArtifact, static (_, _) => throw new InvalidOperationException("Must not execute."));
            Assert.False(first.IsExisting);
            Assert.True(second.IsExisting);
            Assert.Equal(first.OperationId, second.OperationId);

            var state = await WaitForTerminalAsync(registry, first.OperationId);
            Assert.Equal(OperationStatus.Completed, state.Status);
            var events = registry.GetEvents(first.OperationId, 0)!;
            OperationEventStreamContract.Validate(events);
            Assert.Contains(events, item => item.Payload is TypedResultOperationEventPayload);
        }
        finally
        {
            TestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CancellationPropagatesAndTerminatesTheOperation()
    {
        var root = TestSettings.CreateRoot();
        try
        {
            using var registry = new ArtifactOperationRegistry(TestSettings.Create(root), NullLogger<ArtifactOperationRegistry>.Instance);
            var handle = registry.Start(
                "request-cancel",
                "cancel-key",
                OperationKind.VerifyArtifact,
                static async (_, cancellationToken) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return Success();
                });
            var cancellation = registry.Cancel(handle.OperationId);
            Assert.Equal(CancelDisposition.Accepted, cancellation.Disposition);
            var state = await WaitForTerminalAsync(registry, handle.OperationId);
            Assert.Equal(OperationStatus.Cancelled, state.Status);
            OperationEventStreamContract.Validate(registry.GetEvents(handle.OperationId, 0)!);
        }
        finally
        {
            TestSettings.DeleteRoot(root);
        }
    }

    private static ArtifactJobExecution Success() => new(new RenderArtifactResult(ArtifactJobOutcome.Succeeded, null, "text/plain", [], []));

    private static async Task<OperationState> WaitForTerminalAsync(ArtifactOperationRegistry registry, string operationId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var state = registry.Get(operationId)!;
            if (state.Status is OperationStatus.Completed or OperationStatus.Cancelled or OperationStatus.Failed)
                return state;
            await Task.Delay(10);
        }
        throw new TimeoutException("The artifact operation did not become terminal.");
    }
}
