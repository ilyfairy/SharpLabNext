using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SharpLabNext.Contracts;
using SharpLabNext.RuntimeSupervisor.Client;

namespace SharpLabNext.IntegrationTests;

public sealed class RuntimeSupervisorClientTests
{
    [Fact]
    public async Task RunJobAndSseEventsUseContractsWithoutDecodingOutput()
    {
        const string remoteOperationId = "op_remote_client_run";
        var encodedOutput = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello from runtime\n"));
        var runRequest = CreateRunRequest("client-run-request", "client-run-key");
        using var httpClient = new HttpClient(new AsyncDelegateHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/internal/v1/jobs/run")
            {
                Assert.False(request.Headers.Contains("X-SharpLabNext-Runtime-Session-Id"));
                var observed = await request.Content!.ReadFromJsonAsync<RunRequest>(
                    ContractJson.CreateSerializerOptions(),
                    cancellationToken);
                Assert.NotNull(observed);
                Assert.Equal(runRequest.RequestId, observed.RequestId);
                Assert.Equal(runRequest.IdempotencyKey, observed.IdempotencyKey);
                Assert.Equal(runRequest.PipelineResolutionId, observed.PipelineResolutionId);
                Assert.Equal(runRequest.ArtifactRef, observed.ArtifactRef);
                Assert.Equal(runRequest.RuntimeProfileId, observed.RuntimeProfileId);
                Assert.Equal(runRequest.Options.Arguments, observed.Options.Arguments);
                Assert.Equal(runRequest.Options.Instrumentation, observed.Options.Instrumentation);
                Assert.Equal(runRequest.Options.SecurityPolicyId, observed.Options.SecurityPolicyId);
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = JsonContent.Create(
                        new OperationHandle(remoteOperationId, runRequest.RequestId, DateTimeOffset.UtcNow, false),
                        options: ContractJson.CreateSerializerOptions())
                };
            }

            if (request.Method == HttpMethod.Get &&
                request.RequestUri?.AbsolutePath == $"/internal/v1/operations/{remoteOperationId}/events")
            {
                var events = new OperationEvent[]
                {
                    Event(1, new AcceptedOperationEventPayload(runRequest.RequestId, OperationKind.Run)),
                    Event(2, new OutputChunkOperationEventPayload(new OutputChunk(
                        OutputChannel.Stdout,
                        OutputEncoding.Utf8,
                        encodedOutput,
                        false))),
                    Event(3, new TypedResultOperationEventPayload(CompletedRunResult())),
                    Event(4, new CompletedOperationEventPayload(OperationCompletionStatus.Completed, TimeSpan.FromMilliseconds(5)))
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SerializeSse(events), Encoding.UTF8, "text/event-stream")
                };
            }

