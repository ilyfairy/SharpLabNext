using System.Globalization;
using SharpLabNext.ArtifactWorker.Sdk;

namespace SharpLabNext.Worker.Artifacts.JSIL;

internal sealed record JsilReferenceSet(string Id, string TargetFramework, string Path);

internal sealed record JsilWorkerSettings(
    string ReleaseId,
    string WorkerImageId,
    string Version,
    string Commit,
    string ArtifactStoreBaseUrl,
    string WorkRoot,
    string MonoPath,
    string CompilerPath,
    int MaximumProcessOutputBytes,
    long MaximumProcessWorkingSetBytes,
    TimeSpan ArtifactTimeToLive,
    IReadOnlyDictionary<string, JsilReferenceSet> ReferenceSets)
{
    public static JsilWorkerSettings FromConfiguration(IConfiguration configuration, ArtifactWorkerCapabilityManifest manifest)
    {
        var section = configuration.GetSection("Jsil");
        var referenceSets = configuration.GetSection("ReferenceSets").GetChildren().Select(item => new JsilReferenceSet(item.Key, Required(item["TargetFramework"], $"ReferenceSets:{item.Key}:TargetFramework"), Path.GetFullPath(Required(item["Path"], $"ReferenceSets:{item.Key}:Path")))).ToDictionary(static item => item.Id, StringComparer.Ordinal);
        if (referenceSets.Count == 0) throw new InvalidOperationException("At least one JSIL reference set must be configured.");

        var maximumProcessOutputBytes = PositiveInt(section["MaximumProcessOutputBytes"], 64 * 1024, "Jsil:MaximumProcessOutputBytes");
        if (maximumProcessOutputBytes > manifest.Limits.MaximumOutputArtifactBytes) throw new InvalidOperationException("JSIL process output capture exceeds the artifact output limit.");

        return new JsilWorkerSettings(
            Required(section["ReleaseId"], "Jsil:ReleaseId"),
            Required(section["WorkerImageId"], "Jsil:WorkerImageId"),
            Required(section["Version"], "Jsil:Version"),
            Required(section["Commit"], "Jsil:Commit"),
            Required(configuration["ArtifactStore:BaseUrl"], "ArtifactStore:BaseUrl"),
            Path.GetFullPath(Required(section["WorkRoot"], "Jsil:WorkRoot")),
            Path.GetFullPath(Required(section["MonoPath"], "Jsil:MonoPath")),
            Path.GetFullPath(Required(section["CompilerPath"], "Jsil:CompilerPath")),
            maximumProcessOutputBytes,
            PositiveLong(section["MaximumProcessWorkingSetBytes"], 512L * 1024 * 1024, "Jsil:MaximumProcessWorkingSetBytes"),
            TimeSpan.FromMinutes(PositiveInt(section["ArtifactTimeToLiveMinutes"], 60, "Jsil:ArtifactTimeToLiveMinutes")),
            referenceSets);
    }

    public JsilReferenceSet GetReferenceSet(string id, string targetFramework)
    {
        if (!ReferenceSets.TryGetValue(id, out var referenceSet) || !StringComparer.Ordinal.Equals(referenceSet.TargetFramework, targetFramework))
        {
            throw new ArtifactWorkerIncompatibleArtifactException("JSIL does not support this artifact reference set or target framework.");
        }
        return referenceSet;
    }

    private static string Required(string? value, string key) =>
        !string.IsNullOrWhiteSpace(value)
            ? value : throw new InvalidOperationException($"Configuration value '{key}' is required.");

    private static int PositiveInt(string? value, int fallback, string key)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) || result <= 0)
            throw new InvalidOperationException($"Configuration value '{key}' must be a positive integer.");
        return result;
    }

    private static long PositiveLong(string? value, long fallback, string key)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) || result <= 0)
            throw new InvalidOperationException($"Configuration value '{key}' must be a positive integer.");
        return result;
    }
}
