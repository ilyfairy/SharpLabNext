using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using SharpLab.Runtime;
using SharpLabNext.RuntimeJobs;
using SharpLabNext.RuntimeProtocol;

return await RunnerProgram.RunAsync(args);

internal static class RunnerProgram
{
    private const string ChildSwitch = "--runtime-child";
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(2);
    // User code can construct arbitrary exception chains. Keep the diagnostic
    // payload bounded so a malicious chain cannot exhaust the framed protocol
    // writer or the supervisor's deserializer.
    private const int MaximumExceptionDepth = 32;

    public static Task<int> RunAsync(string[] args) =>
        args.Length > 0 && StringComparer.Ordinal.Equals(args[0], ChildSwitch)
            ? RunChildAsync(args[1..])
            : RunParentAsync(args);

    private static async Task<int> RunParentAsync(string[] args)
    {
        var parsed = RunnerArguments.Parse(args);
        await using var protocolWriter = new RuntimeFrameWriter(
            Console.OpenStandardOutput(),
            RuntimeFrameTransport.Base64Line);
        using var childFrames = new AnonymousPipeServerStream(
            PipeDirection.In,
            HandleInheritability.Inheritable);
        // cgroup v2 may kill only the user child without marking the container OOM-killed.
        var oomKillCountBefore = CgroupMemoryEvents.TryReadOomKillCount();
        using var child = StartChild(parsed, childFrames.GetClientHandleAsString());
        childFrames.DisposeLocalCopyOfClientHandle();

        using var outputCancellation = new CancellationTokenSource();
        var stdout = ForwardTextAsync(
            child.StandardOutput.BaseStream,
            protocolWriter,
            RuntimeFrameKind.Stdout,
            outputCancellation.Token);
        var stderr = ForwardTextAsync(
            child.StandardError.BaseStream,
            protocolWriter,
            RuntimeFrameKind.Stderr,
            outputCancellation.Token);
        var structured = ForwardStructuredAsync(childFrames, protocolWriter);
        await child.WaitForExitAsync();
        var childExitReported = await structured;
        await CompleteTextForwardingAsync(child, stdout, stderr, outputCancellation);

        var syntheticStatus = RunnerExitClassification.GetSyntheticStatus(
            childExitReported,
            oomKillCountBefore,
            CgroupMemoryEvents.TryReadOomKillCount());
        if (syntheticStatus is not null)
        {
            await WriteJsonAsync(protocolWriter, RuntimeFrameKind.Exit, new
            {
                status = syntheticStatus,
                exitCode = child.ExitCode,
                elapsedMilliseconds = 0
            });
        }

        return child.ExitCode;
    }

