using System.Globalization;
using SharpLabNext.Contracts;
using SharpLabNext.WorkerHost;

namespace SharpLabNext.Worker.PeachPie;

public static class PeachPieToolchain
{
    public const string LanguageId = "php";
    public const string ToolchainId = "peachpie-stable";
    public const string ArtifactFormat = "dotnet-managed-pe-v1";
    public const string AssemblyName = "SharpLabNext.User";
    public const string CompilerVersion = "1.1.13";
    public const string CompilerCommit = "608bf30cf3f43f97e32825076a2cfdaa25043e50";
    public const string RuntimeAssemblyName = "Peachpie.Runtime.dll";
    public const string LibraryAssemblyName = "Peachpie.Library.dll";
    public const string NativeRuntimeIdentifier = "linux-x64";
    public const string MonoUnixNativeLibraryName = "libMono.Unix.so";
    public const string MonoUnixNativePackagePath = "runtimes/linux-x64/native/libMono.Unix.so";
    public const string MonoUnixNativeArtifactPath = MonoUnixNativeLibraryName;
    public const string MonoUnixNativeSha256 = "ce99f51103806110793e8ac8f24a9218589ad59d7cfdcf41a999bf6835f406b5";
}

public sealed record PeachPieReferenceSetDefinition(string Id, string Path, string TargetFramework, string FrameworkVersion, string? Digest = null, string? AttestationPath = null);

public sealed record PeachPieWorkerIdentity(string ReleaseId, string CompilerVersion, string CompilerCommit, string WorkerImageId)
{
    public BuildIdentity CreateBuildIdentity(string referenceSetId) => new(ReleaseId, PeachPieToolchain.LanguageId, PeachPieToolchain.ToolchainId, CompilerVersion, CompilerCommit, referenceSetId, WorkerImageId);
}

public sealed record PeachPieWorkerSettings(
    PeachPieWorkerIdentity Identity,
    CompilerProcessIsolationOptions BuildProcess,
    string WorkRoot,
    string RuntimeAssemblyPath,
    string LibraryAssemblyPath,
    string MonoUnixNativeLibraryPath,
    IReadOnlyList<PeachPieReferenceSetDefinition> ReferenceSets)
{
    public static PeachPieWorkerSettings FromConfiguration(IConfiguration configuration)
    {
        var worker = configuration.GetSection("PeachPie");
        var compilerVersion = Pinned(worker["CompilerVersion"], PeachPieToolchain.CompilerVersion, "PeachPie:CompilerVersion");
        var compilerCommit = Pinned(worker["CompilerCommit"], PeachPieToolchain.CompilerCommit, "PeachPie:CompilerCommit");
        var processDefaults = CompilerProcessIsolationOptions.Default;
        var process = worker.GetSection("BuildProcess");
        var buildProcess = new CompilerProcessIsolationOptions(
            Boolean(process["Enabled"], processDefaults.Enabled, "BuildProcess:Enabled"),
            PositiveInt(process["MaximumConcurrentProcesses"], processDefaults.MaximumConcurrentProcesses, "BuildProcess:MaximumConcurrentProcesses"),
            PositiveLong(process["MaximumWorkingSetBytes"], processDefaults.MaximumWorkingSetBytes, "BuildProcess:MaximumWorkingSetBytes"),
            PositiveInt(process["MaximumRequestBytes"], processDefaults.MaximumRequestBytes, "BuildProcess:MaximumRequestBytes"),
            PositiveInt(process["MaximumResponseBytes"], processDefaults.MaximumResponseBytes, "BuildProcess:MaximumResponseBytes"),
            PositiveInt(process["MaximumStandardErrorBytes"], processDefaults.MaximumStandardErrorBytes, "BuildProcess:MaximumStandardErrorBytes"),
            PositiveInt(process["MemoryPollIntervalMilliseconds"], processDefaults.MemoryPollIntervalMilliseconds, "BuildProcess:MemoryPollIntervalMilliseconds"));
        buildProcess.Validate();

        var referenceSets = configuration.GetSection("ReferenceSets").GetChildren().Select(section => new PeachPieReferenceSetDefinition(section.Key, Required(section["Path"], $"ReferenceSets:{section.Key}:Path"), Required(section["TargetFramework"], $"ReferenceSets:{section.Key}:TargetFramework"), Required(section["FrameworkVersion"], $"ReferenceSets:{section.Key}:FrameworkVersion"), section["Digest"], section["AttestationPath"])).OrderBy(static definition => definition.Id, StringComparer.Ordinal).ToArray();
        if (referenceSets.Length == 0)
            throw new InvalidOperationException("At least one PeachPie reference set must be configured.");

        var baseDirectory = AppContext.BaseDirectory;
        var runtimeAssemblyPath = Path.GetFullPath(Path.Combine(baseDirectory, PeachPieToolchain.RuntimeAssemblyName));
        var libraryAssemblyPath = Path.GetFullPath(Path.Combine(baseDirectory, PeachPieToolchain.LibraryAssemblyName));
        var monoUnixNativeLibraryPath = Path.GetFullPath(Path.Combine(baseDirectory, "runtimes", PeachPieToolchain.NativeRuntimeIdentifier, "native", PeachPieToolchain.MonoUnixNativeLibraryName));
        return new PeachPieWorkerSettings(new PeachPieWorkerIdentity(worker["ReleaseId"] ?? "content", compilerVersion, compilerCommit, worker["WorkerImageId"] ?? $"sha256:{new string('0', 64)}"), buildProcess, Path.GetFullPath(Environment.ExpandEnvironmentVariables(worker["WorkRoot"] ?? Path.Combine(Path.GetTempPath(), "SharpLabNext", "peachpie-worker"))), runtimeAssemblyPath, libraryAssemblyPath, monoUnixNativeLibraryPath, referenceSets);
    }

    private static string Pinned(string? configured, string expected, string key)
    {
        if (string.IsNullOrWhiteSpace(configured) || StringComparer.Ordinal.Equals(configured, "__pinned__"))
            return expected;
        if (!StringComparer.Ordinal.Equals(configured, expected))
            throw new InvalidOperationException($"Configuration value '{key}' must be the pinned value '{expected}'.");
        return configured;
    }

    private static string Required(string? value, string key) =>
        !string.IsNullOrWhiteSpace(value)
            ? value : throw new InvalidOperationException($"Configuration value '{key}' is required.");

    private static int PositiveInt(string? value, int fallback, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            throw new InvalidOperationException($"Configuration value 'PeachPie:{key}' must be a positive integer.");
        return parsed;
    }

    private static long PositiveLong(string? value, long fallback, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            throw new InvalidOperationException($"Configuration value 'PeachPie:{key}' must be a positive integer.");
        return parsed;
    }

    private static bool Boolean(string? value, bool fallback, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        if (!bool.TryParse(value, out var parsed))
            throw new InvalidOperationException($"Configuration value 'PeachPie:{key}' must be a boolean.");
        return parsed;
    }
}
