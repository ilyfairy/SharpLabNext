using System.Text;
using SharpLabNext.RuntimeProtocol;

namespace SharpLabNext.UnitTests;

public sealed class RuntimeFrameCodecTests
{
    [Fact]
    public async Task FrameRoundTripsWithoutTextMarkers()
    {
        await using var stream = new MemoryStream();
        var expected = new RuntimeFrame(7, RuntimeFrameKind.Stdout, Encoding.UTF8.GetBytes("hello"));

        await RuntimeFrameCodec.WriteAsync(stream, expected, TestContext.Current.CancellationToken);
        stream.Position = 0;
        var actual = await RuntimeFrameCodec.ReadAsync(stream, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(actual);
        Assert.Equal(expected.Sequence, actual.Sequence);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Payload.ToArray(), actual.Payload.ToArray());
    }

    [Fact]
    public async Task InvalidMagicIsRejected()
    {
        await using var stream = new MemoryStream(new byte[18]);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await RuntimeFrameCodec.ReadAsync(stream, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriterAssignsStrictlyIncreasingSequenceNumbers()
    {
        await using var stream = new MemoryStream();
        await using (var writer = new RuntimeFrameWriter(stream))
        {
            await writer.WriteAsync(RuntimeFrameKind.Stdout, "a"u8.ToArray(), TestContext.Current.CancellationToken);
            await writer.WriteAsync(RuntimeFrameKind.Stderr, "b"u8.ToArray(), TestContext.Current.CancellationToken);
        }

        stream.Position = 0;
        var first = await RuntimeFrameCodec.ReadAsync(stream, cancellationToken: TestContext.Current.CancellationToken);
        var second = await RuntimeFrameCodec.ReadAsync(stream, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, first?.Sequence);
        Assert.Equal(2, second?.Sequence);
    }

    [Fact]
    public async Task WriterSharesSequenceAcrossSynchronousAndAsynchronousFrames()
    {
        await using var stream = new MemoryStream();
        await using (var writer = new RuntimeFrameWriter(stream))
        {
            writer.Write(RuntimeFrameKind.Stdout, "sync"u8.ToArray());
            await writer.WriteAsync(RuntimeFrameKind.Exit, "async"u8.ToArray(), TestContext.Current.CancellationToken);
        }

        stream.Position = 0;
        var first = await RuntimeFrameCodec.ReadAsync(stream, cancellationToken: TestContext.Current.CancellationToken);
        var second = await RuntimeFrameCodec.ReadAsync(stream, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, first?.Sequence);
        Assert.Equal(RuntimeFrameKind.Stdout, first?.Kind);
        Assert.Equal(2, second?.Sequence);
        Assert.Equal(RuntimeFrameKind.Exit, second?.Kind);
    }

    [Fact]
    public async Task Base64LogTransportPreservesArbitraryBinaryAndLargeLengthBytes()
    {
        var payload = new byte[65_791];
        Random.Shared.NextBytes(payload);
        await using var stream = new MemoryStream();
        await using (var writer = new RuntimeFrameWriter(stream, RuntimeFrameTransport.Base64Line))
        {
            await writer.WriteAsync(RuntimeFrameKind.JitAssembly, payload, TestContext.Current.CancellationToken);
        }

        Assert.All(stream.ToArray(), static value => Assert.True(value is (>= (byte)'+' and <= (byte)'z') or (byte)'=' or (byte)'\n'));
        stream.Position = 0;
        var reader = new RuntimeFrameLogReader(stream);
        var frame = await reader.ReadAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(frame);
        Assert.Equal(RuntimeFrameKind.JitAssembly, frame.Kind);
        Assert.Equal(payload, frame.Payload.ToArray());
        Assert.Null(await reader.ReadAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Base64LogReaderPreservesMultipleFramesFromOneBufferedRead()
    {
        await using var stream = new MemoryStream();
        await using (var writer = new RuntimeFrameWriter(stream, RuntimeFrameTransport.Base64Line))
        {
            await writer.WriteAsync(RuntimeFrameKind.Stdout, "first"u8.ToArray(), TestContext.Current.CancellationToken);
            await writer.WriteAsync(
                RuntimeFrameKind.Stderr,
                new byte[] { 0, 255, 128, 10 },
                TestContext.Current.CancellationToken);
        }

        stream.Position = 0;
        var reader = new RuntimeFrameLogReader(stream);

        var first = await reader.ReadAsync(cancellationToken: TestContext.Current.CancellationToken);
        var second = await reader.ReadAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, first?.Sequence);
        Assert.Equal(RuntimeFrameKind.Stdout, first?.Kind);
        Assert.Equal("first"u8.ToArray(), first?.Payload.ToArray());
        Assert.Equal(2, second?.Sequence);
        Assert.Equal(RuntimeFrameKind.Stderr, second?.Kind);
        Assert.Equal(new byte[] { 0, 255, 128, 10 }, second?.Payload.ToArray());
        Assert.Null(await reader.ReadAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Base64LogTransportRejectsOversizedOrNonCanonicalLines()
    {
        await using var oversized = new MemoryStream(Encoding.ASCII.GetBytes(new string('A', 128) + "\n"));
        var oversizedReader = new RuntimeFrameLogReader(oversized);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await oversizedReader.ReadAsync(32, TestContext.Current.CancellationToken));

        await using var invalid = new MemoryStream("not base64!\n"u8.ToArray());
        var invalidReader = new RuntimeFrameLogReader(invalid);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await invalidReader.ReadAsync(cancellationToken: TestContext.Current.CancellationToken));

        await using var unterminated = new MemoryStream("U0xOUg=="u8.ToArray());
        var unterminatedReader = new RuntimeFrameLogReader(unterminated);
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await unterminatedReader.ReadAsync(cancellationToken: TestContext.Current.CancellationToken));

        await using var nonCanonical = new MemoryStream("AB==\n"u8.ToArray());
        var nonCanonicalReader = new RuntimeFrameLogReader(nonCanonical);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await nonCanonicalReader.ReadAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void StructuredPayloadRoundTripsThroughTheWireCodec()
    {
        var expected = new RuntimeInspectionPayload("MemoryGraph", "Graph", new RuntimeGraphDocument([new RuntimeGraphRoot("Root", 1)], [new RuntimeGraphNode(1, "System.Int32", "value", "42", [])], false, null));

        var payload = RuntimeStructuredPayloadCodec.Serialize(expected);
        var actual = RuntimeStructuredPayloadCodec.DeserializeInspection(payload);

        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Title, actual.Title);
        var root = Assert.Single(actual.Graph.Roots);
        Assert.Equal("Root", root.Name);
        Assert.Equal(1, root.NodeId);
        var node = Assert.Single(actual.Graph.Nodes);
        Assert.Equal(1, node.Id);
        Assert.Equal("System.Int32", node.TypeName);
        Assert.Equal("value", node.Kind);
        Assert.Equal("42", node.DisplayValue);
        Assert.Empty(node.Edges);
        Assert.Equal(expected.Graph.Truncated, actual.Graph.Truncated);
        Assert.Equal(expected.Graph.TruncationReason, actual.Graph.TruncationReason);
    }
}
