using System.Net.Http.Json;
using System.Text.Json;
using SharpLabNext.Catalog;
using SharpLabNext.Contracts;

namespace SharpLabNext.IntegrationTests;

internal static class GatewayTestCatalog
{
    private const string TestReferenceSetRootEnvironmentVariable = "SHARPLABNEXT_TEST_CORECLR_REFERENCE_SETS";

    public static async Task<CatalogDocument> GetAsync(HttpClient client)
    {
        return await client.GetFromJsonAsync<CatalogDocument>("/api/v1/catalog", ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken) ?? throw new InvalidOperationException("Gateway catalog response was empty.");
    }

    public static async Task<string> GetRevisionAsync(HttpClient client)
    {
        return (await GetAsync(client)).Revision;
    }

    public static async Task<CatalogDocument> LoadRepositoryAsync(CancellationToken cancellationToken)
    {
        var repositoryRoot = FindRepositoryRoot();
        var catalog = await CatalogLoader.LoadCatalogAsync(Path.Combine(repositoryRoot, "profiles", "catalog", "catalog.json"), cancellationToken);
        var releaseLock = await CatalogLoader.LoadReleaseLockAsync(Path.Combine(repositoryRoot, "profiles", "lock.json"), cancellationToken);
        if (!string.Equals(catalog.ReleaseId, releaseLock.ReleaseId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Test Catalog release '{catalog.ReleaseId}' does not match release lock '{releaseLock.ReleaseId}'.");
        }

        return catalog;
    }

    public static void AddRoslynStableReferenceSets(IDictionary<string, string?> environment, CatalogDocument catalog)
    {
        var root = Environment.GetEnvironmentVariable(TestReferenceSetRootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Run eng/test.ps1 or eng/test.sh to materialize the locked CoreCLR reference sets " + $"and set {TestReferenceSetRootEnvironmentVariable}.");
        }

        var toolchain = catalog.Toolchains.Single(static item => item.Id == "roslyn-stable");
        var references = catalog.ReferenceSets.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        foreach (var referenceSetId in toolchain.AllowedReferenceSetIds)
        {
            if (!references.TryGetValue(referenceSetId, out var expected))
                throw new InvalidOperationException($"Catalog reference set '{referenceSetId}' is missing.");
            var path = Path.Combine(root, referenceSetId);
            var manifestPath = Path.Combine(path, "reference-set.attestation.json");
            if (!Directory.Exists(path) || !File.Exists(manifestPath))
                throw new DirectoryNotFoundException($"Materialized reference set '{referenceSetId}' is missing.");

            using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            var referenceSet = document.RootElement.GetProperty("referenceSet");
            var id = referenceSet.GetProperty("id").GetString();
            var targetFramework = referenceSet.GetProperty("targetFramework").GetString();
            var digest = referenceSet.GetProperty("digest").GetString();
            var resolvedVersion = referenceSet.GetProperty("provenance").GetProperty("resolvedVersion").GetString();
            if (!string.Equals(id, referenceSetId, StringComparison.Ordinal) || !string.Equals(targetFramework, expected.TargetFramework, StringComparison.Ordinal) || !string.Equals(digest, expected.Digest, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(resolvedVersion))
            {
                throw new InvalidDataException($"Materialized reference set '{referenceSetId}' does not match the active Catalog.");
            }

            var prefix = $"ReferenceSets__{referenceSetId}__";
            environment[prefix + "Path"] = path;
            environment[prefix + "TargetFramework"] = targetFramework;
            environment[prefix + "FrameworkVersion"] = resolvedVersion;
            environment[prefix + "Digest"] = digest;
            environment[prefix + "IncludeSharpLabRuntime"] =
                File.Exists(Path.Combine(path, "SharpLab.Runtime.dll")) ? "true" : "false";
        }
    }

    internal static string FindRepositoryRoot()
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
}
