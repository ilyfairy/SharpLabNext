using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpLabNext.Contracts;
using SharpLabNext.Gateway;
using SharpLabNext.Operations;
using SharpLabNext.RuntimeSupervisor.Client;

namespace SharpLabNext.IntegrationTests;

public sealed class GatewayRuntimePipelineTests
{
    [Fact]
    public async Task RunForwardsPayloadsWithGatewayIdentityAndPreservesBase64Output()
    {
        var supervisor = new FakeRuntimeSupervisorClient(FakeRuntimeScenario.CompletedRun);
        await using var factory = new GatewayRuntimeTestFactory(supervisor);
        using var client = factory.CreateClient();
        var resolution = await ResolveAsync(client, "run", "dotnet-10-linux-x64");
        var request = CreateRunRequest(resolution.PipelineResolutionId, "gateway-run", "gateway-run-key");

        var handle = await StartAsync<RunRequest>(client, "/api/v1/runs", request);
        var state = await WaitForTerminalAsync(client, handle.OperationId);
        var events = await ReadEventsAsync(client, handle.OperationId);

        Assert.Equal(OperationStatus.Completed, state.Status);
        Assert.Equal(OperationKind.Run, state.Kind);
        Assert.Equal(1, supervisor.RunStartCount);
        Assert.All(events, operationEvent => Assert.Equal(handle.OperationId, operationEvent.OperationId));
        Assert.Equal(Enumerable.Range(1, events.Count).Select(static value => (long)value), events.Select(static item => item.Sequence));
        Assert.Single(events, operationEvent => operationEvent.Payload is AcceptedOperationEventPayload);
        var output = Assert.IsType<OutputChunkOperationEventPayload>(Assert.Single(events, operationEvent => operationEvent.Payload is OutputChunkOperationEventPayload).Payload);
        Assert.Equal(supervisor.EncodedOutput, output.Chunk.Data);
        Assert.IsType<RunResult>(Assert.IsType<TypedResultOperationEventPayload>(Assert.Single(events, operationEvent => operationEvent.Payload is TypedResultOperationEventPayload).Payload).Result);
        Assert.IsType<CompletedOperationEventPayload>(events[^1].Payload);
    }

    [Fact]
    public async Task JitEndpointForwardsTypedResultAndContent()
    {
        var supervisor = new FakeRuntimeSupervisorClient(FakeRuntimeScenario.CompletedJit);
        await using var factory = new GatewayRuntimeTestFactory(supervisor);
        using var client = factory.CreateClient();
        var resolution = await ResolveAsync(client, "jit-asm", "dotnet-10-linux-x64");
        var request = CreateJitRequest(resolution.PipelineResolutionId, "gateway-jit", "gateway-jit-key");

        var handle = await StartAsync<JitRequest>(client, "/api/v1/jit", request);
        var state = await WaitForTerminalAsync(client, handle.OperationId);
        var events = await ReadEventsAsync(client, handle.OperationId);

        Assert.Equal(OperationStatus.Completed, state.Status);
        Assert.Equal(OperationKind.Jit, state.Kind);
        Assert.Equal(1, supervisor.JitStartCount);
        Assert.Contains(events, operationEvent => operationEvent.Payload is ContentProducedOperationEventPayload);
        var jit = Assert.IsType<JitResult>(Assert.IsType<TypedResultOperationEventPayload>(Assert.Single(events, operationEvent => operationEvent.Payload is TypedResultOperationEventPayload).Payload).Result);
        var linkedRange = Assert.Single(Assert.Single(jit.Methods).LinkedRanges);
        Assert.Equal("Program.cs", linkedRange.SourceFilePath);
        Assert.Equal("sequence-point", linkedRange.Precision);
        Assert.IsType<CompletedOperationEventPayload>(events[^1].Payload);
    }

