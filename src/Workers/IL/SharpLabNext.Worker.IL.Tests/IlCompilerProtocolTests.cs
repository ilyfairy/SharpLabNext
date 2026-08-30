using System.Text.Json;
using SharpLabNext.Worker.IL.Compiler;

namespace SharpLabNext.Worker.IL.Tests;

public sealed class IlCompilerProtocolTests
{
    [Fact]
    public void CompilerChildPayloadUsesPascalCaseMemberNames()
    {
        var request = new IlCompilerRequest(IlCompilerProtocol.Version, "dll", 1024, [new IlCompilerSource("Program.il", ".assembly extern mscorlib {}")]);

        var json = JsonSerializer.Serialize(request, IlCompilerProtocol.JsonOptions);

        Assert.Contains("\"ProtocolVersion\"", json, StringComparison.Ordinal);
        Assert.Contains("\"MaxPeBytes\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Sources\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"protocolVersion\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"maxPeBytes\"", json, StringComparison.Ordinal);
    }
}
