using System.Globalization;
using SharpLabNext.ArtifactStore.Client;

namespace SharpLabNext.Worker.IL;

public sealed record IlWorkerIdentity(
    string ReleaseId,
    string ToolchainId,
    string CompilerVersion,
    string? CompilerCommit,
    string WorkerImageId);

public sealed record IlReferenceSetDefinition(
    string Id,
    string Path,
    string TargetFramework,
    string FrameworkVersion,
    string? Digest = null,
    string? AttestationPath = null);

public sealed record IlCompilationLimits(
    int MaxFiles,
    int MaxFileUtf8Bytes,
    int MaxTotalSourceUtf8Bytes,
    int MaxDiagnostics,
    int MaxPeBytes,
    int MaxBuildMilliseconds,
    int MaxConcurrentBuilds,
    int MaxProcessOutputBytes,
    int MaxCompilerResponseBytes,
    int MaxProcessWorkingSetBytes)
{
    public static IlCompilationLimits Default { get; } = new(
        MaxFiles: 32,
        MaxFileUtf8Bytes: 512 * 1024,
        MaxTotalSourceUtf8Bytes: 1024 * 1024,
        MaxDiagnostics: 1_000,
        MaxPeBytes: 16 * 1024 * 1024,
        MaxBuildMilliseconds: 10_000,
        MaxConcurrentBuilds: 4,
        MaxProcessOutputBytes: 64 * 1024,
        MaxCompilerResponseBytes: 1024 * 1024,
        MaxProcessWorkingSetBytes: 512 * 1024 * 1024);
}

public sealed record IlLspLimits(
    int MaxSessions,
    int SessionTtlMinutes,
    int MaxMessageBytes,
    int MaxCompletionItems,
    int MaxDiagnostics,
    int MaxDocumentSymbols,
    int MaxCodeActions,
    int DiagnosticsDebounceMilliseconds)
{
    public static IlLspLimits Default { get; } = new(
        MaxSessions: 64,
        SessionTtlMinutes: 30,
        MaxMessageBytes: 1024 * 1024,
        MaxCompletionItems: 300,
        MaxDiagnostics: 500,
        MaxDocumentSymbols: 2_000,
        MaxCodeActions: 100,
        DiagnosticsDebounceMilliseconds: 100);
}

public sealed record IlDevelopmentArtifactEnvelopeOptions(bool Enabled, int MaxBytes)
{
    public static IlDevelopmentArtifactEnvelopeOptions Default { get; } = new(false, 4 * 1024 * 1024);
}

