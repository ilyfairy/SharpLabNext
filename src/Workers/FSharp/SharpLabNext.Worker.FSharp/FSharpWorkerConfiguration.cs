using System.Globalization;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Worker.FSharp.Compiler;
using SharpLabNext.WorkerHost;

namespace SharpLabNext.Worker.FSharp;

public sealed record FSharpReferenceSetDefinition(
    string Id,
    string Path,
    string TargetFramework,
    string FrameworkVersion,
    string? Digest = null,
    string? AttestationPath = null);

public sealed record FSharpWorkerIdentity(
    string ReleaseId,
    string ToolchainId,
    string CompilerVersion,
    string FSharpCorePackageVersion,
    string? CompilerCommit,
    string WorkerImageId);

public sealed record FSharpCompilationLimits(
    int MaxFiles,
    int MaxFileUtf8Bytes,
    int MaxTotalSourceUtf8Bytes,
    int MaxDiagnostics,
    int MaxPeBytes,
    int MaxPdbBytes,
    int MaxBuildMilliseconds)
{
    public static FSharpCompilationLimits Default { get; } = new(
        MaxFiles: 32,
        MaxFileUtf8Bytes: 512 * 1024,
        MaxTotalSourceUtf8Bytes: 1024 * 1024,
        MaxDiagnostics: 1_000,
        MaxPeBytes: 16 * 1024 * 1024,
        MaxPdbBytes: 8 * 1024 * 1024,
        MaxBuildMilliseconds: 20_000);
}

public sealed record FSharpAstLimits(
    int MaxNodes,
    int MaxDepth,
    int MaxUtf8Bytes,
    int MaxTextPreviewCharacters)
{
    public static FSharpAstLimits Default { get; } = new(
        MaxNodes: 25_000,
        MaxDepth: 128,
        MaxUtf8Bytes: 4 * 1024 * 1024,
        MaxTextPreviewCharacters: 160);
}

public sealed record FSharpLspLimits(
    int MaxSessions,
    int SessionTtlMinutes,
    int MaxMessageBytes,
    int MaxCompletionItems,
    int MaxDiagnostics,
    int MaxHoverCharacters,
    int MaxDocumentSymbols,
    int MaxSemanticTokens,
    int MaxCodeActionEdits)
{
    public static FSharpLspLimits Default { get; } = new(
        MaxSessions: 64,
        SessionTtlMinutes: 30,
        MaxMessageBytes: 1024 * 1024,
        MaxCompletionItems: 200,
        MaxDiagnostics: 500,
        MaxHoverCharacters: 16 * 1024,
        MaxDocumentSymbols: 2_000,
        MaxSemanticTokens: 20_000,
        MaxCodeActionEdits: 500);
}

public sealed record FSharpDevelopmentArtifactEnvelopeOptions(bool Enabled, int MaxBytes)
{
    public static FSharpDevelopmentArtifactEnvelopeOptions Default { get; } = new(false, 4 * 1024 * 1024);
}

