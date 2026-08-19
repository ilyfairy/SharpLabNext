using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace SharpLabNext.Http;

/// <summary>
/// Reads query parameters owned by SharpLabNext with an exact PascalCase
/// spelling. ASP.NET's default query value provider is case-insensitive, so
/// binding a parameter named <c>FromSequence</c> alone would accidentally
/// keep accepting the retired <c>fromSequence</c> spelling.
/// </summary>
public static class PascalCaseQuery
{
    public static bool TryGetOptionalSingle(
        HttpRequest request,
        string name,
        out string? value)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var matchingKeys = request.Query.Keys
            .Where(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matchingKeys.Length == 0)
        {
            value = null;
            return true;
        }

        // Reject both the old lower-camel spelling and any other casing
        // variant instead of letting the framework normalize it silently.
        if (matchingKeys.Any(key => !string.Equals(key, name, StringComparison.Ordinal)))
        {
            value = null;
            return false;
        }

        var values = request.Query[matchingKeys[0]];
        if (values.Count != 1)
        {
            value = null;
            return false;
        }

        value = values[0];
        return true;
    }

    public static bool TryGetOptionalInt32(
        HttpRequest request,
        string name,
        out int? value)
    {
        if (!TryGetOptionalSingle(request, name, out var raw))
        {
            value = null;
            return false;
        }

        if (raw is null)
        {
            value = null;
            return true;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            value = null;
            return false;
        }

        value = parsed;
        return true;
    }

    public static bool TryGetOptionalInt64(
        HttpRequest request,
        string name,
        out long? value)
    {
        if (!TryGetOptionalSingle(request, name, out var raw))
        {
            value = null;
            return false;
        }

        if (raw is null)
        {
            value = null;
            return true;
        }

        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            value = null;
            return false;
        }

        value = parsed;
        return true;
    }
}
