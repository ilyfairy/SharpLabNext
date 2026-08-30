using System.Text.Json;
using SharpLabNext.Contracts;

namespace SharpLabNext.RuntimeProtocol;

public sealed record RuntimeGraphDocument(IReadOnlyList<RuntimeGraphRoot> Roots, IReadOnlyList<RuntimeGraphNode> Nodes, bool Truncated, string? TruncationReason);

public sealed record RuntimeGraphRoot(string Name, int NodeId);

public sealed record RuntimeGraphNode(int Id, string TypeName, string Kind, string? DisplayValue, IReadOnlyList<RuntimeGraphEdge> Edges);

public sealed record RuntimeGraphEdge(string Name, int TargetNodeId);

public sealed record RuntimeInspectionPayload(string Kind, string Title, RuntimeGraphDocument Graph);

public sealed record RuntimeFlowPayload(string EventKind, string? DocumentPath, RuntimeSourceRange? Range, int ManagedThreadId, int? TaskId, string? Name, RuntimeGraphDocument? Value, bool Truncated);

public sealed record RuntimeSourceRange(int StartLine, int StartColumn, int EndLine, int EndColumn);

public static class RuntimeStructuredPayloadCodec
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = ContractJson.CreateSerializerOptions();
        options.MaxDepth = 32;
        return options;
    }

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

    public static RuntimeInspectionPayload DeserializeInspection(ReadOnlySpan<byte> payload) => Deserialize<RuntimeInspectionPayload>(payload, "inspection");

    public static RuntimeFlowPayload DeserializeFlow(ReadOnlySpan<byte> payload) => Deserialize<RuntimeFlowPayload>(payload, "flow");

    public static void Validate(RuntimeFrameKind kind, ReadOnlySpan<byte> payload)
    {
        try
        {
            switch (kind)
            {
                case RuntimeFrameKind.Inspection:
                case RuntimeFrameKind.MemoryGraph:
                    _ = DeserializeInspection(payload);
                    break;
                case RuntimeFrameKind.Flow:
                    _ = DeserializeFlow(payload);
                    break;
                case RuntimeFrameKind.Exception:
                case RuntimeFrameKind.Exit:
                case RuntimeFrameKind.ProtocolError:
                    using (JsonDocument.Parse(payload.ToArray())) { }
                    break;
                default:
                    throw new InvalidDataException($"Runtime frame '{kind}' is not a structured child payload.");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Runtime frame '{kind}' contains invalid JSON.", exception);
        }
    }

    private static T Deserialize<T>(ReadOnlySpan<byte> payload, string payloadName)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload, JsonOptions) ?? throw new InvalidDataException($"The {payloadName} payload is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The {payloadName} payload contains invalid JSON.", exception);
        }
    }
}