public sealed record IlWorkerSettings(
    IlWorkerIdentity Identity,
    IlCompilationLimits CompilationLimits,
    IlLspLimits LspLimits,
    IlDevelopmentArtifactEnvelopeOptions DevelopmentArtifactEnvelope,
    ArtifactBundlePublishingOptions ArtifactPublishing,
    string WorkRoot,
    string DotNetHostPath,
    string CompilerAssemblyPath,
    IReadOnlyList<IlReferenceSetDefinition> ReferenceSets)
{
    public static IlWorkerSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var worker = configuration.GetSection("IlWorker");
        var identity = new IlWorkerIdentity(
            Required(worker["ReleaseId"], "IlWorker:ReleaseId"),
            "mobius-ilasm-stable",
            PinnedOrConfigured(worker["CompilerVersion"], Compiler.IlCompilerProtocol.PackageVersion),
            worker["CompilerCommit"],
            Required(worker["WorkerImageId"], "IlWorker:WorkerImageId"));
        if (!StringComparer.Ordinal.Equals(identity.CompilerVersion, Compiler.IlCompilerProtocol.PackageVersion))
            throw new InvalidOperationException("Configured IL compiler identity does not match the pinned Mobius.ILasm package.");

        var defaults = IlCompilationLimits.Default;
        var limits = worker.GetSection("CompilationLimits");
        var compilationLimits = new IlCompilationLimits(
            PositiveInt(limits["MaxFiles"], defaults.MaxFiles, "MaxFiles"),
            PositiveInt(limits["MaxFileUtf8Bytes"], defaults.MaxFileUtf8Bytes, "MaxFileUtf8Bytes"),
            PositiveInt(limits["MaxTotalSourceUtf8Bytes"], defaults.MaxTotalSourceUtf8Bytes, "MaxTotalSourceUtf8Bytes"),
            PositiveInt(limits["MaxDiagnostics"], defaults.MaxDiagnostics, "MaxDiagnostics"),
            PositiveInt(limits["MaxPeBytes"], defaults.MaxPeBytes, "MaxPeBytes"),
            PositiveInt(limits["MaxBuildMilliseconds"], defaults.MaxBuildMilliseconds, "MaxBuildMilliseconds"),
            PositiveInt(limits["MaxConcurrentBuilds"], defaults.MaxConcurrentBuilds, "MaxConcurrentBuilds"),
            PositiveInt(limits["MaxProcessOutputBytes"], defaults.MaxProcessOutputBytes, "MaxProcessOutputBytes"),
            PositiveInt(limits["MaxCompilerResponseBytes"], defaults.MaxCompilerResponseBytes, "MaxCompilerResponseBytes"),
            PositiveInt(limits["MaxProcessWorkingSetBytes"], defaults.MaxProcessWorkingSetBytes, "MaxProcessWorkingSetBytes"));
        ValidateCompilerLimits(compilationLimits);

        var lspDefaults = IlLspLimits.Default;
        var lsp = worker.GetSection("LspLimits");
        var lspLimits = new IlLspLimits(
            PositiveInt(lsp["MaxSessions"], lspDefaults.MaxSessions, "MaxSessions"),
            PositiveInt(lsp["SessionTtlMinutes"], lspDefaults.SessionTtlMinutes, "SessionTtlMinutes"),
            PositiveInt(lsp["MaxMessageBytes"], lspDefaults.MaxMessageBytes, "MaxMessageBytes"),
            PositiveInt(lsp["MaxCompletionItems"], lspDefaults.MaxCompletionItems, "MaxCompletionItems"),
            PositiveInt(lsp["MaxDiagnostics"], lspDefaults.MaxDiagnostics, "MaxDiagnostics"),
            PositiveInt(lsp["MaxDocumentSymbols"], lspDefaults.MaxDocumentSymbols, "MaxDocumentSymbols"),
            PositiveInt(lsp["MaxCodeActions"], lspDefaults.MaxCodeActions, "MaxCodeActions"),
            PositiveInt(lsp["DiagnosticsDebounceMilliseconds"], lspDefaults.DiagnosticsDebounceMilliseconds, "DiagnosticsDebounceMilliseconds"));

        var envelope = worker.GetSection("DevelopmentArtifactEnvelope");
        var envelopeOptions = new IlDevelopmentArtifactEnvelopeOptions(
            bool.TryParse(envelope["Enabled"], out var enabled) && enabled,
            PositiveInt(envelope["MaxBytes"], IlDevelopmentArtifactEnvelopeOptions.Default.MaxBytes, "MaxBytes"));
        var artifactPublishing = CreateArtifactPublishingOptions(configuration, worker);

        var workRoot = worker["WorkRoot"];
        if (string.IsNullOrWhiteSpace(workRoot))
            workRoot = Path.Combine(Path.GetTempPath(), "SharpLabNext", "il-worker");
        var compilerAssemblyPath = worker["CompilerAssemblyPath"];
        if (string.IsNullOrWhiteSpace(compilerAssemblyPath))
            compilerAssemblyPath = Path.Combine(AppContext.BaseDirectory, "SharpLabNext.Worker.IL.Compiler.dll");
        else if (!Path.IsPathRooted(compilerAssemblyPath))
            compilerAssemblyPath = Path.Combine(AppContext.BaseDirectory, compilerAssemblyPath);
        var dotNetHostPath = worker["DotNetHostPath"];
        if (string.IsNullOrWhiteSpace(dotNetHostPath))
            dotNetHostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

        var referenceSets = configuration.GetSection("ReferenceSets")
            .GetChildren()
            .Select(section => new IlReferenceSetDefinition(
                section.Key,
                Path.GetFullPath(Required(section["Path"], $"ReferenceSets:{section.Key}:Path")),
                Required(section["TargetFramework"], $"ReferenceSets:{section.Key}:TargetFramework"),
                Required(section["FrameworkVersion"], $"ReferenceSets:{section.Key}:FrameworkVersion"),
                section["Digest"],
                section["AttestationPath"]))
            .ToArray();

        return new IlWorkerSettings(
            identity,
            compilationLimits,
            lspLimits,
            envelopeOptions,
            artifactPublishing,
            Path.GetFullPath(workRoot),
            dotNetHostPath,
            Path.GetFullPath(compilerAssemblyPath),
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
            throw new InvalidOperationException($"Configuration value 'IlWorker:{key}' must be a positive integer.");
        return parsed;
    }

    private static void ValidateCompilerLimits(IlCompilationLimits limits)
    {
        if (limits.MaxFiles > Compiler.IlCompilerProtocol.MaxSources)
            throw new InvalidOperationException($"IlWorker:MaxFiles cannot exceed {Compiler.IlCompilerProtocol.MaxSources}.");
        if (limits.MaxDiagnostics > Compiler.IlCompilerProtocol.MaxDiagnostics)
            throw new InvalidOperationException($"IlWorker:MaxDiagnostics cannot exceed {Compiler.IlCompilerProtocol.MaxDiagnostics}.");
        if (limits.MaxPeBytes > Compiler.IlCompilerProtocol.MaxPeBytes)
            throw new InvalidOperationException($"IlWorker:MaxPeBytes cannot exceed {Compiler.IlCompilerProtocol.MaxPeBytes}.");
        if (limits.MaxCompilerResponseBytes > Compiler.IlCompilerProtocol.MaxResponseBytes)
            throw new InvalidOperationException($"IlWorker:MaxCompilerResponseBytes cannot exceed {Compiler.IlCompilerProtocol.MaxResponseBytes}.");
    }
}
