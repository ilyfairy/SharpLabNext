#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property EnableTrimAnalyzer=false
#:property EnableAotAnalyzer=false

using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

const string RuntimeFirstFrameMarker = "SLN-RUNTIME-FIRST-FRAME-V1\n";

var baseAddress = args.Length == 0
    ? new Uri("http://127.0.0.1:8080", UriKind.Absolute) : new Uri(args[0], UriKind.Absolute);
var runtimeImage = Environment.GetEnvironmentVariable("SHARPLABNEXT_E2E_RUNTIME_IMAGE");
using var overallTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
using var http = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(90) };
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    PropertyNamingPolicy = PascalCaseJsonNamingPolicy.Instance,
    DictionaryKeyPolicy = null,
    TypeInfoResolver = new DefaultJsonTypeInfoResolver()
};
var failures = new List<string>();
var passed = 0;
var observedContainerIds = new HashSet<string>(StringComparer.Ordinal);

await EnsureSuccessAsync(await http.GetAsync("/health/ready", overallTimeout.Token), "Gateway readiness");
using var catalogResponse = await http.GetAsync("/api/v1/catalog", overallTimeout.Token);
await EnsureSuccessAsync(catalogResponse, "Catalog");
using var catalog = JsonDocument.Parse(await catalogResponse.Content.ReadAsByteArrayAsync(overallTimeout.Token));
var catalogRevision = catalog.RootElement.GetProperty("Revision").GetString() ?? throw new InvalidOperationException("Catalog revision is missing.");
var catalogReleaseId = catalog.RootElement.GetProperty("ReleaseId").GetString() ?? throw new InvalidOperationException("Catalog release ID is missing.");
if (string.IsNullOrWhiteSpace(runtimeImage))
    runtimeImage = await ResolveRuntimeImageAsync(catalogReleaseId);

var resolution = await ResolveRunPipelineAsync();
var artifactRef = await BuildFailureHarnessAsync(resolution);

await CheckAsync("runtime process crash", () => VerifyRunFailureAsync("crash", "process-crash"));
await CheckAsync("runtime output limit", () => VerifyRunFailureAsync("output", "output-limit-exceeded"));
await CheckAsync("runtime out of memory", () => VerifyRunFailureAsync("oom", "out-of-memory"));
await CheckAsync("runtime timeout", () => VerifyRunFailureAsync("timeout", "timeout"));
await CheckAsync("runtime cancellation", VerifyCancellationAsync);
await CheckAsync("runtime reaper", VerifyReaperAsync);

Console.WriteLine();
Console.WriteLine($"Runtime failure smoke: {passed} passed, {failures.Count} failed.");
if (failures.Count > 0)
{
    foreach (var failure in failures)
        Console.Error.WriteLine($"- {failure}");
    Environment.ExitCode = 1;
}

