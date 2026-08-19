using System.Text.Json;

namespace SharpLabNext.BundleBuilder;

internal static class RuntimePromotionJson
{
    public static T Deserialize<T>(
        byte[] bytes,
        JsonSerializerOptions serializerOptions,
        string documentDescription)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            if (ContainsExplicitNull(document.RootElement))
            {
                throw new BundleValidationException(
                    $"{documentDescription} cannot contain explicit JSON null values; " +
                    "optional properties must be omitted.");
            }

            return document.RootElement.Deserialize<T>(serializerOptions)
                ?? throw new JsonException("The document is empty.");
        }
        catch (JsonException exception)
        {
            throw new BundleValidationException(
                $"{documentDescription} is invalid JSON: {exception.Message}");
        }
    }

    private static bool ContainsExplicitNull(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().Any(ContainsExplicitNull);
        if (element.ValueKind == JsonValueKind.Object)
        {
            return element.EnumerateObject()
                .Any(static property => ContainsExplicitNull(property.Value));
        }
        return false;
    }
}
