using System.Globalization;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.WorkerHost;

namespace SharpLabNext.Worker.Roslyn;

public sealed record ReferenceSetDefinition
{
    public ReferenceSetDefinition(string Id, string Path, string TargetFramework, string FrameworkVersion, string? Digest = null, string? AttestationPath = null, bool? IncludeSharpLabRuntime = null)
    {
        this.Id = Id;
        this.Path = Path;
        this.TargetFramework = TargetFramework;
        this.FrameworkVersion = FrameworkVersion;
        this.Digest = Digest;
        this.AttestationPath = AttestationPath;

        var defaults = ReferenceSetArtifactContractDefaults.For(TargetFramework);
        ArtifactFormat = defaults.ArtifactFormat;
        RuntimeFamily = defaults.RuntimeFamily;
        FrameworkName = defaults.FrameworkName;
        Architecture = defaults.Architecture;
        ExecutableFileExtension = defaults.ExecutableFileExtension;
        LibraryFileExtension = defaults.LibraryFileExtension;
        this.IncludeSharpLabRuntime = IncludeSharpLabRuntime ?? defaults.IncludeSharpLabRuntime;
    }

    public string Id { get; init; }

    public string Path { get; init; }

    public string TargetFramework { get; init; }

    // Reference pack/package version used for provenance and attestation.
    public string FrameworkVersion { get; init; }

    public string? Digest { get; init; }

    public string? AttestationPath { get; init; }

    public string ArtifactFormat { get; init; }

    public string RuntimeFamily { get; init; }

    public string FrameworkName { get; init; }

    // Null means derive the runtime requirement from TargetFramework (framework)
    // or the attested reference-pack version (CoreCLR).
    public string? RuntimeFrameworkVersion { get; init; }

    public string Architecture { get; init; }

    public string ExecutableFileExtension { get; init; }

    public string LibraryFileExtension { get; init; }

    public IReadOnlyList<string> RequiredRuntimeFeatureTags { get; init; } = [];

    public IReadOnlyList<string> MetadataFeatureTags { get; init; } = [];

    public string? CompatibilityGroup { get; init; }

    public bool IncludeSharpLabRuntime { get; init; }

    public string GetRuntimeFrameworkVersion() => RuntimeFrameworkVersion ?? ReferenceSetArtifactContractDefaults.GetRuntimeFrameworkVersion(TargetFramework, FrameworkVersion);

    internal bool IsFrameworkReferenceSet => ReferenceSetArtifactContractDefaults.IsFrameworkTarget(TargetFramework);

    internal bool IsLegacyFrameworkReferenceSet => ReferenceSetArtifactContractDefaults.IsLegacyFrameworkTarget(TargetFramework);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
            throw new InvalidOperationException("Reference set id cannot be empty.");
        if (string.IsNullOrWhiteSpace(Path))
            throw new InvalidOperationException($"Reference set '{Id}' path cannot be empty.");
        if (string.IsNullOrWhiteSpace(TargetFramework))
            throw new InvalidOperationException($"Reference set '{Id}' target framework cannot be empty.");
        if (string.IsNullOrWhiteSpace(FrameworkVersion))
            throw new InvalidOperationException($"Reference set '{Id}' framework version cannot be empty.");
        if (ArtifactFormat is not ("dotnet-managed-pe-v1" or "dotnet-framework-managed-pe-v1"))
        {
            throw new InvalidOperationException($"Reference set '{Id}' artifact format must be 'dotnet-managed-pe-v1' or 'dotnet-framework-managed-pe-v1'.");
        }
        if (string.IsNullOrWhiteSpace(RuntimeFamily))
            throw new InvalidOperationException($"Reference set '{Id}' runtime family cannot be empty.");
        if (string.IsNullOrWhiteSpace(FrameworkName))
            throw new InvalidOperationException($"Reference set '{Id}' framework name cannot be empty.");
        if (RuntimeFrameworkVersion is not null && string.IsNullOrWhiteSpace(RuntimeFrameworkVersion))
            throw new InvalidOperationException($"Reference set '{Id}' runtime framework version cannot be empty.");
        if (Architecture is not ("anycpu" or "x64" or "x86"))
        {
            throw new InvalidOperationException($"Reference set '{Id}' architecture must be 'anycpu', 'x64', or 'x86'.");
        }

