extern alias SharpLabNextWineRunner;

using System.Text;
using DesktopClrJitCapture = SharpLabNextWineRunner::DesktopClrJitCapture;
using DesktopClrJitDisassembly = SharpLabNextWineRunner::DesktopClrJitDisassembly;

namespace SharpLabNext.UnitTests;

public sealed class DesktopClrJitCaptureTests
{
    [Fact]
    public void ParseAndDecodeProduceMethodAssemblyWithoutSourceMappings()
    {
        var capture = DesktopClrJitCapture.Parse(CreateCapture([
            (0x06000001u, "Example.Program.Add", 0x00007ff700001000UL, new byte[] { 0x48, 0x89, 0xc8, 0xc3 })
        ]));

        var document = DesktopClrJitDisassembly.Decode(capture);
        var method = Assert.Single(document.Methods);
        Assert.Equal("0x06000001", method.Method);
        Assert.Equal("prepared", method.Status);
        Assert.Equal(4, method.NativeCodeSize);
        Assert.True(method.InstructionCount >= 2);
        Assert.Empty(method.LinkedRanges);
        Assert.Equal("none", method.MappingSource);
        Assert.Equal("0x7ff700001000", method.Address);
        Assert.Contains("mov rax,rcx", document.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; Total bytes of code 4", document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseFailsClosedForTruncatedCapture()
    {
        var capture = CreateCapture([(0x06000001u, "Example.Program.M", 0x1000UL, new byte[] { 0x90 })]);

        Assert.Throws<InvalidDataException>(() =>
            DesktopClrJitCapture.Parse(capture.AsSpan(0, capture.Length - 1)));
    }

    [Fact]
    public void ParseFailsClosedForADeclaredMethodAboveTheCodeLimit()
    {
        var capture = CreateCapture([(0x06000001u, "Example.Program.M", 0x1000UL, Array.Empty<byte>())],
            declaredCodeLength: 1024 * 1024 + 1);

        Assert.Throws<InvalidDataException>(() => DesktopClrJitCapture.Parse(capture));
    }

    [Fact]
    public void ParseFailsClosedForDuplicateMethodTokens()
    {
        var capture = CreateCapture([
            (0x06000001u, "Example.Program.First", 0x1000UL, new byte[] { 0x90 }),
            (0x06000001u, "Example.Program.Second", 0x2000UL, new byte[] { 0xc3 })
        ]);

        Assert.Throws<InvalidDataException>(() => DesktopClrJitCapture.Parse(capture));
    }

    [Theory]
    [InlineData(0x0f)]
    [InlineData(0xe8)]
    public void DecoderFailsClosedForBadOrTruncatedOpcodes(byte opcode)
    {
        var capture = DesktopClrJitCapture.Parse(CreateCapture([
            (0x06000001u, "Example.Program.M", 0x1000UL, new[] { opcode })
        ]));

        Assert.Throws<InvalidDataException>(() => DesktopClrJitDisassembly.Decode(capture));
    }

    [Fact]
    public void ParseFailsClosedForAnEmptyMethod()
    {
        var capture = CreateCapture([(0x06000001u, "Example.Program.M", 0x1000UL, Array.Empty<byte>())]);

        Assert.Throws<InvalidDataException>(() => DesktopClrJitCapture.Parse(capture));
    }

    private static byte[] CreateCapture(
        IReadOnlyList<(uint Token, string DisplayName, ulong NativeAddress, byte[] NativeCode)> methods,
        int? declaredCodeLength = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write("SLNDCJ01"u8);
        writer.Write(1u);
        writer.Write(methods.Count);
        writer.Write(methods.Sum(method => declaredCodeLength ?? method.NativeCode.Length));
        writer.Write(new Guid("248a82a3-0ba0-4f3e-8b41-18c5bb23e0d2").ToByteArray());
        var runtimeVersion = "4.0.30319"u8.ToArray();
        writer.Write(checked((ushort)runtimeVersion.Length));
        writer.Write(runtimeVersion);
        foreach (var method in methods)
        {
            writer.Write(method.Token);
            writer.Write(method.NativeAddress);
            writer.Write(declaredCodeLength ?? method.NativeCode.Length);
            var name = Encoding.UTF8.GetBytes(method.DisplayName);
            writer.Write(checked((ushort)name.Length));
            writer.Write(name);
            writer.Write(method.NativeCode);
        }
        return stream.ToArray();
    }
}
