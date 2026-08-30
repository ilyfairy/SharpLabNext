using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SharpLabNext.CheckedJitBridge;

internal enum ProcessOutputKind
{
    StandardOutput,
    StandardError
}

internal enum ChildTerminationReason
{
    Exited,
    OutputLimitExceeded,
    TimedOut,
    Cancelled,
    ProtocolFailure
}

internal sealed class BoundedProcessOutput
{
    private readonly object _gate = new();
    private readonly ArrayBufferWriter<byte> _standardOutput = new();
    private readonly ArrayBufferWriter<byte> _standardError = new();
    private readonly int _standardOutputLimit;
    private readonly int _standardErrorLimit;
    private readonly int _totalLimit;
    private bool _limitExceeded;
    private int _totalBytes;

    public BoundedProcessOutput(int standardOutputLimit, int standardErrorLimit, int totalLimit)
    {
        if (standardOutputLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(standardOutputLimit));
        if (standardErrorLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(standardErrorLimit));
        if (totalLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalLimit));

        _standardOutputLimit = standardOutputLimit;
        _standardErrorLimit = standardErrorLimit;
        _totalLimit = totalLimit;
    }

    public bool LimitExceeded
    {
        get
        {
            lock (_gate)
                return _limitExceeded;
        }
    }

    public int TotalBytes
    {
        get
        {
            lock (_gate)
                return _totalBytes;
        }
    }

    public byte[] StandardOutput
    {
        get
        {
            lock (_gate)
                return _standardOutput.WrittenSpan.ToArray();
        }
    }

    public byte[] StandardError
    {
        get
        {
            lock (_gate)
                return _standardError.WrittenSpan.ToArray();
        }
    }

    public bool TryAppend(ProcessOutputKind kind, ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            var destination = kind == ProcessOutputKind.StandardOutput
                ? _standardOutput : _standardError;
            var streamLimit = kind == ProcessOutputKind.StandardOutput
                ? _standardOutputLimit : _standardErrorLimit;
            var streamRemaining = Math.Max(0, streamLimit - destination.WrittenCount);
            var totalRemaining = Math.Max(0, _totalLimit - _totalBytes);
            var accepted = Math.Min(bytes.Length, Math.Min(streamRemaining, totalRemaining));
            if (accepted > 0)
            {
                bytes[..accepted].CopyTo(destination.GetSpan(accepted));
                destination.Advance(accepted);
                _totalBytes += accepted;
            }

            if (accepted == bytes.Length)
                return true;

            _limitExceeded = true;
            return false;
        }
    }
}

internal sealed class BoundedChildProcessLimits
{
    public BoundedChildProcessLimits(int standardOutputBytes, int standardErrorBytes, int totalOutputBytes, TimeSpan executionTimeout, TimeSpan cleanupTimeout)
    {
        if (standardOutputBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(standardOutputBytes));
        if (standardErrorBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(standardErrorBytes));
        if (totalOutputBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalOutputBytes));
        if (executionTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(executionTimeout));
        if (cleanupTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cleanupTimeout));

        StandardOutputBytes = standardOutputBytes;
        StandardErrorBytes = standardErrorBytes;
        TotalOutputBytes = totalOutputBytes;
        ExecutionTimeout = executionTimeout;
        CleanupTimeout = cleanupTimeout;
    }

    public int StandardOutputBytes { get; }

    public int StandardErrorBytes { get; }

    public int TotalOutputBytes { get; }

    public TimeSpan ExecutionTimeout { get; }

    public TimeSpan CleanupTimeout { get; }
}

internal sealed record BoundedChildProcessResult(int ProcessId, int ExitCode, ChildTerminationReason TerminationReason, byte[] StandardOutput, byte[] StandardError);

internal static class BoundedChildProcessRunner
{
    private const int ReadBufferSize = 16 * 1024;

