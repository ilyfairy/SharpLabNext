using System.Globalization;
using SharpLabNext.ArtifactWorker.Sdk;

namespace SharpLabNext.Worker.Artifacts.ConstGenerics;

internal sealed record ConstGenericsArtifactWorkerSettings(
    string ReleaseId,
    string WorkerImageId,
    string ArtifactStoreBaseUrl,
    string WorkRoot,
    string ProcessorDotNetHostPath,
    string ProcessorAssemblyPath,
    string ReferenceRoot,
    string RuntimeReferenceRoot,
    string FrameworkVersion,
    string SystemModuleName,
    int MaximumArtifactFiles,
    long MaximumAssemblyBytes,
    long MaximumPortablePdbBytes,
    long MaximumProcessorWorkingSetBytes,
    int MaximumProcessOutputBytes,
    TimeSpan ArtifactTimeToLive)
{
    public static ConstGenericsArtifactWorkerSettings FromConfiguration(
        IConfiguration configuration,
        ArtifactWorkerCapabilityManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(manifest);
        var section = configuration.GetSection("ConstGenericsArtifactWorker");
        var workRoot = section["WorkRoot"];
        if (string.IsNullOrWhiteSpace(workRoot))
            workRoot = Path.Combine(Path.GetTempPath(), "SharpLabNext", "artifacts-const-generics");
        var processorPath = section["ProcessorAssemblyPath"];
        if (string.IsNullOrWhiteSpace(processorPath))
            processorPath = "SharpLabNext.Worker.Artifacts.ConstGenerics.Processor.dll";
        if (!Path.IsPathFullyQualified(processorPath))
            processorPath = Path.Combine(AppContext.BaseDirectory, processorPath);
        var referenceRoot = section["ReferenceRoot"];
        if (string.IsNullOrWhiteSpace(referenceRoot))
            referenceRoot = Path.Combine(AppContext.BaseDirectory, "reference-sets", "const-generics-ref");
        var runtimeReferenceRoot = section["RuntimeReferenceRoot"];
        if (string.IsNullOrWhiteSpace(runtimeReferenceRoot))
            runtimeReferenceRoot = referenceRoot;

        var maximumAssemblyBytes = PositiveLong(
            section["MaximumAssemblyBytes"],
            32L * 1024 * 1024,
            "ConstGenericsArtifactWorker:MaximumAssemblyBytes");
        var maximumPdbBytes = PositiveLong(
            section["MaximumPortablePdbBytes"],
            16L * 1024 * 1024,
            "ConstGenericsArtifactWorker:MaximumPortablePdbBytes");
        if (maximumAssemblyBytes > manifest.Limits.MaximumInputArtifactBytes ||
            maximumPdbBytes > manifest.Limits.MaximumInputArtifactBytes)
        {
            throw new InvalidOperationException(
                "ConstGenerics artifact file limits cannot exceed the capability manifest input limit.");
        }

        return new ConstGenericsArtifactWorkerSettings(
            Required(section["ReleaseId"], "ConstGenericsArtifactWorker:ReleaseId"),
            Required(section["WorkerImageId"], "ConstGenericsArtifactWorker:WorkerImageId"),
            Required(configuration["ArtifactStore:BaseUrl"], "ArtifactStore:BaseUrl"),
            Path.GetFullPath(workRoot),
            section["ProcessorDotNetHostPath"] ??
                Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
                "dotnet",
            Path.GetFullPath(processorPath),
            Path.GetFullPath(referenceRoot),
            Path.GetFullPath(runtimeReferenceRoot),
            Required(section["FrameworkVersion"], "ConstGenericsArtifactWorker:FrameworkVersion"),
            Required(section["SystemModuleName"], "ConstGenericsArtifactWorker:SystemModuleName"),
            PositiveInt(section["MaximumArtifactFiles"], 32, "ConstGenericsArtifactWorker:MaximumArtifactFiles"),
            maximumAssemblyBytes,
            maximumPdbBytes,
            PositiveLong(
                section["MaximumProcessorWorkingSetBytes"],
                512L * 1024 * 1024,
                "ConstGenericsArtifactWorker:MaximumProcessorWorkingSetBytes"),
            PositiveInt(
                section["MaximumProcessOutputBytes"],
                64 * 1024,
                "ConstGenericsArtifactWorker:MaximumProcessOutputBytes"),
            TimeSpan.FromMinutes(PositiveInt(
                section["ArtifactTimeToLiveMinutes"],
                60,
                "ConstGenericsArtifactWorker:ArtifactTimeToLiveMinutes")));
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
