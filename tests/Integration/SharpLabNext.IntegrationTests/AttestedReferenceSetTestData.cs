using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpLabNext.Catalog;
using SharpLabNext.WorkerHost;

namespace SharpLabNext.IntegrationTests;

internal sealed class AttestedReferenceSetTestData : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly IReadOnlyDictionary<string, string> paths;

    private AttestedReferenceSetTestData(string root, IReadOnlyDictionary<string, string> paths)
    {
        Root = root;
        this.paths = paths;
    }

    public string Root { get; }

    public string PathFor(string referenceSetId) => paths[referenceSetId];

    public void AddToEnvironment(
        IDictionary<string, string?> environment,
        params string[] referenceSetIds)
    {
        foreach (var referenceSetId in referenceSetIds)
        {
            var path = PathFor(referenceSetId);
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
                path,
                ReferenceSetAttestationReader.ManifestFileName)));
            var referenceSet = document.RootElement.GetProperty("referenceSet");
            var prefix = $"ReferenceSets__{referenceSetId}__";
            environment[prefix + "Path"] = path;
            environment[prefix + "TargetFramework"] =
                referenceSet.GetProperty("targetFramework").GetString();
            environment[prefix + "FrameworkVersion"] = referenceSet
                .GetProperty("provenance")
                .GetProperty("resolvedVersion")
                .GetString();
            environment[prefix + "Digest"] = referenceSet.GetProperty("digest").GetString();
            environment[prefix + "IncludeSharpLabRuntime"] =
                File.Exists(Path.Combine(path, "SharpLab.Runtime.dll")) ? "true" : "false";
        }
    }

    public static async Task<AttestedReferenceSetTestData> CreateAsync(CancellationToken cancellationToken)
    {
        var repositoryRoot = FindRepositoryRoot();
        var releaseLock = await CatalogLoader.LoadReleaseLockAsync(
            Path.Combine(repositoryRoot, "profiles", "lock.json"),
            cancellationToken);
        var root = Path.Combine(
            Path.GetTempPath(),
            $"SharpLabNext.AttestedReferenceSets.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            await CreateReferenceSetAsync(
                root,
                "net10-ref",
                "net10.0",
                releaseLock.Components["net10-ref"],
                cancellationToken);
            await CreateReferenceSetAsync(
                root,
                "net11-preview-ref",
                "net11.0",
                releaseLock.Components["net11-preview-ref"],
                cancellationToken);
            paths["net10-ref"] = Path.Combine(root, "net10-ref");
            paths["net11-preview-ref"] = Path.Combine(root, "net11-preview-ref");
            return new AttestedReferenceSetTestData(root, paths);
        }
        catch
        {
            Directory.Delete(root, recursive: true);
            throw;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }

    private static async Task CreateReferenceSetAsync(
        string root,
        string id,
        string targetFramework,
        LockedComponent component,
        CancellationToken cancellationToken)
    {
        var source = FindReferencePath(id, component.ResolvedVersion, targetFramework);
        var destination = Path.Combine(root, id);
        Directory.CreateDirectory(destination);
        foreach (var path in Directory.EnumerateFiles(source, "*.dll", SearchOption.TopDirectoryOnly))
        {
            File.Copy(path, Path.Combine(destination, Path.GetFileName(path)));
        }
        File.Copy(
            typeof(SharpLab.Runtime.RuntimeServices).Assembly.Location,
            Path.Combine(destination, "SharpLab.Runtime.dll"),
            overwrite: true);

        var files = new List<AttestedFile>();
        foreach (var path in Directory.EnumerateFiles(destination, "*.dll", SearchOption.TopDirectoryOnly)
                     .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal))
        {
            await using var stream = File.OpenRead(path);
            files.Add(new AttestedFile(
                Path.GetFileName(path),
                stream.Length,
                $"sha256:{Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant()}"));
        }

        var canonical = new StringBuilder();
        foreach (var file in files)
        {
            canonical.Append(file.Digest)
                .Append("  ")
                .Append(file.Size)
                .Append("  ")
                .Append(file.Path)
                .Append('\n');
        }
        var contentDigest = $"sha256:{Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant()}";
        var document = new AttestationDocument(
            1,
            new AttestedReferenceSet(
                id,
                targetFramework,
                component.PackageContentHash
                    ?? throw new InvalidDataException($"Reference set '{id}' has no package content hash."),
                contentDigest,
                new AttestedProvenance(
                    "nuget-package",
                    component.ResolvedVersion,
                    component.Package,
                    component.SourceUri,
                    $"sha512:{component.Sha512}")),
            files);
        await File.WriteAllTextAsync(
            Path.Combine(destination, ReferenceSetAttestationReader.ManifestFileName),
            JsonSerializer.Serialize(document, JsonOptions) + "\n",
            cancellationToken);
    }

    private static string FindReferencePath(string id, string version, string targetFramework)
    {
        var materializedRoot = Environment.GetEnvironmentVariable(
            "SHARPLABNEXT_TEST_CORECLR_REFERENCE_SETS");
        if (!string.IsNullOrWhiteSpace(materializedRoot))
        {
            var materialized = Path.Combine(materializedRoot, id);
            if (Directory.Exists(materialized))
                return materialized;
        }

        var roots = new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
            "/usr/share/dotnet",
            "/usr/local/share/dotnet"
        };
        foreach (var root in roots.Where(static root => !string.IsNullOrWhiteSpace(root)))
        {
            var candidate = Path.Combine(
                root!,
                "packs",
                "Microsoft.NETCore.App.Ref",
                version,
                "ref",
                targetFramework);
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException(
            $"The {targetFramework} reference pack {version} was not found.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SharpLabNext.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed record AttestationDocument(
        int SchemaVersion,
        AttestedReferenceSet ReferenceSet,
        IReadOnlyList<AttestedFile> Files);

    private sealed record AttestedReferenceSet(
        string Id,
        string TargetFramework,
        string Digest,
        string ContentDigest,
        AttestedProvenance Provenance);

    private sealed record AttestedProvenance(
        string Kind,
        string ResolvedVersion,
        string? Package,
        string? SourceUri,
        string SourceArchiveDigest);

    private sealed record AttestedFile(string Path, long Size, string Digest);
}