    public static async Task<BoundedChildProcessResult> RunAsync(ProcessStartInfo startInfo, BoundedChildProcessLimits limits, CancellationToken cancellationToken, Task? protocolFailureSignal = null, Action? processStarted = null)
    {
        ValidateStartInfo(startInfo);
        if (limits is null)
            throw new ArgumentNullException(nameof(limits));

        var output = new BoundedProcessOutput(limits.StandardOutputBytes, limits.StandardErrorBytes, limits.TotalOutputBytes);
        var outputExceeded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationRegistration = cancellationToken.Register(() => cancellation.TrySetResult(true));
        var timeout = Task.Delay(limits.ExecutionTimeout);
        var neverProtocolFailure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        protocolFailureSignal ??= neverProtocolFailure.Task;

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("The Checked JIT child process could not be started.");
        var processId = process.Id;
        processStarted?.Invoke();

        var stdout = CaptureAsync(process.StandardOutput.BaseStream, ProcessOutputKind.StandardOutput, output, outputExceeded);
        var stderr = CaptureAsync(process.StandardError.BaseStream, ProcessOutputKind.StandardError, output, outputExceeded);
        var wait = process.WaitForExitAsync();

        var completed = await Task.WhenAny(wait, outputExceeded.Task, cancellation.Task, timeout, protocolFailureSignal).ConfigureAwait(false);
        var reason = completed == outputExceeded.Task
            ? ChildTerminationReason.OutputLimitExceeded : completed == cancellation.Task
                ? ChildTerminationReason.Cancelled : completed == timeout
                    ? ChildTerminationReason.TimedOut : completed == protocolFailureSignal
                        ? ChildTerminationReason.ProtocolFailure : ChildTerminationReason.Exited;

        if (reason != ChildTerminationReason.Exited)
            KillProcessTree(process);

        await AwaitCleanupAsync(wait, process, limits.CleanupTimeout).ConfigureAwait(false);
        await AwaitDrainAsync(stdout, stderr, process, limits.CleanupTimeout).ConfigureAwait(false);
        if (output.LimitExceeded)
            reason = ChildTerminationReason.OutputLimitExceeded;

        return new BoundedChildProcessResult(processId, process.ExitCode, reason, output.StandardOutput, output.StandardError);
    }

    private static async Task CaptureAsync(Stream stream, ProcessOutputKind kind, BoundedProcessOutput output, TaskCompletionSource<bool> outputExceeded)
    {
        var buffer = new byte[ReadBufferSize];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false);
            if (read == 0)
                return;
            if (!output.TryAppend(kind, new ReadOnlySpan<byte>(buffer, 0, read)))
            {
                outputExceeded.TrySetResult(true);
                return;
            }
        }
    }

    private static async Task AwaitCleanupAsync(Task wait, Process process, TimeSpan cleanupTimeout)
    {
        if (await Task.WhenAny(wait, Task.Delay(cleanupTimeout)).ConfigureAwait(false) == wait)
        {
            await wait.ConfigureAwait(false);
            return;
        }

        KillProcessTree(process);
        if (await Task.WhenAny(wait, Task.Delay(cleanupTimeout)).ConfigureAwait(false) != wait)
            throw new TimeoutException("The Checked JIT child process did not terminate after process-tree kill.");
        await wait.ConfigureAwait(false);
    }

    private static async Task AwaitDrainAsync(Task stdout, Task stderr, Process process, TimeSpan cleanupTimeout)
    {
        var drains = Task.WhenAll(stdout, stderr);
        if (await Task.WhenAny(drains, Task.Delay(cleanupTimeout)).ConfigureAwait(false) == drains)
        {
            await drains.ConfigureAwait(false);
            return;
        }

        process.StandardOutput.Close();
        process.StandardError.Close();
        if (await Task.WhenAny(drains, Task.Delay(cleanupTimeout)).ConfigureAwait(false) != drains)
            throw new TimeoutException("The Checked JIT child output streams did not drain after termination.");
        await drains.ConfigureAwait(false);
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill.
        }
    }

    private static void ValidateStartInfo(ProcessStartInfo startInfo)
    {
        if (startInfo is null)
            throw new ArgumentNullException(nameof(startInfo));
        if (startInfo.UseShellExecute || !startInfo.RedirectStandardOutput || !startInfo.RedirectStandardError || startInfo.RedirectStandardInput)
        {
            throw new ArgumentException("Checked JIT child start info must use separated argv and redirected output without a shell.", nameof(startInfo));
        }
    }
}

internal static class BoundedStreamReader
{
    public static async Task<byte[]> ReadAsync(Stream stream, int maximumBytes, TaskCompletionSource<bool> failureSignal)
    {
        if (stream is null)
            throw new ArgumentNullException(nameof(stream));
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (failureSignal is null)
            throw new ArgumentNullException(nameof(failureSignal));

        using var output = new MemoryStream(Math.Min(maximumBytes, 16 * 1024));
        var buffer = new byte[8 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false);
            if (read == 0)
                return output.ToArray();
            if (output.Length > maximumBytes - read)
            {
                failureSignal.TrySetResult(true);
                throw new InvalidDataException("Checked JIT child metadata exceeds the bridge limit.");
            }
            output.Write(buffer, 0, read);
        }
    }
}
