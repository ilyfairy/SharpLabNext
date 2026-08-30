using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Contracts;

namespace SharpLabNext.IntegrationTests;

[Collection<ArtifactStoreProcessTestGroup>]
public sealed class GatewayBuildPipelineTests
{
    [Fact]
    public async Task RealWorkerBuildCompileCheckAndAstFlowThroughGatewayAndCas()
    {
        const string internalServiceToken = "shared-internal-service-token-for-gateway-tests";
        var catalog = await GatewayTestCatalog.LoadRepositoryAsync(TestContext.Current.CancellationToken);
        await using var artifactStore = await ArtifactStoreProcess.StartAsync(TestContext.Current.CancellationToken, internalServiceToken: internalServiceToken);
        var workerEnvironment = new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["RoslynWorker__ReleaseId"] = catalog.ReleaseId,
            ["ArtifactStore__BaseUrl"] = artifactStore.HttpClient.BaseAddress!.AbsoluteUri,
            ["RoslynWorker__DevelopmentArtifactEnvelope__Enabled"] = "false"
        };
        GatewayTestCatalog.AddRoslynStableReferenceSets(workerEnvironment, catalog);
        await using var worker = await DotNetWebServiceProcess.StartAsync("src/Workers/Roslyn.Stable/SharpLabNext.Worker.Roslyn.Stable/SharpLabNext.Worker.Roslyn.Stable.csproj", "/health/ready", workerEnvironment, TestContext.Current.CancellationToken, internalServiceToken: internalServiceToken);
        await using var gateway = await DotNetWebServiceProcess.StartAsync(
            "src/Gateway/SharpLabNext.Gateway/SharpLabNext.Gateway.csproj",
            "/health/ready",
            new Dictionary<string, string?>
            {
                ["Services__RoslynStableWorker__BaseAddress"] = worker.HttpClient.BaseAddress!.AbsoluteUri,
                ["Services__LanguageWorkers__roslyn-stable__BaseAddress"] = worker.HttpClient.BaseAddress!.AbsoluteUri,
                ["Services__ArtifactStore__BaseAddress"] = artifactStore.HttpClient.BaseAddress!.AbsoluteUri,
                ["DependencyHealth__Enabled"] = "false"
            },
            TestContext.Current.CancellationToken,
            internalServiceToken: internalServiceToken);

        var workspace = CreateWorkspace();
        var artifactEvents = await ExecuteBuildAsync(gateway.HttpClient, workspace, "il", BuildTarget.Artifact, "artifact");
        var artifactProduced = Assert.Single(artifactEvents.Select(item => item.Payload).OfType<ArtifactProducedOperationEventPayload>());
        var artifactResult = Assert.IsType<BuildResult>(Assert.Single(artifactEvents.Select(item => item.Payload).OfType<TypedResultOperationEventPayload>()).Result);
        Assert.Equal(BuildOutcome.Succeeded, artifactResult.Outcome);
        Assert.Equal(artifactProduced.ArtifactRef, artifactResult.ArtifactRef);
        Assert.Equal("dotnet-managed-pe-v1", artifactProduced.ArtifactFormat);

        var descriptor = await artifactStore.Client.GetArtifactAsync(artifactProduced.ArtifactRef, TestContext.Current.CancellationToken);
        Assert.NotNull(descriptor);
        Assert.Equal(artifactProduced.ArtifactRef, ArtifactIdentity.Compute(descriptor.Manifest));
        Assert.Equal(2, descriptor.Entries.Count);
        var assembly = Assert.Single(descriptor.Entries, entry => entry.Role == "primary-assembly");
        await using (var content = await artifactStore.Client.OpenContentReadAsync(assembly.ContentRef, TestContext.Current.CancellationToken))
        {
            var header = new byte[2];
            await content.Content.ReadExactlyAsync(header, TestContext.Current.CancellationToken);
            Assert.Equal([0x4d, 0x5a], header);
        }