async Task CheckAsync(string name, Func<Task> action)
{
    try
    {
        await action();
        passed++;
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{name}: {exception.Message}");
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

async Task VerifyRunFailureAsync(string mode, string expectedStatus)
{
    var baseline = await GetManagedResourcesAsync();
    var operationId = await StartRunAsync(mode);
    var terminal = await WaitForTerminalAsync(operationId);
    Require(terminal.State.GetProperty("Status").GetString() == "completed", $"Run operation ended as {terminal.State.GetProperty("Status").GetString()}.");
    var result = SingleTypedResult(terminal.Events);
    Require(result.GetProperty("ResultType").GetString() == "run", "Runtime returned the wrong result type.");
    Require(result.GetProperty("Status").GetString() == expectedStatus, $"Expected '{expectedStatus}', received '{result.GetProperty("Status").GetString()}'.");
    if (expectedStatus == "output-limit-exceeded")
    {
        Require(result.GetProperty("OutputTruncated").GetBoolean(), "Output-limit result was not marked truncated.");
        Require(DecodeOutput(terminal.Events, "stdout").StartsWith(RuntimeFirstFrameMarker, StringComparison.Ordinal), "Output-limit operation lost the first pre-start attach frame.");
        Require(terminal.Events.Any(static operationEvent => operationEvent.GetProperty("Payload").GetProperty("Kind").GetString() == "output-truncated"), "Output-limit operation emitted no output-truncated event.");
    }

    RecordCreatedContainer(terminal.Events);
    await WaitForResourceBaselineAsync(baseline);
}

async Task VerifyCancellationAsync()
{
    var baseline = await GetManagedResourcesAsync();
    var operationId = await StartRunAsync("cancel");
    var containerId = await WaitForFreshRunContainerAsync(baseline);
    await VerifyRuntimeContainerLabelsAsync(containerId);

    using var cancelResponse = await http.PostAsJsonAsync($"/api/v1/operations/{operationId}/cancel", new { operationId, reason = "runtime-failure-smoke" }, json, overallTimeout.Token);
    await EnsureSuccessAsync(cancelResponse, "Cancel runtime operation");

    var terminal = await WaitForTerminalAsync(operationId);
    Require(terminal.State.GetProperty("Status").GetString() == "cancelled", $"Cancelled run ended as {terminal.State.GetProperty("Status").GetString()}.");
    var result = SingleTypedResult(terminal.Events);
    Require(result.GetProperty("Status").GetString() == "cancelled", "Cancelled run lost its result identity.");
    RecordCreatedContainer(terminal.Events);
    await WaitForResourceBaselineAsync(baseline);
}

async Task VerifyReaperAsync()
{
    var suffix = Guid.NewGuid().ToString("N");
    var containerName = $"sln-e2e-stale-{suffix}";
    var volumeName = $"sln-e2e-stale-{suffix}";
    const string stale = "2000-01-01T00:00:00.0000000+00:00";
    try
    {
        var volume = await RunDockerAsync("volume", "create", "--label", "com.sharplabnext.runtime-job=workspace", "--label", $"com.sharplabnext.job-id={suffix}", "--label", $"com.sharplabnext.operation-id={suffix}", "--label", "com.sharplabnext.release-id=e2e", "--label", $"com.sharplabnext.created-at={stale}", volumeName);
        Require(volume.ExitCode == 0, $"Could not create stale volume: {volume.StandardError}");

        var container = await RunDockerAsync(
            "run", "--detach",
            "--name", containerName,
            "--label", "com.sharplabnext.runtime-job=true",
            "--label", $"com.sharplabnext.job-id={suffix}",
            "--label", $"com.sharplabnext.operation-id={suffix}",
            "--label", "com.sharplabnext.release-id=e2e",
            "--label", $"com.sharplabnext.created-at={stale}",
            "--network", "none",
            "--read-only",
            "--user", "1654:1654",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges:true",
            "--entrypoint", "/bin/sleep",
            runtimeImage,
            "infinity");
        Require(container.ExitCode == 0, $"Could not create stale container: {container.StandardError}");

        await WaitForDockerResourceRemovalAsync("container", containerName, TimeSpan.FromSeconds(45));
        await WaitForDockerResourceRemovalAsync("volume", volumeName, TimeSpan.FromSeconds(45));
    }
    finally
    {
        _ = await RunDockerAsync("container", "rm", "--force", containerName);
        _ = await RunDockerAsync("volume", "rm", "--force", volumeName);
    }
}

async Task<string> ResolveRuntimeImageAsync(string releaseId)
{
    var images = await RunDockerAsync("image", "ls", "--no-trunc", "--filter", "label=com.sharplabnext.runtime-profile=dotnet-10-linux-x64", "--filter", $"label=org.opencontainers.image.version={releaseId}", "--format", "{{.ID}}");
    Require(images.ExitCode == 0, $"Could not list deployed runtime images: {images.StandardError}");
    var imageIds = ParseLines(images.StandardOutput);
    Require(imageIds.Count == 1, $"Expected one .NET 10 runtime image for release '{releaseId}', found {imageIds.Count}.");
    return imageIds.Single();
}

async Task<JsonElement> ResolveRunPipelineAsync()
{
    using var response = await http.PostAsJsonAsync("/api/v1/selections/resolve", new { languageId = "csharp", toolchainId = "roslyn-stable", referenceSetId = "net10-ref", outputId = "run", runtimeId = "dotnet-10-linux-x64", buildMode = "release", catalogRevision, workspaceRevision = 1 }, json, overallTimeout.Token);
    await EnsureSuccessAsync(response, "Resolve runtime failure pipeline");
    using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(overallTimeout.Token));
    return document.RootElement.Clone();
}

async Task<string> BuildFailureHarnessAsync(JsonElement resolved)
{
    var effective = resolved.GetProperty("EffectiveSelection");
    var referenceSetId = effective.GetProperty("ReferenceSetId").GetString() ?? throw new InvalidOperationException("Resolved reference set is missing.");
    var buildOptions = new { configuration = "release", optimize = true, outputKind = "console", allowUnsafe = false, emitPortablePdb = true, nullableContext = "enable", languageVersion = "14.0", preprocessorSymbols = Array.Empty<string>(), checkOverflow = false };
    var source = """
        using System;
        using System.Collections.Generic;
        using System.Runtime.InteropServices;
        using System.Threading;

        public static class Program
        {
            public static void Main(string[] args)
            {
                switch (args.Length == 0 ? "" : args[0])
                {
                    case "crash":
                        Environment.FailFast("intentional runtime smoke crash");
                        return;
                    case "output":
                        Console.Out.Write("SLN-RUNTIME-FIRST-FRAME-V1\n");
                        Console.Out.Flush();
                        var outputBlock = new string('x', 64 * 1024);
                        for (var block = 0; block < 128; block++)
                            Console.Out.Write(outputBlock);
                        return;
                    case "oom":
                        ExhaustNativeMemory();
                        return;
                    case "timeout":
                    case "cancel":
                        Thread.Sleep(Timeout.InfiniteTimeSpan);
                        return;
                    default:
                        throw new ArgumentException("Unknown test mode.");
                }
            }

            private static void ExhaustNativeMemory()
            {
                const int blockSize = 32 * 1024 * 1024;
                var allocations = new List<IntPtr>();
                while (true)
                {
                    var block = Marshal.AllocHGlobal(blockSize);
                    allocations.Add(block);
                    for (var offset = 0; offset < blockSize; offset += 4096)
                        Marshal.WriteByte(block, offset, 0x7f);
                }
            }
        }
        """;
    var workspace = new { schemaVersion = 1, revision = 1, selectionRevision = 1, languageId = "csharp", files = new[] { new { path = "Program.cs", version = 1, text = source } }, activeFile = "Program.cs", sourceOrder = new[] { "Program.cs" }, referenceSetId, buildOptions };
    var identity = Identity("build");
    var operationId = await StartOperationAsync("/api/v1/builds", new { identity.requestId, identity.idempotencyKey, pipelineResolutionId = resolved.GetProperty("PipelineResolutionId").GetString(), toolchainId = effective.GetProperty("ToolchainId").GetString(), referenceSetId, workspace, deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(60), options = buildOptions, target = "artifact" });
    var terminal = await WaitForTerminalAsync(operationId);
    Require(terminal.State.GetProperty("Status").GetString() == "completed", $"Failure harness build ended as {terminal.State.GetProperty("Status").GetString()}.");
    var result = SingleTypedResult(terminal.Events);
    Require(result.GetProperty("Outcome").GetString() == "succeeded", "Failure harness did not compile.");
    return result.GetProperty("ArtifactRef").GetString() ?? throw new InvalidOperationException("Failure harness returned no artifact reference.");
}

async Task<string> StartRunAsync(string mode)
{
    var identity = Identity("run");
    return await StartOperationAsync("/api/v1/runs", new { identity.requestId, identity.idempotencyKey, pipelineResolutionId = resolution.GetProperty("PipelineResolutionId").GetString(), artifactRef, runtimeProfileId = "dotnet-10-linux-x64", options = new { arguments = new[] { mode }, stdin = (string?)null, instrumentation = "none", securityPolicyId = resolution.GetProperty("PipelinePlan").GetProperty("SecurityPolicyId").GetString() }, deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(45) });
}

async Task<string> StartOperationAsync(string path, object request)
{
    using var response = await http.PostAsJsonAsync(path, request, json, overallTimeout.Token);
    await EnsureSuccessAsync(response, path);
    using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(overallTimeout.Token));
    return document.RootElement.GetProperty("OperationId").GetString() ?? throw new InvalidOperationException($"{path} returned no operation ID.");
}

