using System.Net;
using System.Net.Http.Json;
using SharpLabNext.Contracts;
using SharpLabNext.Gateway;

namespace SharpLabNext.IntegrationTests;

public sealed class ArtifactWorkerClientTests
{
    private const string WorkerId = "artifacts-default";
    private const string ReleaseId = "release-test";
    private const string WorkerImageId = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OperationId = "op_artifact_client";

    [Fact]
    public async Task ExactProcessorIdentityIsAccepted()
    {
        using var httpClient = CreateHttpClient(CreateDescriptor(), CreateRenderResult(CreateIdentity()));
        var client = CreateClient(httpClient);

        var handle = await client.StartRenderAsync(CreateRequest(), TestContext.Current.CancellationToken);
        var events = await client.GetEventsAsync(handle.OperationId, 0, TestContext.Current.CancellationToken);

        var typed = Assert.IsType<TypedResultOperationEventPayload>(events[1].Payload);
        var result = Assert.IsType<RenderArtifactResult>(typed.Result);
        Assert.Equal(WorkerImageId, result.Identity?.WorkerImageId);
    }

    [Fact]
    public async Task MismatchedDescriptorReleaseIsRejectedBeforeOperationStart()
    {
        var startCalled = false;
        using var httpClient = CreateHttpClient(
            CreateDescriptor() with { Service = CreateDescriptor().Service with { ReleaseId = "another-release" } },
            CreateRenderResult(CreateIdentity()),
            () => startCalled = true);
        var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<ArtifactWorkerClientException>(() => client.StartRenderAsync(CreateRequest(), TestContext.Current.CancellationToken));

        Assert.Equal("artifact-worker-protocol-invalid", exception.Error.Code);
        Assert.False(startCalled);
    }

    [Fact]
    public async Task MissingProcessorIdentityIsRejected()
    {
        using var httpClient = CreateHttpClient(CreateDescriptor(), CreateRenderResult(identity: null));
        var client = CreateClient(httpClient);
        var handle = await client.StartRenderAsync(CreateRequest(), TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ArtifactWorkerClientException>(() => client.GetEventsAsync(handle.OperationId, 0, TestContext.Current.CancellationToken));

        Assert.Equal("artifact-worker-protocol-invalid", exception.Error.Code);
        Assert.Contains("identity", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResultFromAnotherWorkerImageIsRejected()
    {
        using var httpClient = CreateHttpClient(
            CreateDescriptor(),
            CreateRenderResult(CreateIdentity() with { WorkerImageId = "another-image" }));
        var client = CreateClient(httpClient);
        var handle = await client.StartRenderAsync(CreateRequest(), TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ArtifactWorkerClientException>(() => client.GetEventsAsync(handle.OperationId, 0, TestContext.Current.CancellationToken));

        Assert.Equal("artifact-worker-protocol-invalid", exception.Error.Code);
        Assert.Contains("selected processor", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ArtifactWorkerClient CreateClient(HttpClient httpClient) => new(httpClient, new ArtifactPipelineOptions(), new ArtifactWorkerClientSettings(WorkerId, ReleaseId, WorkerImageId));

    private static HttpClient CreateHttpClient(WorkerDescriptor descriptor, RenderArtifactResult result, Action? onStart = null) => new(new ArtifactDelegateHandler(request =>
    {
        var path = request.RequestUri?.AbsolutePath;
        if (path == "/api/v1/worker/describe")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(descriptor, options: ContractJson.CreateSerializerOptions())
            };
        }
        if (path == "/api/v1/artifact-renders")
        {
            onStart?.Invoke();
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = JsonContent.Create(new OperationHandle(OperationId, "artifact-client-request", DateTimeOffset.UtcNow, false), options: ContractJson.CreateSerializerOptions())
            };
        }
        if (path == $"/api/v1/operations/{OperationId}/events")
        {
            OperationEvent[] events =
            [
                new(OperationId, 1, DateTimeOffset.UtcNow, "artifact-client-trace", new AcceptedOperationEventPayload("artifact-client-request", OperationKind.RenderArtifact)),
                new(OperationId, 2, DateTimeOffset.UtcNow, "artifact-client-trace", new TypedResultOperationEventPayload(result))
            ];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(events, options: ContractJson.CreateSerializerOptions())
            };
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }))
    {
        BaseAddress = new Uri("http://artifact-worker.test", UriKind.Absolute)
    };

    private static WorkerDescriptor CreateDescriptor() => new(new ServiceIdentity(WorkerId, ServiceKind.ArtifactWorker, ReleaseId, ProtocolVersion.WorkerV1, ["il"], "ready"), "artifact-worker-instance", WorkerKind.ArtifactProcessor, WorkerImageId, ProtocolVersion.WorkerV1, [ProtocolVersion.WorkerV1], [new WorkerCapabilityDescriptor("il", 1, true, [WorkerId])], [WorkerId], DateTimeOffset.UtcNow);

    private static RenderArtifactRequest CreateRequest() => new("artifact-client-request", "artifact-client-key", "artifact-client-pipeline", new ArtifactRef($"sha256:{new string('b', 64)}"), WorkerId, "il", new RenderArtifactOptions(), DateTimeOffset.UtcNow.AddMinutes(1));

    private static RenderArtifactResult CreateRenderResult(ArtifactProcessorIdentity? identity) => new(ArtifactJobOutcome.Succeeded, new ContentRef($"sha256:{new string('c', 64)}"), "text/plain; charset=utf-8", [], [], identity);

    private static ArtifactProcessorIdentity CreateIdentity() => new(ReleaseId, WorkerId, "ilspy/10.1.0.8386", WorkerImageId);

    private sealed class ArtifactDelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(handler(request));
        }
    }
}