    [Fact]
    public async Task JSharpRunResolvesAndForwardsTheDedicatedClr2RuntimeContract()
    {
        var supervisor = new FakeRuntimeSupervisorClient(FakeRuntimeScenario.CompletedRun);
        await using var factory = new GatewayRuntimeTestFactory(supervisor);
        using var client = factory.CreateClient();
        var catalogRevision = await GatewayTestCatalog.GetRevisionAsync(client);
        using var resolveResponse = await client.PostAsJsonAsync("/api/v1/selections/resolve", new ResolveSelectionRequest("jsharp", "vjc-jsharp20", "jsharp20-ref", "run", "wine-jsharp20-linux-x64", BuildConfiguration.Release, catalogRevision, 1), ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        resolveResponse.EnsureSuccessStatusCode();
        var resolution = await resolveResponse.Content.ReadFromJsonAsync<ResolveSelectionResponse>(ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken) ?? throw new InvalidOperationException("Selection response was empty.");

        Assert.Equal("vjc-jsharp20", resolution.PipelinePlan.LanguageWorkerId);
        Assert.Equal("jsharp20-ref", resolution.PipelinePlan.ReferenceSetId);
        Assert.Equal("wine-jsharp20-linux-x64", resolution.PipelinePlan.RuntimeId);
        Assert.Equal("runtime-job-wine-jsharp20", resolution.PipelinePlan.SecurityPolicyId);
        var request = new RunRequest("gateway-jsharp-run", "gateway-jsharp-run-key", resolution.PipelineResolutionId, Artifact(), "wine-jsharp20-linux-x64", new RunOptions([], null, RunInstrumentation.None, "runtime-job-wine-jsharp20"), DateTimeOffset.UtcNow.AddMinutes(1));

        var handle = await StartAsync<RunRequest>(client, "/api/v1/runs", request);
        var state = await WaitForTerminalAsync(client, handle.OperationId);

        Assert.Equal(OperationStatus.Completed, state.Status);
        Assert.Equal("wine-jsharp20-linux-x64", supervisor.LastRunRequest?.RuntimeProfileId);
        Assert.Equal("runtime-job-wine-jsharp20", supervisor.LastRunRequest?.Options.SecurityPolicyId);
    }

    [Theory]
    [InlineData("jit-asm", "wine-jsharp20-linux-x64")]
    [InlineData("il-verify", null)]
    [InlineData("execution-flow", "wine-jsharp20-linux-x64")]
    [InlineData("run-il", null)]
    public async Task JSharpUnsupportedOutputsAreRejectedBeforeRemoteRuntimeCalls(string outputId, string? runtimeId)
    {
        var supervisor = new FakeRuntimeSupervisorClient(FakeRuntimeScenario.CompletedRun);
        await using var factory = new GatewayRuntimeTestFactory(supervisor);
        using var client = factory.CreateClient();
        var catalogRevision = await GatewayTestCatalog.GetRevisionAsync(client);

        using var response = await client.PostAsJsonAsync("/api/v1/selections/resolve", new ResolveSelectionRequest("jsharp", "vjc-jsharp20", "jsharp20-ref", outputId, runtimeId, BuildConfiguration.Release, catalogRevision, 2), ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, supervisor.RunStartCount);
        Assert.Equal(0, supervisor.JitStartCount);
    }

    [Fact]
    public async Task OperationWebSocketReusesRuntimeSessionAcrossWorkspaceRevisionsAndReleasesOnDisconnect()
    {
        var supervisor = new FakeRuntimeSupervisorClient(FakeRuntimeScenario.CompletedRun);
        await using var factory = new GatewayRuntimeTestFactory(supervisor);
        using var client = factory.CreateClient();
        var catalogRevision = await GatewayTestCatalog.GetRevisionAsync(client);
        var webSocketClient = factory.Server.CreateWebSocketClient();
        using var socket = await webSocketClient.ConnectAsync(
            new Uri("ws://localhost/api/v1/operations/ws"),
            TestContext.Current.CancellationToken);
        var selection = new ResolveSelectionRequest("csharp", "roslyn-stable", "net10-ref", "run", "dotnet-10-linux-x64", BuildConfiguration.Release, catalogRevision, 1);

        var firstResolution = await ResolveOverWebSocketAsync(socket, selection, "resolve-session-1");
        var firstHandle = await StartOverWebSocketAsync(socket, "run", CreateRunRequest(firstResolution.PipelineResolutionId, "session-run-1", "session-run-key-1"), "start-session-1");
        _ = await WaitForTerminalAsync(client, firstHandle.OperationId);

        var secondResolution = await ResolveOverWebSocketAsync(
            socket,
            selection with { WorkspaceRevision = 2 },
            "resolve-session-2");
        Assert.NotEqual(firstResolution.PipelineResolutionId, secondResolution.PipelineResolutionId);
        Assert.Equal(0, supervisor.ReleaseCount);
        var secondHandle = await StartOverWebSocketAsync(socket, "run", CreateRunRequest(secondResolution.PipelineResolutionId, "session-run-2", "session-run-key-2"), "start-session-2");
        _ = await WaitForTerminalAsync(client, secondHandle.OperationId);

        var sessionIds = supervisor.RuntimeSessionIds;
        Assert.Equal(2, sessionIds.Count);
        var runtimeSessionId = Assert.Single(sessionIds.Distinct(StringComparer.Ordinal));
        Assert.NotNull(runtimeSessionId);

        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "runtime session test complete", TestContext.Current.CancellationToken);
        var releasedSessionId = await supervisor.SessionReleased.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(runtimeSessionId, releasedSessionId);
        Assert.Equal(1, supervisor.ReleaseCount);
    }

