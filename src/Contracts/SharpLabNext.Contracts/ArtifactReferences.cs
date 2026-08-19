using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpLabNext.Contracts;

[JsonConverter(typeof(ArtifactRefJsonConverter))]
public readonly record struct ArtifactRef
{
    public ArtifactRef(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

[JsonConverter(typeof(ContentRefJsonConverter))]
public readonly record struct ContentRef
{
    public ContentRef(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed class ArtifactRefJsonConverter : JsonConverter<ArtifactRef>
{
    public override ArtifactRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? throw new JsonException("Artifact reference must be a string."));

    public override void Write(Utf8JsonWriter writer, ArtifactRef value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

public sealed class ContentRefJsonConverter : JsonConverter<ContentRef>
{
    public override ContentRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? throw new JsonException("Content reference must be a string."));

    public override void Write(Utf8JsonWriter writer, ContentRef value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
