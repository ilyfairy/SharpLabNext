using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpLabNext.Catalog;

public static class CatalogLoader
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static async Task<CatalogDocument> LoadCatalogAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var catalog = await JsonSerializer.DeserializeAsync<CatalogDocument>(
            stream,
            JsonOptions,
            cancellationToken);
        if (catalog is null)
        {
            throw new CatalogValidationException(["Catalog document is empty."]);
        }

        CatalogValidator.ValidateAndThrow(catalog);
        return catalog;
    }

    public static async Task<ReleaseLockDocument> LoadReleaseLockAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var releaseLock = await JsonSerializer.DeserializeAsync<ReleaseLockDocument>(
            stream,
            JsonOptions,
            cancellationToken);
        if (releaseLock is null)
        {
            throw new CatalogValidationException(["Release lock document is empty."]);
        }

        var errors = new List<string>();
        if (releaseLock.SchemaVersion != 1)
        {
            errors.Add($"Unsupported release lock schema version {releaseLock.SchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(releaseLock.ReleaseId))
        {
            errors.Add("Release lock releaseId is required.");
        }

        if (errors.Count > 0)
        {
            throw new CatalogValidationException(errors);
        }

        return releaseLock;
    }

    internal static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }
}

public sealed class CatalogValidationException(IReadOnlyList<string> errors)
    : Exception(string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