    [Fact]
    public async Task OperationWebSocketRejectsRuntimeStartForAnOlderValidResolution()
    {
        var supervisor = new FakeRuntimeSupervisorClient(FakeRuntimeScenario.CompletedRun);
        await using var factory = new GatewayRuntimeTestFactory(supervisor);
        using var client = factory.CreateClient();
        var catalogRevision = await GatewayTestCatalog.GetRevisionAsync(client);
        var webSocketClient = factory.Server.CreateWebSocketClient();
        using var socket = await webSocketClient.ConnectAsync(
            new Uri("ws://localhost/api/v1/operations/ws"),
            TestContext.Current.CancellationToken);
        var selection = new ResolveSelectionRequest("csharp", "roslyn-stable", "net10-ref", "run", "dotnet-10-linux-x64", BuildConfiguration.Release, catalogRevision, 1);
        var firstResolution = await ResolveOverWebSocketAsync(socket, selection, "resolve-current-1");
        var firstHandle = await StartOverWebSocketAsync(socket, "run", CreateRunRequest(firstResolution.PipelineResolutionId, "current-run-1", "current-run-key-1"), "start-current-1");
        _ = await WaitForTerminalAsync(client, firstHandle.OperationId);
        var secondResolution = await ResolveOverWebSocketAsync(
            socket,
            selection with { WorkspaceRevision = 2 },
            "resolve-current-2");
        Assert.NotEqual(firstResolution.PipelineResolutionId, secondResolution.PipelineResolutionId);
        Assert.Equal(0, supervisor.ReleaseCount);

        await SendWebSocketJsonAsync(socket, new { type = "start", commandId = "start-stale-resolution", operation = "run", request = CreateRunRequest(firstResolution.PipelineResolutionId, "stale-resolution-run", "stale-resolution-run-key") });
        using (var response = await ReceiveWebSocketJsonAsync(socket))
        {
            Assert.Equal("start-stale-resolution", response.RootElement.GetProperty("CommandId").GetString());
            Assert.False(response.RootElement.GetProperty("Ok").GetBoolean());
            Assert.Equal((int)HttpStatusCode.BadRequest, response.RootElement.GetProperty("Status").GetInt32());
            Assert.Equal("runtime-session-resolution-mismatch", response.RootElement.GetProperty("Error").GetProperty("Error").GetString());
        }
        Assert.Equal(1, supervisor.RunStartCount);
        Assert.Single(supervisor.RuntimeSessionIds);

        var secondHandle = await StartOverWebSocketAsync(socket, "run", CreateRunRequest(secondResolution.PipelineResolutionId, "current-run-2", "current-run-key-2"), "start-current-2");
        _ = await WaitForTerminalAsync(client, secondHandle.OperationId);
        Assert.Equal(2, supervisor.RunStartCount);
        Assert.Single(supervisor.RuntimeSessionIds.Distinct(StringComparer.Ordinal));

        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "resolution binding test complete", TestContext.Current.CancellationToken);
        _ = await supervisor.SessionReleased.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OperationWebSocketCancelsRuntimeAndReleasesSessionWhenRuntimeSelectionChanges()
    {
        var supervisor = new FakeRuntimeSupervisorClient(FakeRuntimeScenario.CancelledRun);
        await using var factory = new GatewayRuntimeTestFactory(supervisor);
        using var client = factory.CreateClient();
        var catalogRevision = await GatewayTestCatalog.GetRevisionAsync(client);
        var webSocketClient = factory.Server.CreateWebSocketClient();
        using var socket = await webSocketClient.ConnectAsync(
            new Uri("ws://localhost/api/v1/operations/ws"),
            TestContext.Current.CancellationToken);
        var firstSelection = new ResolveSelectionRequest("csharp", "roslyn-stable", "net10-ref", "run", "dotnet-10-linux-x64", BuildConfiguration.Release, catalogRevision, 1);
        var firstResolution = await ResolveOverWebSocketAsync(socket, firstSelection, "resolve-runtime-1");
        var runHandle = await StartOverWebSocketAsync(socket, "run", CreateRunRequest(firstResolution.PipelineResolutionId, "runtime-change-run", "runtime-change-run-key"), "start-runtime-change");
        await supervisor.WatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var runtimeSessionId = Assert.Single(supervisor.RuntimeSessionIds);
        Assert.NotNull(runtimeSessionId);

        var secondResolution = await ResolveOverWebSocketAsync(
            socket,
            firstSelection with { ReferenceSetId = "net11-preview-ref", RuntimeId = "dotnet-11-preview-linux-x64", WorkspaceRevision = 2 },
            "resolve-runtime-2");

        var state = await WaitForTerminalAsync(client, runHandle.OperationId);
        Assert.Equal(OperationStatus.Cancelled, state.Status);
        Assert.Equal(1, supervisor.CancelCount);
        Assert.Equal(1, supervisor.ReleaseCount);
        Assert.Equal(runtimeSessionId, await supervisor.SessionReleased.Task);

        var secondHandle = await StartOverWebSocketAsync(
            socket,
            "run",
            CreateRunRequest(secondResolution.PipelineResolutionId, "runtime-change-run-2", "runtime-change-run-key-2") with { RuntimeProfileId = "dotnet-11-preview-linux-x64" },
            "start-runtime-change-2");
        _ = await WaitForTerminalAsync(client, secondHandle.OperationId);

        var runtimeSessionIds = supervisor.RuntimeSessionIds;
        Assert.Equal(2, runtimeSessionIds.Count);
        Assert.NotNull(runtimeSessionIds[1]);
        Assert.NotEqual(runtimeSessionId, runtimeSessionIds[1]);
    }

    [Theory]
    [InlineData("run", "run")]
    [InlineData("jit", "jit-asm")]
    public async Task OperationWebSocketCancelsActiveRuntimeAndReleasesSessionOnDisconnect(string operation, string outputId)
    {
        var supervisor = new FakeRuntimeSupervisorClient(FakeRuntimeScenario.CancelledRun);
        await using var factory = new GatewayRuntimeTestFactory(supervisor);
        using var client = factory.CreateClient();
        var catalogRevision = await GatewayTestCatalog.GetRevisionAsync(client);
        var webSocketClient = factory.Server.CreateWebSocketClient();
        using var socket = await webSocketClient.ConnectAsync(
            new Uri("ws://localhost/api/v1/operations/ws"),
            TestContext.Current.CancellationToken);
        var resolution = await ResolveOverWebSocketAsync(socket, new ResolveSelectionRequest("csharp", "roslyn-stable", "net10-ref", outputId, "dotnet-10-linux-x64", BuildConfiguration.Release, catalogRevision, 1), "resolve-disconnect");
        var operationHandle = operation == "run"
            ? await StartOverWebSocketAsync(socket, operation, CreateRunRequest(resolution.PipelineResolutionId, "disconnect-run", "disconnect-run-key"), "start-disconnect") : await StartOverWebSocketAsync(socket, operation, CreateJitRequest(resolution.PipelineResolutionId, "disconnect-jit", "disconnect-jit-key"), "start-disconnect");
        await supervisor.WatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var runtimeSessionId = Assert.Single(supervisor.RuntimeSessionIds);
        Assert.NotNull(runtimeSessionId);

        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "disconnect runtime session", TestContext.Current.CancellationToken);

        var state = await WaitForTerminalAsync(client, operationHandle.OperationId);
        Assert.Equal(OperationStatus.Cancelled, state.Status);
        Assert.Equal(1, supervisor.CancelCount);
        Assert.Equal(FakeRuntimeSupervisorClient.RemoteOperationId, supervisor.CancelledOperationId);
        Assert.Equal(runtimeSessionId, await supervisor.SessionReleased.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(1, supervisor.ReleaseCount);
    }