            if (request.Method == HttpMethod.Post &&
                request.RequestUri?.AbsolutePath == $"/internal/v1/operations/{remoteOperationId}/cancel")
            {
                var observed = await request.Content!.ReadFromJsonAsync<CancelOperationRequest>(
                    ContractJson.CreateSerializerOptions(),
                    cancellationToken);
                Assert.Equal(remoteOperationId, observed?.OperationId);
                Assert.Equal("client-test", observed?.Reason);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(
                        new CancelResult(remoteOperationId, CancelDisposition.AlreadyTerminal, 4),
                        options: ContractJson.CreateSerializerOptions())
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }))
        {
            BaseAddress = new Uri("http://runtime-supervisor.test", UriKind.Absolute),
            Timeout = Timeout.InfiniteTimeSpan
        };
        var client = new RuntimeSupervisorClient(
            httpClient,
            new RuntimeSupervisorClientSettings(TimeSpan.FromSeconds(2)));

        var handle = await client.StartRunAsync(runRequest, TestContext.Current.CancellationToken);
        var events = new List<OperationEvent>();
        await foreach (var operationEvent in client.WatchEventsAsync(
                           handle.OperationId,
                           cancellationToken: TestContext.Current.CancellationToken))
        {
            events.Add(operationEvent);
        }

        Assert.Equal(remoteOperationId, handle.OperationId);
        Assert.Equal(4, events.Count);
        var output = Assert.IsType<OutputChunkOperationEventPayload>(events[1].Payload);
        Assert.Equal(encodedOutput, output.Chunk.Data);
        Assert.Equal("hello from runtime\n", Encoding.UTF8.GetString(Convert.FromBase64String(output.Chunk.Data)));
        var cancellation = await client.CancelAsync(
            remoteOperationId,
            "client-test",
            TestContext.Current.CancellationToken);
        Assert.Equal(CancelDisposition.AlreadyTerminal, cancellation.Disposition);
    }

    [Fact]
    public async Task RuntimeSessionIsSentOnJobsAndReleasedExplicitly()
    {
        const string runtimeSessionId = "rs_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var runRequest = CreateRunRequest("session-run-request", "session-run-key");
        var jitRequest = new JitRequest(
            "session-jit-request",
            "session-jit-key",
            "pr_runtime_client_jit",
            new ArtifactRef($"sha256:{new string('b', 64)}"),
            "dotnet-10-linux-x64",
            new JitOptions(null, "tier0-diffable", "disabled", "coreclr-jitdisasm", "runtime-job-default"),
            DateTimeOffset.UtcNow.AddMinutes(1));
        var observedPaths = new List<string>();
        using var httpClient = new HttpClient(new AsyncDelegateHandler(async (request, cancellationToken) =>
        {
            var path = request.RequestUri?.AbsolutePath
                ?? throw new InvalidOperationException("Runtime Supervisor request path was missing.");
            observedPaths.Add(path);
            if (path == "/internal/v1/jobs/run")
            {
                Assert.Equal(runtimeSessionId, Assert.Single(request.Headers.GetValues(
                    "X-SharpLabNext-Runtime-Session-Id")));
                var observed = await request.Content!.ReadFromJsonAsync<RunRequest>(
                    ContractJson.CreateSerializerOptions(),
                    cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = JsonContent.Create(
                        new OperationHandle("op_session_run", observed!.RequestId, DateTimeOffset.UtcNow, false),
                        options: ContractJson.CreateSerializerOptions())
                };
            }

            if (path == "/internal/v1/jobs/jit")
            {
                Assert.Equal(runtimeSessionId, Assert.Single(request.Headers.GetValues(
                    "X-SharpLabNext-Runtime-Session-Id")));
                var observed = await request.Content!.ReadFromJsonAsync<JitRequest>(
                    ContractJson.CreateSerializerOptions(),
                    cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = JsonContent.Create(
                        new OperationHandle("op_session_jit", observed!.RequestId, DateTimeOffset.UtcNow, false),
                        options: ContractJson.CreateSerializerOptions())
                };
            }

            if (path == $"/internal/v1/sessions/{runtimeSessionId}/release")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }))
        {
            BaseAddress = new Uri("http://runtime-supervisor.test", UriKind.Absolute),
            Timeout = Timeout.InfiniteTimeSpan
        };
        var client = new RuntimeSupervisorClient(
            httpClient,
            new RuntimeSupervisorClientSettings(TimeSpan.FromSeconds(2)));

        _ = await client.StartRunAsync(
            runRequest,
            runtimeSessionId,
            TestContext.Current.CancellationToken);
        _ = await client.StartJitAsync(
            jitRequest,
            runtimeSessionId,
            TestContext.Current.CancellationToken);
        await client.ReleaseSessionAsync(runtimeSessionId, TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "/internal/v1/jobs/run",
                "/internal/v1/jobs/jit",
                $"/internal/v1/sessions/{runtimeSessionId}/release"
            ],
            observedPaths);
    }

    [Fact]
    public async Task SupervisorValidationProblemIsMappedToStructuredError()
    {
        using var httpClient = new HttpClient(new AsyncDelegateHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    """{"Error":"invalid-artifact-ref","Message":"The artifact reference is malformed."}""",
                    Encoding.UTF8,
                    "application/json")
            })))
        {
            BaseAddress = new Uri("http://runtime-supervisor.test", UriKind.Absolute)
        };
        var client = new RuntimeSupervisorClient(
            httpClient,
            new RuntimeSupervisorClientSettings(TimeSpan.FromSeconds(2)));

        var exception = await Assert.ThrowsAsync<RuntimeSupervisorClientException>(() => client.StartRunAsync(
            CreateRunRequest("invalid-request", "invalid-key"),
            TestContext.Current.CancellationToken));

        Assert.Equal("invalid-artifact-ref", exception.Error.Code);
        Assert.Equal(WorkerErrorCategory.InvalidArgument, exception.Error.Category);
        Assert.False(exception.Error.Retryable);
        Assert.Equal(400, exception.StatusCode);
    }

    [Fact]
    public async Task ControlRequestTimeoutIsMappedWithoutUsingHttpClientTimeout()
    {
        using var httpClient = new HttpClient(new AsyncDelegateHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }))
        {
            BaseAddress = new Uri("http://runtime-supervisor.test", UriKind.Absolute),
            Timeout = Timeout.InfiniteTimeSpan
        };
        var client = new RuntimeSupervisorClient(
            httpClient,
            new RuntimeSupervisorClientSettings(TimeSpan.FromMilliseconds(25)));

        var exception = await Assert.ThrowsAsync<RuntimeSupervisorClientException>(() => client.StartRunAsync(
            CreateRunRequest("timeout-request", "timeout-key"),
            TestContext.Current.CancellationToken));

        Assert.Equal("runtime-supervisor-control-timeout", exception.Error.Code);
        Assert.Equal(WorkerErrorCategory.DeadlineExceeded, exception.Error.Category);
        Assert.True(exception.Error.Retryable);
        Assert.False(exception.Error.SafeToRetry);
    }

    [Fact]
    public async Task CancelNotFoundUsesContractDisposition()
    {
        const string operationId = "op_missing_runtime";
        using var httpClient = new HttpClient(new AsyncDelegateHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"/internal/v1/operations/{operationId}/cancel", request.RequestUri?.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }))
        {
            BaseAddress = new Uri("http://runtime-supervisor.test", UriKind.Absolute),
            Timeout = Timeout.InfiniteTimeSpan
        };
        var client = new RuntimeSupervisorClient(
            httpClient,
            new RuntimeSupervisorClientSettings(TimeSpan.FromSeconds(2)));

        var result = await client.CancelAsync(
            operationId,
            "client-test",
            TestContext.Current.CancellationToken);

        Assert.Equal(CancelDisposition.NotFound, result.Disposition);
        Assert.Equal(0, result.LastSequence);
    }

    [Fact]
    public async Task NonObjectErrorBodyStillMapsToStructuredFailure()
    {
        using var httpClient = new HttpClient(new AsyncDelegateHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("\"temporarily unavailable\"", Encoding.UTF8, "application/json")
            })))
        {
            BaseAddress = new Uri("http://runtime-supervisor.test", UriKind.Absolute),
            Timeout = Timeout.InfiniteTimeSpan
        };
        var client = new RuntimeSupervisorClient(
            httpClient,
            new RuntimeSupervisorClientSettings(TimeSpan.FromSeconds(2)));

        var exception = await Assert.ThrowsAsync<RuntimeSupervisorClientException>(() => client.StartRunAsync(
            CreateRunRequest("unavailable-request", "unavailable-key"),
            TestContext.Current.CancellationToken));

        Assert.Equal("runtime-supervisor-http-503", exception.Error.Code);
        Assert.Equal(WorkerErrorCategory.Unavailable, exception.Error.Category);
        Assert.True(exception.Error.Retryable);
        Assert.False(exception.Error.SafeToRetry);
    }

    [Fact]
    public async Task ResumeAtTerminalSequenceAcceptsAnEmptyEventStream()
    {
        const string operationId = "op_terminal_runtime";
        using var httpClient = new HttpClient(new AsyncDelegateHandler((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath == $"/internal/v1/operations/{operationId}/events")
            {
                Assert.Equal("?FromSequence=4", request.RequestUri.Query);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(string.Empty, Encoding.UTF8, "text/event-stream")
                });
            }

            if (request.RequestUri?.AbsolutePath == $"/internal/v1/operations/{operationId}")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(
                        new OperationState(
                            operationId,
                            "terminal-request",
                            OperationKind.Run,
                            OperationStatus.Completed,
                            4,
                            DateTimeOffset.UtcNow,
                            DateTimeOffset.UtcNow,
                            DateTimeOffset.UtcNow,
                            "terminal-trace",
                            null),
                        options: ContractJson.CreateSerializerOptions())
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }))
        {
            BaseAddress = new Uri("http://runtime-supervisor.test", UriKind.Absolute),
            Timeout = Timeout.InfiniteTimeSpan
        };
        var client = new RuntimeSupervisorClient(
            httpClient,
            new RuntimeSupervisorClientSettings(TimeSpan.FromSeconds(2)));
        var events = new List<OperationEvent>();

        await foreach (var operationEvent in client.WatchEventsAsync(
                           operationId,
                           fromSequence: 4,
                           cancellationToken: TestContext.Current.CancellationToken))
        {
            events.Add(operationEvent);
        }

        Assert.Empty(events);
    }

    [Fact]
    public async Task ExistingOperationMayReturnItsOriginalRequestId()
    {
        const string operationId = "op_existing_runtime";
        using var httpClient = new HttpClient(new AsyncDelegateHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = JsonContent.Create(
                    new OperationHandle(
                        operationId,
                        "original-runtime-request",
                        DateTimeOffset.UtcNow,
                        true),
                    options: ContractJson.CreateSerializerOptions())
            })))
        {
            BaseAddress = new Uri("http://runtime-supervisor.test", UriKind.Absolute),
            Timeout = Timeout.InfiniteTimeSpan
        };
        var client = new RuntimeSupervisorClient(
            httpClient,
            new RuntimeSupervisorClientSettings(TimeSpan.FromSeconds(2)));

        var handle = await client.StartRunAsync(
            CreateRunRequest("retry-runtime-request", "existing-runtime-key"),
            TestContext.Current.CancellationToken);

        Assert.True(handle.IsExisting);
        Assert.Equal("original-runtime-request", handle.RequestId);
    }

    private static RunRequest CreateRunRequest(string requestId, string key) => new(
        requestId,
        key,
        "pr_runtime_client",
        new ArtifactRef($"sha256:{new string('a', 64)}"),
        "dotnet-10-linux-x64",
        new RunOptions([], null, RunInstrumentation.None, "runtime-job-default"),
        DateTimeOffset.UtcNow.AddMinutes(1));

    private static RunResult CompletedRunResult() => new(
        RunTerminalStatus.Completed,
        0,
        null,
        TimeSpan.FromMilliseconds(5),
        false,
        new RuntimeIdentity("10.0.9", "runtime-commit", "runtime-image", "linux-x64", "x64"));

    private static OperationEvent Event(long sequence, OperationEventPayload payload) => new(
        "op_remote_client_run",
        sequence,
        DateTimeOffset.UtcNow,
        "remote-trace",
        payload);

    private static string SerializeSse(IEnumerable<OperationEvent> events)
    {
        var options = ContractJson.CreateSerializerOptions();
        return string.Concat(events.Select(operationEvent =>
            $"id: {operationEvent.Sequence}\nevent: operation\ndata: {JsonSerializer.Serialize(operationEvent, options)}\n\n"));
    }

    private sealed class AsyncDelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
