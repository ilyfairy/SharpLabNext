using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SharpLabNext.CheckedJitBridge;

internal static class CheckedJitBridgeContract
{
    public const string ImplementationId = "sharplabnext-checked-jit-bridge-v1";
    public const string SourceMappingKind = "checked-jit-debug-info";
    public const string SourceMappingKindEnvironmentVariable =
        "SHARPLABNEXT_CHECKED_JIT_SOURCE_MAPPING_KIND";
    public const string InstalledAssemblyPath = "/opt/sharplabnext/SharpLabNext.CheckedJitBridge.dll";
}

internal static class CheckedJitBridgeBootstrap
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], RuntimeVersionVerifier.Switch, StringComparison.Ordinal))
            return RuntimeVersionVerifier.Run(args, Console.Error);

        if (args.Length > 0 && string.Equals(args[0], "--child", StringComparison.Ordinal))
            return CheckedJitChildRunner.RunAsync(args).GetAwaiter().GetResult();

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            return CheckedJitBridgeProgram.RunAsync(args, cancellation.Token)
                .GetAwaiter()
                .GetResult();
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }
}

internal static class CheckedJitBridgeProgram
{
    private const int JitFrameChunkSize = 64 * 1024;
    private const int MaximumChildMetadataBytes = 512 * 1024;
    private static readonly TimeSpan MetadataCompletionTimeout = TimeSpan.FromSeconds(2);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly BoundedChildProcessLimits ChildLimits = new(
        standardOutputBytes: 3 * 1024 * 1024,
        standardErrorBytes: 256 * 1024,
        totalOutputBytes: 3 * 1024 * 1024,
        executionTimeout: TimeSpan.FromSeconds(8),
        cleanupTimeout: TimeSpan.FromSeconds(2));

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        using var writer = new RuntimeFrameWriter(Console.OpenStandardOutput());
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
        var started = Stopwatch.StartNew();
        try
        {
            var options = CheckedJitBridgeArguments.Parse(args);
            using var metadata = ManagedAssemblyMetadata.Open(options.AssemblyPath);
            var userAssemblyName = metadata.AssemblyName;
            var nonce = Guid.NewGuid().ToString("N");
            using var childMetadata = new AnonymousPipeServerStream(
                PipeDirection.In,
                HandleInheritability.Inheritable);
            var metadataFailure = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var metadataRead = BoundedStreamReader.ReadAsync(
                childMetadata,
                MaximumChildMetadataBytes,
                metadataFailure);
            var localClientDisposed = false;
            try
            {
                var startInfo = CheckedJitChildProcess.CreateStartInfo(
                    options,
                    childMetadata.GetClientHandleAsString(),
                    nonce,
                    userAssemblyName);
                var child = await BoundedChildProcessRunner.RunAsync(
                    startInfo,
                    ChildLimits,
                    cancellationToken,
                    metadataFailure.Task,
                    () =>
                    {
                        childMetadata.DisposeLocalCopyOfClientHandle();
                        localClientDisposed = true;
                    }).ConfigureAwait(false);

                if (!localClientDisposed)
                    childMetadata.DisposeLocalCopyOfClientHandle();
                return await CompleteAsync(
                    writer,
                    options,
                    nonce,
                    child,
                    metadataRead,
                    started).ConfigureAwait(false);
            }
            finally
            {
                if (!localClientDisposed)
                    childMetadata.DisposeLocalCopyOfClientHandle();
            }
        }
        catch (OutOfMemoryException)
        {
            WriteExit(writer, "out-of-memory", 137, started);
            return 137;
        }
        catch (OperationCanceledException)
        {
            WriteExit(writer, "cancelled", 130, started);
            return 130;
        }
        catch (Exception exception)
        {
            WriteProtocolError(writer, "checked-jit-bridge-failed", exception.Message);
            WriteExit(writer, "inspection-failed", 1, started);
            return 1;
        }
    }

    private static async Task<int> CompleteAsync(
        RuntimeFrameWriter writer,
        CheckedJitBridgeArguments options,
        string nonce,
        BoundedChildProcessResult child,
        Task<byte[]> metadataRead,
        Stopwatch started)
    {
        switch (child.TerminationReason)
        {
            case ChildTerminationReason.OutputLimitExceeded:
                WriteProtocolError(writer, "checked-jit-child-output-limit", "Checked JIT child output exceeded its byte budget.");
                WriteExit(writer, "output-limit-exceeded", 1, started);
                return 1;
            case ChildTerminationReason.TimedOut:
                WriteProtocolError(writer, "checked-jit-child-timeout", "Checked JIT child exceeded its execution deadline.");
                WriteExit(writer, "timeout", 124, started);
                return 124;
            case ChildTerminationReason.Cancelled:
                WriteExit(writer, "cancelled", 130, started);
                return 130;
            case ChildTerminationReason.ProtocolFailure:
                try
                {
                    await AwaitMetadataAsync(metadataRead, MetadataCompletionTimeout).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    WriteProtocolError(writer, "checked-jit-child-protocol", exception.Message);
                }
                WriteExit(writer, "inspection-failed", 1, started);
                return 1;
        }

        var childPayload = await AwaitMetadataAsync(
            metadataRead,
            MetadataCompletionTimeout).ConfigureAwait(false);
        var validated = ChildResultCodec.ParseAndValidate(
            childPayload,
            options.AssemblyPath,
            nonce);
        if (validated.FatalError is not null)
        {
            writer.Write(
                RuntimeFrameKind.Exception,
                BridgePayloadCodec.Serialize(new ExceptionPayload(
                    validated.FatalError.TypeName,
                    validated.FatalError.Message,
                    validated.FatalError.StackTrace,
                    null,
                    started.Elapsed.TotalMilliseconds)));
            var status = child.ExitCode == 137 ? "out-of-memory" : "inspection-failed";
            WriteExit(writer, status, child.ExitCode, started);
            return child.ExitCode;
        }
        if (child.ExitCode != 0)
        {
            WriteProtocolError(
                writer,
                "checked-jit-child-crash",
                CreateChildFailureMessage(child.StandardError, child.ExitCode));
            WriteExit(writer, "process-crash", child.ExitCode, started);
            return child.ExitCode;
        }

        var rawAssembly = StrictUtf8.GetString(child.StandardOutput);
        var sourceMaps = CheckedJitSourceMapping.LoadForDeclaredKind(
            options.AssemblyPath,
            Environment.GetEnvironmentVariable(
                CheckedJitBridgeContract.SourceMappingKindEnvironmentVariable));
        var assemblyText = CheckedJitDisassemblyDocument.SelectPreparedMethods(
            rawAssembly,
            validated.Methods,
            sourceMaps);
        WriteChunks(writer, RuntimeFrameKind.JitAssembly, Encoding.UTF8.GetBytes(assemblyText));
        writer.Write(
            RuntimeFrameKind.JitSummary,
            BridgePayloadCodec.Serialize(new JitSummaryPayload(
                Environment.Version.ToString(),
                validated.AssemblyName,
                options.MethodFilter,
                validated.Methods)));

        var preparedAny = false;
        foreach (var method in validated.Methods)
        {
            if (string.Equals(method.Status, "prepared", StringComparison.Ordinal))
            {
                preparedAny = true;
                break;
            }
        }
        var exitCode = preparedAny && assemblyText.Length > 0 ? 0 : preparedAny ? 1 : 2;
        WriteExit(
            writer,
            exitCode == 0
                ? "completed"
                : exitCode == 2 ? "no-matching-methods" : "inspection-failed",
            exitCode,
            started);
        return exitCode;
    }

    internal static async Task<byte[]> AwaitMetadataAsync(
        Task<byte[]> metadataRead,
        TimeSpan timeout)
    {
        if (metadataRead is null)
            throw new ArgumentNullException(nameof(metadataRead));
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        if (await Task.WhenAny(metadataRead, Task.Delay(timeout)).ConfigureAwait(false) != metadataRead)
        {
            throw new InvalidDataException(
                "Checked JIT child metadata pipe did not reach EOF after the child exited.");
        }
        return await metadataRead.ConfigureAwait(false);
    }

    private static string CreateChildFailureMessage(byte[] stderr, int exitCode)
    {
        var detail = Encoding.UTF8.GetString(stderr);
        if (detail.Length > 1_024)
            detail = detail.Substring(0, 1_024);
        return detail.Length == 0
            ? $"Checked JIT child exited with code {exitCode}."
            : $"Checked JIT child exited with code {exitCode}: {detail}";
    }

    private static void WriteChunks(RuntimeFrameWriter writer, RuntimeFrameKind kind, byte[] content)
    {
        for (var offset = 0; offset < content.Length; offset += JitFrameChunkSize)
        {
            var length = Math.Min(JitFrameChunkSize, content.Length - offset);
            writer.Write(kind, content, offset, length);
        }
    }

    private static void WriteProtocolError(RuntimeFrameWriter writer, string code, string message)
    {
        if (message.Length > 4_096)
            message = message.Substring(0, 4_096);
        writer.Write(
            RuntimeFrameKind.ProtocolError,
            BridgePayloadCodec.Serialize(new ProtocolErrorPayload(code, message)));
    }

    private static void WriteExit(
        RuntimeFrameWriter writer,
        string status,
        int exitCode,
        Stopwatch started) =>
        writer.Write(
            RuntimeFrameKind.Exit,
            BridgePayloadCodec.Serialize(new ExitPayload(
                status,
                exitCode,
                started.Elapsed.TotalMilliseconds)));
}
