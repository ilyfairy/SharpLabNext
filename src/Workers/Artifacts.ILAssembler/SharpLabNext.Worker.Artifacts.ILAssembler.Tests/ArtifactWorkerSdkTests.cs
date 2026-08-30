using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SharpLabNext.ArtifactWorker.Sdk;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.Artifacts.ILAssembler.Tests;

public sealed class ArtifactWorkerSdkTests
{
    [Fact]
    public void CapabilityManifestAndErrorMappingArePublicAndStrict()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "artifact-worker.json");
        var manifest = ArtifactWorkerCapabilityManifestSerializer.Load(manifestPath);
        var identity = new ServiceIdentity("il-assembler", ServiceKind.ArtifactWorker, "test-release", ProtocolVersion.WorkerV1, ["generated-il", "assemble-il", "managed-pe"], "ready");

        ArtifactWorkerCapabilityManifestSerializer.Validate(manifest, identity);
        Assert.Equal(2 * 1024 * 1024, manifest.Limits.MaximumInputArtifactBytes);
        Assert.Equal(["assemble-il"], manifest.TransformIds);
        Assert.Equal(["generated-il"], manifest.RenderOutputIds);

        var error = ArtifactWorkerErrorMapper.Map(new ArtifactWorkerIncompatibleArtifactException("private detail must not be substituted"), "trace-test", "il-assembler", $"sha256:{new string('a', 64)}");
        Assert.Equal("incompatible-artifact", error.Code);
        Assert.Equal(WorkerErrorCategory.IncompatibleArtifact, error.Category);
        Assert.False(error.Retryable);
        Assert.Equal("il-assembler", error.WorkerId);
    }

    [Fact]
    public void ManifestRejectsUnknownMembers()
    {
        var json = """
            {
              "schemaVersion": 1,
              "workerId": "test-worker",
              "protocolVersion": "1.0",
              "capabilities": ["render"],
              "acceptedArtifactFormats": ["input-v1"],
              "producedArtifactFormats": ["output-v1"],
              "transformIds": [],
              "renderOutputIds": ["render"],
              "verificationProfileIds": [],
              "limits": {
                "maximumInputArtifactBytes": 1,
                "maximumOutputArtifactBytes": 1,
                "maximumConcurrentOperations": 1,
                "maximumOperationMilliseconds": 1,
                "maximumRetainedOperations": 1,
                "maximumEventsPerOperation": 8
              },
              "unknown": true
            }
            """;
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(json));

        Assert.Throws<System.Text.Json.JsonException>(() => ArtifactWorkerCapabilityManifestSerializer.Load(content));
    }

    [Fact]
    public async Task OperationRegistryCancellationProducesContractTerminalEvent()
    {
        var manifest = ArtifactWorkerCapabilityManifestSerializer.Load(Path.Combine(AppContext.BaseDirectory, "artifact-worker.json"));
        using var registry = new ArtifactWorkerOperationRegistry(manifest, new ArtifactWorkerHostIdentity($"sha256:{new string('a', 64)}"), NullLogger<ArtifactWorkerOperationRegistry>.Instance);
        var handle = registry.Start(
            "cancel-request",
            "cancel-key",
            OperationKind.TransformArtifact,
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            });

        var cancel = registry.Cancel(handle.OperationId);
        Assert.Equal(CancelDisposition.Accepted, cancel.Disposition);
        OperationState? state = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            state = registry.Get(handle.OperationId);
            if (state?.Status == OperationStatus.Cancelled)
                break;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.NotNull(state);
        Assert.Equal(OperationStatus.Cancelled, state.Status);
        var events = registry.GetEvents(handle.OperationId, 0);
        Assert.NotNull(events);
        OperationEventStreamContract.Validate(events);
        var completed = Assert.IsType<CompletedOperationEventPayload>(events[^1].Payload);
        Assert.Equal(OperationCompletionStatus.Cancelled, completed.Status);
    }
}
