using System.Globalization;
using SharpLabNext.ArtifactProcessing.Protocol;

namespace SharpLabNext.ArtifactWorker;

internal sealed record ArtifactWorkerIdentity(string ReleaseId, string WorkerImageId, string ProcessorId, string IlSpyVersion, string IlVerificationVersion);

internal sealed record ArtifactProcessorLimits(
    int MaxConcurrentJobs,
    int MaxArtifactFiles,
    long MaxArtifactBytes,
    long MaxAssemblyBytes,
    long MaxPortablePdbBytes,
    int MaxOutputCharacters,
    long MaxOutputBytes,
    int MaxFindings,
    int MaxLinkedRanges,
    int MaxProcessorMilliseconds,
    long MaxProcessorMemoryBytes,
    int MaxProcessorResponseBytes,
    int MaxRetainedOperations)
{
    public static ArtifactProcessorLimits Default { get; } = new(
        MaxConcurrentJobs: 2,
        MaxArtifactFiles: 128,
        MaxArtifactBytes: 32L * 1024 * 1024,
        MaxAssemblyBytes: 16L * 1024 * 1024,
        MaxPortablePdbBytes: 8L * 1024 * 1024,
        MaxOutputCharacters: 1_000_000,
        MaxOutputBytes: 4L * 1024 * 1024,
        MaxFindings: 1_000,
        MaxLinkedRanges: 20_000,
        MaxProcessorMilliseconds: 15_000,
        MaxProcessorMemoryBytes: 512L * 1024 * 1024,
        MaxProcessorResponseBytes: 2 * 1024 * 1024,
        MaxRetainedOperations: 512);
}

internal sealed record ArtifactReferenceSet(string Id, IReadOnlyList<string> Paths, string? SystemModuleName);

internal static class ArtifactReferenceSetConfigurationContract
{
    public static IReadOnlyDictionary<string, string> RequiredSystemModules { get; } =
        CreateRequiredSystemModules();

    public static void Validate(IReadOnlyDictionary<string, ArtifactReferenceSet> referenceSets)
    {
        var missing = RequiredSystemModules.Keys.Except(referenceSets.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var unexpected = referenceSets.Keys.Except(RequiredSystemModules.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0 || unexpected.Length > 0)
        {
            throw new InvalidOperationException("ArtifactWorker:ReferenceSets must contain exactly the approved reference closure. " + $"Missing: [{string.Join(", ", missing)}]; unexpected: [{string.Join(", ", unexpected)}].");
        }

        foreach (var (id, systemModuleName) in RequiredSystemModules)
        {
            var configured = referenceSets[id];
            if (configured.Paths.Count == 0 || !StringComparer.Ordinal.Equals(configured.SystemModuleName, systemModuleName))
            {
                throw new InvalidOperationException($"ArtifactWorker:ReferenceSets:{id} must define at least one path and system module '{systemModuleName}'.");
            }
        }
    }

    private static Dictionary<string, string> CreateRequiredSystemModules()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["net10-ref"] = "System.Runtime",
            ["net11-preview-ref"] = "System.Runtime",
            [ArtifactFormatContract.JSharpReferenceSet] = "mscorlib"
        };
        foreach (var id in NetFxManagedReferenceSets.ById.Keys)
            result.Add(id, "mscorlib");
        return result;
    }
}

