using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Formats.Tar;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace SharpLabNext.RuntimeSupervisor;

public sealed record RuntimeContainerSpec(
    string Name,
    string JobId,
    string ReleaseId,
    string Image,
    IReadOnlyList<string> Command,
    IReadOnlyDictionary<string, string> Environment,
    RuntimeSecurityPolicyOptions SecurityPolicy,
    string ManagementLabel,
    string ResourceScope,
    string WorkspaceVolumeName,
    string? TraceParent = null,
    RuntimeContainerIsolationKind IsolationKind = RuntimeContainerIsolationKind.Standard,
    string? WinePrefixPath = null,
    IReadOnlyList<string>? Entrypoint = null);

public enum RuntimeContainerIsolationKind
{
    Standard,
    WineRoot,
    WineNonRoot
}

public sealed record RuntimeContainerExit(long StatusCode, bool OomKilled, string? Error);

public sealed record ManagedRuntimeContainer(string Id, DateTimeOffset CreatedAtUtc, string State);

public sealed record ManagedWorkspaceVolume(string Name, DateTimeOffset CreatedAtUtc);

public sealed record RuntimeWorkspaceMaterialization(string VolumeName, string MaterializerContainerId, string? MeasurementVolumeName = null);

public sealed record RuntimeRunningContainerInspection(string ContainerId, int HostPid, bool Running);

public sealed record RuntimeMeasurementSidecarSpec(
    string JobId,
    string ReleaseId,
    string Image,
    string TargetContainerId,
    int TargetHostPid,
    string Token,
    string MeasurementVolumeName,
    string ManagementLabel,
    string ResourceScope,
    string? TraceParent = null);

public sealed record RuntimeExecSpec(IReadOnlyList<string> Command, string User, string WorkingDirectory, IReadOnlyDictionary<string, string>? Environment = null);

public sealed record RuntimeContainerExecInspection(string ExecId, bool Running, int? ExitCode);

public enum RuntimeMeasurementSignalKind
{
    Capture,
    Finish
}

public sealed record RuntimeContainerResourceUsage(long PeakMemoryBytes, int SampleCount, long CompletionPeakMemoryBytes = 0, int PostCompletionSampleCount = 0);

public sealed record RuntimeContainerMeasurement(string CgroupKind, long PeakMemoryBytes);

public interface IRuntimeContainerResourceMonitor : IAsyncDisposable
{
    int SampleCount { get; }

    Task WaitForSampleAfterAsync(int checkpoint, CancellationToken cancellationToken = default);

    Task WaitForFirstSampleAsync(CancellationToken cancellationToken = default) =>
        WaitForSampleAfterAsync(0, cancellationToken);

    Task<RuntimeContainerResourceUsage> StopAsync(CancellationToken cancellationToken = default);
}

public sealed record RuntimeImageInspection(
    string ImmutableReference,
    string ImageId,
    long SizeBytes,
    string OperatingSystem,
    string Architecture,
    IReadOnlyList<string> RepoDigests,
    IReadOnlyDictionary<string, string>? Labels = null,
    IReadOnlyList<string>? Entrypoint = null);

public sealed record RuntimeImageFileRequest(string Role, string Path);

public sealed record RuntimeImageFileInspection(string Role, string Path, string Sha256, long SizeBytes, string Format, string Architecture);

public interface IDockerEngineClient
{
    Task<bool> PingAsync(CancellationToken cancellationToken = default);

