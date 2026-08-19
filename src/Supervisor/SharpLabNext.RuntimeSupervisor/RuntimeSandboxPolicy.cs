using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace SharpLabNext.RuntimeSupervisor;

public sealed class RuntimeSandboxOptions
{
    public string PolicyId { get; set; } = "runtime-linux-v1";

    public string SeccompProfilePath { get; set; } = "security/runtime-job-seccomp.v1.json";

    public string SeccompProfileSha256 { get; set; } =
        "sha256:01536f1d1df938ae611eba20d6349e0de7a99b6ecdee1549427a0b01b8301e28";

    public string? AppArmorProfile { get; set; }

    public long OpenFilesSoftLimit { get; set; } = 256;

    public long OpenFilesHardLimit { get; set; } = 256;
}

public sealed class RuntimeSandboxPolicy
{
    private const int MaximumSeccompProfileBytes = 1024 * 1024;
    private const long WineOpenFilesMinimum = 512;

    public RuntimeSandboxPolicy(
        IOptions<RuntimeSupervisorOptions> configuredOptions,
        IHostEnvironment environment)
        : this(Load(configuredOptions.Value.Sandbox, environment.ContentRootPath))
    {
    }

    private RuntimeSandboxPolicy(RuntimeSandboxPolicy loaded)
    {
        PolicyId = loaded.PolicyId;
        SeccompProfileSha256 = loaded.SeccompProfileSha256;
        SecurityOptions = loaded.SecurityOptions;
        OpenFilesSoftLimit = loaded.OpenFilesSoftLimit;
        OpenFilesHardLimit = loaded.OpenFilesHardLimit;
    }

    private RuntimeSandboxPolicy(
        string policyId,
        string seccompProfileSha256,
        IReadOnlyList<string> securityOptions,
        long openFilesSoftLimit,
        long openFilesHardLimit)
    {
        PolicyId = policyId;
        SeccompProfileSha256 = seccompProfileSha256;
        SecurityOptions = securityOptions;
        OpenFilesSoftLimit = openFilesSoftLimit;
        OpenFilesHardLimit = openFilesHardLimit;
    }

    public string PolicyId { get; }

    public string SeccompProfileSha256 { get; }

    public IReadOnlyList<string> SecurityOptions { get; }

    public long OpenFilesSoftLimit { get; }

    public long OpenFilesHardLimit { get; }

    public IReadOnlyList<IReadOnlyDictionary<string, object>> CreateUlimits(
        RuntimeContainerIsolationKind isolationKind = RuntimeContainerIsolationKind.Standard)
    {
        var wineIsolation = isolationKind is
            RuntimeContainerIsolationKind.WineRoot or
            RuntimeContainerIsolationKind.WineNonRoot;
        var openFilesSoftLimit = wineIsolation
            ? Math.Max(OpenFilesSoftLimit, WineOpenFilesMinimum)
            : OpenFilesSoftLimit;
        var openFilesHardLimit = wineIsolation
            ? Math.Max(OpenFilesHardLimit, WineOpenFilesMinimum)
            : OpenFilesHardLimit;
        return
    [
        new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Name"] = "nofile",
            ["Soft"] = openFilesSoftLimit,
            ["Hard"] = openFilesHardLimit
        },
        new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Name"] = "core",
            ["Soft"] = 0L,
            ["Hard"] = 0L
        }
    ];
    }

    public static IReadOnlyList<string> ValidateConfiguration(RuntimeSandboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (!IsStableId(options.PolicyId))
            failures.Add("RuntimeSupervisor:Sandbox:PolicyId must be a stable ID.");
        if (string.IsNullOrWhiteSpace(options.SeccompProfilePath) || options.SeccompProfilePath.Contains('\0'))
            failures.Add("RuntimeSupervisor:Sandbox:SeccompProfilePath is required.");
        if (!IsSha256(options.SeccompProfileSha256))
            failures.Add("RuntimeSupervisor:Sandbox:SeccompProfileSha256 must be a lowercase sha256 digest.");
        if (!string.IsNullOrWhiteSpace(options.AppArmorProfile) && !IsStableId(options.AppArmorProfile))
            failures.Add("RuntimeSupervisor:Sandbox:AppArmorProfile must be a stable profile name.");
        if (options.OpenFilesSoftLimit is < 32 or > 4096 ||
            options.OpenFilesHardLimit is < 32 or > 4096 ||
            options.OpenFilesSoftLimit > options.OpenFilesHardLimit)
        {
            failures.Add("RuntimeSupervisor:Sandbox open-file limits are invalid.");
        }
        return failures;
    }

    internal static RuntimeSandboxPolicy Load(RuntimeSandboxOptions options, string contentRootPath)
    {
        var failures = ValidateConfiguration(options);
        if (failures.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures));

        var configuredPath = Environment.ExpandEnvironmentVariables(options.SeccompProfilePath);
        var path = Path.GetFullPath(configuredPath, contentRootPath);
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new InvalidOperationException($"The seccomp profile '{path}' does not exist.");
        if (info.Length is <= 0 or > MaximumSeccompProfileBytes)
            throw new InvalidOperationException("The seccomp profile size is invalid.");

        var bytes = File.ReadAllBytes(path);
        var digest = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(digest),
                Encoding.ASCII.GetBytes(options.SeccompProfileSha256)))
        {
            throw new InvalidOperationException(
                $"The seccomp profile digest '{digest}' does not match the configured identity.");
        }

        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("defaultAction", out var defaultAction) ||
            defaultAction.ValueKind != JsonValueKind.String ||
            defaultAction.GetString() is not ("SCMP_ACT_ERRNO" or "SCMP_ACT_KILL" or "SCMP_ACT_KILL_PROCESS") ||
            !document.RootElement.TryGetProperty("syscalls", out var syscalls) ||
            syscalls.ValueKind != JsonValueKind.Array ||
            syscalls.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("The seccomp profile is not a deny-by-default syscall policy.");
        }

        var securityOptions = new List<string>
        {
            "no-new-privileges:true",
            $"seccomp={Encoding.UTF8.GetString(bytes)}"
        };
        if (!string.IsNullOrWhiteSpace(options.AppArmorProfile))
            securityOptions.Add($"apparmor={options.AppArmorProfile}");

        return new RuntimeSandboxPolicy(
            options.PolicyId,
            digest,
            securityOptions,
            options.OpenFilesSoftLimit,
            options.OpenFilesHardLimit);
    }

    private static bool IsStableId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsSha256(string? value)
    {
        if (value is not { Length: 71 } || !value.StartsWith("sha256:", StringComparison.Ordinal))
            return false;
        return value.AsSpan(7).ToArray().All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }
}
