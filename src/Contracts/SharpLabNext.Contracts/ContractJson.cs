using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SharpLabNext.Contracts;

public static class ContractJson
{
    /// <summary>
    /// Creates options for SharpLabNext business/frontend contracts. All
    /// objects serialized with these options are on a SharpLabNext business
    /// HTTP or operation-control boundary, so their member names are emitted
    /// in PascalCase. Internal child-process, LSP, Docker, GitHub, and
    /// persistence protocols use their own options and do not call this API.
    /// Enum values are still controlled by their explicit kebab-case
    /// converters.
    /// </summary>
    public static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = PascalCaseJsonNamingPolicy.Instance,
            // Dictionary keys are data (file names, syntax property names,
            // metadata keys), not contract member names; preserve them.
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // The public boundary has one canonical shape. Older camelCase
            // payloads are intentionally rejected instead of being silently
            // normalized.
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false
        };
        // Register the polymorphic boundary converters used by the business
        // operation stream. Their discriminators are PascalCase property
        // names and exact kebab-case values.
        options.Converters.Insert(0, OperationEventPayloadJsonConverter.Instance);
        options.Converters.Insert(1, OperationResultJsonConverter.Instance);
        return options;
    }

    /// <summary>
    /// Applies the business wire settings to an options instance owned by a
    /// host framework (for example ASP.NET's <c>HttpJsonOptions</c>).
    /// Keeping this in the contracts assembly prevents one endpoint from
    /// silently retaining the framework default camel-case dictionary policy.
    /// </summary>
    public static void ApplySerializerOptions(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var contractOptions = CreateSerializerOptions();
        options.PropertyNamingPolicy = PascalCaseJsonNamingPolicy.Instance;
        options.DictionaryKeyPolicy = contractOptions.DictionaryKeyPolicy;
        options.DefaultIgnoreCondition = contractOptions.DefaultIgnoreCondition;
        options.PropertyNameCaseInsensitive = false;
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.WriteIndented = contractOptions.WriteIndented;
        EnsureConverter(options, OperationEventPayloadJsonConverter.Instance);
        EnsureConverter(options, OperationResultJsonConverter.Instance);
    }

    private static void EnsureConverter<T>(JsonSerializerOptions options, JsonConverter<T> converter)
    {
        if (!options.Converters.Any(existing => ReferenceEquals(existing, converter)))
            options.Converters.Insert(0, converter);
    }

    /// <summary>
    /// Options for persisted files, content-addressed identities, and
    /// versioned configuration files. Their existing canonical shape is a
    /// separate storage/schema contract and remains Web camelCase. Internal
    /// service protocols that we control should use
    /// <see cref="CreateSerializerOptions"/> instead.
    /// </summary>
    public static JsonSerializerOptions CreateCanonicalSerializerOptions() => new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        WriteIndented = false
    };

    /// <summary>
    /// Creates options for standards-based LSP/JSON-RPC payloads.  LSP is a
    /// separate protocol and its lower camel-case member names must not follow
    /// the business contract naming convention.
    /// </summary>
    public static JsonSerializerOptions CreateLspSerializerOptions() => new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        WriteIndented = false
    };

    /// <summary>
    /// Reads a named JSON property at a business protocol boundary. Property
    /// names are exact; dynamic dictionary keys are never normalized.
    /// </summary>
    public static bool TryGetProperty(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        return element.TryGetProperty(propertyName, out value);
    }

    public static string? GetString(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// Canonical member naming for SharpLabNext-owned JSON records and envelopes.
/// It is deliberately not used by LSP, Docker, GitHub, or other external
/// protocol serializers.
/// </summary>
internal sealed class PascalCaseJsonNamingPolicy : JsonNamingPolicy
{
    public static PascalCaseJsonNamingPolicy Instance { get; } = new();

    public override string ConvertName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || char.IsUpper(name[0]))
            return name;
        return char.ToUpperInvariant(name[0]) + name[1..];
    }
}

internal sealed class OperationEventPayloadJsonConverter : JsonConverter<OperationEventPayload>
{
    public static OperationEventPayloadJsonConverter Instance { get; } = new();

    private static readonly Dictionary<string, Type> Types =
        new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["accepted"] = typeof(AcceptedOperationEventPayload),
            ["progress"] = typeof(ProgressOperationEventPayload),
            ["diagnostic"] = typeof(DiagnosticOperationEventPayload),
            ["output-chunk"] = typeof(OutputChunkOperationEventPayload),
            ["artifact-produced"] = typeof(ArtifactProducedOperationEventPayload),
            ["content-produced"] = typeof(ContentProducedOperationEventPayload),
            ["typed-result"] = typeof(TypedResultOperationEventPayload),
            ["output-truncated"] = typeof(OutputTruncatedOperationEventPayload),
            ["completed"] = typeof(CompletedOperationEventPayload),
            ["failed"] = typeof(FailedOperationEventPayload)
        };

    private static readonly Dictionary<Type, string> Names =
        Types.ToDictionary(static pair => pair.Value, static pair => pair.Key);

    public override OperationEventPayload Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Operation event payload must be a JSON object.");

        // A missing PascalCase discriminator is malformed input. In
        // particular, an older `kind` member must not silently turn into an
        // empty base payload. Unknown *values* remain forward-compatible and
        // intentionally use the ignorable base payload below.
        ContractJsonDiscriminatorValidation.RejectLegacyDiscriminatorAlias(
            root,
            "Kind",
            "operation event payload");
        if (!root.TryGetProperty("Kind", out var kind) ||
            kind.ValueKind != JsonValueKind.String ||
            kind.GetString() is not { } discriminator)
        {
            throw new JsonException("Operation event payload requires a PascalCase Kind discriminator.");
        }

        if (!Types.TryGetValue(discriminator, out var type))
            return new OperationEventPayload();

        // `Kind` is the envelope discriminator, not a member of the concrete
        // payload record. Remove it before strict unmapped-member validation.
        var payload = JsonNode.Parse(root.GetRawText())?.AsObject()
            ?? throw new JsonException("Operation event payload is empty.");
        payload.Remove("Kind");
        return (OperationEventPayload?)JsonSerializer.Deserialize(payload, type, options)
            ?? throw new JsonException("Operation event payload is empty.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        OperationEventPayload value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        var node = JsonSerializer.SerializeToNode(value, value.GetType(), options) as JsonObject
            ?? throw new JsonException("Operation event payload must serialize as an object.");
        if (Names.TryGetValue(value.GetType(), out var discriminator))
            node[GetWireName(options, "Kind")] = discriminator;
        node.WriteTo(writer, options);
    }

    private static string GetWireName(JsonSerializerOptions options, string name) =>
        options.PropertyNamingPolicy?.ConvertName(name) ?? name;
}

