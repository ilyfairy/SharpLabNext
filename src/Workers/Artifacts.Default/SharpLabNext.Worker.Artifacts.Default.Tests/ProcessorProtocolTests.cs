using System.Text.Json;
using SharpLabNext.ArtifactProcessing.Protocol;
using SharpLabNext.Artifacts.Contracts;

namespace SharpLabNext.ArtifactWorker.Tests;

public sealed class ProcessorProtocolTests
{
    [Fact]
    public void RuntimeInstrumentationVersionMatchesCapabilityProbeContract()
    {
        Assert.Equal(
            RuntimeCapabilityProbeContract.ExecutionFlowProcessorVersion,
            ProcessorProtocol.RuntimeInstrumentationVersion);
    }

    [Fact]
    public void EnumValuesUseKebabCaseWireNames()
    {
        var json = JsonSerializer.Serialize(
            new ProcessorResponse(
                ProcessorProtocol.Version,
                ProcessorOutcome.InvalidArtifact,
                "processor",
                "1.0.0",
                "text/plain",
                0,
                [],
                [],
                false,
                null),
            ProcessorProtocol.JsonOptions);

        Assert.Contains("\"Outcome\":\"invalid-artifact\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"outcome\"", json, StringComparison.Ordinal);
        var operation = JsonSerializer.Serialize(ProcessorOperation.DecompiledCSharp, ProcessorProtocol.JsonOptions);
        Assert.Equal("\"decompiled-csharp\"", operation);
    }
}
