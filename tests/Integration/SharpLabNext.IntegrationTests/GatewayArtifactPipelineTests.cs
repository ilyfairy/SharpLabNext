using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpLabNext.Contracts;
using SharpLabNext.Gateway;

namespace SharpLabNext.IntegrationTests;

public sealed class GatewayArtifactPipelineTests
{
    [Fact]
    public async Task ExecutionFlowAcceptsItsIntermediateInstrumentationTransform()
    {
        var worker = new FakeArtifactWorkerClient(FakeArtifactScenario.CompletedTransform);
        await using var factory = new GatewayArtifactTestFactory(worker);
        using var client = factory.CreateClient();
        var resolution = await ResolveAsync(client, "execution-flow");
        var request = new TransformArtifactRequest(
            "artifact-transform-request",
            "artifact-transform-key",
            resolution.PipelineResolutionId,
            Artifact(),
            "artifacts-default",
            "runtime-instrumentation-v1",
            new TransformArtifactOptions(RewriterProfileId: "execution-flow-v1"),
            DateTimeOffset.UtcNow.AddMinutes(1));

        var handle = await StartAsync(client, "/api/v1/artifact-transforms", request);
        var state = await WaitForTerminalAsync(client, handle.OperationId);
        var events = await ReadEventsAsync(client, handle.OperationId);

        Assert.Equal(OperationStatus.Completed, state.Status);
        Assert.Equal(1, worker.TransformStartCount);
        var typed = Assert.IsType<TypedResultOperationEventPayload>(Assert.Single(
            events,
            item => item.Payload is TypedResultOperationEventPayload).Payload);
        var result = Assert.IsType<TransformArtifactResult>(typed.Result);
        Assert.Equal(FakeArtifactWorkerClient.DerivedArtifactRef, result.ArtifactRef);
    }

    [Fact]
    public async Task ThirdPartyArtifactTransformRoutesToResolvedProcessorWorker()
    {
        var worker = new FakeArtifactWorkerClient(FakeArtifactScenario.CompletedTransform);
        await using var factory = new GatewayArtifactTestFactory(worker);
        using var client = factory.CreateClient();
        var resolution = await ResolveAsync(
            client,
            "run",
            languageId: "minilang",
            toolchainId: "minilang-stable",
            runtimeId: "dotnet-10-linux-x64");
        var transform = Assert.Single(
            resolution.PipelinePlan.Stages,
            static stage => stage.Kind == PipelineStageKind.Transform);
        var request = new TransformArtifactRequest(
            "minilang-assemble-request",
            "minilang-assemble-key",
            resolution.PipelineResolutionId,
            Artifact(),
            transform.ProviderId,
            transform.Id,
            new TransformArtifactOptions(),
            DateTimeOffset.UtcNow.AddMinutes(1));

        var handle = await StartAsync(client, "/api/v1/artifact-transforms", request);
        var state = await WaitForTerminalAsync(client, handle.OperationId);

        Assert.Equal(OperationStatus.Completed, state.Status);
        Assert.Equal("il-assembler", Assert.Single(worker.RequestedWorkerIds));
    }

