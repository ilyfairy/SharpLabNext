using System.Text;
using SharpLabNext.RuntimeProtocol;

namespace SharpLabNext.ArtifactWorker.Tests;

public sealed class RuntimeStructuredPayloadProtocolTests
{
    [Fact]
    public void RuntimePayloadsUseStrictPascalCaseMemberNames()
    {
        var payload = new RuntimeFlowPayload(
            "method-enter",
            "Program.cs",
            new RuntimeSourceRange(1, 2, 1, 3),
            7,
            11,
            "Run",
            null,
            false);

        var json = Encoding.UTF8.GetString(RuntimeStructuredPayloadCodec.Serialize(payload));

        Assert.Contains("\"EventKind\"", json, StringComparison.Ordinal);
        Assert.Contains("\"DocumentPath\"", json, StringComparison.Ordinal);
        Assert.Contains("\"ManagedThreadId\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"eventKind\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"documentPath\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimePayloadsRejectLegacyCamelCaseMembers()
    {
        var legacy = "{\"eventKind\":\"method-enter\",\"managedThreadId\":7,\"truncated\":false}"u8.ToArray();

        Assert.Throws<InvalidDataException>(() =>
            RuntimeStructuredPayloadCodec.DeserializeFlow(legacy));
    }
}
