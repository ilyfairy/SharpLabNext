using System.Globalization;

namespace SharpLabNext.Worker.CppCli;

public static class CppCliToolchain
{
    public const string LanguageId = "cppcli";
    public const string ToolchainId = "msvc-cppcli-netfx48";
    public const string ReferenceSetId = "netfx48-ref";
    public const string ArtifactFormat = "dotnet-framework-mixed-pe-v1";
    public const string TargetFramework = "net48";
    public const string FrameworkName = ".NETFramework";
    public const string FrameworkVersion = "4.8";
    public const string RuntimeFamily = "netfx-clr-wine";
    public const string AssemblyName = "SharpLabNext.User";
    public const string OutputFileName = AssemblyName + ".exe";
}

public sealed record CppCliWorkerIdentity(string ReleaseId, string CompilerVersion, string? CompilerCommit, string WorkerImageId)
{
    public SharpLabNext.Contracts.BuildIdentity CreateBuildIdentity() => new(ReleaseId, CppCliToolchain.LanguageId, CppCliToolchain.ToolchainId, CompilerVersion, CompilerCommit, CppCliToolchain.ReferenceSetId, WorkerImageId);
}

public sealed record CppCliProcessLimits(int MaximumProcessOutputBytes, long MaximumProcessWorkingSetBytes, int MaximumDiagnostics);

public sealed record CppCliReferenceSetIdentity(string Digest, string ContentDigest, string SourceUri)
{
    public SharpLabNext.Contracts.ReferenceSetAttestation CreateAttestation() => new(CppCliToolchain.ReferenceSetId, CppCliToolchain.TargetFramework, Digest, ContentDigest, new SharpLabNext.Contracts.ReferenceSetProvenance("operator-image", CppCliToolchain.FrameworkVersion, SourceUri: SourceUri));
}

public sealed record CppCliWorkerSettings(CppCliWorkerIdentity Identity, CppCliReferenceSetIdentity ReferenceSet, CppCliProcessLimits ProcessLimits, string WorkRoot, string CompilerPath)
{
    public static CppCliWorkerSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("CppCli");
        var compilerPath = Environment.ExpandEnvironmentVariables(section["CompilerPath"] ?? "/opt/msvc/bin/x64/cl");
        if (!Path.IsPathRooted(compilerPath)) throw new InvalidOperationException("Configuration value 'CppCli:CompilerPath' must be an absolute path.");

        var maximumProcessOutputBytes = PositiveInt(section["MaximumProcessOutputBytes"], 1024 * 1024, "CppCli:MaximumProcessOutputBytes");
        var maximumProcessWorkingSetBytes = PositiveLong(section["MaximumProcessWorkingSetBytes"], 1024L * 1024 * 1024, "CppCli:MaximumProcessWorkingSetBytes");
        var maximumDiagnostics = PositiveInt(section["MaximumDiagnostics"], 100, "CppCli:MaximumDiagnostics");
        if (maximumDiagnostics > 1_000) throw new InvalidOperationException("Configuration value 'CppCli:MaximumDiagnostics' cannot exceed 1000.");

        var referenceSetDigest = RequiredSha256(section["ReferenceSetDigest"], "CppCli:ReferenceSetDigest");
        return new CppCliWorkerSettings(
            new CppCliWorkerIdentity(section["ReleaseId"] ?? "content", RequiredIdentity(section["CompilerVersion"], "CppCli:CompilerVersion"), OptionalCommit(section["CompilerCommit"]), section["WorkerImageId"] ?? $"sha256:{new string('0', 64)}"),
            new CppCliReferenceSetIdentity(referenceSetDigest, RequiredSha256(section["ReferenceSetContentDigest"] ?? referenceSetDigest, "CppCli:ReferenceSetContentDigest"), RequiredSourceUri(section["ReferenceSetSourceUri"])),
            new CppCliProcessLimits(maximumProcessOutputBytes, maximumProcessWorkingSetBytes, maximumDiagnostics),
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(section["WorkRoot"] ?? Path.Combine(Path.GetTempPath(), "sharplabnext-cppcli"))),
            Path.GetFullPath(compilerPath));
    }

    private static string RequiredIdentity(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(static character => char.IsControl(character)))
        {
            throw new InvalidOperationException($"Configuration value '{key}' is required and must be a bounded identity string.");
        }
        return value;
    }

    private static string? OptionalCommit(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (value.Length != 40 || value.Any(static character => !char.IsAsciiHexDigit(character)))
            throw new InvalidOperationException("Configuration value 'CppCli:CompilerCommit' must be empty or a 40-character Git commit.");
        return value;
    }

    private static string RequiredSha256(string? value, string key)
    {
        if (value is null || value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Configuration value '{key}' must be a lowercase SHA-256 digest.");
        }
        foreach (var character in value.AsSpan(7))
        {
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                throw new InvalidOperationException($"Configuration value '{key}' must be a lowercase SHA-256 digest.");
            }
        }
        return value;
    }

    private static string RequiredSourceUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(static character => char.IsControl(character) || char.IsWhiteSpace(character)) || !Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("Configuration value 'CppCli:ReferenceSetSourceUri' must be an absolute source URI.");
        }
        return value;
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
}
