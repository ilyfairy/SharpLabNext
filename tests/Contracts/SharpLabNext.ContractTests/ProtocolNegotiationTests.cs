using SharpLabNext.Contracts;
using SharpLabNext.Contracts.Grpc;

namespace SharpLabNext.ContractTests;

public sealed class ProtocolNegotiationTests
{
    [Fact]
    public void MinorVersionsAreCompatibleWithinTheSameMajor()
    {
        Assert.True(WorkerProtocol.IsCompatible(new ProtocolVersion(1, 99)));
        Assert.False(WorkerProtocol.IsCompatible(new ProtocolVersion(2, 0)));
    }

    [Fact]
    public void NegotiationSelectsHighestCommonMajorAndLowestAdvertisedMinor()
    {
        var negotiated = WorkerProtocol.Negotiate(
            [new ProtocolVersion(1, 4), new ProtocolVersion(2, 3)],
            [new ProtocolVersion(1, 8), new ProtocolVersion(2, 1)]);

        Assert.Equal(new ProtocolVersion(2, 1), negotiated);
    }

    [Fact]
    public void NegotiationFailsWithoutACommonMajor()
    {
        var negotiated = WorkerProtocol.Negotiate(
            [new ProtocolVersion(1, 4)],
            [new ProtocolVersion(2, 0)]);

        Assert.Null(negotiated);
    }
}
