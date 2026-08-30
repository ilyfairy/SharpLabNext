using SharpLabNext.DesktopClrJitInspector;

namespace SharpLabNext.DesktopClrJitInspector.Tests;

public sealed class DesktopClrJitCaptureTests
{
    [Fact]
    public void CodecRoundTripsBoundedCapture()
    {
        var document = new DesktopClrJitCaptureDocument(
            "4.0.30319.42000",
            new Guid("248a82a3-0ba0-4f3e-8b41-18c5bb23e0d2"),
            new List<DesktopClrJitCaptureMethod> { new(0x06000001, "Probe.Program.Add", 0x00007ff700001000, [0x48, 0x03, 0xc1, 0xc3]) });
        using var stream = new MemoryStream();

        DesktopClrJitCaptureCodec.Write(stream, document);
        stream.Position = 0;
        var actual = DesktopClrJitCaptureCodec.Read(stream);

        var method = Assert.Single(actual.Methods);
        Assert.Equal(document.ModuleVersionId, actual.ModuleVersionId);
        Assert.Equal(document.RuntimeVersion, actual.RuntimeVersion);
        Assert.Equal(0x06000001, method.MetadataToken);
        Assert.Equal("Probe.Program.Add", method.DisplayIdentity);
        Assert.Equal(0x00007ff700001000UL, method.NativeAddress);
        Assert.Equal(new byte[] { 0x48, 0x03, 0xc1, 0xc3 }, method.NativeCode);
    }

    [Fact]
    public void CodecRejectsDuplicateMethodToken()
    {
        var document = new DesktopClrJitCaptureDocument(
            "2.0.50727.42",
            Guid.NewGuid(),
            new List<DesktopClrJitCaptureMethod> { new(0x06000001, "Probe.Program.First", 0x1000, [0xc3]), new(0x06000001, "Probe.Program.Second", 0x2000, [0xc3]) });

        Assert.Throws<InvalidDataException>(() => DesktopClrJitCaptureCodec.Write(new MemoryStream(), document));
    }

    [Fact]
    public void CodecRejectsTrailingOrTruncatedBytes()
    {
        var document = new DesktopClrJitCaptureDocument(
            "4.0.30319.42000",
            Guid.NewGuid(),
            new List<DesktopClrJitCaptureMethod> { new(0x06000001, "Probe.Program.Add", 0x1000, [0xc3]) });
        using var stream = new MemoryStream();
        DesktopClrJitCaptureCodec.Write(stream, document);
        var bytes = stream.ToArray();

        Assert.Throws<InvalidDataException>(() => DesktopClrJitCaptureCodec.Read(new MemoryStream(bytes[..^1])));
        Assert.Throws<InvalidDataException>(() => DesktopClrJitCaptureCodec.Read(new MemoryStream([..bytes, 0])));
    }
}