    Task<RuntimeImageInspection> InspectImageAsync(string immutableReference, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RuntimeImageFileInspection>> InspectImageFilesAsync(string imageId, IReadOnlyList<RuntimeImageFileRequest> files, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This Docker client does not support image-file inspection.");

    Task<string> CreateContainerAsync(RuntimeContainerSpec spec, CancellationToken cancellationToken = default);

    Task<RuntimeWorkspaceMaterialization> MaterializeWorkspaceAsync(string jobId, string releaseId, string image, Stream archive, RuntimeSecurityPolicyOptions securityPolicy, RuntimeContainerIsolationKind isolationKind, string managementLabel, string resourceScope, bool createMeasurementControl = false, CancellationToken cancellationToken = default);

    Task UploadArchiveAsync(string containerId, Stream archive, string destinationPath = "/workspace", CancellationToken cancellationToken = default);

    Task<RuntimeRunningContainerInspection> InspectRunningContainerAsync(string containerId, CancellationToken cancellationToken = default);

    Task<string> CreateRuntimeMeasurementSidecarAsync(RuntimeMeasurementSidecarSpec spec, CancellationToken cancellationToken = default);

    Task<string> CreateContainerExecAsync(string containerId, RuntimeExecSpec spec, CancellationToken cancellationToken = default);

    Task<Stream> StartContainerExecAsync(string execId, CancellationToken cancellationToken = default);

    Task<RuntimeContainerExecInspection> InspectContainerExecAsync(string execId, CancellationToken cancellationToken = default);

    Task WaitForRuntimeMeasurementArmedAsync(string sidecarContainerId, string token, string targetContainerId, CancellationToken cancellationToken = default);

    Task<RuntimeContainerMeasurement> WaitForRuntimeMeasurementAsync(string sidecarContainerId, string token, string targetContainerId, CancellationToken cancellationToken = default);

    Task UploadRuntimeMeasurementSignalAsync(string sidecarContainerId, string token, string targetContainerId, RuntimeMeasurementSignalKind signalKind, CancellationToken cancellationToken = default);

    Task<Stream> AttachContainerOutputAsync(string containerId, CancellationToken cancellationToken = default);

    Task StartContainerAsync(string containerId, CancellationToken cancellationToken = default);

    Task<IRuntimeContainerResourceMonitor> StartContainerResourceMonitorAsync(string containerId, CancellationToken cancellationToken = default);

    Task StopContainerAsync(string containerId, TimeSpan timeout, CancellationToken cancellationToken = default);

    Task<RuntimeContainerExit> WaitContainerAsync(string containerId, CancellationToken cancellationToken = default);

    Task KillContainerAsync(string containerId, CancellationToken cancellationToken = default);

    Task RemoveContainerAsync(string containerId, CancellationToken cancellationToken = default);

    Task RemoveWorkspaceVolumeAsync(string volumeName, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ManagedRuntimeContainer>> ListManagedContainersAsync(string managementLabel, string resourceScope, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ManagedWorkspaceVolume>> ListManagedWorkspaceVolumesAsync(string managementLabel, string resourceScope, CancellationToken cancellationToken = default);
}

public sealed class DockerEngineClient : IDockerEngineClient, IDisposable
{
    private enum RuntimeMeasurementRecordKind
    {
        Armed,
        Completion
    }

    private static readonly SearchValues<char> LowercaseHexCharacters =
        SearchValues.Create("0123456789abcdef");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly RuntimeSupervisorOptions _options;
    private readonly RuntimeSandboxPolicy _sandbox;
    private readonly HttpClient _client;

    public DockerEngineClient(IOptions<RuntimeSupervisorOptions> options, RuntimeSandboxPolicy sandbox) : this(options.Value, sandbox, CreateDockerHandler(options.Value.DockerSocketPath)) { }

    internal DockerEngineClient(RuntimeSupervisorOptions options, RuntimeSandboxPolicy sandbox, HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(handler);
        _options = options;
        _sandbox = sandbox;
        _client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://docker", UriKind.Absolute),
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _client.GetAsync("/_ping", cancellationToken);
            return response.IsSuccessStatusCode &&
                   string.Equals((await response.Content.ReadAsStringAsync(cancellationToken)).Trim(), "OK", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or SocketException)
        {
            return false;
        }
    }

    public async Task<RuntimeImageInspection> InspectImageAsync(string immutableReference, CancellationToken cancellationToken = default)
    {
        ValidateImmutableImageReference(immutableReference);
        using var response = await _client.GetAsync(Api($"/images/{Uri.EscapeDataString(immutableReference)}/json"), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var inspection = await response.Content.ReadFromJsonAsync<InspectImageResponse>(JsonOptions, cancellationToken) ?? throw new DockerEngineException("Docker returned an empty image-inspect response.");
        if (!IsSha256Digest(inspection.Id) ||
            inspection.Size <= 0 ||
            string.IsNullOrWhiteSpace(inspection.OperatingSystem) ||
            string.IsNullOrWhiteSpace(inspection.Architecture))
            throw new DockerEngineException("Docker returned an invalid image-inspect response.");

        var repoDigests = inspection.RepoDigests?.Where(static value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal).ToArray() ?? [];
        if (!repoDigests.Contains(immutableReference, StringComparer.Ordinal))
            throw new DockerEngineException("The inspected image does not retain the requested immutable repository digest.");
        var entrypoint = inspection.Config?.Entrypoint?.ToArray() ?? [];
        if (entrypoint.Length > 32 || entrypoint.Any(static value => string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Contains('\0')))
            throw new DockerEngineException("Docker returned an invalid image entrypoint.");

        return new RuntimeImageInspection(
            immutableReference,
            inspection.Id!,
            inspection.Size,
            inspection.OperatingSystem!,
            inspection.Architecture!,
            repoDigests,
            inspection.Config?.Labels is { } labels
                ? new Dictionary<string, string>(labels, StringComparer.Ordinal) : new Dictionary<string, string>(StringComparer.Ordinal),
            entrypoint);
    }

    public async Task<string> CreateContainerAsync(RuntimeContainerSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ValidateVolumeName(spec.WorkspaceVolumeName);
        if (spec.Entrypoint is not null)
        {
            if (spec.Entrypoint.Count == 0 || spec.Entrypoint.Any(static value => string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Contains('\0')))
            {
                throw new ArgumentException("The Docker container entrypoint is invalid.", nameof(spec));
            }
        }
        var effectiveEnvironment = new Dictionary<string, string>(spec.Environment, StringComparer.Ordinal);
        if (spec.TraceParent is not null)
        {
            ValidateTraceParent(spec.TraceParent);
            effectiveEnvironment["SHARPLABNEXT_TRACEPARENT"] = spec.TraceParent;
        }
        var environment = effectiveEnvironment.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => $"{pair.Key}={pair.Value}").ToArray();
        var isolation = RuntimeContainerIsolation.Resolve(spec.IsolationKind, spec.SecurityPolicy, spec.WinePrefixPath);
        var body = new Dictionary<string, object?>
        {
            ["Image"] = spec.Image,
            ["Cmd"] = spec.Command,
            ["Env"] = environment,
            ["WorkingDir"] = "/workspace",
            ["User"] = isolation.User,
            ["AttachStdout"] = true,
            ["AttachStderr"] = true,
            ["Tty"] = false,
            // The measured keeper blocks on a shell builtin. Keep its stdin open
            // without attaching it so the cgroup still contains one process.
            ["OpenStdin"] = spec.Entrypoint is not null,
            ["NetworkDisabled"] = true,
            ["StopTimeout"] = 1,
            ["Labels"] = CreateManagedLabels(spec.ManagementLabel, "true", spec.JobId, spec.ReleaseId, spec.ResourceScope, traceParent: spec.TraceParent),
            ["HostConfig"] = new Dictionary<string, object?>
            {
                ["NetworkMode"] = "none",
                ["ReadonlyRootfs"] = true,
                ["AutoRemove"] = false,
                ["Privileged"] = false,
                ["Init"] = spec.Entrypoint is null,
                ["IpcMode"] = "none",
                ["CapDrop"] = new[] { "ALL" },
                ["SecurityOpt"] = _sandbox.SecurityOptions,
                ["Ulimits"] = _sandbox.CreateUlimits(spec.IsolationKind),
                ["Memory"] = spec.SecurityPolicy.MemoryBytes,
                ["MemorySwap"] = spec.SecurityPolicy.MemoryBytes,
                ["NanoCpus"] = spec.SecurityPolicy.NanoCpus,
                ["PidsLimit"] = spec.SecurityPolicy.PidsLimit,
                ["OomKillDisable"] = false,
                ["LogConfig"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Type"] = "local",
                    ["Config"] = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["max-size"] = "4m",
                        ["max-file"] = "1",
                        ["compress"] = "false"
                    }
                },
                ["Mounts"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["Type"] = "volume",
                        ["Source"] = spec.WorkspaceVolumeName,
                        ["Target"] = "/workspace",
                        ["ReadOnly"] = true
                    }
                },
                ["Tmpfs"] = isolation.Tmpfs
            }
        };
        if (spec.Entrypoint is not null)
            body["Entrypoint"] = spec.Entrypoint;

        var name = Uri.EscapeDataString(spec.Name);
        using var response = await _client.PostAsJsonAsync(Api($"/containers/create?name={name}"), body, JsonOptions, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<CreateContainerResponse>(JsonOptions, cancellationToken) ?? throw new DockerEngineException("Docker returned an empty create-container response.");
        if (string.IsNullOrWhiteSpace(result.Id))
        {
            throw new DockerEngineException("Docker returned an invalid container ID.");
        }
        ValidateFullContainerIdFromDocker(result.Id);

        return result.Id;
    }

    public async Task<IReadOnlyList<RuntimeImageFileInspection>> InspectImageFilesAsync(string imageId, IReadOnlyList<RuntimeImageFileRequest> files, CancellationToken cancellationToken = default)
    {
        ValidateImageId(imageId);
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count is < 2 or > 8)
            throw new ArgumentOutOfRangeException(nameof(files), "Two to eight image files are required.");
        var roles = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (!IsStableArtifactRole(file.Role) || !IsCanonicalContainerPath(file.Path) || !roles.Add(file.Role) || !paths.Add(file.Path))
            {
                throw new ArgumentException("Image-file requests require unique stable roles and canonical absolute paths.", nameof(files));
            }
        }

        var name = Uri.EscapeDataString($"sln-evidence-inspect-{Guid.NewGuid():N}");
        var body = new Dictionary<string, object?>
        {
            ["Image"] = imageId,
            ["Entrypoint"] = new[] { "/bin/true" },
            ["Cmd"] = Array.Empty<string>(),
            ["WorkingDir"] = "/",
            ["User"] = "0:0",
            ["AttachStdout"] = false,
            ["AttachStderr"] = false,
            ["OpenStdin"] = false,
            ["NetworkDisabled"] = true,
            ["Labels"] = CreateManagedLabels(_options.ContainerLabel, "evidence-inspection", $"evidence-{Guid.NewGuid():N}", "runtime-capability-evidence", _options.ResourceScope),
            ["HostConfig"] = new Dictionary<string, object?>
            {
                ["NetworkMode"] = "none",
                ["ReadonlyRootfs"] = true,
                ["AutoRemove"] = false,
                ["Privileged"] = false,
                ["IpcMode"] = "none",
                ["CapDrop"] = new[] { "ALL" },
                ["SecurityOpt"] = _sandbox.SecurityOptions,
                ["Memory"] = 128L * 1024 * 1024,
                ["MemorySwap"] = 128L * 1024 * 1024,
                ["NanoCpus"] = 250_000_000L,
                ["PidsLimit"] = 8L,
                ["LogConfig"] = new Dictionary<string, object?> { ["Type"] = "none" }
            }
        };

        string? containerId = null;
        Exception? failure = null;
        try
        {
            using (var response = await _client.PostAsJsonAsync(Api($"/containers/create?name={name}"), body, JsonOptions, cancellationToken).ConfigureAwait(false))
            {
                await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
                var created = await response.Content.ReadFromJsonAsync<CreateContainerResponse>(JsonOptions, cancellationToken).ConfigureAwait(false) ?? throw new DockerEngineException("Docker returned an empty inspection-container response.");
                ValidateContainerId(created.Id);
                containerId = created.Id;
            }

            var result = new List<RuntimeImageFileInspection>(files.Count);
            foreach (var file in files)
                result.Add(await InspectContainerFileAsync(containerId, file, cancellationToken).ConfigureAwait(false));
            return result;
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            if (containerId is not null)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await RemoveContainerAsync(containerId, timeout.Token).ConfigureAwait(false);
                }
                catch when (failure is not null) { }
            }
        }
    }

    public async Task<RuntimeWorkspaceMaterialization> MaterializeWorkspaceAsync(string jobId, string releaseId, string image, Stream archive, RuntimeSecurityPolicyOptions securityPolicy, RuntimeContainerIsolationKind isolationKind, string managementLabel, string resourceScope, bool createMeasurementControl = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(securityPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(managementLabel);
        var workspaceOwner = RuntimeContainerIsolation.ResolveWorkspaceOwner(isolationKind);
        var volumeName = $"sln-work-{Guid.NewGuid():N}";
        var measurementVolumeName = createMeasurementControl
            ? $"sln-measure-{Guid.NewGuid():N}" : null;
        string? materializerId = null;
        try
        {
            var volumeBody = new Dictionary<string, object?>
            {
                ["Name"] = volumeName,
                ["Driver"] = "local",
                ["DriverOpts"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["type"] = "tmpfs",
                    ["device"] = "tmpfs",
                    ["o"] =
                        $"size={checked(securityPolicy.MaximumArtifactBytes + 1048576)}," +
                        $"uid={workspaceOwner.Uid},gid={workspaceOwner.Gid},mode=0700"
                },
                ["Labels"] = CreateManagedLabels(managementLabel, "workspace", jobId, releaseId, resourceScope)
            };
            using (var volumeResponse = await _client.PostAsJsonAsync(Api("/volumes/create"), volumeBody, JsonOptions, cancellationToken))
            {
                await EnsureSuccessAsync(volumeResponse, cancellationToken);
            }

            if (measurementVolumeName is not null)
            {
                var measurementLabels = new Dictionary<string, string>(CreateManagedLabels(managementLabel, "workspace", jobId, releaseId, resourceScope), StringComparer.Ordinal)
                {
                    ["com.sharplabnext.measurement-control"] = "true"
                };
                var measurementVolumeBody = new Dictionary<string, object?>
                {
                    ["Name"] = measurementVolumeName,
                    ["Driver"] = "local",
                    ["DriverOpts"] = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["type"] = "tmpfs",
                        ["device"] = "tmpfs",
                        ["o"] = "noexec,nosuid,nodev,size=65536,uid=1654,gid=1654,mode=0700"
                    },
                    ["Labels"] = measurementLabels
                };
                using var measurementVolumeResponse = await _client.PostAsJsonAsync(Api("/volumes/create"), measurementVolumeBody, JsonOptions, cancellationToken);
                await EnsureSuccessAsync(measurementVolumeResponse, cancellationToken);
            }

            var helperName = Uri.EscapeDataString($"sln-materialize-{Guid.NewGuid():N}");
            var helperBody = new Dictionary<string, object?>
            {
                ["Image"] = image,
                ["Entrypoint"] = new[] { "/bin/sh" },
                ["Cmd"] = new[]
                {
                    "-c",
                    "trap 'rm -rf -- /workspace/* /workspace/.[!.]* /workspace/..?*; exit $?' TERM INT; " +
                    "while :; do sleep 2147483647 & wait $!; done"
                },
                ["WorkingDir"] = "/workspace",
                ["User"] = workspaceOwner.User,
                ["AttachStdout"] = false,
                ["AttachStderr"] = false,
                ["OpenStdin"] = false,
                ["NetworkDisabled"] = true,
                ["Labels"] = CreateManagedLabels(managementLabel, "true", jobId, releaseId, resourceScope, materializer: true),
                ["HostConfig"] = new Dictionary<string, object?>
                {
                    ["NetworkMode"] = "none",
                    ["ReadonlyRootfs"] = true,
                    ["AutoRemove"] = false,
                    ["Privileged"] = false,
                    ["IpcMode"] = "none",
                    ["CapDrop"] = new[] { "ALL" },
                    ["SecurityOpt"] = _sandbox.SecurityOptions,
                    ["Ulimits"] = _sandbox.CreateUlimits(),
                    ["Memory"] = securityPolicy.MemoryBytes,
                    ["MemorySwap"] = securityPolicy.MemoryBytes,
                    ["NanoCpus"] = securityPolicy.NanoCpus,
                    ["PidsLimit"] = Math.Min(securityPolicy.PidsLimit, 16),
                    ["LogConfig"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["Type"] = "none"
                    },
                    ["Mounts"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["Type"] = "volume",
                            ["Source"] = volumeName,
                            ["Target"] = "/workspace",
                            ["ReadOnly"] = false
                        }
                    }
                }
            };
            using (var createResponse = await _client.PostAsJsonAsync(Api($"/containers/create?name={helperName}"), helperBody, JsonOptions, cancellationToken))
            {
                await EnsureSuccessAsync(createResponse, cancellationToken);
                var created = await createResponse.Content.ReadFromJsonAsync<CreateContainerResponse>(JsonOptions, cancellationToken) ?? throw new DockerEngineException("Docker returned an empty materializer response.");
                ValidateFullContainerIdFromDocker(created.Id);
                materializerId = created.Id;
            }

            // A local-driver tmpfs exists only while at least one container has it mounted.
            // Keep the materializer running across upload and runtime-container startup so
            // Docker cannot unmount (and therefore discard) the workspace between them.
            await StartContainerAsync(materializerId, cancellationToken).ConfigureAwait(false);
            await UploadArchiveAsync(materializerId, archive, destinationPath: "/workspace", cancellationToken: cancellationToken).ConfigureAwait(false);
            var materialization = new RuntimeWorkspaceMaterialization(volumeName, materializerId, measurementVolumeName);
            materializerId = null;
            measurementVolumeName = null;
            return materialization;
        }
        catch
        {
            if (materializerId is not null)
                await RemoveContainerBestEffortAsync(materializerId).ConfigureAwait(false);

            if (measurementVolumeName is not null)
                await RemoveWorkspaceVolumeBestEffortAsync(measurementVolumeName).ConfigureAwait(false);
            await RemoveWorkspaceVolumeBestEffortAsync(volumeName).ConfigureAwait(false);
            throw;
        }
    }

    private async Task RemoveContainerBestEffortAsync(string containerId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await RemoveContainerAsync(containerId, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception) { }
    }

    private async Task RemoveWorkspaceVolumeBestEffortAsync(string volumeName)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await RemoveWorkspaceVolumeAsync(volumeName, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception) { }
    }

    public async Task UploadArchiveAsync(string containerId, Stream archive, string destinationPath = "/workspace", CancellationToken cancellationToken = default)
    {
        ValidateContainerId(containerId);
        ArgumentNullException.ThrowIfNull(archive);
        if (destinationPath is not ("/workspace" or "/measurement"))
            throw new ArgumentException("Archive destination is not allowed.", nameof(destinationPath));
        using var request = new HttpRequestMessage(HttpMethod.Put, Api($"/containers/{Uri.EscapeDataString(containerId)}/archive?path=" + Uri.EscapeDataString(destinationPath) + (destinationPath == "/measurement" ? "&copyUIDGID=true" : string.Empty)))
        {
            Content = new StreamContent(new NonDisposingStream(archive))
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-tar");
        using var response = await _client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<RuntimeRunningContainerInspection> InspectRunningContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        ValidateFullContainerId(containerId);
        using var response = await _client.GetAsync(Api($"/containers/{Uri.EscapeDataString(containerId)}/json"), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var inspected = await response.Content.ReadFromJsonAsync<InspectContainerResponse>(JsonOptions, cancellationToken).ConfigureAwait(false) ?? throw new DockerEngineException("Docker returned an empty container-inspect response.");
        if (!StringComparer.Ordinal.Equals(inspected.Id, containerId) || inspected.State?.Running is not true || inspected.State.Pid is null or <= 0)
        {
            throw new DockerEngineException("Docker did not return the expected running container and positive host PID.");
        }

        return new RuntimeRunningContainerInspection(containerId, inspected.State.Pid.Value, Running: true);
    }

    public async Task<string> CreateRuntimeMeasurementSidecarAsync(RuntimeMeasurementSidecarSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.JobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.ReleaseId);
        ValidateImageId(spec.Image);
        ValidateFullContainerId(spec.TargetContainerId);
        if (spec.TargetHostPid <= 0)
            throw new ArgumentOutOfRangeException(nameof(spec), "The target host PID must be positive.");
        ValidateRuntimeMeasurementToken(spec.Token);
        ValidateMeasurementVolumeName(spec.MeasurementVolumeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.ManagementLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.ResourceScope);
        if (spec.TraceParent is not null)
            ValidateTraceParent(spec.TraceParent);

        var labels = new Dictionary<string, string>(CreateManagedLabels(spec.ManagementLabel, "true", spec.JobId, spec.ReleaseId, spec.ResourceScope, traceParent: spec.TraceParent), StringComparer.Ordinal)
        {
            ["com.sharplabnext.measurement-sidecar"] = "true",
            ["com.sharplabnext.target-container-id"] = spec.TargetContainerId
        };
        var body = new Dictionary<string, object?>
        {
            ["Image"] = spec.Image,
            ["Entrypoint"] = new[] { "/usr/local/bin/sharplabnext-runtime-measurement" },
            ["Cmd"] = new[] { spec.Token, spec.TargetContainerId },
            ["WorkingDir"] = "/",
            ["User"] = "1654:1654",
            ["AttachStdout"] = false,
            ["AttachStderr"] = false,
            ["Tty"] = false,
            ["OpenStdin"] = false,
            ["NetworkDisabled"] = true,
            ["StopTimeout"] = 1,
            ["Labels"] = labels,
            ["HostConfig"] = new Dictionary<string, object?>
            {
                ["NetworkMode"] = "none",
                ["ReadonlyRootfs"] = true,
                ["AutoRemove"] = false,
                ["Privileged"] = false,
                ["Init"] = false,
                ["IpcMode"] = "private",
                // An empty Docker PidMode is the explicit private namespace.
                    ["CgroupnsMode"] = "host",
                ["CapDrop"] = new[] { "ALL" },
                ["SecurityOpt"] = _sandbox.SecurityOptions,
                ["Ulimits"] = _sandbox.CreateUlimits(),
                ["Memory"] = 32L * 1024 * 1024,
                ["MemorySwap"] = 32L * 1024 * 1024,
                ["NanoCpus"] = 100_000_000L,
                ["PidsLimit"] = 8L,
                ["OomKillDisable"] = false,
                ["LogConfig"] = new Dictionary<string, object?>
                {
                    ["Type"] = "local",
                    ["Config"] = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["max-size"] = "1m",
                        ["max-file"] = "1",
                        ["compress"] = "false"
                    }
                },
                ["Mounts"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["Type"] = "volume",
                        ["Source"] = spec.MeasurementVolumeName,
                        ["Target"] = "/measurement",
                        ["ReadOnly"] = false
                    },
                    new Dictionary<string, object?>
                    {
                        ["Type"] = "bind",
                        ["Source"] = $"/proc/{spec.TargetHostPid}/cgroup",
                        ["Target"] = "/run/sharplabnext-target-cgroup",
                        ["ReadOnly"] = true,
                        ["BindOptions"] = new Dictionary<string, object?>
                        {
                            ["Propagation"] = "rprivate"
                        }
                    }
                },
                ["Tmpfs"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["/tmp"] = "rw,noexec,nosuid,nodev,size=1048576,uid=1654,gid=1654,mode=0700"
                }
            }
        };

        var name = Uri.EscapeDataString($"sln-measurement-{Guid.NewGuid():N}");
        using var response = await _client.PostAsJsonAsync(Api($"/containers/create?name={name}"), body, JsonOptions, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var created = await response.Content.ReadFromJsonAsync<CreateContainerResponse>(JsonOptions, cancellationToken).ConfigureAwait(false) ?? throw new DockerEngineException("Docker returned an empty measurement-sidecar response.");
        ValidateFullContainerIdFromDocker(created.Id);
        return created.Id;
    }

    public async Task<string> CreateContainerExecAsync(string containerId, RuntimeExecSpec spec, CancellationToken cancellationToken = default)
    {
        ValidateFullContainerId(containerId);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(spec.Command);
        if (spec.Command.Count == 0 || spec.Command.Count > 256 || spec.Command.Any(static value => value is null || value.Length > 32 * 1024 || value.Contains('\0')))
        {
            throw new ArgumentException("The Docker exec command is invalid.", nameof(spec));
        }
        if (spec.User is not ("0:0" or "1654:1654"))
            throw new ArgumentException("The Docker exec user is not allowed.", nameof(spec));
        if (spec.WorkingDirectory != "/workspace")
            throw new ArgumentException("The Docker exec working directory is not allowed.", nameof(spec));
        if (spec.Environment is { } execEnvironment && execEnvironment.Any(static pair =>
                string.IsNullOrWhiteSpace(pair.Key) ||
                pair.Key.Length > 256 ||
                pair.Key.Contains('=') ||
                pair.Key.Contains('\0') ||
                pair.Value is null ||
                pair.Value.Length > 32 * 1024 ||
                pair.Value.Contains('\0')))
        {
            throw new ArgumentException("The Docker exec environment is invalid.", nameof(spec));
        }

        var body = new Dictionary<string, object?>
        {
            ["AttachStdin"] = false,
            ["AttachStdout"] = true,
            ["AttachStderr"] = true,
            ["DetachKeys"] = string.Empty,
            ["Tty"] = false,
            ["Cmd"] = spec.Command,
            ["User"] = spec.User,
            ["WorkingDir"] = spec.WorkingDirectory
        };
        if (spec.Environment is { Count: > 0 } environment)
        {
            body["Env"] = environment.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => $"{pair.Key}={pair.Value}").ToArray();
        }
        using var response = await _client.PostAsJsonAsync(Api($"/containers/{Uri.EscapeDataString(containerId)}/exec"), body, JsonOptions, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var created = await response.Content.ReadFromJsonAsync<CreateExecResponse>(JsonOptions, cancellationToken).ConfigureAwait(false) ?? throw new DockerEngineException("Docker returned an empty create-exec response.");
        ValidateFullExecIdFromDocker(created.Id);
        return created.Id;
    }

    public async Task<Stream> StartContainerExecAsync(string execId, CancellationToken cancellationToken = default)
    {
        ValidateFullExecId(execId);
        var body = new Dictionary<string, object?>
        {
            ["Detach"] = false,
            ["Tty"] = false
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, Api($"/exec/{execId}/start"))
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new DockerMultiplexedReadStream(new DockerResponseStream(response, stream));
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public async Task<RuntimeContainerExecInspection> InspectContainerExecAsync(string execId, CancellationToken cancellationToken = default)
    {
        ValidateFullExecId(execId);
        using var response = await _client.GetAsync(Api($"/exec/{execId}/json"), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var inspected = await response.Content.ReadFromJsonAsync<InspectExecResponse>(JsonOptions, cancellationToken).ConfigureAwait(false) ?? throw new DockerEngineException("Docker returned an empty exec-inspect response.");
        if (!StringComparer.Ordinal.Equals(inspected.Id, execId) || inspected.Running is null || inspected.ExitCode is null or < 0 or > 255)
        {
            throw new DockerEngineException("Docker returned a malformed exec-inspect response.");
        }

        return new RuntimeContainerExecInspection(execId, inspected.Running.Value, inspected.Running.Value ? null : checked((int)inspected.ExitCode.Value));
    }

    public async Task WaitForRuntimeMeasurementArmedAsync(string sidecarContainerId, string token, string targetContainerId, CancellationToken cancellationToken = default)
    {
        _ = await WaitForRuntimeMeasurementRecordAsync(sidecarContainerId, token, targetContainerId, RuntimeMeasurementRecordKind.Armed, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RuntimeContainerMeasurement> WaitForRuntimeMeasurementAsync(string sidecarContainerId, string token, string targetContainerId, CancellationToken cancellationToken = default)
    {
        return await WaitForRuntimeMeasurementRecordAsync(sidecarContainerId, token, targetContainerId, RuntimeMeasurementRecordKind.Completion, cancellationToken).ConfigureAwait(false) ?? throw new DockerEngineException("The runtime measurement completion was empty.");
    }

    public async Task UploadRuntimeMeasurementSignalAsync(string sidecarContainerId, string token, string targetContainerId, RuntimeMeasurementSignalKind signalKind, CancellationToken cancellationToken = default)
    {
        ValidateFullContainerId(sidecarContainerId);
        ValidateRuntimeMeasurementToken(token);
        ValidateFullContainerId(targetContainerId);
        var signalName = signalKind switch
        {
            RuntimeMeasurementSignalKind.Capture => "capture",
            RuntimeMeasurementSignalKind.Finish => "finish",
            _ => throw new ArgumentOutOfRangeException(nameof(signalKind), signalKind, "Unknown signal kind.")
        };
        var fileName = $"{signalName}-{token}";
        var uploadFileName = fileName + ".upload";
        var content = System.Text.Encoding.ASCII.GetBytes($"sharplabnext-runtime-measurement-signal-v1\n{token}\n{targetContainerId}\n{signalName}\n");
        await using var contentStream = new MemoryStream(content, writable: false);
        await using var archive = new MemoryStream();
        using (var writer = new TarWriter(archive, TarEntryFormat.Ustar, leaveOpen: true))
        {
            var entry = new UstarTarEntry(TarEntryType.RegularFile, uploadFileName)
            {
                DataStream = contentStream,
                Gid = 1654,
                Uid = 1654,
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                ModificationTime = DateTimeOffset.UnixEpoch,
                GroupName = string.Empty,
                UserName = string.Empty
            };
            await writer.WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        archive.Position = 0;
        await UploadArchiveAsync(sidecarContainerId, archive, destinationPath: "/measurement", cancellationToken).ConfigureAwait(false);
    }

    public async Task<Stream> AttachContainerOutputAsync(string containerId, CancellationToken cancellationToken = default)
    {
        ValidateFullContainerId(containerId);
        using var request = new HttpRequestMessage(HttpMethod.Post, Api($"/containers/{Uri.EscapeDataString(containerId)}/attach?logs=false&stream=true&stdin=false&stdout=true&stderr=true"));
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new DockerMultiplexedReadStream(new DockerResponseStream(response, stream));
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public async Task StartContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        ValidateContainerId(containerId);
        using var response = await _client.PostAsync(Api($"/containers/{Uri.EscapeDataString(containerId)}/start"), content: null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IRuntimeContainerResourceMonitor> StartContainerResourceMonitorAsync(string containerId, CancellationToken cancellationToken = default)
    {
        ValidateContainerId(containerId);
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        HttpResponseMessage? response = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Api($"/containers/{Uri.EscapeDataString(containerId)}/stats?stream=true&one-shot=false"));
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
            await EnsureSuccessAsync(response, lifetime.Token);
            var stream = await response.Content.ReadAsStreamAsync(lifetime.Token);
            var monitor = new DockerContainerResourceMonitor(response, stream, lifetime, () => ReadOneShotMemoryAsync(containerId));
            response = null;
            return monitor;
        }
        catch
        {
            response?.Dispose();
            lifetime.Dispose();
            throw;
        }
    }

    private async Task<long> ReadOneShotMemoryAsync(string containerId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await _client.GetAsync(Api($"/containers/{Uri.EscapeDataString(containerId)}/stats?stream=false&one-shot=true"), timeout.Token).ConfigureAwait(false);
        await EnsureSuccessAsync(response, timeout.Token).ConfigureAwait(false);
        DockerStatsResponse? sample;
        try
        {
            sample = await response.Content.ReadFromJsonAsync<DockerStatsResponse>(JsonOptions, timeout.Token).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new DockerEngineException($"Docker returned malformed one-shot container-stats JSON: {exception.Message}");
        }

        var observed = Math.Max(sample?.MemoryStats?.Usage ?? 0, sample?.MemoryStats?.MaxUsage ?? 0);
        return observed > 0
            ? observed : throw new DockerEngineException("Docker one-shot container-stats returned no positive memory sample.");
    }

    public async Task StopContainerAsync(string containerId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ValidateContainerId(containerId);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Docker stop timeout must be between 1 ms and 10 seconds.");

        var timeoutSeconds = Math.Max(1, checked((int)Math.Ceiling(timeout.TotalSeconds)));
        using var response = await _client.PostAsync(Api($"/containers/{Uri.EscapeDataString(containerId)}/stop?t={timeoutSeconds}"), content: null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<RuntimeContainerExit> WaitContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        ValidateContainerId(containerId);
        using var response = await _client.PostAsync(Api($"/containers/{Uri.EscapeDataString(containerId)}/wait?condition=not-running"), content: null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var wait = await response.Content.ReadFromJsonAsync<WaitContainerResponse>(JsonOptions, cancellationToken) ?? throw new DockerEngineException("Docker returned an empty wait response.");

        using var inspectResponse = await _client.GetAsync(Api($"/containers/{Uri.EscapeDataString(containerId)}/json"), cancellationToken);
        await EnsureSuccessAsync(inspectResponse, cancellationToken);
        var inspect = await inspectResponse.Content.ReadFromJsonAsync<InspectContainerResponse>(JsonOptions, cancellationToken) ?? throw new DockerEngineException("Docker returned an empty inspect response.");
        return new RuntimeContainerExit(wait.StatusCode, inspect.State?.OomKilled == true, wait.Error?.Message);
    }

    public async Task KillContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        ValidateContainerId(containerId);
        using var response = await _client.PostAsync(Api($"/containers/{Uri.EscapeDataString(containerId)}/kill?signal=KILL"), content: null, cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
        {
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task RemoveContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        ValidateContainerId(containerId);
        using var response = await _client.DeleteAsync(Api($"/containers/{Uri.EscapeDataString(containerId)}?force=true&v=true"), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task RemoveWorkspaceVolumeAsync(string volumeName, CancellationToken cancellationToken = default)
    {
        ValidateVolumeName(volumeName);
        using var response = await _client.DeleteAsync(Api($"/volumes/{Uri.EscapeDataString(volumeName)}?force=true"), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<ManagedRuntimeContainer>> ListManagedContainersAsync(string managementLabel, string resourceScope, CancellationToken cancellationToken = default)
    {
        ValidateManagementLabel(managementLabel);
        ValidateResourceScope(resourceScope);
        var filters = JsonSerializer.Serialize(new Dictionary<string, string[]>
        {
            ["label"] =
            [
                managementLabel + "=true",
                "com.sharplabnext.resource-scope=" + resourceScope
            ]
        }, JsonOptions);
        using var response = await _client.GetAsync(Api($"/containers/json?all=true&filters={Uri.EscapeDataString(filters)}"), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var containers = await response.Content.ReadFromJsonAsync<ListContainerResponse[]>(JsonOptions, cancellationToken) ?? [];
        return containers.Where(static container => !string.IsNullOrWhiteSpace(container.Id)).Select(static container => new ManagedRuntimeContainer(container.Id!, ParseContainerCreatedAt(container), container.State ?? string.Empty)).ToArray();
    }

    public async Task<IReadOnlyList<ManagedWorkspaceVolume>> ListManagedWorkspaceVolumesAsync(string managementLabel, string resourceScope, CancellationToken cancellationToken = default)
    {
        ValidateManagementLabel(managementLabel);
        ValidateResourceScope(resourceScope);
        var filters = JsonSerializer.Serialize(new Dictionary<string, string[]>
        {
            ["label"] =
            [
                $"{managementLabel}=workspace",
                "com.sharplabnext.resource-scope=" + resourceScope
            ]
        }, JsonOptions);
        using var response = await _client.GetAsync(Api($"/volumes?filters={Uri.EscapeDataString(filters)}"), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<ListVolumesResponse>(JsonOptions, cancellationToken);
        return result?.Volumes?.Where(static volume => !string.IsNullOrWhiteSpace(volume.Name)).Select(static volume => new ManagedWorkspaceVolume(volume.Name!, ParseVolumeCreatedAt(volume))).ToArray() ?? [];
    }

    public void Dispose() => _client.Dispose();

    internal static IReadOnlyDictionary<string, string> CreateManagedLabels(string managementLabel, string managementValue, string jobId, string releaseId, string resourceScope, bool materializer = false, string? traceParent = null)
    {
        ValidateManagementLabel(managementLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(managementValue);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceScope);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [managementLabel] = managementValue,
            ["com.sharplabnext.job-id"] = jobId,
            ["com.sharplabnext.operation-id"] = jobId,
            ["com.sharplabnext.release-id"] = releaseId,
            ["com.sharplabnext.resource-scope"] = resourceScope,
            ["com.sharplabnext.created-at"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        };
        if (materializer)
        {
            labels["com.sharplabnext.materializer"] = "true";
        }
        if (traceParent is not null)
        {
            ValidateTraceParent(traceParent);
            labels["com.sharplabnext.traceparent"] = traceParent;
        }

        return labels;
    }

    private static void ValidateManagementLabel(string managementLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managementLabel);
        if (managementLabel.Length > 128 || managementLabel.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_' or '/')) || RuntimeManagedLabelPolicy.IsReservedManagementLabel(managementLabel))
        {
            throw new ArgumentException("The Docker management label is malformed or collides with a reserved SharpLabNext label.", nameof(managementLabel));
        }
    }

    private static void ValidateResourceScope(string resourceScope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceScope);
        if (resourceScope.Length > 128 || resourceScope.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.' or ':')))
        {
            throw new ArgumentException("The Docker resource scope is malformed.", nameof(resourceScope));
        }
    }

    private static void ValidateTraceParent(string traceParent)
    {
        if (!ActivityContext.TryParse(traceParent, traceState: null, out _))
            throw new ArgumentException("The Docker job traceparent is not valid W3C trace context.", nameof(traceParent));
    }

    private static SocketsHttpHandler CreateDockerHandler(string socketPath) =>
        new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

    private string Api(string path) => $"/{_options.DockerApiVersion}{path}";

    private async Task<RuntimeImageFileInspection> InspectContainerFileAsync(string containerId, RuntimeImageFileRequest requested, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(Api($"/containers/{Uri.EscapeDataString(containerId)}/archive?path=" + Uri.EscapeDataString(requested.Path)), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using var archive = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new TarReader(archive, leaveOpen: true);
        MemoryStream? bytes = null;
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
        {
            if (entry.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes)
                continue;
            if (bytes is not null || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile) || entry.DataStream is null)
            {
                throw new DockerEngineException($"Image path '{requested.Path}' must resolve to exactly one regular non-link file.");
            }

            bytes = new MemoryStream(entry.Length > 0 && entry.Length <= 268_435_456 ? checked((int)entry.Length) : 0);
            var buffer = new byte[64 * 1024];
            long length = 0;
            while (true)
            {
                var read = await entry.DataStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                length = checked(length + read);
                if (length > 268_435_456)
                {
                    throw new DockerEngineException($"Image file '{requested.Path}' exceeds the evidence size limit.");
                }
                bytes.Write(buffer, 0, read);
            }
            if (length == 0)
                throw new DockerEngineException($"Image file '{requested.Path}' is empty.");
        }
        if (bytes is null)
            throw new DockerEngineException($"Image path '{requested.Path}' did not return a regular file.");

        using (bytes)
        {
            var content = bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length));
            var (format, architecture) = ClassifyImageFile(requested.Path, content);
            return new RuntimeImageFileInspection(requested.Role, requested.Path, $"sha256:{Convert.ToHexStringLower(SHA256.HashData(content))}", bytes.Length, format, architecture);
        }
    }

    private static (string Format, string Architecture) ClassifyImageFile(string path, ReadOnlySpan<byte> content)
    {
        if (content.Length >= 4 && content[..4].SequenceEqual("\u007fELF"u8))
        {
            if (content.Length < 20 || content[4] != 2 || content[5] != 1 || BinaryPrimitives.ReadUInt16LittleEndian(content[18..20]) != 0x3e)
            {
                throw new DockerEngineException($"Image ELF '{path}' is not Linux x64.");
            }
            return ("elf", "x64");
        }

        if (content.Length >= 2 && content[..2].SequenceEqual("MZ"u8))
        {
            using var stream = new MemoryStream(content.ToArray(), writable: false);
            using var reader = new PEReader(stream, PEStreamOptions.PrefetchEntireImage);
            if (reader.PEHeaders.CoffHeader.Machine != Machine.Amd64 && reader.PEHeaders.CorHeader is null)
            {
                throw new DockerEngineException($"Image PE '{path}' is not x64.");
            }
            return reader.PEHeaders.CorHeader is null
                ? ("pe", "x64") : ("managed-pe", "anycpu");
        }

        if (content.Length >= 2 && content[..2].SequenceEqual("#!"u8))
            return ("script", "shell");

        throw new DockerEngineException($"Image file '{path}' is not a supported ELF, PE, managed PE, or script artifact.");
    }

    private static bool IsStableArtifactRole(string role) => role is
        "helper" or "desktop-helper" or "control-host" or "runtime-host" or "support-assembly" or
        "jit-library" or "profiler";

    private static bool IsCanonicalContainerPath(string path) =>
        path.Length is >= 2 and <= 4096 &&
        path[0] == '/' && path[^1] != '/' &&
        !path.Contains('\0') && !path.Contains('\\') && !path.Contains("//", StringComparison.Ordinal) &&
        path[1..].Split('/').All(static segment => segment.Length > 0 && segment is not "." and not ".." && segment.All(static character => !char.IsControl(character)));

    private static void ValidateImageId(string imageId)
    {
        if (!IsSha256Digest(imageId))
            throw new ArgumentException("The Docker image ID must be a canonical sha256 digest.", nameof(imageId));
    }

    private static void ValidateContainerId(string containerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        if (containerId.Length > 128 || containerId.Any(static character => !char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException("The Docker container ID is malformed.", nameof(containerId));
        }
    }

    private static void ValidateFullContainerId(string containerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        if (containerId.Length != 64 || containerId.AsSpan().IndexOfAnyExcept(LowercaseHexCharacters) >= 0)
        {
            throw new ArgumentException("The Docker container ID must contain exactly 64 lowercase hexadecimal characters.", nameof(containerId));
        }
    }

    private static void ValidateFullContainerIdFromDocker(string? containerId)
    {
        if (containerId is not { Length: 64 } ||
            containerId.AsSpan().IndexOfAnyExcept(LowercaseHexCharacters) >= 0)
        {
            throw new DockerEngineException("Docker returned a noncanonical full container ID.");
        }
    }

    private static void ValidateFullExecId(string execId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(execId);
        if (execId.Length != 64 || execId.AsSpan().IndexOfAnyExcept(LowercaseHexCharacters) >= 0)
        {
            throw new ArgumentException("The Docker exec ID must contain exactly 64 lowercase hexadecimal characters.", nameof(execId));
        }
    }

    private static void ValidateFullExecIdFromDocker(string? execId)
    {
        if (execId is not { Length: 64 } ||
            execId.AsSpan().IndexOfAnyExcept(LowercaseHexCharacters) >= 0)
        {
            throw new DockerEngineException("Docker returned a noncanonical full exec ID.");
        }
    }

    private static void ValidateImmutableImageReference(string immutableReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(immutableReference);
        const string marker = "@sha256:";
        var markerIndex = immutableReference.LastIndexOf(marker, StringComparison.Ordinal);
        if (markerIndex <= 0 || markerIndex + marker.Length + 64 != immutableReference.Length || immutableReference.Any(char.IsWhiteSpace) || immutableReference[(markerIndex + marker.Length)..].Any(static character => !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)))
        {
            throw new ArgumentException("The Docker image reference must contain one canonical immutable sha256 repository digest.", nameof(immutableReference));
        }
    }

    private static bool IsSha256Digest(string? value) =>
        value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept(LowercaseHexCharacters) < 0;

    private static void ValidateRuntimeMeasurementToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (token.Length != 32 || token.AsSpan().IndexOfAnyExcept(LowercaseHexCharacters) >= 0)
        {
            throw new ArgumentException("The runtime measurement token must contain exactly 32 lowercase hexadecimal characters.", nameof(token));
        }
    }

    private async Task<RuntimeContainerMeasurement?> WaitForRuntimeMeasurementRecordAsync(string sidecarContainerId, string token, string targetContainerId, RuntimeMeasurementRecordKind recordKind, CancellationToken cancellationToken)
    {
        ValidateFullContainerId(sidecarContainerId);
        ValidateRuntimeMeasurementToken(token);
        ValidateFullContainerId(targetContainerId);
        var prefix = recordKind switch
        {
            RuntimeMeasurementRecordKind.Armed => "armed",
            RuntimeMeasurementRecordKind.Completion => "completion",
            _ => throw new ArgumentOutOfRangeException(nameof(recordKind), recordKind, "Unknown record kind.")
        };
        var fileName = $"{prefix}-{token}";
        var requestPath = Api($"/containers/{Uri.EscapeDataString(sidecarContainerId)}/archive?path=" + Uri.EscapeDataString($"/measurement/{fileName}"));

        while (true)
        {
            using var response = await _client.GetAsync(requestPath, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
                continue;
            }

            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            await using var archive = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await ReadRuntimeMeasurementArchiveAsync(archive, fileName, token, targetContainerId, recordKind, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<RuntimeContainerMeasurement?> ReadRuntimeMeasurementArchiveAsync(Stream archive, string expectedFileName, string expectedToken, string expectedTargetContainerId, RuntimeMeasurementRecordKind recordKind, CancellationToken cancellationToken)
    {
        using var reader = new TarReader(archive, leaveOpen: true);
        var entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false);
        if (entry is null || !StringComparer.Ordinal.Equals(entry.Name, expectedFileName) || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile) || entry.DataStream is null || !string.IsNullOrEmpty(entry.LinkName) || entry.Length is <= 0 or > 512 || entry.Uid != 1654 || entry.Gid != 1654 || entry.Mode != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
        {
            throw new DockerEngineException("The runtime measurement archive must contain one canonical regular measurement file.");
        }

        var content = new byte[checked((int)entry.Length)];
        var offset = 0;
        while (offset < content.Length)
        {
            var read = await entry.DataStream.ReadAsync(content.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new DockerEngineException("The runtime measurement archive ended inside its file payload.");
            offset += read;
        }

        if (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new DockerEngineException("The runtime measurement archive contains an unexpected additional entry.");
        }

        return ParseRuntimeMeasurement(content, expectedToken, expectedTargetContainerId, recordKind);
    }

    private static RuntimeContainerMeasurement? ParseRuntimeMeasurement(ReadOnlySpan<byte> content, string expectedToken, string expectedTargetContainerId, RuntimeMeasurementRecordKind recordKind)
    {
        foreach (var value in content)
        {
            if (value != (byte)'\n' && (value < 0x20 || value > 0x7e))
                throw new DockerEngineException("The runtime measurement is not canonical ASCII.");
        }

        var lines = System.Text.Encoding.ASCII.GetString(content).Split('\n');
        var expectedHeader = recordKind switch
        {
            RuntimeMeasurementRecordKind.Armed => "sharplabnext-runtime-measurement-sidecar-armed-v1",
            RuntimeMeasurementRecordKind.Completion => "sharplabnext-runtime-measurement-sidecar-v1",
            _ => throw new ArgumentOutOfRangeException(nameof(recordKind), recordKind, "Unknown record kind.")
        };
        var expectedLineCount = recordKind == RuntimeMeasurementRecordKind.Armed ? 5 : 6;
        if (lines.Length != expectedLineCount || lines[^1].Length != 0 || !StringComparer.Ordinal.Equals(lines[0], expectedHeader) || !StringComparer.Ordinal.Equals(lines[1], expectedToken) || !StringComparer.Ordinal.Equals(lines[2], expectedTargetContainerId) || lines[3] is not ("cgroup-v1" or "cgroup-v2"))
        {
            throw new DockerEngineException("The runtime measurement payload is malformed or noncanonical.");
        }

        if (recordKind == RuntimeMeasurementRecordKind.Armed)
            return null;
        if (!TryParseCanonicalPositiveInt64(lines[4], out var peakMemoryBytes))
            throw new DockerEngineException("The runtime measurement peak is malformed or noncanonical.");
        return new RuntimeContainerMeasurement(lines[3], peakMemoryBytes);
    }

    private static bool TryParseCanonicalPositiveInt64(string value, out long result)
    {
        result = 0;
        return value.Length > 0 && value[0] is >= '1' and <= '9' &&
            value.All(static character => character is >= '0' and <= '9') &&
            long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out result) &&
            result > 0;
    }

    private static void ValidateVolumeName(string volumeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeName);
        if (volumeName.Length > 128 || volumeName.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
        {
            throw new ArgumentException("The Docker volume name is malformed.", nameof(volumeName));
        }
    }

    private static void ValidateMeasurementVolumeName(string volumeName)
    {
        ValidateVolumeName(volumeName);
        const string prefix = "sln-measure-";
        if (volumeName.Length != prefix.Length + 32 || !volumeName.StartsWith(prefix, StringComparison.Ordinal) || volumeName.AsSpan(prefix.Length).IndexOfAnyExcept(LowercaseHexCharacters) >= 0)
        {
            throw new ArgumentException("The runtime measurement control volume name is not canonical.", nameof(volumeName));
        }
    }

    private static DateTimeOffset ParseVolumeCreatedAt(ListVolumeResponse volume)
    {
        if (volume.Labels is not null && volume.Labels.TryGetValue("com.sharplabnext.created-at", out var labeled) && DateTimeOffset.TryParse(labeled, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var labeledTime))
        {
            return labeledTime;
        }

        return DateTimeOffset.TryParse(volume.CreatedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var created)
            ? created : DateTimeOffset.UnixEpoch;
    }

    private static DateTimeOffset ParseContainerCreatedAt(ListContainerResponse container)
    {
        if (container.Labels is not null && container.Labels.TryGetValue("com.sharplabnext.created-at", out var labeled) && DateTimeOffset.TryParse(labeled, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var labeledTime))
        {
            return labeledTime;
        }

        return DateTimeOffset.FromUnixTimeSeconds(container.Created);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (body.Length > 4096)
        {
            body = body[..4096];
        }

        throw new DockerEngineException($"Docker Engine returned HTTP {(int)response.StatusCode} ({response.StatusCode}): {body}");
    }

    private sealed record CreateContainerResponse(string Id);

    private sealed record CreateExecResponse(string Id);

    private sealed record WaitContainerResponse(long StatusCode, DockerErrorResponse? Error);

    private sealed record DockerErrorResponse(string? Message);

    private sealed record InspectContainerResponse(string? Id, InspectContainerState? State);

    private sealed record InspectContainerState(bool? OomKilled, bool? Running, int? Pid);

    private sealed record InspectExecResponse([property: JsonPropertyName("ID")] string? Id, bool? Running, long? ExitCode);

    private sealed record InspectImageResponse(string? Id, IReadOnlyList<string>? RepoDigests, long Size, [property: JsonPropertyName("Os")] string? OperatingSystem, string? Architecture, InspectImageConfig? Config);

    private sealed record InspectImageConfig(IReadOnlyDictionary<string, string>? Labels, IReadOnlyList<string>? Entrypoint);

    private sealed record DockerStatsResponse([property: JsonPropertyName("memory_stats")] DockerMemoryStats? MemoryStats);

    private sealed record DockerMemoryStats(long Usage, [property: JsonPropertyName("max_usage")] long MaxUsage);

    private sealed record ListContainerResponse(string? Id, long Created, string? State, IReadOnlyDictionary<string, string>? Labels);

    private sealed record ListVolumesResponse(IReadOnlyList<ListVolumeResponse>? Volumes);

    private sealed record ListVolumeResponse(string? Name, string? CreatedAt, IReadOnlyDictionary<string, string>? Labels);

    private sealed class DockerContainerResourceMonitor : IRuntimeContainerResourceMonitor
    {
        private const int MaximumStatsLineLength = 1024 * 1024;
        private readonly HttpResponseMessage _response;
        private readonly Stream _stream;
        private readonly CancellationTokenSource _lifetime;
        private readonly Func<Task<long>> _oneShotMemoryReader;
        private readonly object _sampleGate = new();
        private TaskCompletionSource<bool> _sampleChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task<RuntimeContainerResourceUsage> _completion;
        private int _sampleCount;
        private bool _sampleStreamTerminated;
        private Exception? _sampleStreamFailure;
        private int _stopRequested;
        private int _disposed;

        public DockerContainerResourceMonitor(HttpResponseMessage response, Stream stream, CancellationTokenSource lifetime, Func<Task<long>> oneShotMemoryReader)
        {
            _response = response;
            _stream = stream;
            _lifetime = lifetime;
            _oneShotMemoryReader = oneShotMemoryReader;
            _completion = ReadUsageAsync(stream, lifetime.Token);
        }

        public int SampleCount => Volatile.Read(ref _sampleCount);

        public async Task WaitForSampleAfterAsync(int checkpoint, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(checkpoint);

            while (true)
            {
                Task? changed;
                Exception? terminalFailure;
                lock (_sampleGate)
                {
                    if (_sampleCount > checkpoint)
                        return;
                    terminalFailure = _sampleStreamTerminated ? _sampleStreamFailure : null;
                    changed = _sampleStreamTerminated ? null : _sampleChanged.Task;
                }

                if (terminalFailure is not null)
                    throw terminalFailure;
                if (changed is null)
                {
                    throw new DockerEngineException("Docker container-stats stream ended before the required sample was observed.");
                }
                await changed!.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task WaitForFirstSampleAsync(CancellationToken cancellationToken = default) =>
            await WaitForSampleAfterAsync(0, cancellationToken).ConfigureAwait(false);

        public async Task<RuntimeContainerResourceUsage> StopAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _stopRequested, 1) == 0)
                await _lifetime.CancelAsync().ConfigureAwait(false);

            try
            {
                return await _completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                DisposeResources();
            }
        }

        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                await StopAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A broken Docker stats stream must never hold cleanup hostage.
                // StopAsync has already cancelled the shared lifetime; disposing
                // the response and stream forces any blocked reader to unwind.
                DisposeResources();
            }
        }

        private async Task<RuntimeContainerResourceUsage> ReadUsageAsync(Stream stream, CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 16 * 1024, leaveOpen: true);
            var peak = 0L;
            var count = 0;
            OperationCanceledException? streamCancellation = null;
            try
            {
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    if (line.Length > MaximumStatsLineLength)
                        throw new DockerEngineException("Docker returned an oversized container-stats sample.");

                    var sample = JsonSerializer.Deserialize<DockerStatsResponse>(line, JsonOptions) ?? throw new DockerEngineException("Docker returned an empty container-stats sample.");
                    var observed = Math.Max(sample.MemoryStats?.Usage ?? 0, sample.MemoryStats?.MaxUsage ?? 0);
                    if (observed <= 0)
                        continue;
                    peak = Math.Max(peak, observed);
                    count++;
                    RecordSample();
                }
            }
            catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
            {
                streamCancellation = exception;
                TerminateSampleStream(exception);
            }
            catch (JsonException exception)
            {
                var failure = new DockerEngineException($"Docker returned malformed container-stats JSON: {exception.Message}");
                TerminateSampleStream(failure);
                throw failure;
            }
            catch (Exception exception)
            {
                TerminateSampleStream(exception);
                throw;
            }

            if (count > 0)
            {
                if (streamCancellation is null)
                    TerminateSampleStream(failure: null);
                return new RuntimeContainerResourceUsage(peak, count);
            }
            if (streamCancellation is not null)
                throw streamCancellation;

            // Very short-lived jobs can exit before Docker emits the first
            // streaming stats line. A one-shot sample closes that race while
            // retaining the fail-closed contract when Docker has no positive
            // memory observation at all.
            try
            {
                var oneShot = await _oneShotMemoryReader().ConfigureAwait(false);
                RecordSample();
                TerminateSampleStream(failure: null);
                return new RuntimeContainerResourceUsage(oneShot, 1);
            }
            catch (Exception exception)
            {
                TerminateSampleStream(exception);
                throw;
            }
        }

        private void RecordSample()
        {
            TaskCompletionSource<bool> changed;
            lock (_sampleGate)
            {
                if (_sampleStreamTerminated)
                    throw new InvalidOperationException("Docker stats produced a sample after stream termination.");
                checked
                {
                    _sampleCount++;
                }
                changed = _sampleChanged;
                _sampleChanged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            changed.TrySetResult(true);
        }

        private void TerminateSampleStream(Exception? failure)
        {
            TaskCompletionSource<bool> changed;
            lock (_sampleGate)
            {
                if (_sampleStreamTerminated)
                    return;
                _sampleStreamTerminated = true;
                _sampleStreamFailure = failure ?? new DockerEngineException("Docker container-stats ended before the requested sample was observed.");
                changed = _sampleChanged;
            }
            changed.TrySetResult(true);
        }

        private void DisposeResources()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _stream.Dispose();
            _response.Dispose();
            _lifetime.Dispose();
        }
    }

    private sealed class NonDisposingStream(Stream stream) : Stream
    {
        public override bool CanRead => stream.CanRead;
        public override bool CanSeek => stream.CanSeek;
        public override bool CanWrite => stream.CanWrite;
        public override long Length => stream.Length;
        public override long Position
        {
            get => stream.Position;
            set => stream.Position = value;
        }

        public override void Flush() => stream.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            stream.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            stream.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => stream.Read(buffer);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            stream.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            stream.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => stream.Seek(offset, origin);

        public override void SetLength(long value) => stream.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) =>
            stream.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => stream.Write(buffer);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            stream.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            stream.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => base.DisposeAsync();
    }

    internal sealed class DockerResponseStream(HttpResponseMessage response, Stream stream) : Stream
    {
        private int _disposed;

        public override bool CanRead => stream.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => stream.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            stream.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
            {
                base.Dispose(disposing);
                return;
            }

            var disposeResources = Interlocked.Exchange(ref _disposed, 1) == 0;
            try
            {
                if (disposeResources)
                    stream.Dispose();
            }
            finally
            {
                try
                {
                    if (disposeResources)
                        response.Dispose();
                }
                finally
                {
                    base.Dispose(disposing);
                }
            }
        }

        public override async ValueTask DisposeAsync()
        {
            var disposeResources = Interlocked.Exchange(ref _disposed, 1) == 0;
            try
            {
                if (disposeResources)
                    await stream.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    if (disposeResources)
                        response.Dispose();
                }
                finally
                {
                    await base.DisposeAsync().ConfigureAwait(false);
                    GC.SuppressFinalize(this);
                }
            }
        }
    }

    internal sealed class DockerMultiplexedReadStream(Stream source) : Stream
    {
        private const int HeaderSize = 8;
        private const int MaximumFrameBytes = 8 * 1024 * 1024;
        private int _remaining;
        private byte _streamKind;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override int Read(Span<byte> buffer)
        {
            var temporary = new byte[buffer.Length];
            var read = Read(temporary, 0, temporary.Length);
            temporary.AsSpan(0, read).CopyTo(buffer);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty)
            {
                return 0;
            }

            while (true)
            {
                if (_remaining == 0 && !await ReadHeaderAsync(cancellationToken))
                {
                    return 0;
                }

                if (_streamKind == 1)
                {
                    var read = await source.ReadAsync(buffer[..Math.Min(buffer.Length, _remaining)], cancellationToken);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("Docker log stream ended inside a stdout frame.");
                    }

                    _remaining -= read;
                    return read;
                }

                await DiscardCurrentFrameAsync(cancellationToken);
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                source.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await source.DisposeAsync();
            await base.DisposeAsync();
            GC.SuppressFinalize(this);
        }

        private async ValueTask<bool> ReadHeaderAsync(CancellationToken cancellationToken)
        {
            var header = new byte[HeaderSize];
            var first = await source.ReadAsync(header.AsMemory(0, 1), cancellationToken);
            if (first == 0)
            {
                return false;
            }

            await source.ReadExactlyAsync(header.AsMemory(1), cancellationToken);
            if (header[0] is not (1 or 2) || header[1] != 0 || header[2] != 0 || header[3] != 0)
            {
                throw new InvalidDataException("Docker returned an invalid multiplexed log header.");
            }

            _streamKind = header[0];
            _remaining = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
            if (_remaining < 0 || _remaining > MaximumFrameBytes)
            {
                throw new InvalidDataException("Docker returned an invalid multiplexed log frame length.");
            }

            return true;
        }

        private async Task DiscardCurrentFrameAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[Math.Min(_remaining, 8192)];
            while (_remaining > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, _remaining)), cancellationToken);
                if (read == 0)
                {
                    throw new EndOfStreamException("Docker log stream ended inside a stderr frame.");
                }

                _remaining -= read;
            }
        }
    }
}

