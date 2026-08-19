using SharpLabNext.Contracts;
using SharpLabNext.WorkerHost;
using System.Text.RegularExpressions;

namespace SharpLabNext.Worker.GSharp;

public static class GSharpToolchain
{
    public const string LanguageId = "gsharp";
    public const string ToolchainId = "gsharp-stable";
    public const string LegacyToolchainId = "gsharp-legacy-0.3.8";
    public const string ArtifactFormat = "dotnet-managed-pe-v1";
    public const string AssemblyName = "SharpLabNext.User";
}

public sealed record GSharpWorkerIdentity(
    string ReleaseId,
    string WorkerImageId);

public sealed record GSharpToolchainProfile(
    string ToolchainId,
    string CompilerVersion,
    string CompilerCommit,
    string CompilerAssemblyPath,
    string LanguageServerAssemblyPath)
{
    public BuildIdentity CreateBuildIdentity(
        GSharpWorkerIdentity workerIdentity,
        string referenceSetId) => new(
        workerIdentity.ReleaseId,
        GSharpToolchain.LanguageId,
        ToolchainId,
        CompilerVersion,
        CompilerCommit,
        referenceSetId,
        workerIdentity.WorkerImageId);
}

public sealed record GSharpProcessLimits(
    int MaximumProcessOutputBytes,
    long MaximumProcessWorkingSetBytes,
    TimeSpan SessionTtl);

public sealed record GSharpReferenceSetDefinition(
    string Id,
    string RootPath,
    string TargetFramework,
    string FrameworkVersion,
    string? Digest,
    string? AttestationPath);

public sealed record GSharpWorkerSettings(
    GSharpWorkerIdentity Identity,
    GSharpProcessLimits ProcessLimits,
    string WorkRoot,
    string DotNetHostPath,
    IReadOnlyDictionary<string, GSharpToolchainProfile> Toolchains,
    IReadOnlyList<GSharpReferenceSetDefinition> ReferenceSets)
{
    public static GSharpWorkerSettings FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("GSharp");
        var toolchains = section.GetSection("Toolchains")
            .GetChildren()
            .Select(static item => new GSharpToolchainProfile(
                item.Key,
                GSharpCompilerIdentity.RequireVersion(
                    item["CompilerVersion"],
                    $"GSharp:Toolchains:{item.Key}:CompilerVersion"),
                GSharpCompilerIdentity.RequireCommit(
                    item["CompilerCommit"],
                    $"GSharp:Toolchains:{item.Key}:CompilerCommit"),
                ConfiguredPath(item["CompilerAssemblyPath"],
                    $"GSharp:Toolchains:{item.Key}:CompilerAssemblyPath"),
                ConfiguredPath(item["LanguageServerAssemblyPath"],
                    $"GSharp:Toolchains:{item.Key}:LanguageServerAssemblyPath")))
            .ToDictionary(static item => item.ToolchainId, StringComparer.Ordinal);
        if (toolchains.Count == 0)
            throw new InvalidOperationException("At least one G# toolchain profile must be configured.");

        var referenceSets = configuration.GetSection("ReferenceSets")
            .GetChildren()
            .Select(static item => new GSharpReferenceSetDefinition(
                item.Key,
                Required(item["Path"], $"ReferenceSets:{item.Key}:Path"),
                Required(item["TargetFramework"], $"ReferenceSets:{item.Key}:TargetFramework"),
                Required(item["FrameworkVersion"], $"ReferenceSets:{item.Key}:FrameworkVersion"),
                item["Digest"],
                item["AttestationPath"]))
            .OrderBy(static item => item.Id, StringComparer.Ordinal)
            .ToArray();
        if (referenceSets.Length == 0)
            throw new InvalidOperationException("At least one G# reference set must be configured.");

        var maximumProcessOutputBytes = section.GetValue("MaximumProcessOutputBytes", 1024 * 1024);
        var maximumProcessWorkingSetBytes = section.GetValue("MaximumProcessWorkingSetBytes", 512L * 1024 * 1024);
        var sessionTtlMinutes = section.GetValue("SessionTtlMinutes", 15);
        if (maximumProcessOutputBytes <= 0 || maximumProcessWorkingSetBytes <= 0 || sessionTtlMinutes <= 0)
            throw new InvalidOperationException("G# process limits must be positive.");

        return new GSharpWorkerSettings(
            new GSharpWorkerIdentity(
                section["ReleaseId"] ?? "development",
                section["WorkerImageId"] ?? $"sha256:{new string('0', 64)}"),
            new GSharpProcessLimits(
                maximumProcessOutputBytes,
                maximumProcessWorkingSetBytes,
                TimeSpan.FromMinutes(sessionTtlMinutes)),
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(
                section["WorkRoot"] ?? Path.Combine(Path.GetTempPath(), "sharplabnext-gsharp"))),
            section["DotNetHostPath"] ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            toolchains,
            referenceSets);
    }

    public GSharpToolchainProfile GetToolchain(string toolchainId) =>
        Toolchains.TryGetValue(toolchainId, out var toolchain)
            ? toolchain
            : throw new InvalidOperationException($"G# toolchain profile '{toolchainId}' is not configured.");

    private static string ConfiguredPath(string? value, string key) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(Required(value, key)));

    private static string Required(string? value, string key) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Configuration value '{key}' is required.");
}