        var compileCheckEvents = await ExecuteBuildAsync(gateway.HttpClient, workspace, "compile-check", BuildTarget.CompileCheck, "compile-check");
        var compileCheck = Assert.IsType<CompilationCheckResult>(Assert.Single(compileCheckEvents.Select(item => item.Payload).OfType<TypedResultOperationEventPayload>()).Result);
        Assert.True(compileCheck.CompilationSucceeded);
        Assert.DoesNotContain(compileCheck.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var astEvents = await ExecuteBuildAsync(gateway.HttpClient, workspace, "ast", BuildTarget.Ast, "ast");
        var ast = Assert.IsType<AstResult>(Assert.Single(astEvents.Select(item => item.Payload).OfType<TypedResultOperationEventPayload>()).Result);
        Assert.Equal("Workspace", ast.Document.Root.Kind);
        Assert.Equal(workspace.Revision, ast.Document.WorkspaceRevision);
        Assert.NotEmpty(ast.Document.Root.Children);

        var explainEvents = await ExecuteExplainAsync(gateway.HttpClient, workspace);
        var explain = Assert.IsType<ExplainResult>(Assert.Single(explainEvents.Select(item => item.Payload).OfType<TypedResultOperationEventPayload>()).Result);
        Assert.Equal(workspace.Revision, explain.Document.WorkspaceRevision);
        Assert.Equal(workspace.SelectionRevision, explain.Document.SelectionRevision);
        Assert.Contains(explain.Document.Files.SelectMany(static file => file.Nodes), static node => node.Kind == "InvocationExpression");
    }