async Task<TerminalOperation> WaitForTerminalAsync(string operationId)
{
    JsonElement state = default;
    for (var attempt = 0; attempt < 900; attempt++)
    {
        using var response = await http.GetAsync($"/api/v1/operations/{operationId}", overallTimeout.Token);
        await EnsureSuccessAsync(response, $"Operation {operationId}");
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(overallTimeout.Token));
        state = document.RootElement.Clone();
        if (state.GetProperty("Status").GetString() is "completed" or "failed" or "cancelled") break;
        await Task.Delay(100, overallTimeout.Token);
    }

    Require(state.ValueKind == JsonValueKind.Object, $"Operation {operationId} never returned state.");
    Require(state.GetProperty("Status").GetString() is "completed" or "failed" or "cancelled", $"Operation {operationId} did not become terminal.");
    using var eventResponse = await http.GetAsync($"/api/v1/operations/{operationId}/events?FromSequence=0", overallTimeout.Token);
    await EnsureSuccessAsync(eventResponse, $"Events for {operationId}");
    var body = await eventResponse.Content.ReadAsStringAsync(overallTimeout.Token);
    var events = body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(static line => line.StartsWith("data: ", StringComparison.Ordinal)).Select(static line =>
        {
            using var document = JsonDocument.Parse(line["data: ".Length..]);
            return document.RootElement.Clone();
        }).ToArray();
    Require(events.Length > 0, $"Operation {operationId} returned no events.");
    return new TerminalOperation(state, events);
}