        ValidateFileExtension(ExecutableFileExtension, nameof(ExecutableFileExtension));
        ValidateFileExtension(LibraryFileExtension, nameof(LibraryFileExtension));
        ValidateTags(RequiredRuntimeFeatureTags, nameof(RequiredRuntimeFeatureTags));
        ValidateTags(MetadataFeatureTags, nameof(MetadataFeatureTags));
        if (CompatibilityGroup is not null && string.IsNullOrWhiteSpace(CompatibilityGroup))
            throw new InvalidOperationException($"Reference set '{Id}' compatibility group cannot be empty.");
    }

    private void ValidateFileExtension(string value, string name)
    {
        if (value is not (".dll" or ".exe"))
            throw new InvalidOperationException($"Reference set '{Id}' {name} must be '.dll' or '.exe'.");
    }

    private void ValidateTags(IReadOnlyList<string> values, string name)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException($"Reference set '{Id}' {name} cannot contain empty values.");
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            throw new InvalidOperationException($"Reference set '{Id}' {name} cannot contain duplicate values.");
    }
}

internal sealed record ReferenceSetArtifactContractDefaults(string ArtifactFormat, string RuntimeFamily, string FrameworkName, string Architecture, string ExecutableFileExtension, string LibraryFileExtension, bool IncludeSharpLabRuntime)
{
    public static ReferenceSetArtifactContractDefaults For(string targetFramework)
    {
        if (IsFrameworkTarget(targetFramework))
        {
            return new("dotnet-framework-managed-pe-v1", "netfx-clr-wine", ".NETFramework", "anycpu", ".exe", ".dll", IncludeSharpLabRuntime: false);
        }

        if (IsCoreTarget(targetFramework))
        {
            return new("dotnet-managed-pe-v1", "coreclr", "Microsoft.NETCore.App", "anycpu", ".dll", ".dll", IncludeSharpLabRuntime: SupportsNetStandard21(targetFramework));
        }

        throw new InvalidOperationException($"Target framework '{targetFramework}' is not a supported CoreCLR or .NET Framework application TFM.");
    }

    public static string GetRuntimeFrameworkVersion(string targetFramework, string referencePackVersion)
    {
        if (!IsFrameworkTarget(targetFramework))
            return referencePackVersion;

        var digits = targetFramework.AsSpan(3);
        return digits.Length switch
        {
            2 => $"{digits[0]}.{digits[1]}",
            3 => $"{digits[0]}.{digits[1]}.{digits[2]}",
            _ => throw new InvalidOperationException($"Target framework '{targetFramework}' is not a recognized .NET Framework TFM.")
        };
    }

    public static bool IsFrameworkTarget(string targetFramework)
    {
        if (!targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase))
            return false;
        var version = targetFramework.AsSpan(3);
        return version.Length is 2 or 3 && version.IndexOf('.') < 0 && IsAsciiDigits(version) && version[0] is >= '2' and <= '4';
    }

    public static bool IsLegacyFrameworkTarget(string targetFramework) =>
        IsFrameworkTarget(targetFramework) && targetFramework[3] is '2' or '3';

    private static bool IsCoreTarget(string targetFramework)
    {
        if (targetFramework.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase))
            return Version.TryParse(VersionPrefix(targetFramework[10..]), out _);
        if (!targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase))
            return false;
        return Version.TryParse(VersionPrefix(targetFramework[3..]), out var version) && version.Major >= 5;
    }

    private static bool SupportsNetStandard21(string targetFramework)
    {
        if (!targetFramework.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase))
            return true;
        return Version.TryParse(VersionPrefix(targetFramework[10..]), out var version) && version.Major >= 3;
    }

    private static string VersionPrefix(string value)
    {
        var suffix = value.IndexOf('-');
        return suffix < 0 ? value : value[..suffix];
    }

    private static bool IsAsciiDigits(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is < '0' or > '9')
                return false;
        }
        return true;
    }
}

public sealed record RoslynWorkerIdentity(string ReleaseId, string ToolchainId, string CompilerVersion, string? CompilerCommit, string WorkerImageId)
{
    public IReadOnlyList<string> SupportedLanguageIds { get; init; } = ["csharp", "visual-basic"];

    public string ArtifactFormat { get; init; } = "dotnet-managed-pe-v1";

    public string ArtifactRuntimeFamily { get; init; } = "coreclr";

    public string ArtifactFrameworkName { get; init; } = "Microsoft.NETCore.App";

    public string? ArtifactFrameworkVersion { get; init; }

    public string ArtifactArchitecture { get; init; } = "anycpu";

    public string ExecutableFileExtension { get; init; } = ".dll";

    public string LibraryFileExtension { get; init; } = ".dll";

