using System;
using System.Collections.Generic;
using System.Text.Json;

namespace SharpLabNext.CheckedJitBridge;

internal sealed record JitSummaryPayload(string RuntimeVersion, string Assembly, string? MethodFilter, IReadOnlyList<JitMethodResult> Methods);

internal sealed record ExitPayload(string Status, int ExitCode, double ElapsedMilliseconds);

internal sealed record ProtocolErrorPayload(string Code, string Message);

internal sealed record ExceptionPayload(string TypeName, string Message, string? StackTrace, object? InnerException, double ElapsedMilliseconds);

internal static class BridgePayloadCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = 32
    };

    public static byte[] Serialize<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
}