    [Fact]
    public async Task OperationWebSocketAbortCancelsRuntimeAndReleasesSession()
    {
        var supervisor = new FakeRuntimeSupervisorClient(FakeRuntimeScenario.CancelledRun);
        await using var factory = new GatewayRuntimeTestFactory(supervisor);
        using var client = factory.CreateClient();
        var catalogRevision = await GatewayTestCatalog.GetRevisionAsync(client);
        var webSocketClient = factory.Server.CreateWebSocketClient();
        using var socket = await webSocketClient.ConnectAsync(
            new Uri("ws://localhost/api/v1/operations/ws"),
            TestContext.Current.CancellationToken);
        var resolution = await ResolveOverWebSocketAsync(socket, new ResolveSelectionRequest("csharp", "roslyn-stable", "net10-ref", "run", "dotnet-10-linux-x64", BuildConfiguration.Release, catalogRevision, 1), "resolve-abort");
        var handle = await StartOverWebSocketAsync(socket, "run", CreateRunRequest(resolution.PipelineResolutionId, "abort-run", "abort-run-key"), "start-abort");
        await supervisor.WatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var runtimeSessionId = Assert.Single(supervisor.RuntimeSessionIds);
        Assert.NotNull(runtimeSessionId);

        socket.Abort();

        var state = await WaitForTerminalAsync(client, handle.OperationId);
        Assert.Equal(OperationStatus.Cancelled, state.Status);
        Assert.Equal(1, supervisor.CancelCount);
        Assert.Equal(runtimeSessionId, await supervisor.SessionReleased.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(1, supervisor.ReleaseCount);
    }

    [Fact]
    public async Task LocalCancellationIsPropagatedAndEndsCancelled()
    {
        var supervisor = new FakeRuntimeSupervisorClient(FakeRuntimeScenario.CancelledRun);
        await using var factory = new GatewayRuntimeTestFactory(supervisor);
        using var client = factory.CreateClient();
        var resolution = await ResolveAsync(client, "run", "dotnet-10-linux-x64");
        var request = CreateRunRequest(resolution.PipelineResolutionId, "gateway-cancel", "gateway-cancel-key");
        var handle = await StartAsync<RunRequest>(client, "/api/v1/runs", request);
        await supervisor.WatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        using var cancelResponse = await client.PostAsJsonAsync($"/api/v1/operations/{handle.OperationId}/cancel", new CancelOperationRequest(handle.OperationId, "test-cancel"), ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        cancelResponse.EnsureSuccessStatusCode();
        var state = await WaitForTerminalAsync(client, handle.OperationId);
        var events = await ReadEventsAsync(client, handle.OperationId);

        Assert.Equal(OperationStatus.Cancelled, state.Status);
        Assert.Equal(1, supervisor.CancelCount);
        Assert.Equal(FakeRuntimeSupervisorClient.RemoteOperationId, supervisor.CancelledOperationId);
        var result = Assert.IsType<RunResult>(Assert.IsType<TypedResultOperationEventPayload>(Assert.Single(events, operationEvent => operationEvent.Payload is TypedResultOperationEventPayload).Payload).Result);
        Assert.Equal(RunTerminalStatus.Cancelled, result.Status);
        var completed = Assert.IsType<CompletedOperationEventPayload>(events[^1].Payload);
        Assert.Equal(OperationCompletionStatus.Cancelled, completed.Status);
    }

    [Fact]
    public async Task RuntimeAndOutputMustMatchResolvedPipeline()
    {
        var supervisor = new FakeRuntimeSupervisorClient(FakeRuntimeScenario.CompletedRun);
        await using var factory = new GatewayRuntimeTestFactory(supervisor);
        using var client = factory.CreateClient();
        var resolution = await ResolveAsync(client, "run", "dotnet-10-linux-x64");
        var wrongRuntime = CreateRunRequest(resolution.PipelineResolutionId, "wrong-runtime", "wrong-runtime-key") with { RuntimeProfileId = "dotnet-11-preview-linux-x64" };
        var wrongOutput = CreateJitRequest(resolution.PipelineResolutionId, "wrong-output", "wrong-output-key");

        using var runtimeResponse = await client.PostAsJsonAsync("/api/v1/runs", wrongRuntime, ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        using var outputResponse = await client.PostAsJsonAsync("/api/v1/jits", wrongOutput, ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, runtimeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, outputResponse.StatusCode);
        Assert.Equal(0, supervisor.RunStartCount);
        Assert.Equal(0, supervisor.JitStartCount);
    }

    [Fact]
    public async Task MissingRequestIdentityIsRejectedBeforeCallingSupervisor()
    {
        var supervisor = new FakeRuntimeSupervisorClient(FakeRuntimeScenario.CompletedRun);
        await using var factory = new GatewayRuntimeTestFactory(supervisor);
        using var client = factory.CreateClient();
        var resolution = await ResolveAsync(client, "run", "dotnet-10-linux-x64");
        var request = CreateRunRequest(resolution.PipelineResolutionId, null!, "gateway-missing-id-key");

        using var response = await client.PostAsJsonAsync("/api/v1/runs", request, ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, supervisor.RunStartCount);
    }

    [Fact]
    public async Task SupervisorFailureIsStructuredAndNeverMarkedSafeToRetry()
    {
        var supervisor = new FakeRuntimeSupervisorClient(FakeRuntimeScenario.UnavailableStart);
        await using var factory = new GatewayRuntimeTestFactory(supervisor);
        using var client = factory.CreateClient();
        var resolution = await ResolveAsync(client, "run", "dotnet-10-linux-x64");
        var request = CreateRunRequest(resolution.PipelineResolutionId, "gateway-unavailable", "gateway-unavailable-key");

        var handle = await StartAsync<RunRequest>(client, "/api/v1/runs", request);
        var state = await WaitForTerminalAsync(client, handle.OperationId);
        var events = await ReadEventsAsync(client, handle.OperationId);

        Assert.Equal(OperationStatus.Failed, state.Status);
        Assert.NotNull(state.Error);
        Assert.Equal("runtime-supervisor-unavailable", state.Error.Code);
        Assert.True(state.Error.Retryable);
        Assert.False(state.Error.SafeToRetry);
        var failed = Assert.IsType<FailedOperationEventPayload>(events[^1].Payload);
        Assert.False(failed.Error.SafeToRetry);
    }

    [Fact]
    public async Task ExistingRemoteOperationKeepsItsOriginalRequestIdentity()
    {
        var supervisor = new FakeRuntimeSupervisorClient(FakeRuntimeScenario.ExistingRemoteOperation);
        await using var factory = new GatewayRuntimeTestFactory(supervisor);
        using var client = factory.CreateClient();
        var resolution = await ResolveAsync(client, "run", "dotnet-10-linux-x64");
        var request = CreateRunRequest(resolution.PipelineResolutionId, "gateway-retry-request", "gateway-retry-key");

        var handle = await StartAsync<RunRequest>(client, "/api/v1/runs", request);
        var state = await WaitForTerminalAsync(client, handle.OperationId);
        var events = await ReadEventsAsync(client, handle.OperationId);

        Assert.Equal(OperationStatus.Completed, state.Status);
        var localAccepted = Assert.IsType<AcceptedOperationEventPayload>(events[0].Payload);
        Assert.Equal(request.RequestId, localAccepted.RequestId);
    }

    [Fact]
    public async Task InvalidAcceptedEventBecomesProtocolFailure()
    {
        var supervisor = new FakeRuntimeSupervisorClient(FakeRuntimeScenario.InvalidAccepted);
        await using var factory = new GatewayRuntimeTestFactory(supervisor);
        using var client = factory.CreateClient();
        var resolution = await ResolveAsync(client, "run", "dotnet-10-linux-x64");
        var request = CreateRunRequest(resolution.PipelineResolutionId, "gateway-invalid-accepted", "gateway-invalid-accepted-key");

        var handle = await StartAsync<RunRequest>(client, "/api/v1/runs", request);
        var state = await WaitForTerminalAsync(client, handle.OperationId);

        Assert.Equal(OperationStatus.Failed, state.Status);
        var error = Assert.IsType<WorkerError>(state.Error);
        Assert.Equal("runtime-supervisor-protocol-invalid", error.Code);
        Assert.False(error.Retryable);
        Assert.False(error.SafeToRetry);
    }

    [Fact]
    public async Task CompletionWithoutTypedResultBecomesProtocolFailure()
    {
        var supervisor = new FakeRuntimeSupervisorClient(FakeRuntimeScenario.CompletedWithoutResult);
        await using var factory = new GatewayRuntimeTestFactory(supervisor);
        using var client = factory.CreateClient();
        var resolution = await ResolveAsync(client, "jit-asm", "dotnet-10-linux-x64");
        var request = CreateJitRequest(resolution.PipelineResolutionId, "gateway-missing-result", "gateway-missing-result-key");

        var handle = await StartAsync<JitRequest>(client, "/api/v1/jit", request);
        var state = await WaitForTerminalAsync(client, handle.OperationId);

        Assert.Equal(OperationStatus.Failed, state.Status);
        Assert.Equal("runtime-supervisor-protocol-invalid", Assert.IsType<WorkerError>(state.Error).Code);
    }

    [Fact]
    public async Task CancellationWhileStartingInterruptsTheSupervisorRequest()
    {
        var supervisor = new FakeRuntimeSupervisorClient(FakeRuntimeScenario.BlockedStart);
        await using var factory = new GatewayRuntimeTestFactory(supervisor);
        using var client = factory.CreateClient();
        var resolution = await ResolveAsync(client, "run", "dotnet-10-linux-x64");
        var request = CreateRunRequest(resolution.PipelineResolutionId, "gateway-start-cancel", "gateway-start-cancel-key");
        var handle = await StartAsync<RunRequest>(client, "/api/v1/runs", request);
        await supervisor.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        using var cancelResponse = await client.PostAsJsonAsync($"/api/v1/operations/{handle.OperationId}/cancel", new CancelOperationRequest(handle.OperationId, "cancel-during-start"), ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        cancelResponse.EnsureSuccessStatusCode();
        var state = await WaitForTerminalAsync(client, handle.OperationId);

        Assert.Equal(OperationStatus.Cancelled, state.Status);
        Assert.Equal(0, supervisor.CancelCount);
    }

    [Fact]
    public async Task DispatchProgressPrecedesABlockedSupervisorStart()
    {
        var supervisor = new FakeRuntimeSupervisorClient(FakeRuntimeScenario.BlockedStart);
        await using var factory = new GatewayRuntimeTestFactory(supervisor);
        using var client = factory.CreateClient();
        var resolution = await ResolveAsync(client, "run", "dotnet-10-linux-x64");
        var request = CreateRunRequest(resolution.PipelineResolutionId, "gateway-dispatch-progress", "gateway-dispatch-progress-key");

        var handle = await StartAsync<RunRequest>(client, "/api/v1/runs", request);
        await supervisor.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var events = factory.Services.GetRequiredService<OperationStore>().GetEvents(handle.OperationId);

        Assert.NotNull(events);
        Assert.Collection(
            events,
            operationEvent => Assert.IsType<AcceptedOperationEventPayload>(operationEvent.Payload),
            operationEvent =>
            {
                var progress = Assert.IsType<ProgressOperationEventPayload>(operationEvent.Payload);
                Assert.Equal("runtime-supervisor-dispatch", progress.Stage);
            });

        using var cancelResponse = await client.PostAsJsonAsync($"/api/v1/operations/{handle.OperationId}/cancel", new CancelOperationRequest(handle.OperationId, "dispatch-progress-test-complete"), ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        cancelResponse.EnsureSuccessStatusCode();
        Assert.Equal(OperationStatus.Cancelled, (await WaitForTerminalAsync(client, handle.OperationId)).Status);
    }

    [Fact]
    public async Task CancellationHasAHardBoundaryWhenSupervisorCallsIgnoreTokens()
    {
        var supervisor = new FakeRuntimeSupervisorClient(FakeRuntimeScenario.UnresponsiveCancellation);
        var runtimeOptions = new RuntimePipelineOptions { MaximumDuration = TimeSpan.FromSeconds(5), ControlRequestTimeout = TimeSpan.FromMilliseconds(25), CancellationGracePeriod = TimeSpan.FromMilliseconds(25) };
        await using var factory = new GatewayRuntimeTestFactory(supervisor, runtimeOptions);
        using var client = factory.CreateClient();
        var resolution = await ResolveAsync(client, "run", "dotnet-10-linux-x64");
        var request = CreateRunRequest(resolution.PipelineResolutionId, "gateway-unresponsive-cancel", "gateway-unresponsive-cancel-key");
        var handle = await StartAsync<RunRequest>(client, "/api/v1/runs", request);
        await supervisor.WatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        using var cancelResponse = await client.PostAsJsonAsync($"/api/v1/operations/{handle.OperationId}/cancel", new CancelOperationRequest(handle.OperationId, "unresponsive-supervisor"), ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        cancelResponse.EnsureSuccessStatusCode();
        var state = await WaitForTerminalAsync(client, handle.OperationId);

        Assert.Equal(OperationStatus.Cancelled, state.Status);
        Assert.Equal(1, supervisor.CancelCount);
    }

    [Fact]
    public async Task RuntimeTimeoutResultCanArriveDuringGatewayDeadlineGrace()
    {
        var supervisor = new FakeRuntimeSupervisorClient(FakeRuntimeScenario.TimeoutAtDeadline);
        var runtimeOptions = new RuntimePipelineOptions { MaximumDuration = TimeSpan.FromSeconds(5), ControlRequestTimeout = TimeSpan.FromMilliseconds(50), CancellationGracePeriod = TimeSpan.FromMilliseconds(500) };
        await using var factory = new GatewayRuntimeTestFactory(supervisor, runtimeOptions);
        using var client = factory.CreateClient();
        var resolution = await ResolveAsync(client, "run", "dotnet-10-linux-x64");
        var request = CreateRunRequest(resolution.PipelineResolutionId, "gateway-runtime-timeout", "gateway-runtime-timeout-key") with { DeadlineUtc = DateTimeOffset.UtcNow.AddMilliseconds(200) };

        var handle = await StartAsync<RunRequest>(client, "/api/v1/runs", request);
        var state = await WaitForTerminalAsync(client, handle.OperationId);
        var events = await ReadEventsAsync(client, handle.OperationId);

        Assert.Equal(OperationStatus.Completed, state.Status);
        var result = Assert.IsType<RunResult>(Assert.IsType<TypedResultOperationEventPayload>(Assert.Single(events, operationEvent => operationEvent.Payload is TypedResultOperationEventPayload).Payload).Result);
        Assert.Equal(RunTerminalStatus.Timeout, result.Status);
        Assert.Equal(0, supervisor.CancelCount);
    }

    [Fact]
    public async Task GatewayDeadlineFailsBoundedlyWhenSupervisorNeverFinishes()
    {
        var supervisor = new FakeRuntimeSupervisorClient(FakeRuntimeScenario.UnresponsiveCancellation);
        var runtimeOptions = new RuntimePipelineOptions { MaximumDuration = TimeSpan.FromMilliseconds(50), ControlRequestTimeout = TimeSpan.FromMilliseconds(25), CancellationGracePeriod = TimeSpan.FromMilliseconds(25) };
        await using var factory = new GatewayRuntimeTestFactory(supervisor, runtimeOptions);
        using var client = factory.CreateClient();
        var resolution = await ResolveAsync(client, "run", "dotnet-10-linux-x64");
        var request = CreateRunRequest(resolution.PipelineResolutionId, "gateway-hard-deadline", "gateway-hard-deadline-key");

        var handle = await StartAsync<RunRequest>(client, "/api/v1/runs", request);
        var state = await WaitForTerminalAsync(client, handle.OperationId);

        Assert.Equal(OperationStatus.Failed, state.Status);
        var error = Assert.IsType<WorkerError>(state.Error);
        Assert.Equal("runtime-pipeline-deadline-exceeded", error.Code);
        Assert.Equal(WorkerErrorCategory.DeadlineExceeded, error.Category);
        Assert.False(error.SafeToRetry);
        Assert.Equal(1, supervisor.CancelCount);
    }

    private static async Task<ResolveSelectionResponse> ResolveAsync(HttpClient client, string outputId, string runtimeId)
    {
        var catalogRevision = await GatewayTestCatalog.GetRevisionAsync(client);
        using var response = await client.PostAsJsonAsync("/api/v1/selections/resolve", new ResolveSelectionRequest("csharp", "roslyn-stable", "net10-ref", outputId, runtimeId, BuildConfiguration.Release, catalogRevision, 1), ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ResolveSelectionResponse>(ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken) ?? throw new InvalidOperationException("Selection response was empty.");
    }

    private static async Task<ResolveSelectionResponse> ResolveOverWebSocketAsync(WebSocket socket, ResolveSelectionRequest request, string commandId)
    {
        await SendWebSocketJsonAsync(socket, new { type = "resolve-selection", commandId, request });
        using var response = await ReceiveWebSocketJsonAsync(socket);
        Assert.Equal(commandId, response.RootElement.GetProperty("CommandId").GetString());
        Assert.True(response.RootElement.GetProperty("Ok").GetBoolean());
        return response.RootElement.GetProperty("Payload").Deserialize<ResolveSelectionResponse>(ContractJson.CreateSerializerOptions()) ?? throw new InvalidOperationException("Selection response was empty.");
    }

    private static async Task<OperationHandle> StartOverWebSocketAsync<TRequest>(WebSocket socket, string operation, TRequest request, string commandId)
    {
        await SendWebSocketJsonAsync(socket, new { type = "start", commandId, operation, request });
        using var response = await ReceiveWebSocketJsonAsync(socket);
        Assert.Equal(commandId, response.RootElement.GetProperty("CommandId").GetString());
        Assert.True(response.RootElement.GetProperty("Ok").GetBoolean());
        Assert.Equal((int)HttpStatusCode.Accepted, response.RootElement.GetProperty("Status").GetInt32());
        return response.RootElement.GetProperty("Payload").Deserialize<OperationHandle>(ContractJson.CreateSerializerOptions()) ?? throw new InvalidOperationException("Operation handle was empty.");
    }

    private static async Task SendWebSocketJsonAsync<T>(WebSocket socket, T value)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, ContractJson.CreateSerializerOptions());
        await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonDocument> ReceiveWebSocketJsonAsync(WebSocket socket)
    {
        using var content = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, TestContext.Current.CancellationToken);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            content.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return JsonDocument.Parse(content.ToArray());
        }
    }

    private static async Task<OperationHandle> StartAsync<TRequest>(HttpClient client, string path, TRequest request)
    {
        using var response = await client.PostAsJsonAsync(path, request, ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<OperationHandle>(ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken) ?? throw new InvalidOperationException("Operation handle was empty.");
    }

    private static async Task<OperationState> WaitForTerminalAsync(HttpClient client, string operationId)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var state = await client.GetFromJsonAsync<OperationState>($"/api/v1/operations/{operationId}", ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken) ?? throw new InvalidOperationException("Operation state was empty.");
            if (state.Status is OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled)
                return state;

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("Gateway runtime operation did not complete.");
    }

    private static async Task<IReadOnlyList<OperationEvent>> ReadEventsAsync(HttpClient client, string operationId)
    {
        using var response = await client.GetAsync($"/api/v1/operations/{operationId}/events?FromSequence=0", HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var events = body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(static line => line.StartsWith("data: ", StringComparison.Ordinal))
            .Select(static line => JsonSerializer.Deserialize<OperationEvent>(line.AsSpan("data: ".Length), ContractJson.CreateSerializerOptions()) ?? throw new InvalidOperationException("Operation event was empty.")).ToArray();
        OperationEventStreamContract.Validate(events);
        return events;
    }

    private static RunRequest CreateRunRequest(string pipelineId, string requestId, string key) => new(requestId, key, pipelineId, Artifact(), "dotnet-10-linux-x64", new RunOptions([], null, RunInstrumentation.None, "runtime-job-default"), DateTimeOffset.UtcNow.AddMinutes(1));

    private static JitRequest CreateJitRequest(string pipelineId, string requestId, string key) => new(requestId, key, pipelineId, Artifact(), "dotnet-10-linux-x64", new JitOptions(null, "tier0-diffable", "disabled", "coreclr-jitdisasm", "runtime-job-default"), DateTimeOffset.UtcNow.AddMinutes(1));

    private static ArtifactRef Artifact() => new($"sha256:{new string('a', 64)}");
}

internal sealed class GatewayRuntimeTestFactory(FakeRuntimeSupervisorClient supervisor, RuntimePipelineOptions? runtimeOptions = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IRuntimeSupervisorClient>();
            services.AddSingleton<IRuntimeSupervisorClient>(supervisor);
            if (runtimeOptions is not null)
            {
                services.RemoveAll<RuntimePipelineOptions>();
                services.AddSingleton(runtimeOptions);
            }
        });
    }
}

internal enum FakeRuntimeScenario
{
    CompletedRun,
    CompletedJit,
    CancelledRun,
    UnavailableStart,
    InvalidAccepted,
    CompletedWithoutResult,
    BlockedStart,
    UnresponsiveCancellation,
    TimeoutAtDeadline,
    ExistingRemoteOperation
}

internal sealed class FakeRuntimeSupervisorClient(FakeRuntimeScenario scenario) : IRuntimeSupervisorClient
{
    public const string RemoteOperationId = "op_remote_gateway_runtime";
    private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _unresponsiveWatcher = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<CancelResult> _unresponsiveCancel = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<string?> _runtimeSessionIds = new();
    private int _releaseCount;
    private string? _requestId;
    private OperationKind _kind;
    private DateTimeOffset _deadlineUtc;

    public string EncodedOutput { get; } = Convert.ToBase64String(Encoding.UTF8.GetBytes("runtime output\n"));

    public TaskCompletionSource WatchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<string> SessionReleased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<string?> RuntimeSessionIds => _runtimeSessionIds.ToArray();

    public int ReleaseCount => Volatile.Read(ref _releaseCount);

    public int RunStartCount { get; private set; }

    public RunRequest? LastRunRequest { get; private set; }

    public int JitStartCount { get; private set; }

    public int CancelCount { get; private set; }

    public string? CancelledOperationId { get; private set; }

    public Task<OperationHandle> StartRunAsync(RunRequest request, CancellationToken cancellationToken = default) => StartRunCoreAsync(request, cancellationToken);

    public Task<OperationHandle> StartRunAsync(RunRequest request, string? runtimeSessionId, CancellationToken cancellationToken = default)
    {
        _runtimeSessionIds.Enqueue(runtimeSessionId);
        return StartRunCoreAsync(request, cancellationToken);
    }

    private Task<OperationHandle> StartRunCoreAsync(RunRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RunStartCount++;
        LastRunRequest = request;
        _requestId = request.RequestId;
        _kind = OperationKind.Run;
        _deadlineUtc = request.DeadlineUtc;
        if (scenario == FakeRuntimeScenario.ExistingRemoteOperation)
        {
            _requestId = "original-runtime-request";
            return Task.FromResult(new OperationHandle(RemoteOperationId, _requestId, DateTimeOffset.UtcNow, true));
        }

        if (scenario == FakeRuntimeScenario.UnavailableStart)
            return Task.FromException<OperationHandle>(new RuntimeSupervisorClientException(new WorkerError("runtime-supervisor-unavailable", WorkerErrorCategory.Unavailable, "The runtime supervisor is unavailable.", true, true, "remote-trace", "runtime-supervisor", "runtime-image")));

        if (scenario == FakeRuntimeScenario.BlockedStart)
        {
            StartEntered.TrySetResult();
            return WaitForStartCancellationAsync(cancellationToken);
        }

        return Task.FromResult(Handle(request.RequestId));
    }

    public Task<OperationHandle> StartJitAsync(JitRequest request, CancellationToken cancellationToken = default) => StartJitCoreAsync(request, cancellationToken);

    public Task<OperationHandle> StartJitAsync(JitRequest request, string? runtimeSessionId, CancellationToken cancellationToken = default)
    {
        _runtimeSessionIds.Enqueue(runtimeSessionId);
        return StartJitCoreAsync(request, cancellationToken);
    }

    private Task<OperationHandle> StartJitCoreAsync(JitRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        JitStartCount++;
        _requestId = request.RequestId;
        _kind = OperationKind.Jit;
        return Task.FromResult(Handle(request.RequestId));
    }

    public Task<OperationState?> GetOperationAsync(string operationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public async IAsyncEnumerable<OperationEvent> WatchEventsAsync(string operationId, long fromSequence = 0, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Assert.Equal(RemoteOperationId, operationId);
        Assert.Equal(0, fromSequence);
        var requestId = _requestId ?? throw new InvalidOperationException("Runtime job was not started.");
        var acceptedKind = scenario == FakeRuntimeScenario.InvalidAccepted
            ? (_kind == OperationKind.Run ? OperationKind.Jit : OperationKind.Run) : _kind;
        yield return Event(40, new AcceptedOperationEventPayload(requestId, acceptedKind));
        WatchStarted.TrySetResult();

        if (scenario == FakeRuntimeScenario.InvalidAccepted)
            yield break;

        if (scenario == FakeRuntimeScenario.CompletedWithoutResult)
        {
            yield return Event(90, new CompletedOperationEventPayload(OperationCompletionStatus.Completed, TimeSpan.FromMilliseconds(10)));
            yield break;
        }

        if (scenario == FakeRuntimeScenario.UnresponsiveCancellation)
        {
            await _unresponsiveWatcher.Task;
            yield break;
        }

        if (scenario == FakeRuntimeScenario.TimeoutAtDeadline)
        {
            var remaining = _deadlineUtc - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining, CancellationToken.None);

            yield return Event(70, new TypedResultOperationEventPayload(TimeoutRunResult()));
            yield return Event(90, new CompletedOperationEventPayload(OperationCompletionStatus.Completed, TimeSpan.FromMilliseconds(200)));
            yield break;
        }

        if (scenario == FakeRuntimeScenario.CancelledRun)
        {
            await _cancelled.Task.WaitAsync(cancellationToken);
            yield return Event(70, new TypedResultOperationEventPayload(_kind == OperationKind.Run ? CancelledRunResult() : CancelledJitResult()));
            yield return Event(90, new CompletedOperationEventPayload(OperationCompletionStatus.Cancelled, TimeSpan.FromMilliseconds(10)));
            yield break;
        }

        cancellationToken.ThrowIfCancellationRequested();
        yield return Event(50, new ProgressOperationEventPayload("runtime", "Running isolated job.", 0.5));
        if (scenario is FakeRuntimeScenario.CompletedRun or FakeRuntimeScenario.ExistingRemoteOperation)
        {
            yield return Event(60, new OutputChunkOperationEventPayload(new OutputChunk(OutputChannel.Stdout, OutputEncoding.Utf8, EncodedOutput, false)));
            yield return Event(70, new TypedResultOperationEventPayload(CompletedRunResult()));
        }
        else
        {
            var contentRef = new ContentRef($"sha256:{new string('b', 64)}");
            yield return Event(60, new ContentProducedOperationEventPayload(contentRef, "text/plain", 128));
            yield return Event(70, new TypedResultOperationEventPayload(CompletedJitResult(contentRef)));
        }

        yield return Event(90, new CompletedOperationEventPayload(OperationCompletionStatus.Completed, TimeSpan.FromMilliseconds(10)));
    }

    public Task<CancelResult> CancelAsync(string operationId, string? reason = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancelCount++;
        CancelledOperationId = operationId;
        if (scenario == FakeRuntimeScenario.UnresponsiveCancellation)
            return _unresponsiveCancel.Task;

        _cancelled.TrySetResult();
        _ = reason;
        return Task.FromResult(new CancelResult(operationId, CancelDisposition.Accepted, 1));
    }

    public Task ReleaseSessionAsync(string runtimeSessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _releaseCount);
        SessionReleased.TrySetResult(runtimeSessionId);
        return Task.CompletedTask;
    }

    private static OperationHandle Handle(string requestId) => new(RemoteOperationId, requestId, DateTimeOffset.UtcNow, false);

    private static async Task<OperationHandle> WaitForStartCancellationAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("The blocked start request was not cancelled.");
    }