    [Fact]
    public async Task InstrumentationTransformRejectsAnUnpinnedRewriterProfile()
    {
        var worker = new FakeArtifactWorkerClient(FakeArtifactScenario.CompletedTransform);
        await using var factory = new GatewayArtifactTestFactory(worker);
        using var client = factory.CreateClient();
        var resolution = await ResolveAsync(client, "execution-flow");
        var request = new TransformArtifactRequest(
            "artifact-transform-invalid",
            "artifact-transform-invalid-key",
            resolution.PipelineResolutionId,
            Artifact(),
            "artifacts-default",
            "runtime-instrumentation-v1",
            new TransformArtifactOptions(RewriterProfileId: "arbitrary"),
            DateTimeOffset.UtcNow.AddMinutes(1));

        using var response = await client.PostAsJsonAsync(
            "/api/v1/artifact-transforms",
            request,
            ContractJson.CreateSerializerOptions(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, worker.TransformStartCount);
    }

    [Fact]
    public async Task RenderRelaysTypedContentResultAndTerminalEvent()
    {
        var worker = new FakeArtifactWorkerClient(FakeArtifactScenario.CompletedRender);
        await using var factory = new GatewayArtifactTestFactory(worker);
        using var client = factory.CreateClient();
        var resolution = await ResolveAsync(client, "il");
        var request = new RenderArtifactRequest(
            "artifact-render-request",
            "artifact-render-key",
            resolution.PipelineResolutionId,
            Artifact(),
            "artifacts-default",
            "il",
            new RenderArtifactOptions(),
            DateTimeOffset.UtcNow.AddMinutes(1));

        var handle = await StartAsync(client, "/api/v1/artifact-renders", request);
        var state = await WaitForTerminalAsync(client, handle.OperationId);
        var events = await ReadEventsAsync(client, handle.OperationId);

        Assert.Equal(OperationStatus.Completed, state.Status);
        Assert.Equal(1, worker.RenderStartCount);
        var content = Assert.IsType<ContentProducedOperationEventPayload>(Assert.Single(
            events,
            item => item.Payload is ContentProducedOperationEventPayload).Payload);
        Assert.Equal(FakeArtifactWorkerClient.ContentRef, content.ContentRef);
        var typed = Assert.IsType<TypedResultOperationEventPayload>(Assert.Single(
            events,
            item => item.Payload is TypedResultOperationEventPayload).Payload);
        var result = Assert.IsType<RenderArtifactResult>(typed.Result);
        Assert.Equal(ArtifactJobOutcome.Succeeded, result.Outcome);
        Assert.DoesNotContain(events, item => item.Payload is AcceptedOperationEventPayload accepted
            && accepted.RequestId == FakeArtifactWorkerClient.RemoteRequestId);
    }

    [Fact]
    public async Task TerminalStateObservedBetweenEventPollsStillRelaysFinalEvents()
    {
        var worker = new FakeArtifactWorkerClient(FakeArtifactScenario.TerminalSnapshotBeforeFinalEvents);
        await using var factory = new GatewayArtifactTestFactory(worker);
        using var client = factory.CreateClient();
        var resolution = await ResolveAsync(client, "il");
        var request = new RenderArtifactRequest(
            "artifact-render-race-request",
            "artifact-render-race-key",
            resolution.PipelineResolutionId,
            Artifact(),
            "artifacts-default",
            "il",
            new RenderArtifactOptions(),
            DateTimeOffset.UtcNow.AddMinutes(1));

        var handle = await StartAsync(client, "/api/v1/artifact-renders", request);
        var state = await WaitForTerminalAsync(client, handle.OperationId);
        var events = await ReadEventsAsync(client, handle.OperationId);

        Assert.Equal(OperationStatus.Completed, state.Status);
        Assert.Null(state.Error);
        Assert.Contains(events, static item => item.Payload is TypedResultOperationEventPayload);
        Assert.Contains(events, static item => item.Payload is CompletedOperationEventPayload);
    }

    [Fact]
    public async Task VerificationRequiresTheResolvedVerificationStage()
    {
        var worker = new FakeArtifactWorkerClient(FakeArtifactScenario.CompletedVerification);
        await using var factory = new GatewayArtifactTestFactory(worker);
        using var client = factory.CreateClient();
        var wrongResolution = await ResolveAsync(client, "il");
        var request = VerificationRequest(wrongResolution.PipelineResolutionId);

        using var rejected = await client.PostAsJsonAsync(
            "/api/v1/verifications",
            request,
            ContractJson.CreateSerializerOptions(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(0, worker.VerifyStartCount);
    }

    [Fact]
    public async Task VerificationRelaysStructuredFindings()
    {
        var worker = new FakeArtifactWorkerClient(FakeArtifactScenario.CompletedVerification);
        await using var factory = new GatewayArtifactTestFactory(worker);
        using var client = factory.CreateClient();
        var resolution = await ResolveAsync(client, "il-verify");

        var handle = await StartAsync(client, "/api/v1/verifications", VerificationRequest(resolution.PipelineResolutionId));
        var state = await WaitForTerminalAsync(client, handle.OperationId);
        var events = await ReadEventsAsync(client, handle.OperationId);

        Assert.Equal(OperationStatus.Completed, state.Status);
        var typed = Assert.IsType<TypedResultOperationEventPayload>(Assert.Single(
            events,
            item => item.Payload is TypedResultOperationEventPayload).Payload);
        var result = Assert.IsType<VerifyArtifactResult>(typed.Result);
        Assert.Equal(ArtifactVerificationOutcome.Findings, result.Outcome);
        Assert.Equal("stack-unbalanced", Assert.Single(result.Findings).Code);
    }

    [Fact]
    public async Task ClientCancellationCancelsTheRemoteArtifactOperation()
    {
        var worker = new FakeArtifactWorkerClient(FakeArtifactScenario.BlockedRender);
        await using var factory = new GatewayArtifactTestFactory(worker);
        using var client = factory.CreateClient();
        var resolution = await ResolveAsync(client, "il");
        var request = new RenderArtifactRequest(
            "artifact-cancel-request",
            "artifact-cancel-key",
            resolution.PipelineResolutionId,
            Artifact(),
            "artifacts-default",
            "il",
            new RenderArtifactOptions(),
            DateTimeOffset.UtcNow.AddMinutes(1));
        var handle = await StartAsync(client, "/api/v1/artifact-renders", request);
        await worker.WatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        using var cancel = await client.PostAsJsonAsync(
            $"/api/v1/operations/{handle.OperationId}/cancel",
            new CancelOperationRequest(handle.OperationId, "test"),
            ContractJson.CreateSerializerOptions(),
            TestContext.Current.CancellationToken);
        cancel.EnsureSuccessStatusCode();
        var state = await WaitForTerminalAsync(client, handle.OperationId);

        Assert.Equal(OperationStatus.Cancelled, state.Status);
        Assert.Equal(1, worker.CancelCount);
        Assert.Equal(FakeArtifactWorkerClient.RemoteOperationId, worker.CancelledOperationId);
    }

    [Fact]
    public async Task UnexpectedRemoteResultFailsTheGatewayOperation()
    {
        var worker = new FakeArtifactWorkerClient(FakeArtifactScenario.WrongResult);
        await using var factory = new GatewayArtifactTestFactory(worker);
        using var client = factory.CreateClient();
        var resolution = await ResolveAsync(client, "il");
        var request = new RenderArtifactRequest(
            "artifact-wrong-result",
            "artifact-wrong-result-key",
            resolution.PipelineResolutionId,
            Artifact(),
            "artifacts-default",
            "il",
            new RenderArtifactOptions(),
            DateTimeOffset.UtcNow.AddMinutes(1));

        var handle = await StartAsync(client, "/api/v1/artifact-renders", request);
        var state = await WaitForTerminalAsync(client, handle.OperationId);

        Assert.Equal(OperationStatus.Failed, state.Status);
        Assert.Equal("artifact-worker-protocol-invalid", state.Error?.Code);
        Assert.False(state.Error?.SafeToRetry);
    }

    private static VerifyArtifactRequest VerificationRequest(string pipelineId) => new(
        "artifact-verify-request",
        "artifact-verify-key",
        pipelineId,
        Artifact(),
        "artifacts-default",
        new VerifyArtifactOptions("net10-default"),
        DateTimeOffset.UtcNow.AddMinutes(1));

    private static ArtifactRef Artifact() => new($"sha256:{new string('a', 64)}");

    private static async Task<ResolveSelectionResponse> ResolveAsync(
        HttpClient client,
        string outputId,
        string languageId = "csharp",
        string toolchainId = "roslyn-stable",
        string? runtimeId = null)
    {
        var catalogRevision = await GatewayTestCatalog.GetRevisionAsync(client);
        using var response = await client.PostAsJsonAsync(
            "/api/v1/selections/resolve",
            new ResolveSelectionRequest(
                languageId,
                toolchainId,
                "net10-ref",
                outputId,
                runtimeId,
                BuildConfiguration.Release,
                catalogRevision,
                1),
            ContractJson.CreateSerializerOptions(),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ResolveSelectionResponse>(
            ContractJson.CreateSerializerOptions(),
            TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("Selection response was empty.");
    }

    private static async Task<OperationHandle> StartAsync<T>(HttpClient client, string path, T request)
    {
        using var response = await client.PostAsJsonAsync(
            path,
            request,
            ContractJson.CreateSerializerOptions(),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<OperationHandle>(
            ContractJson.CreateSerializerOptions(),
            TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("Operation handle was empty.");
    }

    private static async Task<OperationState> WaitForTerminalAsync(HttpClient client, string operationId)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var state = await client.GetFromJsonAsync<OperationState>(
                $"/api/v1/operations/{operationId}",
                ContractJson.CreateSerializerOptions(),
                TestContext.Current.CancellationToken)
                ?? throw new InvalidOperationException("Operation state was empty.");
            if (state.Status is OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled)
                return state;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("Artifact operation did not complete.");
    }

    private static async Task<IReadOnlyList<OperationEvent>> ReadEventsAsync(HttpClient client, string operationId)
    {
        using var response = await client.GetAsync(
            $"/api/v1/operations/{operationId}/events?FromSequence=0",
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var events = body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(static line => line.StartsWith("data: ", StringComparison.Ordinal))
            .Select(static line => JsonSerializer.Deserialize<OperationEvent>(
                line.AsSpan("data: ".Length),
                ContractJson.CreateSerializerOptions())
                ?? throw new InvalidOperationException("Operation event was empty."))
            .ToArray();
        OperationEventStreamContract.Validate(events);
        return events;
    }
}

internal sealed class GatewayArtifactTestFactory(FakeArtifactWorkerClient worker)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IArtifactWorkerClientFactory>();
            services.AddSingleton<IArtifactWorkerClientFactory>(worker);
        });
    }
}

internal enum FakeArtifactScenario
{
    CompletedTransform,
    CompletedRender,
    CompletedVerification,
    TerminalSnapshotBeforeFinalEvents,
    BlockedRender,
    WrongResult
}

internal sealed class FakeArtifactWorkerClient(FakeArtifactScenario scenario)
    : IArtifactWorkerClient, IArtifactWorkerClientFactory
{
    public const string RemoteOperationId = "op_remote_artifact";
    public const string RemoteRequestId = "remote-existing-request";
    public static readonly ContentRef ContentRef = new($"sha256:{new string('b', 64)}");
    public static readonly ArtifactRef DerivedArtifactRef = new($"sha256:{new string('c', 64)}");
    private string? _requestId;
    private OperationKind _kind;
    private int _eventPollCount;

    public int TransformStartCount { get; private set; }
    public int RenderStartCount { get; private set; }
    public int VerifyStartCount { get; private set; }
    public int CancelCount { get; private set; }
    public string? CancelledOperationId { get; private set; }
    public TaskCompletionSource WatchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<string> RequestedWorkerIds { get; } = [];

    public IArtifactWorkerClient Create(string workerId)
    {
        RequestedWorkerIds.Add(workerId);
        return this;
    }

    public Task<OperationHandle> StartTransformAsync(
        TransformArtifactRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TransformStartCount++;
        _requestId = request.RequestId;
        _kind = OperationKind.TransformArtifact;
        return Task.FromResult(Handle(request.RequestId));
    }

    public Task<OperationHandle> StartRenderAsync(
        RenderArtifactRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RenderStartCount++;
        _requestId = request.RequestId;
        _kind = OperationKind.RenderArtifact;
        return Task.FromResult(Handle(request.RequestId));
    }

    public Task<OperationHandle> StartVerifyAsync(
        VerifyArtifactRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VerifyStartCount++;
        _requestId = request.RequestId;
        _kind = OperationKind.VerifyArtifact;
        return Task.FromResult(Handle(request.RequestId));
    }

    public Task<OperationState?> GetOperationAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var terminal = scenario == FakeArtifactScenario.TerminalSnapshotBeforeFinalEvents &&
            Volatile.Read(ref _eventPollCount) >= 2;
        return Task.FromResult<OperationState?>(new OperationState(
            operationId,
            _requestId ?? string.Empty,
            _kind,
            terminal ? OperationStatus.Completed : OperationStatus.Running,
            terminal ? 5 : 2,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            terminal ? DateTimeOffset.UtcNow : null,
            "remote-artifact-trace",
            null));
    }

    public async Task<IReadOnlyList<OperationEvent>> GetEventsAsync(
        string operationId,
        long fromSequence,
        CancellationToken cancellationToken = default)
    {
        Assert.Equal(RemoteOperationId, operationId);
        WatchStarted.TrySetResult();
        var poll = Interlocked.Increment(ref _eventPollCount);
        if (scenario == FakeArtifactScenario.BlockedRender)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }

        var requestId = _requestId ?? throw new InvalidOperationException("Operation was not started.");
        var events = new List<OperationEvent>
        {
            Event(1, new AcceptedOperationEventPayload(requestId, _kind)),
            Event(2, new ProgressOperationEventPayload("processor", null, 0.5))
        };
        if (scenario == FakeArtifactScenario.TerminalSnapshotBeforeFinalEvents)
        {
            if (poll == 1)
                return events.Where(item => item.Sequence > fromSequence).ToArray();
            if (poll == 2)
                return [];
        }
        if (scenario == FakeArtifactScenario.CompletedTransform)
        {
            events.Add(Event(3, new ArtifactProducedOperationEventPayload(
                DerivedArtifactRef,
                "dotnet-managed-pe-v1",
                "runtime-instrumented")));
            events.Add(Event(4, new TypedResultOperationEventPayload(new TransformArtifactResult(
                ArtifactJobOutcome.Succeeded,
                DerivedArtifactRef,
                new ArtifactRef($"sha256:{new string('a', 64)}"),
                "dotnet-managed-pe-v1",
                []))));
            events.Add(Event(5, new CompletedOperationEventPayload(
                OperationCompletionStatus.Completed,
                TimeSpan.FromMilliseconds(10))));
        }
        else if (scenario is FakeArtifactScenario.CompletedRender or FakeArtifactScenario.TerminalSnapshotBeforeFinalEvents)
        {
            events.Add(Event(3, new ContentProducedOperationEventPayload(ContentRef, "text/plain", 20)));
            events.Add(Event(4, new TypedResultOperationEventPayload(new RenderArtifactResult(
                ArtifactJobOutcome.Succeeded,
                ContentRef,
                "text/plain",
                [],
                []))));
            events.Add(Event(5, new CompletedOperationEventPayload(
                OperationCompletionStatus.Completed,
                TimeSpan.FromMilliseconds(10))));
        }
        else if (scenario == FakeArtifactScenario.CompletedVerification)
        {
            events.Add(Event(3, new TypedResultOperationEventPayload(new VerifyArtifactResult(
                ArtifactVerificationOutcome.Findings,
                [new VerificationFinding("stack-unbalanced", "Stack is unbalanced.", null, null, null, null, null)],
                "microsoft-ilverification",
                "10.0.9"))));
            events.Add(Event(4, new CompletedOperationEventPayload(
                OperationCompletionStatus.Completed,
                TimeSpan.FromMilliseconds(10))));
        }
        else
        {
            events.Add(Event(3, new TypedResultOperationEventPayload(new VerifyArtifactResult(
                ArtifactVerificationOutcome.Valid,
                [],
                "microsoft-ilverification",
                "10.0.9"))));
            events.Add(Event(4, new CompletedOperationEventPayload(
                OperationCompletionStatus.Completed,
                TimeSpan.FromMilliseconds(10))));
        }
        return events.Where(item => item.Sequence > fromSequence).ToArray();
    }

    public Task<CancelResult> CancelAsync(
        string operationId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancelCount++;
        CancelledOperationId = operationId;
        _ = reason;
        return Task.FromResult(new CancelResult(operationId, CancelDisposition.Accepted, 1));
    }

    private static OperationHandle Handle(string requestId) => new(
        RemoteOperationId,
        requestId,
        DateTimeOffset.UtcNow,
        false);

    private static OperationEvent Event(long sequence, OperationEventPayload payload) => new(
        RemoteOperationId,
        sequence,
        DateTimeOffset.UtcNow,
        "remote-artifact-trace",
        payload);
}
