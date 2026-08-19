using System.Buffers;
using System.Diagnostics;
using SharpLabNext.RuntimeProtocol;

return await ProcessBridgeProgram.RunAsync(args);

internal static class ProcessBridgeProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        await using var writer = new RuntimeFrameWriter(
            Console.OpenStandardOutput(),
            RuntimeFrameTransport.Base64Line);
        var started = Stopwatch.StartNew();
        try
        {
            var parsed = ProcessBridgeArguments.Parse(args);
            using var process = StartProcess(parsed);
            var stdout = ForwardOutputAsync(process.StandardOutput.BaseStream, writer, RuntimeFrameKind.Stdout);
            var stderr = parsed.FiltersWineNoise
                ? ForwardWineStderrAsync(process.StandardError.BaseStream, writer)
                : ForwardOutputAsync(process.StandardError.BaseStream, writer, RuntimeFrameKind.Stderr);
            var stdin = ForwardInputAsync(process.StandardInput.BaseStream);

            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr, stdin);
            await WriteJsonAsync(writer, RuntimeFrameKind.Exit, new
            {
                status = process.ExitCode == 0 ? "completed" : "non-zero-exit",
                exitCode = process.ExitCode,
                elapsedMilliseconds = started.Elapsed.TotalMilliseconds
            });
            return process.ExitCode;
        }
        catch (Exception exception)
        {
            await WriteJsonAsync(writer, RuntimeFrameKind.ProtocolError, new
            {
                code = "process-bridge-failed",
                message = exception.Message
            });
            await WriteJsonAsync(writer, RuntimeFrameKind.Exit, new
            {
                status = "process-crash",
                exitCode = 1,
                elapsedMilliseconds = started.Elapsed.TotalMilliseconds
            });
            return 1;
        }
    }

    private static Process StartProcess(ProcessBridgeArguments parsed)
    {
        var startInfo = new ProcessStartInfo(parsed.Executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        foreach (var argument in parsed.FixedArguments)
            startInfo.ArgumentList.Add(argument);
        foreach (var argument in parsed.UserArguments)
            startInfo.ArgumentList.Add(argument);

        if (parsed.FiltersWineNoise)
        {
            startInfo.Environment.TryAdd("WINEDEBUG", "-all");
            startInfo.Environment.TryAdd("XDG_CACHE_HOME", "/tmp/.cache");
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The bridged process could not be started.");
    }

    private static async Task ForwardOutputAsync(
        Stream stream,
        RuntimeFrameWriter writer,
        RuntimeFrameKind kind)
    {
        var buffer = new byte[16 * 1024];
        while (await stream.ReadAsync(buffer) is var read && read > 0)
            await writer.WriteAsync(kind, buffer.AsMemory(0, read));
    }

    private static async Task ForwardWineStderrAsync(
        Stream stream,
        RuntimeFrameWriter writer)
    {
        var filter = new WineStderrFilter(writer);
        var buffer = new byte[16 * 1024];
        while (await stream.ReadAsync(buffer) is var read && read > 0)
            await filter.WriteAsync(buffer.AsMemory(0, read));
        await filter.CompleteAsync();
    }

    private static async Task ForwardInputAsync(Stream destination)
    {
        try
        {
            var inputPath = Environment.GetEnvironmentVariable("SHARPLABNEXT_STDIN_PATH");
            if (!string.IsNullOrWhiteSpace(inputPath) && File.Exists(inputPath))
            {
                await using var input = new FileStream(
                    inputPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 16 * 1024,
                    useAsync: true);
                await input.CopyToAsync(destination);
            }
        }
        catch (IOException)
        {
            // The user process may close stdin before consuming all input.
        }
        finally
        {
            await destination.DisposeAsync();
        }
    }

    private static ValueTask WriteJsonAsync(RuntimeFrameWriter writer, RuntimeFrameKind kind, object value) =>
        writer.WriteAsync(kind, RuntimeStructuredPayloadCodec.Serialize(value));
}

internal sealed class WineStderrFilter(RuntimeFrameWriter writer)
{
    private static byte[][] Warnings { get; } =
    [
        "wineserver: could not save registry branch to system.reg : Read-only file system"u8.ToArray(),
        "wineserver: could not save registry branch to userdef.reg : Read-only file system"u8.ToArray(),
        "wineserver: could not save registry branch to user.reg : Read-only file system"u8.ToArray()
    ];

    private readonly List<byte> _pending = new(128);
    private bool _suppressLineEnding;
    private bool _heldCarriageReturn;

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes)
    {
        var output = new ArrayBufferWriter<byte>(bytes.Length);
        foreach (var value in bytes.Span)
        {
            if (_suppressLineEnding)
            {
                if (value == (byte)'\n')
                {
                    _suppressLineEnding = false;
                    _heldCarriageReturn = false;
                    continue;
                }
                if (value == (byte)'\r' && !_heldCarriageReturn)
                {
                    _heldCarriageReturn = true;
                    continue;
                }

                if (_heldCarriageReturn)
                    Append(output, (byte)'\r');
                _suppressLineEnding = false;
                _heldCarriageReturn = false;
            }

            _pending.Add(value);
            Drain(output);
        }

        if (output.WrittenCount > 0)
            await writer.WriteAsync(RuntimeFrameKind.Stderr, output.WrittenMemory);
    }

    public async ValueTask CompleteAsync()
    {
        _suppressLineEnding = false;
        _heldCarriageReturn = false;
        if (_pending.Count == 0)
            return;

        await writer.WriteAsync(RuntimeFrameKind.Stderr, _pending.ToArray());
        _pending.Clear();
    }

    private void Drain(ArrayBufferWriter<byte> output)
    {
        while (_pending.Count > 0)
        {
            var warning = Warnings.FirstOrDefault(PendingStartsWith);
            if (warning is not null)
            {
                _pending.RemoveRange(0, warning.Length);
                _suppressLineEnding = true;
                _heldCarriageReturn = false;
                continue;
            }

            if (Warnings.Any(WarningStartsWithPending))
                return;

            Append(output, _pending[0]);
            _pending.RemoveAt(0);
        }
    }

    private bool PendingStartsWith(byte[] warning)
    {
        if (_pending.Count < warning.Length)
            return false;
        for (var index = 0; index < warning.Length; index++)
        {
            if (_pending[index] != warning[index])
                return false;
        }
        return true;
    }

    private bool WarningStartsWithPending(byte[] warning)
    {
        if (_pending.Count > warning.Length)
            return false;
        for (var index = 0; index < _pending.Count; index++)
        {
            if (_pending[index] != warning[index])
                return false;
        }
        return true;
    }

    private static void Append(ArrayBufferWriter<byte> output, byte value)
    {
        output.GetSpan(1)[0] = value;
        output.Advance(1);
    }
}

