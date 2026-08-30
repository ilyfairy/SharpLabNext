extern alias SharpLabNextRunner;
extern alias SharpLabNextWineRunner;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SharpLabNext.RuntimeProtocol;
using CgroupMemoryEvents = SharpLabNextRunner::CgroupMemoryEvents;
using RunnerExitClassification = SharpLabNextRunner::RunnerExitClassification;
using RunnerProgram = SharpLabNextRunner::RunnerProgram;
using ProcessBridgeArguments = SharpLabNextWineRunner::ProcessBridgeArguments;
using WineStderrFilter = SharpLabNextWineRunner::WineStderrFilter;

namespace SharpLabNext.IntegrationTests;

public sealed class RunnerProtocolTests
{
    private const string DescendantParentOutput = "runner-descendant-parent-output";

    [Theory]
    [InlineData("low 0\nhigh 0\noom 3\noom_kill 2\noom_group_kill 0\n", 2L)]
    [InlineData("oom_kill\t17\r\n", 17L)]
    public void CgroupMemoryEventsReadsExactOomKillCounter(string content, long expected)
    {
        Assert.Equal((ulong)expected, CgroupMemoryEvents.ParseOomKillCount(content));
    }

    [Fact]
    public void CgroupMemoryEventsToleratesMissingAndMalformedInput()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "memory.events");

        Assert.Null(CgroupMemoryEvents.TryReadOomKillCount(missingPath));
        Assert.Null(CgroupMemoryEvents.ParseOomKillCount("oom_kill not-a-number\n"));
        Assert.Null(CgroupMemoryEvents.ParseOomKillCount("oom 4\noom_group_kill 1\n"));
    }

    [Fact]
    public void RunnerOnlySynthesizesOutOfMemoryForAnObservedOomKill()
    {
        Assert.Equal("out-of-memory", RunnerExitClassification.GetSyntheticStatus(childExitReported: false, oomKillCountBefore: 4, oomKillCountAfter: 5));
        Assert.Equal("process-crash", RunnerExitClassification.GetSyntheticStatus(childExitReported: false, oomKillCountBefore: 4, oomKillCountAfter: 4));
        Assert.Equal("process-crash", RunnerExitClassification.GetSyntheticStatus(childExitReported: false, oomKillCountBefore: null, oomKillCountAfter: 5));
    }

    [Fact]
    public void RunnerKeepsStructuredExitAuthoritativeWhenOomCounterIncreases()
    {
        Assert.Null(RunnerExitClassification.GetSyntheticStatus(childExitReported: true, oomKillCountBefore: 4, oomKillCountAfter: 5));
    }

    [Fact]
    public async Task RunnerSeparatesStdoutStderrInspectionAndExitFrames()
    {
        var runnerPath = Path.Combine(AppContext.BaseDirectory, "SharpLabNext.Runner.dll");
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "SharpLabNext.RunnerFixture.dll");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(runnerPath);
        startInfo.ArgumentList.Add(fixturePath);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start Runner.");
        var stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var frames = new List<RuntimeFrame>();
        var reader = new RuntimeFrameLogReader(process.StandardOutput.BaseStream);
        while (await reader.ReadAsync(cancellationToken: TestContext.Current.CancellationToken) is { } frame)
        {
            frames.Add(frame);
        }

        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        var processError = await stderrTask;

        Assert.Equal(7, process.ExitCode);
        Assert.Empty(processError);
        Assert.Contains(frames, frame => frame.Kind == RuntimeFrameKind.Stdout && Encoding.UTF8.GetString(frame.Payload.Span) == "fixture-stdout");
        Assert.Contains(frames, frame => frame.Kind == RuntimeFrameKind.Stderr && Encoding.UTF8.GetString(frame.Payload.Span) == "fixture-stderr");
        Assert.Contains(frames, static frame => frame.Kind == RuntimeFrameKind.Inspection);
        var graphFrame = Assert.Single(frames, static frame => frame.Kind == RuntimeFrameKind.MemoryGraph);
        var graph = RuntimeStructuredPayloadCodec.DeserializeInspection(graphFrame.Payload.Span);
        Assert.Equal("MemoryGraph", graph.Kind);
        Assert.Equal(2, graph.Graph.Roots.Count);
        Assert.Contains(graph.Graph.Nodes, static node => node.Edges.Any(edge => edge.TargetNodeId == node.Id));
        var exit = Assert.Single(frames, static frame => frame.Kind == RuntimeFrameKind.Exit);
        using var exitJson = JsonDocument.Parse(exit.Payload);
        Assert.Equal(7, exitJson.RootElement.GetProperty("ExitCode").GetInt32());
    }

    [Fact]
    public async Task RunnerPreservesUserExceptionStackAndInnerExceptionFrames()
    {
        var runnerPath = Path.Combine(AppContext.BaseDirectory, "SharpLabNext.Runner.dll");
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "SharpLabNext.RunnerFixture.dll");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(runnerPath);
        startInfo.ArgumentList.Add(fixturePath);
        startInfo.ArgumentList.Add("runtime-user-exception");
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start Runner.");
        var stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var frames = new List<RuntimeFrame>();
        var reader = new RuntimeFrameLogReader(process.StandardOutput.BaseStream);
        while (await reader.ReadAsync(cancellationToken: TestContext.Current.CancellationToken) is { } frame)
            frames.Add(frame);

        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, process.ExitCode);
        Assert.Empty(await stderrTask);
        var exception = Assert.Single(frames, static frame => frame.Kind == RuntimeFrameKind.Exception);
        using var exceptionJson = JsonDocument.Parse(exception.Payload);
        var root = exceptionJson.RootElement;
        Assert.Equal("System.InvalidOperationException", root.GetProperty("TypeName").GetString());
        Assert.Equal("outer runtime failure", root.GetProperty("Message").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("StackTrace").GetString()));
        var inner = root.GetProperty("InnerException");
        Assert.Equal("System.ArgumentException", inner.GetProperty("TypeName").GetString());
        Assert.Equal("inner runtime failure", inner.GetProperty("Message").GetString());
        Assert.False(string.IsNullOrWhiteSpace(inner.GetProperty("StackTrace").GetString()));

        var exit = Assert.Single(frames, static frame => frame.Kind == RuntimeFrameKind.Exit);
        using var exitJson = JsonDocument.Parse(exit.Payload);
        Assert.Equal("user-exception", exitJson.RootElement.GetProperty("Status").GetString());
    }

    [Fact]
    public async Task StructuredExitDoesNotWaitForPipeEnd()
    {
        var exitPayload = RuntimeStructuredPayloadCodec.Serialize(new { Status = "completed", ExitCode = 0 });
        await using var encoded = new MemoryStream();
        await RuntimeFrameCodec.WriteAsync(encoded, new RuntimeFrame(1, RuntimeFrameKind.Exit, exitPayload), TestContext.Current.CancellationToken);
        await using var source = new FailOnReadAfterContentStream(encoded.ToArray());
        await using var forwarded = new MemoryStream();
        await using (var writer = new RuntimeFrameWriter(forwarded))
        {
            Assert.True(await RunnerProgram.ForwardStructuredAsync(source, writer));
        }

        forwarded.Position = 0;
        var exit = await RuntimeFrameCodec.ReadAsync(forwarded, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(exit);
        Assert.Equal(RuntimeFrameKind.Exit, exit.Kind);
        Assert.Null(await RuntimeFrameCodec.ReadAsync(forwarded, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunnerDescendantsCannotHoldStructuredPipeOpen()
    {
        var runnerPath = Path.Combine(AppContext.BaseDirectory, "SharpLabNext.Runner.dll");
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "SharpLabNext.RunnerFixture.dll");
        var nonce = Guid.NewGuid().ToString("N");
        var pidPath = Path.Combine(Path.GetTempPath(), $"sln-runner-descendant-{nonce}.pid");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(runnerPath);
        startInfo.ArgumentList.Add(fixturePath);
        startInfo.ArgumentList.Add("runner-descendant-parent");
        startInfo.ArgumentList.Add(pidPath);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start Runner.");
        Process? descendant = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var frames = new List<RuntimeFrame>();
            var reader = new RuntimeFrameLogReader(process.StandardOutput.BaseStream);
            var expectedOutputLength =
                DescendantParentOutput.Length +
                (1024 * 1024);
            var observedOutputLength = 0;
            var exitSeen = false;
            try
            {
                while (await reader.ReadAsync(cancellationToken: timeout.Token) is { } frame)
                {
                    frames.Add(frame);
                    if (frame.Kind == RuntimeFrameKind.Stdout)
                        observedOutputLength = checked(observedOutputLength + frame.Payload.Length);
                    else if (frame.Kind == RuntimeFrameKind.Exit)
                        exitSeen = true;

                    if (exitSeen && observedOutputLength == expectedOutputLength)
                        break;
                }
            }
            catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException($"Runner did not complete the descendant scenario. " + $"Exited={process.HasExited}; frames={string.Join(',', frames.Select(static frame => frame.Kind))}; " + $"stdout={observedOutputLength}/{expectedOutputLength}; pidFile={File.Exists(pidPath)}.", exception);
            }

            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);
            var stdout = frames.Where(static frame => frame.Kind == RuntimeFrameKind.Stdout).SelectMany(static frame => frame.Payload.ToArray()).ToArray();
            Assert.Equal(expectedOutputLength, stdout.Length);
            Assert.Equal(DescendantParentOutput, Encoding.UTF8.GetString(stdout.AsSpan(0, DescendantParentOutput.Length)));
            Assert.True(stdout.AsSpan(DescendantParentOutput.Length, 1024 * 1024).IndexOfAnyExcept((byte)'x') < 0);
            var exit = Assert.Single(frames, static frame => frame.Kind == RuntimeFrameKind.Exit);
            using var exitJson = JsonDocument.Parse(exit.Payload);
            Assert.Equal("completed", exitJson.RootElement.GetProperty("Status").GetString());
            Assert.Equal(0, exitJson.RootElement.GetProperty("ExitCode").GetInt32());
            var descendantId = int.Parse(await File.ReadAllTextAsync(pidPath, timeout.Token), System.Globalization.CultureInfo.InvariantCulture);
            descendant = Process.GetProcessById(descendantId);
            Assert.False(descendant.HasExited);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            if (descendant is null && File.Exists(pidPath) && int.TryParse(await File.ReadAllTextAsync(pidPath, CancellationToken.None), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var descendantId))
            {
                try
                {
                    descendant = Process.GetProcessById(descendantId);
                }
                catch (ArgumentException) { }
            }
            if (descendant is not null && !descendant.HasExited)
            {
                descendant.Kill(entireProcessTree: true);
                await descendant.WaitForExitAsync(CancellationToken.None);
            }
            descendant?.Dispose();
            File.Delete(pidPath);
        }
    }

    private sealed class FailOnReadAfterContentStream(byte[] content) : MemoryStream(content)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == Length)
                throw new InvalidOperationException("The structured reader read past the Exit frame.");
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    [Fact]
    public async Task ProcessBridgeForwardsFixedAndUserArgumentsStdinAndRuntimeFrames()
    {
        var runnerPath = Path.Combine(AppContext.BaseDirectory, "SharpLabNext.WineRunner.dll");
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "SharpLabNext.RunnerFixture.dll");
        var stdinPath = Path.Combine(Path.GetTempPath(), $"sln-wine-stdin-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(stdinPath, "from-stdin", TestContext.Current.CancellationToken);
        try
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(runnerPath);
            startInfo.ArgumentList.Add("bridge");
            startInfo.ArgumentList.Add("dotnet");
            startInfo.ArgumentList.Add(fixturePath);
            startInfo.ArgumentList.Add("process-bridge");
            startInfo.ArgumentList.Add("fixed argument");
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add("user argument;$(not-a-shell)");
            startInfo.Environment["SHARPLABNEXT_STDIN_PATH"] = stdinPath;
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start Wine Runner.");
            var stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            var frames = new List<RuntimeFrame>();
            var reader = new RuntimeFrameLogReader(process.StandardOutput.BaseStream);
            while (await reader.ReadAsync(cancellationToken: TestContext.Current.CancellationToken) is { } frame)
                frames.Add(frame);

            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.Equal(23, process.ExitCode);
            Assert.Empty(await stderrTask);
            Assert.Contains(frames, frame => frame.Kind == RuntimeFrameKind.Stdout && Encoding.UTF8.GetString(frame.Payload.Span) == "bridge-stdout:fixed argument:user argument;$(not-a-shell):from-stdin");
            var stderr = string.Concat(frames.Where(static frame => frame.Kind == RuntimeFrameKind.Stderr).Select(static frame => Encoding.UTF8.GetString(frame.Payload.Span)));
            Assert.Contains("wineserver: could not save registry branch to user.reg : Read-only file system\n", stderr, StringComparison.Ordinal);
            Assert.Contains("bridge-stderr", stderr, StringComparison.Ordinal);
            var exit = Assert.Single(frames, static frame => frame.Kind == RuntimeFrameKind.Exit);
            using var exitJson = JsonDocument.Parse(exit.Payload);
            Assert.Equal("non-zero-exit", exitJson.RootElement.GetProperty("Status").GetString());
            Assert.Equal(23, exitJson.RootElement.GetProperty("ExitCode").GetInt32());
        }
        finally
        {
            File.Delete(stdinPath);
        }
    }

    [Fact]
    public void ProcessBridgeRequiresAnExistingAbsoluteExecutable()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe");

        var exception = Assert.Throws<FileNotFoundException>(() => ProcessBridgeArguments.Parse(["bridge", missing, "--"]));

        Assert.Equal(Path.GetFullPath(missing), exception.FileName);
        Assert.Contains("process bridge", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessBridgeReportsGenericProtocolErrorsAsRuntimeFrames()
    {
        var runnerPath = Path.Combine(AppContext.BaseDirectory, "SharpLabNext.WineRunner.dll");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(runnerPath);
        startInfo.ArgumentList.Add("bridge");
        startInfo.ArgumentList.Add("../injected-executable");
        startInfo.ArgumentList.Add("--");
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start Process Bridge.");
        var stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var frames = new List<RuntimeFrame>();
        var reader = new RuntimeFrameLogReader(process.StandardOutput.BaseStream);
        while (await reader.ReadAsync(cancellationToken: TestContext.Current.CancellationToken) is { } frame)
            frames.Add(frame);

        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, process.ExitCode);
        Assert.Empty(await stderrTask);
        var protocolError = Assert.Single(frames, static frame => frame.Kind == RuntimeFrameKind.ProtocolError);
        using var errorJson = JsonDocument.Parse(protocolError.Payload);
        Assert.Equal("process-bridge-failed", errorJson.RootElement.GetProperty("Code").GetString());
        var exit = Assert.Single(frames, static frame => frame.Kind == RuntimeFrameKind.Exit);
        using var exitJson = JsonDocument.Parse(exit.Payload);
        Assert.Equal("process-crash", exitJson.RootElement.GetProperty("Status").GetString());
        Assert.Equal(1, exitJson.RootElement.GetProperty("ExitCode").GetInt32());
    }

    [Fact]
    public async Task WineRunnerFiltersOnlyExactReadOnlyRegistryWarningsAcrossChunks()
    {
        const string visibleBefore = "user-stderr-before\n";
        const string visibleWithoutLineBreak = "stderr-ok";
        const string systemWarning = "wineserver: could not save registry branch to system.reg : Read-only file system\n";
        const string userDefaultWarning = "wineserver: could not save registry branch to userdef.reg : Read-only file system\n";
        const string userWarning = "wineserver: could not save registry branch to user.reg : Read-only file system\n";
        const string lookalike = "wineserver: could not save registry branch to other.reg : Read-only file system\n";
        const string visibleAfter = "user-stderr-after";
        var input = Encoding.UTF8.GetBytes(visibleBefore + visibleWithoutLineBreak + systemWarning + userDefaultWarning + userWarning + lookalike + visibleAfter);
        await using var framed = new MemoryStream();
        await using (var writer = new RuntimeFrameWriter(framed))
        {
            var filter = new WineStderrFilter(writer);
            for (var offset = 0; offset < input.Length; offset += 7)
                await filter.WriteAsync(input.AsMemory(offset, Math.Min(7, input.Length - offset)));
            await filter.CompleteAsync();
        }

        framed.Position = 0;
        using var visible = new MemoryStream();
        while (await RuntimeFrameCodec.ReadAsync(
                   framed,
                   cancellationToken: TestContext.Current.CancellationToken) is { } frame)
        {
            Assert.Equal(RuntimeFrameKind.Stderr, frame.Kind);
            visible.Write(frame.Payload.Span);
        }

        Assert.Equal(visibleBefore + visibleWithoutLineBreak + lookalike + visibleAfter, Encoding.UTF8.GetString(visible.ToArray()));
    }
}
