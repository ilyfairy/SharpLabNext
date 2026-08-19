using System.Diagnostics;
using System.Text;
using SharpLabNext.RuntimeProtocol;

namespace SharpLabNext.IntegrationTests;

public sealed class LegacyRunOutputTests
{
    [Fact]
    public async Task ManagedAndNativeWritesShareOneCapturePosition()
    {
        using var process = StartLegacyRun("interleaved-output", maximumOutputBytes: 1024 * 1024);
        var stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var reader = new RuntimeFrameLogReader(process.StandardOutput.BaseStream);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        var stdout = new MemoryStream();
        var stderr = new MemoryStream();
        while (await reader.ReadAsync(cancellationToken: timeout.Token) is { } frame)
        {
            if (frame.Kind == RuntimeFrameKind.Stdout)
                stdout.Write(frame.Payload.Span);
            else if (frame.Kind == RuntimeFrameKind.Stderr)
                stderr.Write(frame.Payload.Span);
        }
        await process.WaitForExitAsync(timeout.Token);

        Assert.Equal(0, process.ExitCode);
        Assert.Empty(await stderrTask);
        Assert.Equal(
            "stdout-managed-a|stdout-native-b|stdout-managed-c",
            Encoding.UTF8.GetString(stdout.ToArray()));
        Assert.Equal(
            "stderr-managed-a|stderr-native-b|stderr-managed-c",
            Encoding.UTF8.GetString(stderr.ToArray()));
    }

    [Fact]
    public async Task OutputIsFramedBeforeTheLegacyEntryPointReturns()
    {
        using var process = StartLegacyRun("stream", maximumOutputBytes: 1024 * 1024);
        var stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var reader = new RuntimeFrameLogReader(process.StandardOutput.BaseStream);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        var first = await reader.ReadAsync(cancellationToken: timeout.Token);

        Assert.NotNull(first);
        Assert.Equal(RuntimeFrameKind.Stdout, first!.Kind);
        Assert.Equal("stream-first", Encoding.UTF8.GetString(first.Payload.Span));
        Assert.False(process.HasExited);

        var remaining = new List<RuntimeFrame>();
        while (await reader.ReadAsync(cancellationToken: timeout.Token) is { } frame)
            remaining.Add(frame);
        await process.WaitForExitAsync(timeout.Token);

        Assert.Equal(0, process.ExitCode);
        Assert.Empty(await stderrTask);
        Assert.Contains(remaining, static frame =>
            frame.Kind == RuntimeFrameKind.Stderr &&
            Encoding.UTF8.GetString(frame.Payload.Span) == "stream-second");
        Assert.Contains(remaining, static frame => frame.Kind == RuntimeFrameKind.Exit);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(64 * 1024)]
    public async Task OutputOverflowIsFramedWhileTheLegacyEntryPointIsStillRunning(
        int maximumOutputBytes)
    {
        using var process = StartLegacyRun("output-limit", maximumOutputBytes);
        var stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var reader = new RuntimeFrameLogReader(process.StandardOutput.BaseStream);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        long observed = 0;
        try
        {
            while (observed <= maximumOutputBytes)
            {
                var frame = await reader.ReadAsync(cancellationToken: timeout.Token);
                Assert.NotNull(frame);
                if (frame!.Kind is RuntimeFrameKind.Stdout or RuntimeFrameKind.Stderr)
                    observed += frame.Payload.Length;
            }

            Assert.Equal(maximumOutputBytes + 1, observed);
            Assert.False(process.HasExited);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }

        _ = await stderrTask;
    }

    private static Process StartLegacyRun(string mode, int maximumOutputBytes)
    {
        var root = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("The test build configuration could not be resolved.");
        var helper = Path.Combine(
            root,
            "src",
            "RuntimeJobs",
            "SharpLabNext.LegacyJitInspector",
            "bin",
            configuration,
            "netcoreapp2.0",
            "SharpLabNext.LegacyJitInspector.dll");
        var fixture = Path.Combine(
            root,
            "tests",
            "Fixtures",
            "SharpLabNext.LegacyJitFixture",
            "bin",
            configuration,
            "netcoreapp2.0",
            "SharpLabNext.LegacyJitFixture.dll");
        Assert.True(File.Exists(helper), $"Legacy helper is missing: {helper}");
        Assert.True(File.Exists(fixture), $"Legacy fixture is missing: {fixture}");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(helper);
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add(fixture);
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(mode);
        startInfo.Environment["SHARPLABNEXT_MAX_OUTPUT_BYTES"] =
            maximumOutputBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The legacy run fixture process could not be started.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SharpLabNext.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("SharpLabNext.slnx was not found above the test output directory.");
    }
}
