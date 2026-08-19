using System.Globalization;
using SharpLabNext.ArtifactWorker.Sdk;
using SharpLabNext.Worker.IL.Compiler;

namespace SharpLabNext.Worker.Artifacts.ILAssembler;

public sealed record IlAssemblerReferenceSet(
    string Id,
    string TargetFramework,
    string FrameworkName,
    string FrameworkVersion,
    string RuntimeFamily,
    string Architecture);

public sealed record IlAssemblerWorkerSettings(
    string ReleaseId,
    string WorkerImageId,
    string CompilerVersion,
    string ArtifactStoreBaseUrl,
    string WorkRoot,
    string DotNetHostPath,
    string CompilerAssemblyPath,
    int MaxProcessOutputBytes,
    int MaxCompilerResponseBytes,
    long MaxProcessWorkingSetBytes,
    TimeSpan ArtifactTimeToLive,
    IReadOnlyDictionary<string, IlAssemblerReferenceSet> ReferenceSets)
{
    public static IlAssemblerWorkerSettings FromConfiguration(
        IConfiguration configuration,
        ArtifactWorkerCapabilityManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(manifest);
        var section = configuration.GetSection("ArtifactAssembler");
        var configuredCompilerVersion = section["CompilerVersion"];
        var compilerVersion = string.IsNullOrWhiteSpace(configuredCompilerVersion) ||
                              string.Equals(configuredCompilerVersion, "__pinned__", StringComparison.Ordinal)
            ? IlCompilerProtocol.PackageVersion
            : configuredCompilerVersion;
        if (!string.Equals(compilerVersion, IlCompilerProtocol.PackageVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("Configured IL assembler version does not match the pinned compiler protocol.");

        var workRoot = section["WorkRoot"];
        if (string.IsNullOrWhiteSpace(workRoot))
            workRoot = Path.Combine(Path.GetTempPath(), "SharpLabNext", "artifact-il-assembler");
        var compilerPath = section["CompilerAssemblyPath"];
        if (string.IsNullOrWhiteSpace(compilerPath))
            compilerPath = "SharpLabNext.Worker.IL.Compiler.dll";
        if (!Path.IsPathRooted(compilerPath))
            compilerPath = Path.Combine(AppContext.BaseDirectory, compilerPath);
        var dotnetHost = section["DotNetHostPath"];
        if (string.IsNullOrWhiteSpace(dotnetHost))
            dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

        var responseLimit = PositiveInt(
            section["MaxCompilerResponseBytes"],
            IlCompilerProtocol.MaxResponseBytes,
            "ArtifactAssembler:MaxCompilerResponseBytes");
        if (responseLimit > IlCompilerProtocol.MaxResponseBytes)
            throw new InvalidOperationException("The compiler response limit exceeds the child-process protocol limit.");
        if (manifest.Limits.MaximumOutputArtifactBytes > IlCompilerProtocol.MaxPeBytes)
            throw new InvalidOperationException("The artifact output limit exceeds the child-process protocol limit.");

        var referenceSets = configuration.GetSection("ReferenceSets")
            .GetChildren()
            .Select(item => new IlAssemblerReferenceSet(
                item.Key,
                Required(item["TargetFramework"], $"ReferenceSets:{item.Key}:TargetFramework"),
                Required(item["FrameworkName"], $"ReferenceSets:{item.Key}:FrameworkName"),
                Required(item["FrameworkVersion"], $"ReferenceSets:{item.Key}:FrameworkVersion"),
                Required(item["RuntimeFamily"], $"ReferenceSets:{item.Key}:RuntimeFamily"),
                Required(item["Architecture"], $"ReferenceSets:{item.Key}:Architecture")))
            .ToDictionary(static item => item.Id, StringComparer.Ordinal);
        if (referenceSets.Count == 0)
            throw new InvalidOperationException("At least one IL assembler reference set must be configured.");

        return new IlAssemblerWorkerSettings(
            Required(section["ReleaseId"], "ArtifactAssembler:ReleaseId"),
            Required(section["WorkerImageId"], "ArtifactAssembler:WorkerImageId"),
            compilerVersion,
            Required(configuration["ArtifactStore:BaseUrl"], "ArtifactStore:BaseUrl"),
            Path.GetFullPath(workRoot),
            dotnetHost,
            Path.GetFullPath(compilerPath),
            PositiveInt(section["MaxProcessOutputBytes"], 64 * 1024, "ArtifactAssembler:MaxProcessOutputBytes"),
            responseLimit,
            PositiveLong(section["MaxProcessWorkingSetBytes"], 512L * 1024 * 1024, "ArtifactAssembler:MaxProcessWorkingSetBytes"),
            TimeSpan.FromMinutes(PositiveInt(
                section["ArtifactTimeToLiveMinutes"],
                60,
                "ArtifactAssembler:ArtifactTimeToLiveMinutes")),
            referenceSets);
    }

    public IlAssemblerReferenceSet GetReferenceSet(string id, string targetFramework)
    {
        if (!ReferenceSets.TryGetValue(id, out var referenceSet) ||
            !string.Equals(referenceSet.TargetFramework, targetFramework, StringComparison.Ordinal))
        {
            throw new ArtifactWorkerIncompatibleArtifactException(
                "The CIL artifact references an unsupported reference set or target framework.");
        }
        return referenceSet;
    }

    private static string Required(string? value, string key) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Configuration value '{key}' is required.");

    private static int PositiveInt(string? value, int fallback, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) || result <= 0)
            throw new InvalidOperationException($"Configuration value '{key}' must be a positive integer.");
        return result;
    }

    private static long PositiveLong(string? value, long fallback, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) || result <= 0)
            throw new InvalidOperationException($"Configuration value '{key}' must be a positive integer.");
        return result;
    }
}