internal static partial class GSharpCompilerIdentity
{
    public static string RequireVersion(
        string? value,
        string configurationKey = "GSharp:CompilerVersion")
    {
        if (string.IsNullOrWhiteSpace(value) || !SemanticVersionPattern().IsMatch(value))
        {
            throw new InvalidOperationException(
                $"Configuration value '{configurationKey}' must be a semantic version.");
        }
        return value;
    }

    public static string RequireCommit(
        string? value,
        string configurationKey = "GSharp:CompilerCommit")
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length != 40 ||
            value.Any(static character => !char.IsAsciiHexDigit(character)))
        {
            throw new InvalidOperationException(
                $"Configuration value '{configurationKey}' must be a 40-character Git commit.");
        }
        return value;
    }

    public static string GetFeatureVersion(string compilerVersion)
    {
        compilerVersion = RequireVersion(compilerVersion);
        var firstSeparator = compilerVersion.IndexOf('.');
        var secondSeparator = compilerVersion.IndexOf('.', firstSeparator + 1);
        return compilerVersion[..secondSeparator];
    }

    [GeneratedRegex(
        "^(0|[1-9]\\d*)\\.(0|[1-9]\\d*)\\.(0|[1-9]\\d*)(?:-(?:0|[1-9]\\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\\.(?:0|[1-9]\\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SemanticVersionPattern();
}

public sealed record LoadedGSharpReferenceSet(
    GSharpReferenceSetDefinition Definition,
    IReadOnlyList<string> ReferenceAssemblyPaths,
    ReferenceSetAttestation Attestation);

public sealed class GSharpReferenceSetProvider
{
    private readonly IReadOnlyDictionary<string, LoadedGSharpReferenceSet> _referenceSets;

    public GSharpReferenceSetProvider(
        IReadOnlyList<GSharpReferenceSetDefinition> definitions,
        bool requireAttestation)
    {
        var referenceSets = new Dictionary<string, LoadedGSharpReferenceSet>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(definition.RootPath));
            if (!Directory.Exists(root))
                throw new InvalidDataException($"G# reference set '{definition.Id}' does not exist.");
            var references = Directory.EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly)
                .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal)
                .ToArray();
            if (references.Length == 0)
                throw new InvalidDataException($"G# reference set '{definition.Id}' contains no assemblies.");
            var attestation = ReferenceSetAttestationReader.LoadAndVerify(
                root,
                definition.Id,
                definition.TargetFramework,
                definition.FrameworkVersion,
                definition.Digest,
                requireAttestation,
                definition.AttestationPath);
            if (!referenceSets.TryAdd(
                    definition.Id,
                    new LoadedGSharpReferenceSet(definition with { RootPath = root }, references, attestation)))
            {
                throw new InvalidDataException($"Duplicate G# reference set '{definition.Id}'.");
            }
        }
        _referenceSets = referenceSets;
    }

    public IReadOnlyList<ReferenceSetAttestation> Attestations =>
        _referenceSets.Values.Select(static item => item.Attestation).ToArray();

    public LoadedGSharpReferenceSet Get(string id) =>
        _referenceSets.TryGetValue(id, out var referenceSet)
            ? referenceSet
            : throw new SharpLabNext.LanguageWorker.Sdk.LanguageWorkerRequestException(
                "unsupported-reference-set",
                $"G# reference set '{id}' is unavailable.",
                StatusCodes.Status503ServiceUnavailable);
}