internal sealed record RuntimeContainerIsolation(string User, IReadOnlyDictionary<string, string> Tmpfs)
{
    public static RuntimeWorkspaceOwner ResolveWorkspaceOwner(RuntimeContainerIsolationKind kind) => kind switch
    {
        RuntimeContainerIsolationKind.Standard => new("1654:1654", 1654, 1654),
        RuntimeContainerIsolationKind.WineRoot => new("0:0", 0, 0),
        RuntimeContainerIsolationKind.WineNonRoot => new("1654:1654", 1654, 1654),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported runtime isolation kind.")
    };

    public static RuntimeContainerIsolation Resolve(RuntimeContainerIsolationKind kind, RuntimeSecurityPolicyOptions securityPolicy, string? winePrefixPath = null)
    {
        ArgumentNullException.ThrowIfNull(securityPolicy);
        return kind switch
        {
            RuntimeContainerIsolationKind.Standard => new(
                "1654:1654",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["/tmp"] =
                        $"rw,noexec,nosuid,nodev,size={securityPolicy.TmpfsBytes},uid=1654,gid=1654,mode=0700"
                }),
            RuntimeContainerIsolationKind.WineRoot => ResolveWineRoot(winePrefixPath),
            RuntimeContainerIsolationKind.WineNonRoot => ResolveWineNonRoot(securityPolicy, winePrefixPath),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported runtime isolation kind.")
        };
    }

    private static RuntimeContainerIsolation ResolveWineRoot(string? winePrefixPath)
    {
        ValidateWinePrefix(winePrefixPath);

        return new RuntimeContainerIsolation(
            "0:0",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["/tmp"] = "rw,exec,nosuid,nodev,size=64m",
                [$"{winePrefixPath!}/drive_c/users/root/Temp"] =
                    "rw,exec,nosuid,nodev,size=256m"
            });
    }

    private static RuntimeContainerIsolation ResolveWineNonRoot(RuntimeSecurityPolicyOptions securityPolicy, string? winePrefixPath)
    {
        ValidateWinePrefix(winePrefixPath);
        return new RuntimeContainerIsolation(
            "1654:1654",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["/tmp"] =
                    $"rw,exec,nosuid,nodev,size={securityPolicy.TmpfsBytes},uid=0,gid=0,mode=1777"
            });
    }

    private static void ValidateWinePrefix(string? winePrefixPath)
    {
        if (string.IsNullOrWhiteSpace(winePrefixPath) || !winePrefixPath.StartsWith("/opt/", StringComparison.Ordinal) || winePrefixPath.EndsWith('/') || winePrefixPath.Contains("..", StringComparison.Ordinal) || winePrefixPath.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not ('/' or '-' or '_' or '.')))
        {
            throw new InvalidOperationException("Wine isolation requires a safe absolute prefix below /opt.");
        }
    }
}

internal readonly record struct RuntimeWorkspaceOwner(string User, int Uid, int Gid);

public sealed class DockerEngineException(string message) : Exception(message);