    private static async Task<IReadOnlyList<OperationEvent>> ExecuteExplainAsync(HttpClient client, WorkspaceSnapshot workspace)
    {
        var catalogRevision = await GatewayTestCatalog.GetRevisionAsync(client);
        using var resolveResponse = await client.PostAsJsonAsync("/api/v1/selections/resolve", new ResolveSelectionRequest("csharp", "roslyn-stable", "net10-ref", "explain", null, BuildConfiguration.Release, catalogRevision, workspace.Revision), ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        resolveResponse.EnsureSuccessStatusCode();
        var resolution = await resolveResponse.Content.ReadFromJsonAsync<ResolveSelectionResponse>(ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken) ?? throw new InvalidOperationException("Selection response was empty.");
        using var startResponse = await client.PostAsJsonAsync("/api/v1/explanations", new ExplainRequest("real-explain-request", "real-explain-key", resolution.PipelineResolutionId, workspace, DateTimeOffset.UtcNow.AddSeconds(30)), ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, startResponse.StatusCode);
        var handle = await startResponse.Content.ReadFromJsonAsync<OperationHandle>(ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken) ?? throw new InvalidOperationException("Operation handle was empty.");
        var state = await WaitForTerminalAsync(client, handle.OperationId);
        Assert.True(state.Status == OperationStatus.Completed, $"Explain failed: {state.Error?.Code}: {state.Error?.PublicMessage}");
        return await GetEventsAsync(client, handle.OperationId);
    }

    private static async Task<IReadOnlyList<OperationEvent>> ExecuteBuildAsync(HttpClient client, WorkspaceSnapshot workspace, string outputId, BuildTarget target, string suffix)
    {
        var catalogRevision = await GatewayTestCatalog.GetRevisionAsync(client);
        var resolveRequest = new ResolveSelectionRequest("csharp", "roslyn-stable", "net10-ref", outputId, null, BuildConfiguration.Release, catalogRevision, workspace.Revision);
        using var resolveResponse = await client.PostAsJsonAsync("/api/v1/selections/resolve", resolveRequest, ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        resolveResponse.EnsureSuccessStatusCode();
        var resolution = await resolveResponse.Content.ReadFromJsonAsync<ResolveSelectionResponse>(ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken) ?? throw new InvalidOperationException("Selection response was empty.");
        var request = new BuildRequest($"real-{suffix}-request", $"real-{suffix}-key", resolution.PipelineResolutionId, "roslyn-stable", "net10-ref", workspace, DateTimeOffset.UtcNow.AddSeconds(30), Target: target);
        using var startResponse = await client.PostAsJsonAsync("/api/v1/builds", request, ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, startResponse.StatusCode);
        var handle = await startResponse.Content.ReadFromJsonAsync<OperationHandle>(ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken) ?? throw new InvalidOperationException("Operation handle was empty.");
        var state = await WaitForTerminalAsync(client, handle.OperationId);
        Assert.True(state.Status == OperationStatus.Completed, $"Build failed: {state.Error?.Code}: {state.Error?.PublicMessage}");

        return await GetEventsAsync(client, handle.OperationId);
    }

    private static async Task<IReadOnlyList<OperationEvent>> GetEventsAsync(HttpClient client, string operationId)
    {
        using var eventsResponse = await client.GetAsync($"/api/v1/operations/{operationId}/events?FromSequence=0", HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        eventsResponse.EnsureSuccessStatusCode();
        var body = await eventsResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var events = body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
            .Select(line => JsonSerializer.Deserialize<OperationEvent>(line.AsSpan("data: ".Length), ContractJson.CreateSerializerOptions()) ?? throw new InvalidOperationException("Operation event was empty.")).ToArray();
        OperationEventStreamContract.Validate(events);
        Assert.IsType<CompletedOperationEventPayload>(events[^1].Payload);
        return events;
    }

    private static async Task<OperationState> WaitForTerminalAsync(HttpClient client, string operationId)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var state = await client.GetFromJsonAsync<OperationState>($"/api/v1/operations/{operationId}", ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken) ?? throw new InvalidOperationException("Operation state was empty.");
            if (state.Status is OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled)
                return state;

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("Gateway build operation did not complete.");
    }

    private static WorkspaceSnapshot CreateWorkspace()
    {
        var options = new BuildOptions(BuildConfiguration.Release, Optimize: true, BuildOutputKind.Console, AllowUnsafe: false, EmitPortablePdb: true, NullableContextMode.Enable, LanguageVersion: "14.0");
        return new WorkspaceSnapshot(
            ContractSchemaVersions.WorkspaceSnapshot,
            Revision: 42,
            SelectionRevision: 7,
            LanguageId: "csharp",
            Files:
            [
                new WorkspaceFile("Program.cs", 1, "System.Console.WriteLine(Helper.Value);"),
                new WorkspaceFile("Helper.cs", 1, "internal static class Helper { public static int Value => 42; }")
            ],
            ActiveFile: "Program.cs",
            SourceOrder: ["Program.cs", "Helper.cs"],
            ReferenceSetId: "net10-ref",
            BuildOptions: options);
    }

}

internal sealed class DotNetWebServiceProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _standardOutput;
    private readonly Task<string> _standardError;
    private readonly string? _internalServiceTokenFile;

    private DotNetWebServiceProcess(Process process, Task<string> standardOutput, Task<string> standardError, HttpClient httpClient, string? internalServiceTokenFile)
    {
        _process = process;
        _standardOutput = standardOutput;
        _standardError = standardError;
        _internalServiceTokenFile = internalServiceTokenFile;
        HttpClient = httpClient;
    }

    public HttpClient HttpClient { get; }

    public static async Task<DotNetWebServiceProcess> StartAsync(string projectPath, string readinessPath, IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken, string? configuration = "Release", bool noBuild = true, string? internalServiceToken = null)
    {
        var repositoryRoot = FindRepositoryRoot();
        var port = ReserveTcpPort();
        var address = new Uri($"http://127.0.0.1:{port}", UriKind.Absolute);
        var startInfo = new ProcessStartInfo { FileName = "dotnet", WorkingDirectory = repositoryRoot, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(projectPath);
        if (noBuild)
            startInfo.ArgumentList.Add("--no-build");
        if (!string.IsNullOrWhiteSpace(configuration))
        {
            startInfo.ArgumentList.Add("--configuration");
            startInfo.ArgumentList.Add(configuration);
        }
        startInfo.ArgumentList.Add("--no-launch-profile");
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add(address.AbsoluteUri);
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        foreach (var (key, value) in environment)
            startInfo.Environment[key] = value;

        string? internalServiceTokenFile = null;
        if (internalServiceToken is not null)
        {
            internalServiceTokenFile = Path.GetTempFileName();
            await File.WriteAllTextAsync(internalServiceTokenFile, internalServiceToken + Environment.NewLine, cancellationToken);
            startInfo.Environment["InternalServiceAuth__Required"] = "true";
            startInfo.Environment["InternalServiceAuth__TokenFile"] = internalServiceTokenFile;
        }

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start service project '{projectPath}'.");
        }
        catch
        {
            if (internalServiceTokenFile is not null)
                File.Delete(internalServiceTokenFile);
            throw;
        }
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        var client = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(30) };
        if (internalServiceToken is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", internalServiceToken);
        var service = new DotNetWebServiceProcess(process, output, error, client, internalServiceTokenFile);
        try
        {
            for (var attempt = 0; attempt < 300; attempt++)
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException($"Service '{projectPath}' exited during startup.{Environment.NewLine}stdout:{Environment.NewLine}{await output}{Environment.NewLine}stderr:{Environment.NewLine}{await error}");
                }

                try
                {
                    using var response = await client.GetAsync(readinessPath, cancellationToken);
                    if (response.StatusCode == HttpStatusCode.OK)
                        return service;
                }
                catch (HttpRequestException) { }

                await Task.Delay(50, cancellationToken);
            }

            throw new TimeoutException($"Service '{projectPath}' did not become ready.");
        }
        catch
        {
            await service.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        HttpClient.Dispose();
        if (!_process.HasExited)
            _process.Kill(entireProcessTree: true);

        try
        {
            await _process.WaitForExitAsync();
            _ = await _standardOutput;
            _ = await _standardError;
        }
        finally
        {
            _process.Dispose();
            if (_internalServiceTokenFile is not null)
                File.Delete(_internalServiceTokenFile);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SharpLabNext.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the SharpLabNext repository root.");
    }

    private static int ReserveTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
