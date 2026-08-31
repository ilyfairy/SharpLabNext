using System.Net;
using System.Net.Http.Json;
using System.Text;
using SharpLabNext.Contracts;
using SharpLabNext.Worker.Client;

namespace SharpLabNext.IntegrationTests;

public sealed class ToolchainWorkerClientTests
{
    [Fact]
    public async Task WorkerProblemIsMappedToStructuredError()
    {
        using var httpClient = new HttpClient(new DelegateHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/v1/worker/describe")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(CreateDescriptor(), options: ContractJson.CreateSerializerOptions())
                };
            }

            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(
                    """{"Title":"unavailable","Detail":"Reference set is unavailable.","Code":"unavailable","TraceId":"worker-trace","WorkerId":"roslyn-stable"}""",
                    Encoding.UTF8,
                    "application/problem+json")
            };
        }))
        {
            BaseAddress = new Uri("http://worker.test", UriKind.Absolute)
        };
        var client = new ToolchainWorkerClient(httpClient, new ToolchainWorkerClientSettings("roslyn-stable", "content", "worker-image"));

        var exception = await Assert.ThrowsAsync<ToolchainWorkerException>(() => client.BuildAsync(CreateRequest(), TestContext.Current.CancellationToken));
        Assert.Equal("unavailable", exception.Error.Code);
        Assert.Equal(WorkerErrorCategory.Unavailable, exception.Error.Category);
        Assert.True(exception.Error.Retryable);
        Assert.True(exception.Error.SafeToRetry);
        Assert.Equal("worker-trace", exception.Error.TraceId);
    }

    [Fact]
    public async Task MismatchedWorkerReleaseIsRejectedBeforeBuild()
    {
        var buildCalled = false;
        using var httpClient = new HttpClient(new DelegateHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/v1/build")
                buildCalled = true;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                    CreateDescriptor() with { Service = CreateDescriptor().Service with { ReleaseId = "another-release" } },
                    options: ContractJson.CreateSerializerOptions())
            };
        }))
        {
            BaseAddress = new Uri("http://worker.test", UriKind.Absolute)
        };
        var client = new ToolchainWorkerClient(httpClient, new ToolchainWorkerClientSettings("roslyn-stable", "content", "worker-image"));

        var exception = await Assert.ThrowsAsync<ToolchainWorkerException>(() => client.BuildAsync(CreateRequest(), TestContext.Current.CancellationToken));
        Assert.Equal("worker-protocol-invalid", exception.Error.Code);
        Assert.False(buildCalled);
    }

    [Fact]
    public async Task MissingReferenceSetAttestationIsRejectedBeforeBuild()
    {
        var buildCalled = false;
        using var httpClient = new HttpClient(new DelegateHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/v1/build")
                buildCalled = true;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(CreateDescriptor(), options: ContractJson.CreateSerializerOptions())
            };
        }))
        {
            BaseAddress = new Uri("http://worker.test", UriKind.Absolute)
        };
        var client = new ToolchainWorkerClient(
            httpClient,
            new ToolchainWorkerClientSettings(
                "roslyn-stable",
                "content",
                "worker-image",
                new Dictionary<string, string> { ["net10-ref"] = "sha512-expected" }));

        var exception = await Assert.ThrowsAsync<ToolchainWorkerException>(() => client.BuildAsync(CreateRequest(), TestContext.Current.CancellationToken));

        Assert.Equal("worker-protocol-invalid", exception.Error.Code);
        Assert.Contains("omitted reference-set attestations", exception.Message, StringComparison.Ordinal);
        Assert.False(buildCalled);
    }

    [Fact]
    public async Task MismatchedReferenceSetAttestationIsRejectedBeforeBuild()
    {
        var buildCalled = false;
        using var httpClient = new HttpClient(new DelegateHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/v1/build")
                buildCalled = true;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                    CreateDescriptor() with { ReferenceSets = [CreateReferenceSetAttestation("sha512-actual")] },
                    options: ContractJson.CreateSerializerOptions())
            };
        }))
        {
            BaseAddress = new Uri("http://worker.test", UriKind.Absolute)
        };
        var client = new ToolchainWorkerClient(
            httpClient,
            new ToolchainWorkerClientSettings(
                "roslyn-stable",
                "content",
                "worker-image",
                new Dictionary<string, string> { ["net10-ref"] = "sha512-expected" }));

        var exception = await Assert.ThrowsAsync<ToolchainWorkerException>(() => client.BuildAsync(CreateRequest(), TestContext.Current.CancellationToken));

        Assert.Equal("worker-protocol-invalid", exception.Error.Code);
        Assert.Contains("active release lock", exception.Message, StringComparison.Ordinal);
        Assert.False(buildCalled);
    }

    [Fact]
    public async Task GenericArtifactCapabilityAllowsNonManagedPeWorkerBuild()
    {
        var buildCalled = false;
        using var httpClient = new HttpClient(new DelegateHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/v1/worker/describe")
            {
                var descriptor = CreateDescriptor();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(
                        descriptor with
                        {
                            Service = descriptor.Service with { Id = "minilang-stable", Capabilities = ["artifact"] },
                            Capabilities =
                            [
                                new WorkerCapabilityDescriptor("artifact", 1, true, ["minilang-stable"])
                            ],
                            ProfileIds = ["minilang-stable"]
                        },
                        options: ContractJson.CreateSerializerOptions())
                };
            }
            buildCalled = true;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(
                    """{"Title":"unavailable","Detail":"Sample failure after capability validation."}""",
                    Encoding.UTF8,
                    "application/problem+json")
            };
        }))
        {
            BaseAddress = new Uri("http://worker.test", UriKind.Absolute)
        };
        var client = new ToolchainWorkerClient(httpClient, new ToolchainWorkerClientSettings("minilang-stable", "content", "worker-image"));
        var request = CreateRequest() with { ToolchainId = "minilang-stable", Target = BuildTarget.Artifact };

        _ = await Assert.ThrowsAsync<ToolchainWorkerException>(() => client.BuildAsync(request, TestContext.Current.CancellationToken));

        Assert.True(buildCalled);
    }

    [Fact]
    public async Task SharedWorkerAcceptsBuildFromRequestedToolchainProfile()
    {
        const string workerId = "gsharp-worker";
        const string requestedToolchainId = "gsharp-legacy-0.3.8";
        var request = CreateRequest() with { ToolchainId = requestedToolchainId };
        var descriptor = CreateDescriptor() with
        {
            Service = CreateDescriptor().Service with { Id = workerId },
            Capabilities =
            [
                new WorkerCapabilityDescriptor("compile-check", 1, true, ["gsharp-stable", requestedToolchainId])
            ],
            ProfileIds = ["gsharp-stable", requestedToolchainId]
        };
        var buildCalled = false;
        using var httpClient = new HttpClient(new DelegateHandler(message =>
        {
            if (message.RequestUri?.AbsolutePath == "/api/v1/worker/describe")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(descriptor, options: ContractJson.CreateSerializerOptions())
                };
            }

            buildCalled = true;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                    new ToolchainBuildResponse(
                        request.RequestId,
                        new CompilationCheckResult(
                            true,
                            [],
                            CreateBuildIdentity() with { ToolchainId = requestedToolchainId },
                            request.Workspace.Revision,
                            request.Workspace.SelectionRevision),
                        null),
                    options: ContractJson.CreateSerializerOptions())
            };
        }))
        {
            BaseAddress = new Uri("http://worker.test", UriKind.Absolute)
        };
        var client = new ToolchainWorkerClient(httpClient, new ToolchainWorkerClientSettings(workerId, "content", "worker-image"));

        var response = await client.BuildAsync(request, TestContext.Current.CancellationToken);

        Assert.True(buildCalled);
        var result = Assert.IsType<CompilationCheckResult>(response.Result);
        Assert.Equal(requestedToolchainId, result.Identity.ToolchainId);
    }

    [Fact]
    public async Task SharedWorkerRejectsCapabilityNotExposedByRequestedProfile()
    {
        const string workerId = "gsharp-worker";
        const string requestedToolchainId = "gsharp-legacy-0.3.8";
        var request = CreateRequest() with { ToolchainId = requestedToolchainId };
        var descriptor = CreateDescriptor() with
        {
            Service = CreateDescriptor().Service with { Id = workerId },
            Capabilities =
            [
                new WorkerCapabilityDescriptor("compile-check", 1, true, ["gsharp-stable"])
            ],
            ProfileIds = ["gsharp-stable", requestedToolchainId]
        };
        var buildCalled = false;
        using var httpClient = new HttpClient(new DelegateHandler(message =>
        {
            if (message.RequestUri?.AbsolutePath == "/api/v1/build")
                buildCalled = true;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(descriptor, options: ContractJson.CreateSerializerOptions())
            };
        }))
        {
            BaseAddress = new Uri("http://worker.test", UriKind.Absolute)
        };
        var client = new ToolchainWorkerClient(httpClient, new ToolchainWorkerClientSettings(workerId, "content", "worker-image"));

        var exception = await Assert.ThrowsAsync<ToolchainWorkerException>(() => client.BuildAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal("worker-capability-unavailable", exception.Error.Code);
        Assert.False(buildCalled);
    }

    [Fact]
    public async Task SuccessfulProductionArtifactBuildAcceptsReferenceWithoutDevelopmentEnvelope()
    {
        var request = CreateRequest() with { Target = BuildTarget.Artifact };
        var artifactRef = new ArtifactRef($"sha256:{new string('a', 64)}");
        using var httpClient = CreateArtifactWorkerHttpClient(request, new BuildResult(BuildOutcome.Succeeded, artifactRef, [], CreateBuildIdentity(), request.Workspace.Revision, request.Workspace.SelectionRevision));
        var client = new ToolchainWorkerClient(httpClient, new ToolchainWorkerClientSettings("roslyn-stable", "content", "worker-image"));

        var response = await client.BuildAsync(request, TestContext.Current.CancellationToken);

        var result = Assert.IsType<BuildResult>(response.Result);
        Assert.Equal(artifactRef, result.ArtifactRef);
        Assert.Null(response.DevelopmentArtifact);
    }

    [Fact]
    public async Task SuccessfulArtifactBuildWithoutReferenceIsRejected()
    {
        var request = CreateRequest() with { Target = BuildTarget.Artifact };
        using var httpClient = CreateArtifactWorkerHttpClient(request, new BuildResult(BuildOutcome.Succeeded, null, [], CreateBuildIdentity(), request.Workspace.Revision, request.Workspace.SelectionRevision));
        var client = new ToolchainWorkerClient(httpClient, new ToolchainWorkerClientSettings("roslyn-stable", "content", "worker-image"));

        var exception = await Assert.ThrowsAsync<ToolchainWorkerException>(() => client.BuildAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal("worker-protocol-invalid", exception.Error.Code);
        Assert.Contains("artifact reference", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProductionArtifactWithMismatchedBuildIdentityIsRejected()
    {
        var request = CreateRequest() with { Target = BuildTarget.Artifact };
        var artifactRef = new ArtifactRef($"sha256:{new string('a', 64)}");
        using var httpClient = CreateArtifactWorkerHttpClient(request, new BuildResult(
            BuildOutcome.Succeeded,
            artifactRef,
            [],
            CreateBuildIdentity() with { ReferenceSetId = "another-reference-set" },
            request.Workspace.Revision,
            request.Workspace.SelectionRevision));
        var client = new ToolchainWorkerClient(httpClient, new ToolchainWorkerClientSettings("roslyn-stable", "content", "worker-image"));

        var exception = await Assert.ThrowsAsync<ToolchainWorkerException>(() => client.BuildAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal("worker-protocol-invalid", exception.Error.Code);
        Assert.Contains("identity", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplainAcceptsHostedToolchainIdentityDistinctFromWorkerIdentity()
    {
        const string workerId = "roslyn-worker";
        var request = CreateExplainRequest();
        using var httpClient = new HttpClient(new DelegateHandler(message =>
        {
            if (message.RequestUri?.AbsolutePath == "/api/v1/worker/describe")
            {
                var descriptor = CreateDescriptor();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(
                        descriptor with
                        {
                            Service = descriptor.Service with { Id = workerId, Capabilities = ["compile-check", "explain"] },
                            Capabilities =
                            [
..descriptor.Capabilities,
                                new WorkerCapabilityDescriptor("explain", 1, true, ["roslyn-stable"])
                            ]
                        },
                        options: ContractJson.CreateSerializerOptions())
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ToolchainExplainResponse(request.RequestId, new ExplainResult(new ExplanationDocument("csharp", "roslyn-stable", request.Workspace.Revision, request.Workspace.SelectionRevision, [new ExplanationFile("Program.cs", [])], false), new BuildIdentity("content", "csharp", "roslyn-stable", "5.6.0", null, request.Workspace.ReferenceSetId, "worker-image"))), options: ContractJson.CreateSerializerOptions())
            };
        }))
        {
            BaseAddress = new Uri("http://worker.test", UriKind.Absolute)
        };
        var client = new ToolchainWorkerClient(httpClient, new ToolchainWorkerClientSettings(workerId, "content", "worker-image"));

        var response = await client.ExplainAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(request.RequestId, response.RequestId);
        Assert.Equal(request.Workspace.Revision, response.Result.Document.WorkspaceRevision);
    }

    private static WorkerDescriptor CreateDescriptor() => new(new ServiceIdentity("roslyn-stable", ServiceKind.ToolchainWorker, "content", ProtocolVersion.WorkerV1, ["compile-check"], "ready"), "instance", WorkerKind.Toolchain, "worker-image", ProtocolVersion.WorkerV1, [ProtocolVersion.WorkerV1], [new WorkerCapabilityDescriptor("compile-check", 1, true, ["roslyn-stable"])], ["roslyn-stable"], DateTimeOffset.UtcNow);

    private static ReferenceSetAttestation CreateReferenceSetAttestation(string digest) => new("net10-ref", "net10.0", digest, $"sha256:{new string('a', 64)}", new ReferenceSetProvenance("nuget-package", "10.0.9", "Microsoft.NETCore.App.Ref"));

    private static BuildRequest CreateRequest() => new("client-request", "client-key", "client-pipeline", "roslyn-stable", "net10-ref", new WorkspaceSnapshot(ContractSchemaVersions.WorkspaceSnapshot, 1, 1, "csharp", [new WorkspaceFile("Program.cs", 1, "System.Console.WriteLine(42);")], "Program.cs", ["Program.cs"], "net10-ref", new BuildOptions(BuildConfiguration.Release, true, BuildOutputKind.Console, false, true)), DateTimeOffset.UtcNow.AddMinutes(1), Target: BuildTarget.CompileCheck);

    private static HttpClient CreateArtifactWorkerHttpClient(BuildRequest request, BuildResult result) => new(new DelegateHandler(message =>
    {
        if (message.RequestUri?.AbsolutePath == "/api/v1/worker/describe")
        {
            var descriptor = CreateDescriptor();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                    descriptor with
                    {
                        Service = descriptor.Service with { Capabilities = ["managed-pe"] },
                        Capabilities =
                        [
                            new WorkerCapabilityDescriptor("managed-pe", 1, true, ["roslyn-stable"])
                        ]
                    },
                    options: ContractJson.CreateSerializerOptions())
            };
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new ToolchainBuildResponse(request.RequestId, result, null), options: ContractJson.CreateSerializerOptions())
        };
    }))
    {
        BaseAddress = new Uri("http://worker.test", UriKind.Absolute)
    };

    private static BuildIdentity CreateBuildIdentity() => new("content", "csharp", "roslyn-stable", "5.6.0", null, "net10-ref", "worker-image");

    private static ExplainRequest CreateExplainRequest()
    {
        var build = CreateRequest();
        return new ExplainRequest("client-explain-request", "client-explain-key", "client-explain-pipeline", build.Workspace, DateTimeOffset.UtcNow.AddMinutes(1));
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(handler(request));
        }
    }
}
