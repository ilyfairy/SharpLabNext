using System.Globalization;

namespace SharpLabNext.Worker.JSharp;

public static class JSharpToolchain
{
    public const string LanguageId = "jsharp";
    public const string ToolchainId = "vjc-jsharp20";
    public const string ReferenceSetId = "jsharp20-ref";
    public const string ArtifactFormat = "dotnet-framework-managed-pe-v1";
    public const string TargetFramework = "net20";
    public const string FrameworkName = ".NETFramework";
    public const string FrameworkVersion = "2.0";
    public const string RuntimeFamily = "netfx-clr-wine";
    public const string RuntimeFeatureTag = "runtime.jsharp20-wine";
    public const string Architecture = "x64";
    public const string WinePrefixPath = "/opt/wine-jsharp20";
    public const string WineArchitecture = "win64";
    public const string AssemblyName = "SharpLabNext.User";
    public const string OutputFileName = AssemblyName + ".exe";
}

public sealed record JSharpWorkerIdentity(
    string ReleaseId,
    string CompilerVersion,
    string? CompilerCommit,
    string WorkerImageId)
{
    public SharpLabNext.Contracts.BuildIdentity CreateBuildIdentity() => new(
        ReleaseId,
        JSharpToolchain.LanguageId,
        JSharpToolchain.ToolchainId,
        CompilerVersion,
        CompilerCommit,
        JSharpToolchain.ReferenceSetId,
        WorkerImageId);
}

public sealed record JSharpProcessLimits(
    int MaximumProcessOutputBytes,
    long MaximumProcessWorkingSetBytes,
    int MaximumDiagnostics,
    int MemoryPollIntervalMilliseconds);

public sealed record JSharpReferenceSetIdentity(
    string Digest,
    string ContentDigest,
    string SourceUri)
{
    public SharpLabNext.Contracts.ReferenceSetAttestation CreateAttestation() => new(
        JSharpToolchain.ReferenceSetId,
        JSharpToolchain.TargetFramework,
        Digest,
        ContentDigest,
        new SharpLabNext.Contracts.ReferenceSetProvenance(
            "operator-image",
            JSharpToolchain.FrameworkVersion,
            SourceUri: SourceUri,
            SourceArchiveDigest: Digest));
}

public sealed record JSharpWorkerSettings(
    JSharpWorkerIdentity Identity,
    JSharpReferenceSetIdentity ReferenceSet,
    JSharpProcessLimits ProcessLimits,
    string WorkRoot,
    string CompilerHostPath,
    string CompilerPath)
{
    public static JSharpWorkerSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("JSharp");
        var compilerHostPath = RequiredAbsolutePath(
            section["CompilerHostPath"] ?? "/usr/bin/wine-stable",
            "JSharp:CompilerHostPath");
        var compilerPath = RequiredAbsolutePath(
            section["CompilerPath"] ?? "/opt/sharplabnext/jsharp20/vjc.exe",
            "JSharp:CompilerPath");

        var maximumProcessOutputBytes = BoundedInt(
            section["MaximumProcessOutputBytes"],
            1024 * 1024,
            4 * 1024,
            16 * 1024 * 1024,
            "JSharp:MaximumProcessOutputBytes");
        var maximumProcessWorkingSetBytes = BoundedLong(
            section["MaximumProcessWorkingSetBytes"],
            512L * 1024 * 1024,
            64L * 1024 * 1024,
            8L * 1024 * 1024 * 1024,
            "JSharp:MaximumProcessWorkingSetBytes");
        var maximumDiagnostics = BoundedInt(
            section["MaximumDiagnostics"],
            100,
            1,
            1_000,
            "JSharp:MaximumDiagnostics");
        var memoryPollIntervalMilliseconds = BoundedInt(
            section["MemoryPollIntervalMilliseconds"],
            25,
            10,
            1_000,
            "JSharp:MemoryPollIntervalMilliseconds");

        var referenceSetDigest = RequiredSha256(
            section["ReferenceSetDigest"],
            "JSharp:ReferenceSetDigest");
        return new JSharpWorkerSettings(
            new JSharpWorkerIdentity(
                RequiredIdentity(section["ReleaseId"] ?? "development", "JSharp:ReleaseId"),
                RequiredIdentity(section["CompilerVersion"], "JSharp:CompilerVersion"),
                OptionalCommit(section["CompilerCommit"]),
                RequiredSha256(
                    section["WorkerImageId"] ?? $"sha256:{new string('0', 64)}",
                    "JSharp:WorkerImageId")),
            new JSharpReferenceSetIdentity(
                referenceSetDigest,
                RequiredSha256(
                    section["ReferenceSetContentDigest"] ?? referenceSetDigest,
                    "JSharp:ReferenceSetContentDigest"),
                RequiredSourceUri(section["ReferenceSetSourceUri"])),
            new JSharpProcessLimits(
                maximumProcessOutputBytes,
                maximumProcessWorkingSetBytes,
                maximumDiagnostics,
                memoryPollIntervalMilliseconds),
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(
                section["WorkRoot"] ?? Path.Combine(Path.GetTempPath(), "sharplabnext-jsharp"))),
            compilerHostPath,
            compilerPath);
    }

    private static string RequiredAbsolutePath(string value, string key)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value);
        if (!Path.IsPathFullyQualified(expanded) || expanded.Contains('\0'))
            throw new InvalidOperationException($"Configuration value '{key}' must be an absolute host path.");
        return Path.GetFullPath(expanded);
    }

    private static string RequiredIdentity(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 128 ||
            value.Any(static character => char.IsControl(character)))
        {
            throw new InvalidOperationException(
                $"Configuration value '{key}' is required and must be a bounded identity string.");
        }
        return value;
    }

    private static string? OptionalCommit(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (value.Length != 40 || value.Any(static character => !char.IsAsciiHexDigit(character)))
        {
            throw new InvalidOperationException(
                "Configuration value 'JSharp:CompilerCommit' must be empty or a 40-character Git commit.");
        }
        return value;
    }

    private static string RequiredSha256(string? value, string key)
    {
        if (value is null || value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal))
            throw InvalidDigest(key);
        foreach (var character in value.AsSpan(7))
        {
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                throw InvalidDigest(key);
        }
        return value;
    }

    private static InvalidOperationException InvalidDigest(string key) => new(
        $"Configuration value '{key}' must be a lowercase SHA-256 digest.");

    private static string RequiredSourceUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 ||
            value.Any(static character => char.IsControl(character) || char.IsWhiteSpace(character)) ||
            !Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException(
                "Configuration value 'JSharp:ReferenceSetSourceUri' must be an absolute source URI.");
        }
        return value;
    }

    private static int BoundedInt(string? value, int fallback, int minimum, int maximum, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < minimum || parsed > maximum)
        {
            throw new InvalidOperationException(
                $"Configuration value '{key}' must be between {minimum} and {maximum}.");
        }
        return parsed;
    }

    private static long BoundedLong(string? value, long fallback, long minimum, long maximum, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < minimum || parsed > maximum)
        {
            throw new InvalidOperationException(
                $"Configuration value '{key}' must be between {minimum} and {maximum}.");
        }
        return parsed;
    }
}