JsonElement SingleTypedResult(IReadOnlyList<JsonElement> events)
{
    var results = events.Where(static operationEvent => operationEvent.GetProperty("Payload").GetProperty("Kind").GetString() == "typed-result").Select(static operationEvent => operationEvent.GetProperty("Payload").GetProperty("Result")).ToArray();
    Require(results.Length == 1, $"Operation returned {results.Length} typed results.");
    return results[0];
}

string DecodeOutput(IReadOnlyList<JsonElement> events, string channel)
{
    var output = new StringBuilder();
    foreach (var operationEvent in events)
    {
        var payload = operationEvent.GetProperty("Payload");
        if (payload.GetProperty("Kind").GetString() != "output-chunk")
            continue;
        var chunk = payload.GetProperty("Chunk");
        if (chunk.GetProperty("Channel").GetString() != channel)
            continue;
        var data = chunk.GetProperty("Data").GetString() ?? string.Empty;
        output.Append(Encoding.UTF8.GetString(Convert.FromBase64String(data)));
    }
    return output.ToString();
}

void RecordCreatedContainer(IReadOnlyList<JsonElement> events)
{
    var messages = events.Select(static operationEvent => operationEvent.GetProperty("Payload")).Where(static payload => payload.GetProperty("Kind").GetString() == "progress").Select(static payload => payload.GetProperty("Message").GetString()).Where(static message => message?.StartsWith("Created isolated container ", StringComparison.Ordinal) == true).ToArray();
    Require(messages.Length == 1, $"Runtime operation reported {messages.Length} created containers.");
    var containerId = messages[0]!["Created isolated container ".Length..].TrimEnd('.');
    Require(observedContainerIds.Add(containerId), "A one-shot runtime container ID was reused.");
}

async Task VerifyRuntimeContainerLabelsAsync(string containerId)
{
    var inspect = await RunDockerAsync("container", "inspect", containerId);
    Require(inspect.ExitCode == 0, $"Could not inspect runtime container: {inspect.StandardError}");
    using var document = JsonDocument.Parse(inspect.StandardOutput);
    var labels = document.RootElement[0].GetProperty("Config").GetProperty("Labels");
    var jobId = labels.GetProperty("com.sharplabnext.job-id").GetString();
    Require(!string.IsNullOrWhiteSpace(jobId), "Runtime container omitted job-id.");
    Require(labels.GetProperty("com.sharplabnext.operation-id").GetString() == jobId, "Runtime container operation-id did not match job-id.");
    Require(!string.IsNullOrWhiteSpace(labels.GetProperty("com.sharplabnext.release-id").GetString()), "Runtime container omitted release-id.");
    Require(DateTimeOffset.TryParse(labels.GetProperty("com.sharplabnext.created-at").GetString(), out _), "Runtime container omitted a valid created-at timestamp.");
}

