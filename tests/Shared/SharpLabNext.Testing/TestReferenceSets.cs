using System.Text.Json;

namespace SharpLabNext.Testing;

public sealed record TestReferenceSet(string Id, string TargetFramework, string Path, string Version, string Digest);

public static class TestReferenceSets
{
    private const string MaterializedRootVariable = "SHARPLABNEXT_TEST_CORECLR_REFERENCE_SETS";
    private const string ManifestFileName = "reference-set.attestation.json";
    private static readonly Lazy<TestReferenceSet> Net10Value = new(() => Resolve("net10-ref", "net10.0", "SHARPLABNEXT_NET10_REF_PATH"));
    private static readonly Lazy<TestReferenceSet> Net11Value = new(() => Resolve("net11-preview-ref", "net11.0", "SHARPLABNEXT_NET11_REF_PATH"));

    public static TestReferenceSet Net10 => Net10Value.Value;

    public static TestReferenceSet Net11 => Net11Value.Value;

    public static TestReferenceSet Resolve(string id, string targetFramework, string explicitPathVariable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFramework);
        ArgumentException.ThrowIfNullOrWhiteSpace(explicitPathVariable);

        var path = ResolvePath(id, targetFramework, explicitPathVariable);
        var manifestPath = Path.Combine(path, ManifestFileName);
        if (File.Exists(manifestPath))
            return ReadManifest(manifestPath, path, id, targetFramework);

        var versionDirectory = Directory.GetParent(path)?.Parent;
        var version = versionDirectory?.Name;
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException($"Reference set '{id}' at '{path}' has no attestation and its package version cannot be inferred.");
        }

        return new TestReferenceSet(id, targetFramework, path, version, $"content-{id}");
    }

    private static string ResolvePath(string id, string targetFramework, string explicitPathVariable)
    {
        var materializedRoot = Environment.GetEnvironmentVariable(MaterializedRootVariable);
        if (!string.IsNullOrWhiteSpace(materializedRoot))
        {
            var materialized = Path.Combine(materializedRoot, id);
            if (Directory.Exists(materialized))
                return Path.GetFullPath(materialized);
        }

        var explicitPath = Environment.GetEnvironmentVariable(explicitPathVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath) && Directory.Exists(explicitPath))
            return Path.GetFullPath(explicitPath);

        foreach (var root in DotNetRoots())
        {
            var packRoot = Path.Combine(root, "packs", "Microsoft.NETCore.App.Ref");
            if (!Directory.Exists(packRoot))
                continue;

            var match = Directory.EnumerateDirectories(packRoot).Select(static path => new DirectoryInfo(path)).Where(directory => Directory.Exists(Path.Combine(directory.FullName, "ref", targetFramework))).OrderByDescending(static directory => VersionPrefix(directory.Name)).ThenByDescending(static directory => directory.Name, StringComparer.Ordinal).FirstOrDefault();
            if (match is not null)
                return Path.Combine(match.FullName, "ref", targetFramework);
        }

        throw new InvalidOperationException($"Reference set '{id}' for '{targetFramework}' was not found. " + $"Set {MaterializedRootVariable} or {explicitPathVariable}.");
    }

    private static IEnumerable<string> DotNetRoots()
    {
        var values = new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
            "/usr/share/dotnet",
            "/usr/local/share/dotnet"
        };
        return values.Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => Path.GetFullPath(value!)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static Version VersionPrefix(string value)
    {
        var separator = value.IndexOf('-');
        var prefix = separator >= 0 ? value[..separator] : value;
        return Version.TryParse(prefix, out var version) ? version : new Version(0, 0);
    }

    private static TestReferenceSet ReadManifest(string manifestPath, string path, string expectedId, string expectedTargetFramework)
    {
        using var stream = File.OpenRead(manifestPath);
        using var document = JsonDocument.Parse(stream);
        var referenceSet = document.RootElement.GetProperty("referenceSet");
        var id = referenceSet.GetProperty("id").GetString();
        var targetFramework = referenceSet.GetProperty("targetFramework").GetString();
        var version = referenceSet.GetProperty("provenance").GetProperty("resolvedVersion").GetString();
        var digest = referenceSet.GetProperty("digest").GetString();
        if (!string.Equals(id, expectedId, StringComparison.Ordinal) || !string.Equals(targetFramework, expectedTargetFramework, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(digest))
        {
            throw new InvalidDataException($"Reference set manifest '{manifestPath}' does not match '{expectedId}/{expectedTargetFramework}'.");
        }

        return new TestReferenceSet(expectedId, expectedTargetFramework, path, version, digest);
    }
}