    private static Process StartChild(RunnerArguments parsed, string pipeHandle)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The current .NET host path is unavailable.");
        var startInfo = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true
        };
        if (StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet"))
        {
            startInfo.ArgumentList.Add(typeof(RunnerProgram).Assembly.Location);
        }
        startInfo.ArgumentList.Add(ChildSwitch);
        startInfo.ArgumentList.Add(pipeHandle);
        startInfo.ArgumentList.Add(parsed.AssemblyPath);
        startInfo.ArgumentList.Add("--");
        foreach (var argument in parsed.UserArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["DOTNET_EnableDiagnostics"] = "0";
        startInfo.Environment["COMPlus_EnableDiagnostics"] = "0";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The isolated user process could not be started.");
    }

    private static async Task ForwardTextAsync(
        Stream stream,
        RuntimeFrameWriter writer,
        RuntimeFrameKind kind,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (await stream.ReadAsync(buffer, cancellationToken) is var read && read > 0)
        {
            // Finish a frame once its payload has been read. Cancelling a write can
            // leave the outer runtime protocol with a partial frame.
            await writer.WriteAsync(kind, buffer.AsMemory(0, read), CancellationToken.None);
        }
    }

    private static async Task CompleteTextForwardingAsync(
        Process child,
        Task stdout,
        Task stderr,
        CancellationTokenSource cancellation)
    {
        var forwarding = Task.WhenAll(stdout, stderr);
        try
        {
            await forwarding.WaitAsync(OutputDrainTimeout);
            return;
        }
        catch (TimeoutException)
        {
            cancellation.Cancel();
            CloseReadPipe(child.StandardOutput.BaseStream);
            CloseReadPipe(child.StandardError.BaseStream);
        }

        // On Windows an async pipe read can remain pending while a descendant
        // retains an inherited writer, even after cancellation and handle close.
        // The Runner must exit so the Supervisor can remove the process tree.
        ObserveFault(forwarding);
    }

    private static void CloseReadPipe(Stream stream)
    {
        if (stream is FileStream fileStream)
        {
            fileStream.SafeFileHandle.Dispose();
            return;
        }

        stream.Dispose();
    }

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    internal static async Task<bool> ForwardStructuredAsync(
        Stream stream,
        RuntimeFrameWriter writer)
    {
        while (await RuntimeFrameCodec.ReadAsync(stream) is { } frame)
        {
            if (frame.Kind is not (
                    RuntimeFrameKind.Inspection or
                    RuntimeFrameKind.MemoryGraph or
                    RuntimeFrameKind.Flow or
                    RuntimeFrameKind.Exception or
                    RuntimeFrameKind.Exit or
                    RuntimeFrameKind.ProtocolError))
            {
                throw new InvalidDataException($"The user child emitted forbidden frame kind '{frame.Kind}'.");
            }

            RuntimeStructuredPayloadCodec.Validate(frame.Kind, frame.Payload.Span);
            await writer.WriteAsync(frame.Kind, frame.Payload);
            if (frame.Kind == RuntimeFrameKind.Exit)
                return true;
        }
        return false;
    }

    private static async Task<int> RunChildAsync(string[] args)
    {
        var parsed = ChildRunnerArguments.Parse(args);
        await using var pipe = new AnonymousPipeClientStream(PipeDirection.Out, parsed.PipeHandle);
        PipeHandleInheritance.Disable(pipe.SafePipeHandle);
        await using var writer = new RuntimeFrameWriter(pipe);
        ConfigureStandardInput();
        using var inspectionScope = RuntimeServices.PushInspectionSink(new FramedInspectionSink(writer));
        using var flowScope = IsExecutionFlowEnabled()
            ? RuntimeServices.PushFlowSink(new FramedFlowSink(writer))
            : null;
        var started = DateTimeOffset.UtcNow;
        try
        {
            var loadContext = new RuntimeArtifactLoadContext(
                parsed.AssemblyPath,
                typeof(RuntimeServices).Assembly);
            var assembly = loadContext.LoadFromAssemblyPath(parsed.AssemblyPath);
            var entryPoint = assembly.EntryPoint
                ?? throw new InvalidOperationException("The user assembly does not define an entry point.");
            var invocationArguments = entryPoint.GetParameters().Length == 0
                ? null
                : new object?[] { parsed.UserArguments };
            var result = entryPoint.Invoke(null, invocationArguments);
            var exitCode = await AwaitResultAsync(result);
            await WriteJsonAsync(writer, RuntimeFrameKind.Exit, new
            {
                status = exitCode == 0 ? "completed" : "non-zero-exit",
                exitCode,
                elapsedMilliseconds = (DateTimeOffset.UtcNow - started).TotalMilliseconds
            });
            return exitCode;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is OutOfMemoryException)
        {
            await WriteOutOfMemoryAsync(writer, started);
            return 137;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            await WriteExceptionAsync(writer, exception.InnerException, started);
            return 1;
        }
        catch (OutOfMemoryException)
        {
            await WriteOutOfMemoryAsync(writer, started);
            return 137;
        }
        catch (Exception exception)
        {
            await WriteExceptionAsync(writer, exception, started);
            return 1;
        }
    }

    private static async Task<int> AwaitResultAsync(object? result)
    {
        switch (result)
        {
            case null:
                return 0;
            case int exitCode:
                return exitCode;
            case Task<int> exitTask:
                return await exitTask;
            case Task task:
                await task;
                return 0;
            default:
                throw new InvalidOperationException($"Unsupported entry point return type '{result.GetType()}'.");
        }
    }

    private static void ConfigureStandardInput()
    {
        var inputPath = Environment.GetEnvironmentVariable("SHARPLABNEXT_STDIN_PATH");
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            Console.SetIn(TextReader.Null);
            return;
        }

        Console.SetIn(new StreamReader(
            new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read),
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true));
    }

    private static bool IsExecutionFlowEnabled() =>
        StringComparer.Ordinal.Equals(
            Environment.GetEnvironmentVariable("SHARPLABNEXT_INSTRUMENTATION"),
            "execution-flow");

    private static async Task WriteExceptionAsync(
        RuntimeFrameWriter writer,
        Exception exception,
        DateTimeOffset started)
    {
        // Keep the source model aligned with the shared runtime contract;
        // RuntimeStructuredPayloadCodec emits these members as PascalCase.
        await WriteJsonAsync(writer, RuntimeFrameKind.Exception, new
        {
            typeName = exception.GetType().FullName ?? exception.GetType().Name,
            message = exception.Message,
            stackTrace = exception.StackTrace,
            innerException = CreateInnerExceptionPayload(exception.InnerException),
            elapsedMilliseconds = (DateTimeOffset.UtcNow - started).TotalMilliseconds
        });
        await WriteJsonAsync(writer, RuntimeFrameKind.Exit, new
        {
            status = "user-exception",
            exitCode = 1,
            elapsedMilliseconds = (DateTimeOffset.UtcNow - started).TotalMilliseconds
        });
    }

    private static object? CreateInnerExceptionPayload(Exception? exception, int depth = 1)
    {
        if (exception is null || depth > MaximumExceptionDepth)
            return null;

        return new
        {
            typeName = exception.GetType().FullName ?? exception.GetType().Name,
            message = exception.Message,
            stackTrace = exception.StackTrace,
            innerException = CreateInnerExceptionPayload(exception.InnerException, depth + 1)
        };
    }

    private static ValueTask WriteOutOfMemoryAsync(
        RuntimeFrameWriter writer,
        DateTimeOffset started) =>
        WriteJsonAsync(writer, RuntimeFrameKind.Exit, new
        {
            status = "out-of-memory",
            exitCode = 137,
            elapsedMilliseconds = (DateTimeOffset.UtcNow - started).TotalMilliseconds
        });

    private static ValueTask WriteJsonAsync(RuntimeFrameWriter writer, RuntimeFrameKind kind, object value) =>
        writer.WriteAsync(kind, RuntimeStructuredPayloadCodec.Serialize(value));
}