internal sealed record ArtifactWorkerSettings(
    ArtifactWorkerIdentity Identity,
    ArtifactProcessorLimits Limits,
    string ArtifactStoreBaseUrl,
    string ProcessorAssemblyPath,
    string DotNetHostPath,
    string WorkRoot,
    IReadOnlyDictionary<string, ArtifactReferenceSet> ReferenceSets,
    IReadOnlySet<string> VerificationProfiles)
{
    public static ArtifactWorkerSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var worker = configuration.GetSection("ArtifactWorker");
        var identity = new ArtifactWorkerIdentity(Required(worker["ReleaseId"], "ArtifactWorker:ReleaseId", "content"), Required(worker["WorkerImageId"], "ArtifactWorker:WorkerImageId", "content"), "artifacts-default", ProcessorProtocol.IlSpyVersion, ProcessorProtocol.IlVerificationVersion);

        var defaults = ArtifactProcessorLimits.Default;
        var limits = worker.GetSection("Limits");
        var configuredLimits = new ArtifactProcessorLimits(
            PositiveInt(limits["MaxConcurrentJobs"], defaults.MaxConcurrentJobs),
            PositiveInt(limits["MaxArtifactFiles"], defaults.MaxArtifactFiles),
            PositiveLong(limits["MaxArtifactBytes"], defaults.MaxArtifactBytes),
            PositiveLong(limits["MaxAssemblyBytes"], defaults.MaxAssemblyBytes),
            PositiveLong(limits["MaxPortablePdbBytes"], defaults.MaxPortablePdbBytes),
            PositiveInt(limits["MaxOutputCharacters"], defaults.MaxOutputCharacters),
            PositiveLong(limits["MaxOutputBytes"], defaults.MaxOutputBytes),
            PositiveInt(limits["MaxFindings"], defaults.MaxFindings),
            PositiveInt(limits["MaxLinkedRanges"], defaults.MaxLinkedRanges),
            PositiveInt(limits["MaxProcessorMilliseconds"], defaults.MaxProcessorMilliseconds),
            PositiveLong(limits["MaxProcessorMemoryBytes"], defaults.MaxProcessorMemoryBytes),
            PositiveInt(limits["MaxProcessorResponseBytes"], defaults.MaxProcessorResponseBytes),
            PositiveInt(limits["MaxRetainedOperations"], defaults.MaxRetainedOperations));

        var processorPath = worker["ProcessorAssemblyPath"];
        if (string.IsNullOrWhiteSpace(processorPath))
        {
            processorPath = Path.Combine(AppContext.BaseDirectory, "SharpLabNext.Worker.Artifacts.Default.Processor.dll");
        }
        processorPath = Path.GetFullPath(processorPath);

        var workRoot = worker["WorkRoot"];
        if (string.IsNullOrWhiteSpace(workRoot))
            workRoot = Path.Combine(Path.GetTempPath(), "SharpLabNext", "artifact-worker");
        workRoot = Path.GetFullPath(workRoot);

        var dotNetHost = worker["DotNetHostPath"];
        if (string.IsNullOrWhiteSpace(dotNetHost))
            dotNetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

        var referenceSets = worker.GetSection("ReferenceSets").GetChildren().Select(section => new ArtifactReferenceSet(section.Key, section.GetSection("Paths").Get<string[]>()?.Where(static path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToArray() ?? [], section["SystemModuleName"])).ToDictionary(static item => item.Id, StringComparer.Ordinal);
        ArtifactReferenceSetConfigurationContract.Validate(referenceSets);

        var verificationProfiles = worker.GetSection("VerificationProfiles").Get<string[]>()?.Where(static value => !string.IsNullOrWhiteSpace(value)).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(["default"], StringComparer.Ordinal);

        return new ArtifactWorkerSettings(
            identity,
            configuredLimits,
            Required(
                configuration["ArtifactStore:BaseUrl"],
                "ArtifactStore:BaseUrl",
                "http://artifact-store:8080"),
            processorPath,
            dotNetHost,
            workRoot,
            referenceSets,
            verificationProfiles);
    }

    private static string Required(string? value, string key, string? fallback = null) =>
        !string.IsNullOrWhiteSpace(value)
            ? value : fallback ?? throw new InvalidOperationException($"Configuration value '{key}' is required.");

    private static int PositiveInt(string? value, int fallback) =>
        string.IsNullOrWhiteSpace(value)
            ? fallback : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed : throw new InvalidOperationException("Artifact worker limits must be positive integers.");

    private static long PositiveLong(string? value, long fallback) =>
        string.IsNullOrWhiteSpace(value)
            ? fallback : long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed : throw new InvalidOperationException("Artifact worker limits must be positive integers.");
}