    public IReadOnlyList<string> RequiredRuntimeFeatureTags { get; init; } = [];

    public IReadOnlyList<string> MetadataFeatureTags { get; init; } = [];

    public string? CompatibilityGroup { get; init; }

    public bool SupportsLanguage(string languageId) =>
        SupportedLanguageIds.Contains(languageId, StringComparer.Ordinal);
}

public sealed record CompilationLimits(int MaxFiles, int MaxFileUtf8Bytes, int MaxTotalSourceUtf8Bytes, int MaxDiagnostics, int MaxPeBytes, int MaxPdbBytes, int MaxBuildMilliseconds)
{
    public static CompilationLimits Default { get; } = new(
        MaxFiles: 32,
        MaxFileUtf8Bytes: 512 * 1024,
        MaxTotalSourceUtf8Bytes: 1024 * 1024,
        MaxDiagnostics: 1_000,
        MaxPeBytes: 16 * 1024 * 1024,
        MaxPdbBytes: 8 * 1024 * 1024,
        MaxBuildMilliseconds: 15_000);
}

public sealed record AstLimits(int MaxNodes, int MaxDepth, int MaxUtf8Bytes, int MaxTextPreviewCharacters)
{
    public static AstLimits Default { get; } = new(MaxNodes: 25_000, MaxDepth: 128, MaxUtf8Bytes: 4 * 1024 * 1024, MaxTextPreviewCharacters: 160);
}

public sealed record DevelopmentArtifactEnvelopeOptions(bool Enabled, int MaxBytes)
{
    public static DevelopmentArtifactEnvelopeOptions Default { get; } = new(false, 4 * 1024 * 1024);
}

public sealed record LspLimits(
    int MaxSessions,
    int SessionTtlMinutes,
    int MaxMessageBytes,
    int MaxConcurrentRequestsPerConnection,
    int MaxCompletionItems,
    int MaxCompletionCacheItems,
    int MaxDiagnostics,
    int MaxHoverCharacters,
    int MaxSemanticTokens,
    int MaxDocumentSymbols,
    int MaxCodeActions,
    int DiagnosticsDebounceMilliseconds)
{
    public static LspLimits Default { get; } = new(
        MaxSessions: 64,
        SessionTtlMinutes: 30,
        MaxMessageBytes: 1024 * 1024,
        MaxConcurrentRequestsPerConnection: 8,
        MaxCompletionItems: 200,
        MaxCompletionCacheItems: 512,
        MaxDiagnostics: 500,
        MaxHoverCharacters: 16 * 1024,
        MaxSemanticTokens: 20_000,
        MaxDocumentSymbols: 2_000,
        MaxCodeActions: 20,
        DiagnosticsDebounceMilliseconds: 100);
}