public sealed record FSharpWorkerSettings(
    FSharpWorkerIdentity Identity,
    FSharpCompilationLimits CompilationLimits,
    FSharpAstLimits AstLimits,
    FSharpLspLimits LspLimits,
    CompilerProcessIsolationOptions BuildProcess,
    FSharpDevelopmentArtifactEnvelopeOptions DevelopmentArtifactEnvelope,
    ArtifactBundlePublishingOptions ArtifactPublishing,
    string WorkRoot,
    IReadOnlyList<FSharpReferenceSetDefinition> ReferenceSets)
{
    public static FSharpWorkerSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var worker = configuration.GetSection("FSharpWorker");
        var identity = new FSharpWorkerIdentity(
            Required(worker["ReleaseId"], "FSharpWorker:ReleaseId"),
            "fsharp-stable",
            PinnedOrConfigured(worker["CompilerVersion"], FSharpCompilerFacade.CompilerVersion),
            PinnedOrConfigured(worker["FSharpCorePackageVersion"], FSharpCompilerFacade.FSharpCorePackageVersion),
            worker["CompilerCommit"],
            Required(worker["WorkerImageId"], "FSharpWorker:WorkerImageId"));
        if (!StringComparer.Ordinal.Equals(identity.CompilerVersion, FSharpCompilerFacade.CompilerVersion) ||
            !StringComparer.Ordinal.Equals(identity.FSharpCorePackageVersion, FSharpCompilerFacade.FSharpCorePackageVersion))
        {
            throw new InvalidOperationException("Configured F# compiler identity does not match the pinned adapter packages.");
        }

        var compilationDefaults = FSharpCompilationLimits.Default;
        var compilation = worker.GetSection("CompilationLimits");
        var compilationLimits = new FSharpCompilationLimits(
            PositiveInt(compilation["MaxFiles"], compilationDefaults.MaxFiles, "MaxFiles"),
            PositiveInt(compilation["MaxFileUtf8Bytes"], compilationDefaults.MaxFileUtf8Bytes, "MaxFileUtf8Bytes"),
            PositiveInt(compilation["MaxTotalSourceUtf8Bytes"], compilationDefaults.MaxTotalSourceUtf8Bytes, "MaxTotalSourceUtf8Bytes"),
            PositiveInt(compilation["MaxDiagnostics"], compilationDefaults.MaxDiagnostics, "MaxDiagnostics"),
            PositiveInt(compilation["MaxPeBytes"], compilationDefaults.MaxPeBytes, "MaxPeBytes"),
            PositiveInt(compilation["MaxPdbBytes"], compilationDefaults.MaxPdbBytes, "MaxPdbBytes"),
            PositiveInt(compilation["MaxBuildMilliseconds"], compilationDefaults.MaxBuildMilliseconds, "MaxBuildMilliseconds"));

        var astDefaults = FSharpAstLimits.Default;
        var ast = worker.GetSection("AstLimits");
        var astLimits = new FSharpAstLimits(
            PositiveInt(ast["MaxNodes"], astDefaults.MaxNodes, "MaxNodes"),
            PositiveInt(ast["MaxDepth"], astDefaults.MaxDepth, "MaxDepth"),
            PositiveInt(ast["MaxUtf8Bytes"], astDefaults.MaxUtf8Bytes, "MaxUtf8Bytes"),
            PositiveInt(ast["MaxTextPreviewCharacters"], astDefaults.MaxTextPreviewCharacters, "MaxTextPreviewCharacters"));

        var lspDefaults = FSharpLspLimits.Default;
        var lsp = worker.GetSection("LspLimits");
        var lspLimits = new FSharpLspLimits(
            PositiveInt(lsp["MaxSessions"], lspDefaults.MaxSessions, "MaxSessions"),
            PositiveInt(lsp["SessionTtlMinutes"], lspDefaults.SessionTtlMinutes, "SessionTtlMinutes"),
            PositiveInt(lsp["MaxMessageBytes"], lspDefaults.MaxMessageBytes, "MaxMessageBytes"),
            PositiveInt(lsp["MaxCompletionItems"], lspDefaults.MaxCompletionItems, "MaxCompletionItems"),
            PositiveInt(lsp["MaxDiagnostics"], lspDefaults.MaxDiagnostics, "MaxDiagnostics"),
            PositiveInt(lsp["MaxHoverCharacters"], lspDefaults.MaxHoverCharacters, "MaxHoverCharacters"),
            PositiveInt(lsp["MaxDocumentSymbols"], lspDefaults.MaxDocumentSymbols, "MaxDocumentSymbols"),
            PositiveInt(lsp["MaxSemanticTokens"], lspDefaults.MaxSemanticTokens, "MaxSemanticTokens"),
            PositiveInt(lsp["MaxCodeActionEdits"], lspDefaults.MaxCodeActionEdits, "MaxCodeActionEdits"));

        var processDefaults = CompilerProcessIsolationOptions.Default;
        var process = worker.GetSection("BuildProcess");
        var buildProcess = new CompilerProcessIsolationOptions(
            Boolean(process["Enabled"], processDefaults.Enabled, "BuildProcess:Enabled"),
            PositiveInt(
                process["MaximumConcurrentProcesses"],
                processDefaults.MaximumConcurrentProcesses,
                "BuildProcess:MaximumConcurrentProcesses"),
            PositiveLong(
                process["MaximumWorkingSetBytes"],
                processDefaults.MaximumWorkingSetBytes,
                "BuildProcess:MaximumWorkingSetBytes"),
            PositiveInt(
                process["MaximumRequestBytes"],
                processDefaults.MaximumRequestBytes,
                "BuildProcess:MaximumRequestBytes"),
            PositiveInt(
                process["MaximumResponseBytes"],
                processDefaults.MaximumResponseBytes,
                "BuildProcess:MaximumResponseBytes"),
            PositiveInt(
                process["MaximumStandardErrorBytes"],
                processDefaults.MaximumStandardErrorBytes,
                "BuildProcess:MaximumStandardErrorBytes"),
            PositiveInt(
                process["MemoryPollIntervalMilliseconds"],
                processDefaults.MemoryPollIntervalMilliseconds,
                "BuildProcess:MemoryPollIntervalMilliseconds"));
        buildProcess.Validate();

        var envelope = worker.GetSection("DevelopmentArtifactEnvelope");
        var envelopeOptions = new FSharpDevelopmentArtifactEnvelopeOptions(
            bool.TryParse(envelope["Enabled"], out var enabled) && enabled,
            PositiveInt(envelope["MaxBytes"], FSharpDevelopmentArtifactEnvelopeOptions.Default.MaxBytes, "MaxBytes"));
        var artifactPublishing = CreateArtifactPublishingOptions(configuration, worker);
        var workRoot = worker["WorkRoot"];
        if (string.IsNullOrWhiteSpace(workRoot))
            workRoot = Path.Combine(Path.GetTempPath(), "SharpLabNext", "fsharp-worker");

        var referenceSets = configuration.GetSection("ReferenceSets")
            .GetChildren()
            .Select(section => new FSharpReferenceSetDefinition(
                section.Key,
                Required(section["Path"], $"ReferenceSets:{section.Key}:Path"),
                Required(section["TargetFramework"], $"ReferenceSets:{section.Key}:TargetFramework"),
                Required(section["FrameworkVersion"], $"ReferenceSets:{section.Key}:FrameworkVersion"),
                section["Digest"],
                section["AttestationPath"]))
            .ToArray();

        return new FSharpWorkerSettings(
            identity,
            compilationLimits,
            astLimits,
            lspLimits,
            buildProcess,
            envelopeOptions,
            artifactPublishing,
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(workRoot)),
            referenceSets);
    }

    private static string PinnedOrConfigured(string? value, string pinned) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value, "__pinned__", StringComparison.Ordinal)
            ? pinned
            : value;

    private static ArtifactBundlePublishingOptions CreateArtifactPublishingOptions(
        IConfiguration configuration,
        IConfigurationSection worker)
    {
        var baseUrl = configuration["ArtifactStore:BaseUrl"] ?? "http://artifact-store:8080";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseAddress) ||
            (!string.Equals(baseAddress.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(baseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            !string.IsNullOrEmpty(baseAddress.UserInfo) ||
            !string.IsNullOrEmpty(baseAddress.Query) ||
            !string.IsNullOrEmpty(baseAddress.Fragment))
        {
            throw new InvalidOperationException(
                "Configuration value 'ArtifactStore:BaseUrl' must be an absolute HTTP(S) URL without credentials, query, or fragment.");
        }
        return new ArtifactBundlePublishingOptions(
            baseAddress,
            TimeSpan.FromMinutes(PositiveInt(
                worker["ArtifactTimeToLiveMinutes"],
                60,
                "ArtifactTimeToLiveMinutes")));
    }

    private static string Required(string? value, string key) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Configuration value '{key}' is required.");

    private static int PositiveInt(string? value, int fallback, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            throw new InvalidOperationException($"Configuration value '{key}' must be a positive integer.");
        return parsed;
    }

    private static long PositiveLong(string? value, long fallback, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            throw new InvalidOperationException($"Configuration value '{key}' must be a positive integer.");
        return parsed;
    }

    private static bool Boolean(string? value, bool fallback, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        if (!bool.TryParse(value, out var parsed))
            throw new InvalidOperationException($"Configuration value '{key}' must be a boolean.");
        return parsed;
    }
}