internal static class RunnerExitClassification
{
    public static string? GetSyntheticStatus(
        bool childExitReported,
        ulong? oomKillCountBefore,
        ulong? oomKillCountAfter)
    {
        if (childExitReported)
            return null;

        return CgroupMemoryEvents.OomKillCountIncreased(oomKillCountBefore, oomKillCountAfter)
            ? "out-of-memory"
            : "process-crash";
    }
}

internal static class CgroupMemoryEvents
{
    private const string DefaultPath = "/sys/fs/cgroup/memory.events";

    public static ulong? TryReadOomKillCount(string path = DefaultPath)
    {
        try
        {
            return ParseOomKillCount(File.ReadAllText(path));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static ulong? ParseOomKillCount(string content)
    {
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields is not ["oom_kill", var value])
                continue;

            return ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                ? count
                : null;
        }

        return null;
    }

    public static bool OomKillCountIncreased(ulong? before, ulong? after) =>
        before.HasValue && after.HasValue && after.Value > before.Value;
}

internal sealed record RunnerArguments(string AssemblyPath, string[] UserArguments)
{
    public static RunnerArguments Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException("Usage: SharpLabNext.Runner <absolute-assembly-path> [-- <arguments...>]");
        }

        var assemblyPath = Path.GetFullPath(args[0]);
        if (!Path.IsPathFullyQualified(assemblyPath) || !File.Exists(assemblyPath))
        {
            throw new FileNotFoundException("User entry assembly was not found.", assemblyPath);
        }

        var separator = Array.IndexOf(args, "--");
        var userArguments = separator < 0 ? args[1..] : args[(separator + 1)..];
        return new RunnerArguments(assemblyPath, userArguments);
    }
}

