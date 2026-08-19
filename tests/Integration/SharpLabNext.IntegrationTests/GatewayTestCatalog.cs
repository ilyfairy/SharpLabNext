using System.Net.Http.Json;
using SharpLabNext.Catalog;
using SharpLabNext.Contracts;

namespace SharpLabNext.IntegrationTests;

internal static class GatewayTestCatalog
{
    public static async Task<CatalogDocument> GetAsync(HttpClient client)
    {
        return await client.GetFromJsonAsync<CatalogDocument>(
            "/api/v1/catalog",
            ContractJson.CreateSerializerOptions(),
            TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("Gateway catalog response was empty.");
    }

    public static async Task<string> GetRevisionAsync(HttpClient client)
    {
        return (await GetAsync(client)).Revision;
    }

    public static async Task<CatalogDocument> LoadRepositoryAsync(CancellationToken cancellationToken)
    {
        var repositoryRoot = FindRepositoryRoot();
        var catalog = await CatalogLoader.LoadCatalogAsync(
            Path.Combine(repositoryRoot, "profiles", "catalog", "catalog.json"),
            cancellationToken);
        var releaseLock = await CatalogLoader.LoadReleaseLockAsync(
            Path.Combine(repositoryRoot, "profiles", "lock.json"),
            cancellationToken);
        if (!string.Equals(catalog.ReleaseId, releaseLock.ReleaseId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Test Catalog release '{catalog.ReleaseId}' does not match release lock '{releaseLock.ReleaseId}'.");
        }

        return catalog;
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
}