internal sealed record ProcessBridgeArguments(
    string Executable,
    string[] FixedArguments,
    string[] UserArguments)
{
    private const string Usage =
        "Usage: SharpLabNext.WineRunner bridge <executable> [fixed arguments...] -- [user arguments...]";

    public bool FiltersWineNoise => IsWineExecutable(Executable);

    public static ProcessBridgeArguments Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length < 3 || !string.Equals(args[0], "bridge", StringComparison.Ordinal))
            throw new ArgumentException(Usage, nameof(args));

        var separator = Array.IndexOf(args, "--", 2);
        if (separator < 0)
            throw new ArgumentException($"{Usage} The '--' separator is required.", nameof(args));

        var executable = ValidateExecutable(args[1]);
        var fixedArguments = args[2..separator];
        var userArguments = args[(separator + 1)..];
        ValidateArguments(fixedArguments, "fixed");
        ValidateArguments(userArguments, "user");
        return new ProcessBridgeArguments(executable, fixedArguments, userArguments);
    }

    internal static bool IsWineExecutable(string executable)
    {
        var normalized = executable.Replace('\\', '/');
        var fileName = normalized[(normalized.LastIndexOf('/') + 1)..];
        var nameWithoutExtension = fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4]
            : fileName;
        return nameWithoutExtension.Equals("wine", StringComparison.OrdinalIgnoreCase) ||
               nameWithoutExtension.Equals("wine64", StringComparison.OrdinalIgnoreCase) ||
               nameWithoutExtension.Equals("wine-stable", StringComparison.OrdinalIgnoreCase) ||
               nameWithoutExtension.Equals("wine64-stable", StringComparison.OrdinalIgnoreCase) ||
               nameWithoutExtension.Equals("wine-development", StringComparison.OrdinalIgnoreCase) ||
               nameWithoutExtension.Equals("wine64-development", StringComparison.OrdinalIgnoreCase) ||
               nameWithoutExtension.Equals("wine-staging", StringComparison.OrdinalIgnoreCase) ||
               nameWithoutExtension.Equals("wine64-staging", StringComparison.OrdinalIgnoreCase);
    }

    private static string ValidateExecutable(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            throw new ArgumentException("The process bridge executable is invalid.", nameof(value));

        if (!value.Contains('/') && !value.Contains('\\'))
        {
            if (value[0] == '-' || value is "." or ".." || value.Any(static character =>
                    !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '+' or '-')))
            {
                throw new ArgumentException("The process bridge executable name is invalid.", nameof(value));
            }

            return value;
        }

        if (!Path.IsPathFullyQualified(value) || ContainsTraversalSegment(value))
            throw new ArgumentException("The process bridge executable path must be absolute and canonical.", nameof(value));

        var fullPath = Path.GetFullPath(value);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The process bridge executable was not found.", fullPath);
        return fullPath;
    }

    private static void ValidateArguments(IEnumerable<string> arguments, string group)
    {
        if (arguments.Any(static argument => argument.Contains('\0')))
            throw new ArgumentException($"A {group} process bridge argument contains a null character.");
    }

    private static bool ContainsTraversalSegment(string path) =>
        path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Any(static segment => segment is "." or "..");
}
