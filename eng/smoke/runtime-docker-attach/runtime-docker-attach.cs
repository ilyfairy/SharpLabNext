#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property EnableTrimAnalyzer=false
#:property EnableAotAnalyzer=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:project ../../../src/Supervisor/SharpLabNext.RuntimeSupervisor/SharpLabNext.RuntimeSupervisor.csproj

using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SharpLabNext.RuntimeSupervisor;

const string AttachMarker = "SLN-DOCKER-ATTACH-8M-V1\n";
const int PayloadBytes = 8 * 1024 * 1024;

if (!OperatingSystem.IsLinux())
{
    Console.WriteLine("SKIP Docker pre-start attach smoke requires the production Linux Unix-socket transport.");
    return;
}

var runtimeImage = args.Length > 0
    ? args[0] : Environment.GetEnvironmentVariable("SHARPLABNEXT_E2E_RUNTIME_IMAGE");
if (string.IsNullOrWhiteSpace(runtimeImage))
    throw new InvalidOperationException("An immutable runtime image reference is required.");

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
var supervisorOptions = Options.Create(new RuntimeSupervisorOptions { DockerSocketPath = "/var/run/docker.sock" });
using var docker = new DockerEngineClient(supervisorOptions, new RuntimeSandboxPolicy(supervisorOptions, new SmokeHostEnvironment(ResolveSupervisorRoot())));
if (!await docker.PingAsync(timeout.Token))
    throw new InvalidOperationException("Production Docker client could not reach the daemon.");
if (runtimeImage.Contains('@', StringComparison.Ordinal))
    _ = await docker.InspectImageAsync(runtimeImage, timeout.Token);
else
    Require(IsCanonicalImageId(runtimeImage), "The runtime image must be a canonical local image ID or repository digest.");

var containerName = $"sln-e2e-attach-{Guid.NewGuid():N}";
var workspaceVolume = $"sln-e2e-attach-workspace-{Guid.NewGuid():N}";
string? containerId = null;
try
{
    containerId = await docker.CreateContainerAsync(
        new RuntimeContainerSpec(
            containerName,
            $"attach-{Guid.NewGuid():N}",
            "runtime-attach-smoke-v1",
            runtimeImage,
            [
                "-c",
                "printf 'SLN-DOCKER-ATTACH-8M-V1\\n'; dd if=/dev/zero bs=65536 count=128 2>/dev/null | tr '\\000' x"
            ],
            new Dictionary<string, string>(StringComparer.Ordinal),
            new RuntimeSecurityPolicyOptions { Id = "runtime-attach-smoke", MemoryBytes = 64 * 1024 * 1024, NanoCpus = 1_000_000_000, PidsLimit = 32, MaximumDurationSeconds = 30, MaximumArtifactBytes = 1024 * 1024, MaximumOutputBytes = PayloadBytes + AttachMarker.Length, TmpfsBytes = 1024 * 1024 },
            "com.sharplabnext.runtime-job",
            "runtime-attach-smoke-v1",
            workspaceVolume,
            Entrypoint: ["/bin/sh"]),
        timeout.Token);

    await using var output = await docker.AttachContainerOutputAsync(containerId, timeout.Token);
    await docker.StartContainerAsync(containerId, timeout.Token);
    using var captured = new MemoryStream(PayloadBytes + AttachMarker.Length);
    await output.CopyToAsync(captured, timeout.Token);
    var exit = await docker.WaitContainerAsync(containerId, timeout.Token);
    Require(exit.StatusCode == 0 && !exit.OomKilled, $"Attach smoke container exited with {exit.StatusCode}.");

    var bytes = captured.ToArray();
    var marker = Encoding.ASCII.GetBytes(AttachMarker);
    Require(bytes.Length == marker.Length + PayloadBytes, "Pre-start attach did not capture the exact output length.");
    Require(bytes.AsSpan().StartsWith(marker), "Pre-start attach lost the first Docker stdout bytes.");
    Require(bytes.AsSpan(marker.Length).IndexOfAnyExcept((byte)'x') < 0, "Pre-start attach changed the Docker stdout payload.");

    Console.WriteLine("PASS Docker pre-start attach captured marker plus 8 MiB exactly.");
}
finally
{
    using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    if (containerId is not null)
    {
        try
        {
            await docker.RemoveContainerAsync(containerId, cleanupTimeout.Token);
        }
        finally
        {
            await docker.RemoveWorkspaceVolumeAsync(workspaceVolume, cleanupTimeout.Token);
        }
    }
    else
    {
        await docker.RemoveWorkspaceVolumeAsync(workspaceVolume, cleanupTimeout.Token);
    }
}

static string ResolveSupervisorRoot([CallerFilePath] string sourcePath = "")
{
    var configured = Environment.GetEnvironmentVariable("SHARPLABNEXT_SOURCE_SUPERVISOR_ROOT");
    if (!string.IsNullOrWhiteSpace(configured))
        return Path.GetFullPath(configured);
    return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath) ?? throw new InvalidOperationException("Smoke source path is unavailable."), "..", "..", "..", "src", "Supervisor", "SharpLabNext.RuntimeSupervisor"));
}

static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

static bool IsCanonicalImageId(string value)
{
    if (value.Length != "sha256:".Length + 64 || !value.StartsWith("sha256:", StringComparison.Ordinal))
    {
        return false;
    }

    foreach (var character in value.AsSpan("sha256:".Length))
    {
        if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            return false;
    }
    return true;
}

sealed class SmokeHostEnvironment(string contentRootPath) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ApplicationName { get; set; } = "SharpLabNext.RuntimeAttachSmoke";
    public string ContentRootPath { get; set; } = contentRootPath;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