async Task<ResourceSnapshot> GetManagedResourcesAsync()
{
    var containers = await RunDockerAsync("container", "ls", "--all", "--filter", "label=com.sharplabnext.runtime-job=true", "--format", "{{.ID}}");
    Require(containers.ExitCode == 0, $"Could not list runtime containers: {containers.StandardError}");
    var volumes = await RunDockerAsync("volume", "ls", "--filter", "label=com.sharplabnext.runtime-job=workspace", "--format", "{{.Name}}");
    Require(volumes.ExitCode == 0, $"Could not list runtime volumes: {volumes.StandardError}");
    return new ResourceSnapshot(ParseLines(containers.StandardOutput), ParseLines(volumes.StandardOutput));
}

async Task<string> WaitForFreshRunContainerAsync(ResourceSnapshot baseline)
{
    for (var attempt = 0; attempt < 150; attempt++)
    {
        var list = await RunDockerAsync("container", "ls", "--filter", "label=com.sharplabnext.runtime-job=true", "--format", "{{.ID}} {{.Names}}");
        Require(list.ExitCode == 0, $"Could not observe runtime container: {list.StandardError}");
        foreach (var line in list.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && !baseline.Containers.Contains(parts[0]) && parts[1].StartsWith("sln-run-", StringComparison.Ordinal))
            {
                return parts[0];
            }
        }

        await Task.Delay(100, overallTimeout.Token);
    }

    throw new InvalidOperationException("The cancellable one-shot container never became observable.");
}

async Task WaitForResourceBaselineAsync(ResourceSnapshot baseline)
{
    for (var attempt = 0; attempt < 150; attempt++)
    {
        var current = await GetManagedResourcesAsync();
        if (current.Containers.All(baseline.Containers.Contains) && current.Volumes.All(baseline.Volumes.Contains))
        {
            return;
        }

        await Task.Delay(100, overallTimeout.Token);
    }

    var remaining = await GetManagedResourcesAsync();
    throw new InvalidOperationException($"Runtime cleanup left containers [{string.Join(", ", remaining.Containers.Except(baseline.Containers))}] " + $"and volumes [{string.Join(", ", remaining.Volumes.Except(baseline.Volumes))}].");
}

async Task WaitForDockerResourceRemovalAsync(string kind, string name, TimeSpan timeout)
{
    var stopwatch = Stopwatch.StartNew();
    while (stopwatch.Elapsed < timeout)
    {
        var inspect = await RunDockerAsync(kind, "inspect", name);
        if (inspect.ExitCode != 0)
            return;
        await Task.Delay(250, overallTimeout.Token);
    }

    throw new InvalidOperationException($"The reaper did not remove Docker {kind} '{name}'.");
}

async Task<DockerResult> RunDockerAsync(params string[] arguments)
{
    var startInfo = new ProcessStartInfo("docker")
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    foreach (var argument in arguments)
        startInfo.ArgumentList.Add(argument);
    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Docker CLI could not be started.");
    var stdout = process.StandardOutput.ReadToEndAsync(overallTimeout.Token);
    var stderr = process.StandardError.ReadToEndAsync(overallTimeout.Token);
    try
    {
        await process.WaitForExitAsync(overallTimeout.Token);
    }
    catch
    {
        if (!process.HasExited)
            process.Kill(entireProcessTree: true);
        throw;
    }

    return new DockerResult(process.ExitCode, await stdout, await stderr);
}

static HashSet<string> ParseLines(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);

static (string requestId, string idempotencyKey) Identity(string kind) { var requestId = $"req_{Guid.NewGuid():N}"; return (requestId, $"{kind}:{requestId}"); }

static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
{
    if (response.IsSuccessStatusCode)
        return;
    var body = await response.Content.ReadAsStringAsync();
    throw new InvalidOperationException($"{operation} failed with {(int)response.StatusCode}: {body}");
}

static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

sealed record TerminalOperation(JsonElement State, IReadOnlyList<JsonElement> Events);

sealed record ResourceSnapshot(HashSet<string> Containers, HashSet<string> Volumes);

sealed record DockerResult(int ExitCode, string StandardOutput, string StandardError);

sealed class PascalCaseJsonNamingPolicy : JsonNamingPolicy
{
    public static readonly PascalCaseJsonNamingPolicy Instance = new();

    public override string ConvertName(string name) =>
        name.Length == 0 || char.IsUpper(name[0])
            ? name : char.ToUpperInvariant(name[0]) + name[1..];
}