public sealed record RoslynWorkerSettings(
    RoslynWorkerIdentity Identity,
    CompilationLimits CompilationLimits,
    AstLimits AstLimits,
    LspLimits LspLimits,
    CompilerProcessIsolationOptions BuildProcess,
    DevelopmentArtifactEnvelopeOptions DevelopmentArtifactEnvelope,
    ArtifactBundlePublishingOptions ArtifactPublishing,
    IReadOnlyList<ReferenceSetDefinition> ReferenceSets)
{
    public static RoslynWorkerSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var worker = configuration.GetSection("RoslynWorker");
        var artifactContract = worker.GetSection("ArtifactContract");
        var identity = new RoslynWorkerIdentity(Required(worker["ReleaseId"], "RoslynWorker:ReleaseId"), Required(worker["ToolchainId"], "RoslynWorker:ToolchainId"), PinnedOrConfigured(worker["CompilerVersion"], CSharpBuildService.GetLoadedCompilerVersion()), worker["CompilerCommit"], Required(worker["WorkerImageId"], "RoslynWorker:WorkerImageId"))
        {
            SupportedLanguageIds = StringList(worker.GetSection("SupportedLanguageIds"), ["csharp", "visual-basic"], requireAtLeastOne: true),
            ArtifactFormat = artifactContract["Format"] ?? "dotnet-managed-pe-v1",
            ArtifactRuntimeFamily = artifactContract["RuntimeFamily"] ?? "coreclr",
            ArtifactFrameworkName = artifactContract["FrameworkName"] ?? "Microsoft.NETCore.App",
            ArtifactFrameworkVersion = Optional(artifactContract["FrameworkVersion"], "RoslynWorker:ArtifactContract:FrameworkVersion"),
            ArtifactArchitecture = artifactContract["Architecture"] ?? "anycpu",
            ExecutableFileExtension = artifactContract["ExecutableFileExtension"] ?? ".dll",
            LibraryFileExtension = artifactContract["LibraryFileExtension"] ?? ".dll",
            RequiredRuntimeFeatureTags = StringList(artifactContract.GetSection("RequiredRuntimeFeatureTags"), [], requireAtLeastOne: false),
            MetadataFeatureTags = StringList(artifactContract.GetSection("MetadataFeatureTags"), [], requireAtLeastOne: false),
            CompatibilityGroup = artifactContract["CompatibilityGroup"]
        };

        if (identity.SupportedLanguageIds.Any(static id => id is not ("csharp" or "visual-basic")))
            throw new InvalidOperationException("RoslynWorker:SupportedLanguageIds only accepts 'csharp' or 'visual-basic'.");
        ValidateArtifactContract(identity);
        if (identity.CompatibilityGroup is not null && string.IsNullOrWhiteSpace(identity.CompatibilityGroup))
            throw new InvalidOperationException("RoslynWorker:ArtifactContract:CompatibilityGroup cannot be empty.");

        var compilationDefaults = CompilationLimits.Default;
        var compilation = worker.GetSection("CompilationLimits");
        var compilationLimits = new CompilationLimits(
            PositiveInt(compilation["MaxFiles"], compilationDefaults.MaxFiles, "MaxFiles"),
            PositiveInt(compilation["MaxFileUtf8Bytes"], compilationDefaults.MaxFileUtf8Bytes, "MaxFileUtf8Bytes"),
            PositiveInt(compilation["MaxTotalSourceUtf8Bytes"], compilationDefaults.MaxTotalSourceUtf8Bytes, "MaxTotalSourceUtf8Bytes"),
            PositiveInt(compilation["MaxDiagnostics"], compilationDefaults.MaxDiagnostics, "MaxDiagnostics"),
            PositiveInt(compilation["MaxPeBytes"], compilationDefaults.MaxPeBytes, "MaxPeBytes"),
            PositiveInt(compilation["MaxPdbBytes"], compilationDefaults.MaxPdbBytes, "MaxPdbBytes"),
            PositiveInt(compilation["MaxBuildMilliseconds"], compilationDefaults.MaxBuildMilliseconds, "MaxBuildMilliseconds"));

        var astDefaults = AstLimits.Default;
        var ast = worker.GetSection("AstLimits");
        var astLimits = new AstLimits(PositiveInt(ast["MaxNodes"], astDefaults.MaxNodes, "MaxNodes"), PositiveInt(ast["MaxDepth"], astDefaults.MaxDepth, "MaxDepth"), PositiveInt(ast["MaxUtf8Bytes"], astDefaults.MaxUtf8Bytes, "MaxUtf8Bytes"), PositiveInt(ast["MaxTextPreviewCharacters"], astDefaults.MaxTextPreviewCharacters, "MaxTextPreviewCharacters"));

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

        var envelope = worker.GetSection("DevelopmentArtifactEnvelope");
        var envelopeOptions = new DevelopmentArtifactEnvelopeOptions(bool.TryParse(envelope["Enabled"], out var enabled) && enabled, PositiveInt(envelope["MaxBytes"], DevelopmentArtifactEnvelopeOptions.Default.MaxBytes, "MaxBytes"));

        var artifactStoreBaseUrl = configuration["ArtifactStore:BaseUrl"] ?? "http://artifact-store:8080";
        if (!Uri.TryCreate(artifactStoreBaseUrl, UriKind.Absolute, out var artifactStoreBaseAddress) || (!string.Equals(artifactStoreBaseAddress.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && !string.Equals(artifactStoreBaseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) || !string.IsNullOrEmpty(artifactStoreBaseAddress.UserInfo) || !string.IsNullOrEmpty(artifactStoreBaseAddress.Query) || !string.IsNullOrEmpty(artifactStoreBaseAddress.Fragment))
        {
            throw new InvalidOperationException("Configuration value 'ArtifactStore:BaseUrl' must be an absolute HTTP(S) URL without credentials, query, or fragment.");
        }
        var artifactPublishing = new ArtifactBundlePublishingOptions(artifactStoreBaseAddress, TimeSpan.FromMinutes(PositiveInt(worker["ArtifactTimeToLiveMinutes"], 60, "ArtifactTimeToLiveMinutes")));

        var lspDefaults = LspLimits.Default;
        var lsp = worker.GetSection("LspLimits");
        var lspLimits = new LspLimits(
            PositiveInt(lsp["MaxSessions"], lspDefaults.MaxSessions, "MaxSessions"),
            PositiveInt(lsp["SessionTtlMinutes"], lspDefaults.SessionTtlMinutes, "SessionTtlMinutes"),
            PositiveInt(lsp["MaxMessageBytes"], lspDefaults.MaxMessageBytes, "MaxMessageBytes"),
            PositiveInt(lsp["MaxConcurrentRequestsPerConnection"], lspDefaults.MaxConcurrentRequestsPerConnection, "MaxConcurrentRequestsPerConnection"),
            PositiveInt(lsp["MaxCompletionItems"], lspDefaults.MaxCompletionItems, "MaxCompletionItems"),
            PositiveInt(lsp["MaxCompletionCacheItems"], lspDefaults.MaxCompletionCacheItems, "MaxCompletionCacheItems"),
            PositiveInt(lsp["MaxDiagnostics"], lspDefaults.MaxDiagnostics, "MaxDiagnostics"),
            PositiveInt(lsp["MaxHoverCharacters"], lspDefaults.MaxHoverCharacters, "MaxHoverCharacters"),
            PositiveInt(lsp["MaxSemanticTokens"], lspDefaults.MaxSemanticTokens, "MaxSemanticTokens"),
            PositiveInt(lsp["MaxDocumentSymbols"], lspDefaults.MaxDocumentSymbols, "MaxDocumentSymbols"),
            PositiveInt(lsp["MaxCodeActions"], lspDefaults.MaxCodeActions, "MaxCodeActions"),
            PositiveInt(lsp["DiagnosticsDebounceMilliseconds"], lspDefaults.DiagnosticsDebounceMilliseconds, "DiagnosticsDebounceMilliseconds"));

        var referenceSets = configuration.GetSection("ReferenceSets").GetChildren().Select(section => CreateReferenceSetDefinition(section, artifactContract)).ToArray();

        return new RoslynWorkerSettings(identity, compilationLimits, astLimits, lspLimits, buildProcess, envelopeOptions, artifactPublishing, referenceSets);
    }

    private static string PinnedOrConfigured(string? value, string pinned) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value, "__pinned__", StringComparison.Ordinal)
            ? pinned : value;

    private static ReferenceSetDefinition CreateReferenceSetDefinition(IConfigurationSection section, IConfigurationSection legacyArtifactContract)
    {
        var keyPrefix = $"ReferenceSets:{section.Key}";
        var definition = new ReferenceSetDefinition(section.Key, Required(section["Path"], $"{keyPrefix}:Path"), Required(section["TargetFramework"], $"{keyPrefix}:TargetFramework"), Required(section["FrameworkVersion"], $"{keyPrefix}:FrameworkVersion"), section["Digest"], section["AttestationPath"], NullableBoolean(section["IncludeSharpLabRuntime"], $"{keyPrefix}:IncludeSharpLabRuntime"));

        definition = definition with
        {
            ArtifactFormat = FirstConfigured(section["ArtifactFormat"], legacyArtifactContract["Format"]) ?? definition.ArtifactFormat,
            RuntimeFamily = FirstConfigured(section["RuntimeFamily"], legacyArtifactContract["RuntimeFamily"]) ?? definition.RuntimeFamily,
            FrameworkName = FirstConfigured(section["FrameworkName"], legacyArtifactContract["FrameworkName"]) ?? definition.FrameworkName,
            RuntimeFrameworkVersion = FirstConfigured(section["RuntimeFrameworkVersion"], legacyArtifactContract["FrameworkVersion"]),
            Architecture = FirstConfigured(section["Architecture"], legacyArtifactContract["Architecture"]) ?? definition.Architecture,
            ExecutableFileExtension = FirstConfigured(section["ExecutableFileExtension"], section["ExecutableExtension"], legacyArtifactContract["ExecutableFileExtension"]) ?? definition.ExecutableFileExtension,
            LibraryFileExtension = FirstConfigured(section["LibraryFileExtension"], section["LibraryExtension"], legacyArtifactContract["LibraryFileExtension"]) ?? definition.LibraryFileExtension,
            RequiredRuntimeFeatureTags = OptionalStringList(section.GetSection("RequiredRuntimeFeatureTags"), $"{keyPrefix}:RequiredRuntimeFeatureTags") ?? OptionalStringList(legacyArtifactContract.GetSection("RequiredRuntimeFeatureTags"), "RoslynWorker:ArtifactContract:RequiredRuntimeFeatureTags") ?? definition.RequiredRuntimeFeatureTags,
            MetadataFeatureTags = OptionalStringList(section.GetSection("MetadataFeatureTags"), $"{keyPrefix}:MetadataFeatureTags") ?? OptionalStringList(legacyArtifactContract.GetSection("MetadataFeatureTags"), "RoslynWorker:ArtifactContract:MetadataFeatureTags") ?? definition.MetadataFeatureTags,
            CompatibilityGroup = FirstConfigured(section["CompatibilityGroup"], legacyArtifactContract["CompatibilityGroup"])
        };
        definition.Validate();
        return definition;
    }

    private static string? FirstConfigured(params string?[] values) => values.FirstOrDefault(static value => value is not null);

    private static string Required(string? value, string key) =>
        !string.IsNullOrWhiteSpace(value)
            ? value : throw new InvalidOperationException($"Configuration value '{key}' is required.");

    private static string? Optional(string? value, string key)
    {
        if (value is null)
            return null;
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Configuration value '{key}' cannot be empty.");
        return value;
    }

    private static void ValidateArtifactContract(RoslynWorkerIdentity identity)
    {
        if (identity.ArtifactFormat is not ("dotnet-managed-pe-v1" or "dotnet-framework-managed-pe-v1"))
        {
            throw new InvalidOperationException("RoslynWorker:ArtifactContract:Format must be 'dotnet-managed-pe-v1' or 'dotnet-framework-managed-pe-v1'.");
        }
        if (string.IsNullOrWhiteSpace(identity.ArtifactRuntimeFamily))
            throw new InvalidOperationException("RoslynWorker:ArtifactContract:RuntimeFamily cannot be empty.");
        if (string.IsNullOrWhiteSpace(identity.ArtifactFrameworkName))
            throw new InvalidOperationException("RoslynWorker:ArtifactContract:FrameworkName cannot be empty.");
        if (identity.ArtifactArchitecture is not ("anycpu" or "x64"))
        {
            throw new InvalidOperationException("RoslynWorker:ArtifactContract:Architecture must be 'anycpu' or 'x64'.");
        }
        ValidateFileExtension(identity.ExecutableFileExtension, "ExecutableFileExtension");
        ValidateFileExtension(identity.LibraryFileExtension, "LibraryFileExtension");

        if (StringComparer.Ordinal.Equals(identity.ArtifactFormat, "dotnet-framework-managed-pe-v1") && (!StringComparer.Ordinal.Equals(identity.ArtifactRuntimeFamily, "netfx-clr-wine") || !StringComparer.Ordinal.Equals(identity.ArtifactFrameworkName, ".NETFramework") || !StringComparer.Ordinal.Equals(identity.ArtifactFrameworkVersion, "4.8") || !StringComparer.Ordinal.Equals(identity.ExecutableFileExtension, ".exe") || !StringComparer.Ordinal.Equals(identity.LibraryFileExtension, ".dll")))
        {
            throw new InvalidOperationException("The framework managed-PE contract requires netfx-clr-wine, .NETFramework 4.8, .exe applications, and .dll libraries.");
        }
    }

    private static void ValidateFileExtension(string value, string key)
    {
        if (value is not (".dll" or ".exe"))
        {
            throw new InvalidOperationException($"RoslynWorker:ArtifactContract:{key} must be '.dll' or '.exe'.");
        }
    }

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

    private static bool? NullableBoolean(string? value, string key)
    {
        if (value is null)
            return null;
        if (!bool.TryParse(value, out var parsed))
            throw new InvalidOperationException($"Configuration value '{key}' must be a boolean.");
        return parsed;
    }

    private static string[]? OptionalStringList(IConfigurationSection section, string key)
    {
        if (!section.Exists())
            return null;
        return StringList(section, [], requireAtLeastOne: false, key);
    }

    private static string[] StringList(IConfigurationSection section, IReadOnlyList<string> fallback, bool requireAtLeastOne, string? displayPath = null)
    {
        var path = displayPath ?? section.Path;
        var values = section.GetChildren().Select(child => child.Value).Where(static value => value is not null).Select(static value => value!).ToArray();
        if (values.Length == 0)
            values = fallback.ToArray();
        if (requireAtLeastOne && values.Length == 0)
            throw new InvalidOperationException($"Configuration section '{path}' must contain at least one value.");
        if (values.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException($"Configuration section '{path}' cannot contain empty values.");
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new InvalidOperationException($"Configuration section '{path}' cannot contain duplicate values.");

        return values;
    }
}