internal sealed class OperationResultJsonConverter : JsonConverter<OperationResult>
{
    public static OperationResultJsonConverter Instance { get; } = new();

    private static readonly Dictionary<string, Type> Types =
        new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["build"] = typeof(BuildResult),
            ["compile-check"] = typeof(CompilationCheckResult),
            ["ast"] = typeof(AstResult),
            ["generated-source"] = typeof(GeneratedSourceResult),
            ["artifact-transform"] = typeof(TransformArtifactResult),
            ["artifact-render"] = typeof(RenderArtifactResult),
            ["artifact-verification"] = typeof(VerifyArtifactResult),
            ["run"] = typeof(RunResult),
            ["jit"] = typeof(JitResult),
            ["explain"] = typeof(ExplainResult)
        };

    private static readonly Dictionary<Type, string> Names =
        Types.ToDictionary(static pair => pair.Value, static pair => pair.Key);

    public override OperationResult Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Operation result must be a JSON object.");

        // Keep unknown PascalCase values forward-compatible, but reject a
        // missing discriminator so legacy lower-camel `resultType` payloads
        // cannot be accepted as an empty result.
        ContractJsonDiscriminatorValidation.RejectLegacyDiscriminatorAlias(
            root,
            "ResultType",
            "operation result");
        if (!root.TryGetProperty("ResultType", out var resultType) ||
            resultType.ValueKind != JsonValueKind.String ||
            resultType.GetString() is not { } discriminator)
        {
            throw new JsonException("Operation result requires a PascalCase ResultType discriminator.");
        }

        if (!Types.TryGetValue(discriminator, out var type))
            return new OperationResult();

        // `ResultType` belongs to the polymorphic envelope, not the concrete
        // result record. Remove it before strict unmapped-member validation.
        var result = JsonNode.Parse(root.GetRawText())?.AsObject()
            ?? throw new JsonException("Operation result is empty.");
        result.Remove("ResultType");
        return (OperationResult?)JsonSerializer.Deserialize(result, type, options)
            ?? throw new JsonException("Operation result is empty.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        OperationResult value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        var node = JsonSerializer.SerializeToNode(value, value.GetType(), options) as JsonObject
            ?? throw new JsonException("Operation result must serialize as an object.");
        if (Names.TryGetValue(value.GetType(), out var discriminator))
            node[GetWireName(options, "ResultType")] = discriminator;
        node.WriteTo(writer, options);
    }

    private static string GetWireName(JsonSerializerOptions options, string name) =>
        options.PropertyNamingPolicy?.ConvertName(name) ?? name;
}

internal static class ContractJsonDiscriminatorValidation
{
    /// <summary>
    /// Unknown polymorphic values are intentionally treated as opaque for
    /// forward compatibility.  Their reserved discriminator, however, is
    /// still part of our protocol and must not be supplied under a legacy
    /// lower-camel (or other case-only) alias.  Without this check an object
    /// containing both <c>ResultType</c> and <c>resultType</c> could bypass the
    /// strict DTO converter simply by taking the unknown-value fallback path.
    /// </summary>
    public static void RejectLegacyDiscriminatorAlias(
        JsonElement root,
        string wireName,
        string contractName)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!StringComparer.Ordinal.Equals(property.Name, wireName) &&
                StringComparer.OrdinalIgnoreCase.Equals(property.Name, wireName))
            {
                throw new JsonException(
                    $"{contractName} requires the PascalCase {wireName} discriminator.");
            }
        }
    }
}

public sealed class KebabCaseJsonStringEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private static readonly Dictionary<TEnum, string> NamesByValue = typeof(TEnum)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .ToDictionary(
            static field => (TEnum)field.GetValue(null)!,
            static field => field.GetCustomAttribute<EnumMemberAttribute>()?.Value
                ?? JsonNamingPolicy.KebabCaseLower.ConvertName(field.Name));

    private static readonly Dictionary<string, TEnum> ValuesByName = NamesByValue
        .ToDictionary(static pair => pair.Value, static pair => pair.Key, StringComparer.Ordinal);

    public override TEnum Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String ||
            reader.GetString() is not { } name ||
            !ValuesByName.TryGetValue(name, out var value))
        {
            throw new JsonException($"The value is not a valid {typeof(TEnum).Name} wire name.");
        }

        return value;
    }

    public override void Write(
        Utf8JsonWriter writer,
        TEnum value,
        JsonSerializerOptions options)
    {
        if (!NamesByValue.TryGetValue(value, out var name))
            throw new JsonException($"The value is not a defined {typeof(TEnum).Name} member.");

        writer.WriteStringValue(name);
    }
}
