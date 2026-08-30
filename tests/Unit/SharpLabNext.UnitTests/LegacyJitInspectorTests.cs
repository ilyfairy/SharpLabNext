using System.Text;
using SharpLabNext.LegacyJitInspector;

namespace SharpLabNext.UnitTests;

public sealed class LegacyJitInspectorTests
{
    [Fact]
    public void RuntimeVersionSwitchIsRemovedBeforeCommandParsing()
    {
        var args = new[]
        {
            RuntimeVersionGuard.Switch,
            "11.0.0-preview.5.26302.115",
            "run",
            "/workspace/Program.dll",
            "--",
            RuntimeVersionGuard.Switch,
            "user-value"
        };

        var remaining = RuntimeVersionGuard.Extract(args, out var expected);

        Assert.Equal("11.0.0-preview.5.26302.115", expected);
        Assert.Equal(args[2..], remaining);
    }

    [Fact]
    public void RuntimeVersionGuardComparesNumericRuntimeIdentity()
    {
        var expected = RuntimeVersionGuard.ParseNumericPrefix("7.0.20-preview.1");

        Assert.True(RuntimeVersionGuard.IsCompatible(expected, new Version(7, 0, 20)));
        Assert.False(RuntimeVersionGuard.IsCompatible(expected, new Version(7, 0, 19)));
        Assert.False(RuntimeVersionGuard.IsCompatible(expected, new Version(8, 0, 20)));
    }

    [Fact]
    public void RuntimeVersionGuardRejectsAHostFromAnotherMajorVersion()
    {
        Assert.Throws<InvalidOperationException>(() => RuntimeVersionGuard.Validate("0.0.0"));
    }

    [Fact]
    public void RuntimeVersionGuardAcceptsTheCurrentHostVersion()
    {
        RuntimeVersionGuard.Validate(RuntimeVersionGuard.CurrentRuntimeVersion().ToString());
    }

    [Fact]
    public void LegacyInvocationWithoutVersionSwitchRemainsCompatible()
    {
        var args = new[] { "run", "/workspace/Program.dll", "--", "--runtime-version" };

        var remaining = RuntimeVersionGuard.Extract(args, out var expected);

        Assert.Null(expected);
        Assert.Same(args, remaining);
    }

    [Fact]
    public void FrameWriterEmitsCanonicalBase64Line()
    {
        using var stream = new MemoryStream();
        using (var writer = new RuntimeFrameWriter(stream))
            writer.Write(RuntimeFrameKind.Stdout, Encoding.UTF8.GetBytes("captured"));

        var line = Encoding.ASCII.GetString(stream.ToArray()).TrimEnd('\n');
        var frame = Convert.FromBase64String(line);

        Assert.Equal("SLNR", Encoding.ASCII.GetString(frame, 0, 4));
        Assert.Equal(1, frame[4]);
        Assert.Equal((byte)RuntimeFrameKind.Stdout, frame[5]);
        Assert.Equal(1L, BitConverter.ToInt64(frame, 6));
        Assert.Equal(8, BitConverter.ToInt32(frame, 14));
        Assert.Equal("captured", Encoding.UTF8.GetString(frame, 18, 8));
    }

    [Theory]
    [InlineData("WindowsAbi", "SharpLabNext.LegacyJitFixture.WindowsAbi", true)]
    [InlineData("*Abi", "SharpLabNext.LegacyJitFixture.WindowsAbi", true)]
    [InlineData("Missing", "SharpLabNext.LegacyJitFixture.WindowsAbi", false)]
    public void MethodFilterSupportsSubstringAndWildcard(string filter, string displayName, bool expected)
    {
        Assert.Equal(expected, JitMethodInspector.MatchesFilter(displayName, filter));
    }

    [Fact]
    public void WineCaptureDirectoryAcceptsOnlyTheFixedTmpfsPath()
    {
        Assert.Equal(@"z:\TMP", RunOutputCapture.ResolveCaptureDirectory(@"z:\TMP", isWindows: true));
        Assert.Throws<InvalidOperationException>(() =>
            RunOutputCapture.ResolveCaptureDirectory(@"C:\users\root\Temp", isWindows: true));
        Assert.Throws<InvalidOperationException>(() =>
            RunOutputCapture.ResolveCaptureDirectory(@"Z:\tmp", isWindows: false));
    }

    [Fact]
    public void NativeCaptureDirectoryKeepsThePlatformDefaultWhenNotConfigured()
    {
        Assert.Equal(Path.GetTempPath(), RunOutputCapture.ResolveCaptureDirectory(null!, OperatingSystem.IsWindows()));
    }

    [Fact]
    public void DisassemblySelectionCountsInstructionsAndLinksMethodSource()
    {
        const int token = 0x06000001;
        var method = new JitMethodResult("0x06000001", token, "Sample.Type.Add", "prepared", "0x1234", null);
        var source = new Dictionary<int, MethodSourceSpan>
        {
            [token] = new MethodSourceSpan("Sample.cs", new JitTextRange(4, 0, 6, 1))
        };
        const string text =
            "; Assembly listing for method Sample.Type:Add(int,int):int\n" +
            "; optimized code\n" +
            "       8BC1       mov      eax, ecx\n" +
            "       C3         ret\n" +
            "; Total bytes of code 2\n";

        var selected = JitDisassemblyDocument.SelectPreparedMethods(
            text,
            new[] { method },
            source);

        Assert.Contains("Assembly listing for method", selected);
        Assert.Equal(2, method.InstructionCount);
        Assert.Equal(2, method.NativeCodeSize);
        Assert.Equal("method", method.MappingSource);
        var link = Assert.Single(method.LinkedRanges);
        Assert.Equal("Sample.cs", link.SourceFilePath);
        Assert.Equal(4, link.SourceRange.StartLine);
    }

}