internal sealed record ChildRunnerArguments(string PipeHandle, string AssemblyPath, string[] UserArguments)
{
    public static ChildRunnerArguments Parse(string[] args)
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[0]))
        {
            throw new ArgumentException("The runtime child arguments are invalid.");
        }
        var parsed = RunnerArguments.Parse(args[1..]);
        return new ChildRunnerArguments(args[0], parsed.AssemblyPath, parsed.UserArguments);
    }
}

internal sealed class FramedInspectionSink(RuntimeFrameWriter writer) : IInspectionSink
{
    public void Write(InspectionRecord inspection)
    {
        var roots = inspection.Kind == InspectionKind.MemoryGraph
            ? inspection.Values.Select((value, index) => ($"Root {index + 1}", value)).ToArray()
            : new[] { (inspection.Title, inspection.Value) };
        var graph = RuntimeValueGraphBuilder.Build(roots);
        var payload = RuntimeStructuredPayloadCodec.Serialize(new RuntimeInspectionPayload(
            inspection.Kind.ToString(),
            inspection.Title,
            graph));
        var frameKind = inspection.Kind == InspectionKind.MemoryGraph
            ? RuntimeFrameKind.MemoryGraph
            : RuntimeFrameKind.Inspection;
        writer.Write(frameKind, payload);
    }
}

internal sealed class FramedFlowSink(RuntimeFrameWriter writer) : IFlowSink
{
    private const int MaximumEvents = 10_000;
    private const int MaximumBytes = 512 * 1024;
    private int _eventCount;
    private int _totalBytes;
    private bool _truncated;

    public void Write(FlowRecord flow)
    {
        if (_truncated)
        {
            return;
        }

        var value = flow.Value is null
            ? null
            : RuntimeValueGraphBuilder.Build(new[] { (flow.Name ?? "Value", (object?)flow.Value) });
        var payload = RuntimeStructuredPayloadCodec.Serialize(new RuntimeFlowPayload(
            ToEventKind(flow.Kind),
            flow.DocumentPath,
            CreateRange(flow),
            Environment.CurrentManagedThreadId,
            Task.CurrentId,
            flow.Name,
            value,
            false));
        if (++_eventCount > MaximumEvents || checked(_totalBytes + payload.Length) > MaximumBytes)
        {
            WriteTruncated();
            return;
        }

        _totalBytes += payload.Length;
        writer.Write(RuntimeFrameKind.Flow, payload);
    }

    private void WriteTruncated()
    {
        _truncated = true;
        var payload = RuntimeStructuredPayloadCodec.Serialize(new RuntimeFlowPayload(
            "truncated",
            null,
            null,
            Environment.CurrentManagedThreadId,
            Task.CurrentId,
            null,
            null,
            true));
        writer.Write(RuntimeFrameKind.Flow, payload);
    }

    private static RuntimeSourceRange? CreateRange(FlowRecord flow) =>
        flow.StartLine < 0
            ? null
            : new RuntimeSourceRange(
                Math.Max(0, flow.StartLine - 1),
                Math.Max(0, flow.StartColumn - 1),
                Math.Max(0, flow.EndLine - 1),
                Math.Max(0, flow.EndColumn - 1));

    private static string ToEventKind(FlowEventKind kind) => kind switch
    {
        FlowEventKind.SequencePoint => "sequence-point",
        FlowEventKind.Branch => "branch",
        FlowEventKind.Method => "method",
        FlowEventKind.Loop => "loop",
        FlowEventKind.Jump => "jump",
        FlowEventKind.Value => "value",
        FlowEventKind.Exception => "exception",
        _ => "unknown"
    };
}