    private static OperationEvent Event(long sequence, OperationEventPayload payload) => new(RemoteOperationId, sequence, DateTimeOffset.UtcNow, "remote-runtime-trace", payload);

    private static RunResult CompletedRunResult() => new(RunTerminalStatus.Completed, 0, null, TimeSpan.FromMilliseconds(10), false, RuntimeIdentity());

    private static RunResult CancelledRunResult() => new(RunTerminalStatus.Cancelled, null, null, TimeSpan.FromMilliseconds(10), false, RuntimeIdentity());

    private static JitResult CancelledJitResult() => new(JitTerminalStatus.Cancelled, null, null, [], TimeSpan.FromMilliseconds(10), RuntimeJitIdentity());

    private static RunResult TimeoutRunResult() => new(RunTerminalStatus.Timeout, null, null, TimeSpan.FromMilliseconds(200), false, RuntimeIdentity());

    private static JitResult CompletedJitResult(ContentRef contentRef) => new(
        JitTerminalStatus.Completed,
        contentRef,
        contentRef,
        [new JitMethodSummary(
            "method-1",
            "Program.Main()",
            32,
            8,
            [new LinkedRange("Program.cs", new TextRange(2, 0, 2, 12), new TextRange(4, 0, 4, 18), "sequence-point")])],
        TimeSpan.FromMilliseconds(10),
        RuntimeJitIdentity());

    private static JitIdentity RuntimeJitIdentity() => new(
        "10.0.9",
        "runtime-commit",
        "10.0.9",
        "jit-commit",
        "runtime-image",
        "linux-x64",
        "x64",
        "baseline-x64",
        "tier0-diffable",
        "disabled",
        "coreclr-jitdisasm",
        "jit-disasm");

    private static RuntimeIdentity RuntimeIdentity() => new("10.0.9", "runtime-commit", "runtime-image", "linux-x64", "x64");
}
