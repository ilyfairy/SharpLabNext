extern alias ArtifactWorkerHost;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpLabNext.ArtifactWorker;
using SharpLabNext.Contracts;
using SharpLabNext.Observability;

namespace SharpLabNext.ArtifactWorker.Tests;

public sealed class ArtifactWorkerEndpointTests
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions =
        ContractJson.CreateSerializerOptions();

    [Fact]
    public async Task StartPollEventsAndIdempotencyUseTheOperationContract()
    {
        var root = TestSettings.CreateRoot();
        try
        {
            var executor = new EndpointExecutor();
            await using var factory = CreateFactory(root, executor);
            using var client = factory.CreateClient();
            Assert.Same(SharpLabNextTelemetry.Metrics, factory.Services.GetRequiredService<SharpLabNextMetrics>());
            var request = Request("request-first", "same-key");

            var first = await PostAsync(client, request);
            var second = await PostAsync(client, request with { RequestId = "request-second" });

            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
            var firstHandle = await ReadAsync<OperationHandle>(first);
            var secondHandle = await ReadAsync<OperationHandle>(second);
            Assert.False(firstHandle.IsExisting);
            Assert.True(secondHandle.IsExisting);
            Assert.Equal(firstHandle.OperationId, secondHandle.OperationId);
            Assert.Equal("request-first", secondHandle.RequestId);

            var state = await WaitForTerminalAsync(client, firstHandle.OperationId);
            Assert.Equal(OperationStatus.Completed, state.Status);
            var events = await client.GetFromJsonAsync<OperationEvent[]>($"/api/v1/operations/{firstHandle.OperationId}/events?FromSequence=0", JsonOptions, TestContext.Current.CancellationToken);
            Assert.NotNull(events);
            OperationEventStreamContract.Validate(events);
            Assert.Contains(events, item => item.Payload is ContentProducedOperationEventPayload);
            var typed = Assert.IsType<TypedResultOperationEventPayload>(Assert.Single(events, static item => item.Payload is TypedResultOperationEventPayload).Payload);
            var render = Assert.IsType<RenderArtifactResult>(typed.Result);
            Assert.Equal("artifacts-default", render.Identity?.ProcessorId);
            Assert.Equal("development", render.Identity?.WorkerImageId);
            Assert.Equal(1, executor.RenderCount);
        }
        finally
        {
            TestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CancelEndpointCancelsAWorkingOperation()
    {
        var root = TestSettings.CreateRoot();
        try
        {
            var executor = new EndpointExecutor();
            await using var factory = CreateFactory(root, executor);
            using var client = factory.CreateClient();
            using var started = await PostAsync(client, Request("request-blocking", "blocking-key"));
            var handle = await ReadAsync<OperationHandle>(started);

            using var cancelled = await client.PostAsJsonAsync($"/api/v1/operations/{handle.OperationId}/cancel", new CancelOperationRequest(handle.OperationId, "test cancellation"), JsonOptions, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
            var result = await ReadAsync<CancelResult>(cancelled);
            Assert.Equal(CancelDisposition.Accepted, result.Disposition);

            var state = await WaitForTerminalAsync(client, handle.OperationId);
            Assert.Equal(OperationStatus.Cancelled, state.Status);
            var events = await client.GetFromJsonAsync<OperationEvent[]>($"/api/v1/operations/{handle.OperationId}/events?FromSequence=0", JsonOptions, TestContext.Current.CancellationToken);
            Assert.NotNull(events);
            OperationEventStreamContract.Validate(events);
            Assert.IsType<CompletedOperationEventPayload>(events[^1].Payload);
        }
        finally
        {
            TestSettings.DeleteRoot(root);
        }
    }

    private static WebApplicationFactory<ArtifactWorkerHost::Program> CreateFactory(string root, EndpointExecutor executor) =>
        new WebApplicationFactory<ArtifactWorkerHost::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ArtifactWorker:WorkRoot"] = root,
                    ["ArtifactWorker:ReleaseId"] = "test-release",
                    ["ArtifactWorker:WorkerImageId"] = $"sha256:{new string('a', 64)}",
                    ["ArtifactStore:BaseUrl"] = "http://artifact-store.test"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IArtifactJobExecutor>();
                services.AddSingleton<IArtifactJobExecutor>(executor);
            });
        });

    private static RenderArtifactRequest Request(string requestId, string idempotencyKey) => new(requestId, idempotencyKey, "pipeline-test", new ArtifactRef($"sha256:{new string('b', 64)}"), "artifacts-default", "il", new RenderArtifactOptions(), DateTimeOffset.UtcNow.AddSeconds(30));

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, RenderArtifactRequest request) =>
        client.PostAsJsonAsync("/api/v1/artifact-renders", request, JsonOptions, TestContext.Current.CancellationToken);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions, TestContext.Current.CancellationToken) ?? throw new InvalidOperationException("The endpoint returned an empty response.");

    private static async Task<OperationState> WaitForTerminalAsync(HttpClient client, string operationId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var state = await client.GetFromJsonAsync<OperationState>($"/api/v1/operations/{operationId}", JsonOptions, TestContext.Current.CancellationToken) ?? throw new InvalidOperationException("The operation endpoint returned an empty response.");
            if (state.Status is OperationStatus.Completed or OperationStatus.Cancelled or OperationStatus.Failed)
                return state;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("The artifact operation did not become terminal.");
    }

    private sealed class EndpointExecutor : IArtifactJobExecutor
    {
        private static readonly ContentRef ContentRef = new($"sha256:{new string('c', 64)}");
        private int _renderCount;

        public int RenderCount => Volatile.Read(ref _renderCount);

        public Task<ArtifactJobExecution> TransformAsync(TransformArtifactRequest request, string operationId, CancellationToken cancellationToken) =>
            Task.FromResult(new ArtifactJobExecution(new TransformArtifactResult(ArtifactJobOutcome.Succeeded, request.ArtifactRef, request.ArtifactRef, "dotnet-managed-pe-v1", [])));

        public async Task<ArtifactJobExecution> RenderAsync(RenderArtifactRequest request, string operationId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _renderCount);
            if (request.RequestId == "request-blocking")
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ArtifactJobExecution(new RenderArtifactResult(ArtifactJobOutcome.Succeeded, ContentRef, "text/plain; charset=utf-8", [], []), new ProducedContent(ContentRef, "text/plain; charset=utf-8", 4));
        }

        public Task<ArtifactJobExecution> VerifyAsync(VerifyArtifactRequest request, string operationId, CancellationToken cancellationToken) =>
            Task.FromResult(new ArtifactJobExecution(new VerifyArtifactResult(ArtifactVerificationOutcome.Valid, [], "microsoft-ilverification", "10.0.9")));
    }
}
