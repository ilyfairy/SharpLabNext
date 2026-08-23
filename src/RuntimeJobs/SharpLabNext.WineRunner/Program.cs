using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Iced.Intel;
using SharpLabNext.RuntimeProtocol;

return args.Length > 0 && string.Equals(args[0], "desktop-jit", StringComparison.Ordinal)
    ? await DesktopClrJitProgram.RunAsync(args)
    : await ProcessBridgeProgram.RunAsync(args);

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
            var processExitCode = process.ExitCode;
            if (parsed.FiltersWineNoise && !await StopWineServerAsync(parsed, CancellationToken.None))
            {
                await WriteJsonAsync(writer, RuntimeFrameKind.ProtocolError, new
                {
                    code = "wine-server-cleanup-failed",
                    message = "The Wine server did not terminate after the bridged process exited."
                });
                processExitCode = processExitCode == 0 ? 1 : processExitCode;
            }
            await WriteJsonAsync(writer, RuntimeFrameKind.Exit, new
            {
                status = processExitCode == 0 ? "completed" : "non-zero-exit",
                exitCode = processExitCode,
                elapsedMilliseconds = started.Elapsed.TotalMilliseconds
            });
            return processExitCode;
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

    internal static async Task<bool> StopWineServerAsync(
        ProcessBridgeArguments parsed,
        CancellationToken cancellationToken)
    {
        // Wine keeps a per-prefix daemon after the client exits.  A measured
        // keeper must be left with exactly one process, so terminate that daemon
        // before publishing the framed exit record.  The helper commands inherit
        // WINEPREFIX and never write to the runtime workspace.
        var wineserver = parsed.WineserverExecutable;
        if (wineserver is null)
            return false;

        _ = await RunWineServerCommandAsync(
            wineserver,
            "-k",
            TimeSpan.FromSeconds(2),
            cancellationToken).ConfigureAwait(false);
        // wineserver -k returns non-zero when the server has already exited.
        // In either case, -w is the authoritative bounded residue check.
        return await RunWineServerCommandAsync(
            wineserver,
            "-w",
            TimeSpan.FromSeconds(2),
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<bool> RunWineServerCommandAsync(
        string executable,
        string argument,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["WINEDEBUG"] = "-all";
        try
        {
            if (!process.Start())
                return false;
        }
        catch (Exception)
        {
            return false;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var completed = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            completed = true;
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // The process may have exited between HasExited and Kill. The
                // output tasks are still observed in the bounded cleanup below.
            }
        }
        catch (Exception)
        {
            completed = false;
        }

        return completed && process.HasExited && process.ExitCode == 0;
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

    public string? WineserverExecutable =>
        FiltersWineNoise
            ? ResolveWineserverExecutable(Executable)
            : null;

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

    private static string? ResolveWineserverExecutable(string executable)
    {
        if (executable.Contains('/') || executable.Contains('\\'))
        {
            var directory = Path.GetDirectoryName(executable);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                var sibling = Path.Combine(directory, "wineserver");
                if (File.Exists(sibling))
                    return sibling;
            }
        }

        return "wineserver";
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

internal static class DesktopClrJitProgram
{
    private const string WineExecutable = "/usr/lib/wine/wine64";
    private const string WineServerExecutable = "/usr/lib/wine/wineserver64";
    private const string HelperPath = @"Z:\opt\sharplabnext\SharpLabNext.DesktopClrJitInspector.exe";
    private const string CapturePath = "/tmp/sharplabnext-desktop-jit.bin";
    private const string CaptureTemporaryPath = CapturePath + ".tmp";
    private const int HelperOutputLimitBytes = 64 * 1024;
    private const int JitFrameChunkSize = 64 * 1024;

    public static async Task<int> RunAsync(string[] args)
    {
        await using var writer = new RuntimeFrameWriter(
            Console.OpenStandardOutput(),
            RuntimeFrameTransport.Base64Line);
        var started = Stopwatch.StartNew();
        try
        {
            var options = DesktopClrJitArguments.Parse(args);
            DeleteStaleCapture();
            var wineServerStopped = false;
            try
            {
                await RunHelperAsync(options).ConfigureAwait(false);
                wineServerStopped = await StopWineServerAsync().ConfigureAwait(false);
                if (!wineServerStopped)
                {
                    throw new InvalidDataException("The Desktop CLR JIT Wine server did not terminate.");
                }
                var capture = await DesktopClrJitCapture.ReadAsync(CapturePath).ConfigureAwait(false);
                if (capture.ModuleVersionId != ReadModuleVersionId(options.AssemblyPath))
                    throw new InvalidDataException("The Desktop CLR JIT capture does not match the user assembly MVID.");
                var document = DesktopClrJitDisassembly.Decode(capture);
                await WriteChunksAsync(
                    writer,
                    RuntimeFrameKind.JitAssembly,
                    Encoding.UTF8.GetBytes(document.Text)).ConfigureAwait(false);
                await writer.WriteAsync(
                    RuntimeFrameKind.JitSummary,
                    RuntimeStructuredPayloadCodec.Serialize(new
                    {
                        runtimeVersion = capture.RuntimeVersion,
                        assembly = Path.GetFileNameWithoutExtension(options.AssemblyPath),
                        methodFilter = options.MethodFilter,
                        methods = document.Methods
                    })).ConfigureAwait(false);
                var completed = document.Methods.Count > 0;
                await WriteExitAsync(
                        writer,
                        completed ? "completed" : "no-matching-methods",
                        completed ? 0 : 2,
                        started.Elapsed.TotalMilliseconds)
                    .ConfigureAwait(false);
                return completed ? 0 : 2;
            }
            finally
            {
                if (!wineServerStopped)
                {
                    try
                    {
                        _ = await StopWineServerAsync().ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Preserve the original helper/protocol failure. The
                        // Supervisor removes a failed one-shot generation.
                    }
                }
                DeleteStaleCapture();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await writer.WriteAsync(
                RuntimeFrameKind.ProtocolError,
                RuntimeStructuredPayloadCodec.Serialize(new
                {
                    code = "desktop-clr-jit-failed",
                    message = BoundedMessage(exception)
                })).ConfigureAwait(false);
            await WriteExitAsync(writer, "inspection-failed", 1, started.Elapsed.TotalMilliseconds)
                .ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task RunHelperAsync(DesktopClrJitArguments options)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(WineExecutable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add(HelperPath);
        process.StartInfo.ArgumentList.Add("capture");
        process.StartInfo.ArgumentList.Add(ToWinePath(options.AssemblyPath));
        process.StartInfo.ArgumentList.Add(@"Z:\tmp\sharplabnext-desktop-jit.bin");
        if (options.MethodFilter is not null)
            process.StartInfo.ArgumentList.Add(options.MethodFilter);
        process.StartInfo.Environment["WINEDEBUG"] = "-all";
        process.StartInfo.Environment["XDG_CACHE_HOME"] = "/tmp/.cache";
        if (!process.Start())
            throw new InvalidOperationException("The Desktop CLR JIT helper could not be started.");

        var stdout = DrainHelperOutputAsync(process.StandardOutput.BaseStream, process);
        var stderr = DrainHelperOutputAsync(process.StandardError.BaseStream, process);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(), stdout, stderr).ConfigureAwait(false);
        }
        catch
        {
            TryKill(process);
            throw;
        }

        if (process.ExitCode != 0)
            throw new InvalidDataException("The Desktop CLR JIT helper did not complete successfully.");
    }

    private static async Task<bool> StopWineServerAsync()
    {
        _ = await ProcessBridgeProgram.RunWineServerCommandAsync(
            WineServerExecutable,
            "-k",
            TimeSpan.FromSeconds(2),
            CancellationToken.None).ConfigureAwait(false);
        return await ProcessBridgeProgram.RunWineServerCommandAsync(
            WineServerExecutable,
            "-w",
            TimeSpan.FromSeconds(2),
            CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task DrainHelperOutputAsync(Stream stream, Process process)
    {
        var buffer = new byte[8 * 1024];
        var observed = 0;
        while (await stream.ReadAsync(buffer).ConfigureAwait(false) is var read && read > 0)
        {
            observed = checked(observed + read);
            if (observed <= HelperOutputLimitBytes)
                continue;

            TryKill(process);
            throw new InvalidDataException("The Desktop CLR JIT helper output exceeds the protocol limit.");
        }
    }

    private static string ToWinePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith("/workspace/", StringComparison.Ordinal))
            throw new ArgumentException("The Desktop CLR JIT entry assembly must be under /workspace.", nameof(path));
        return "Z:" + fullPath.Replace('/', '\\');
    }

    private static void DeleteStaleCapture()
    {
        DeleteRegularFile(CapturePath);
        DeleteRegularFile(CaptureTemporaryPath);
    }

    private static void DeleteRegularFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            return;
        if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("The Desktop CLR JIT capture path must not be a symbolic link.");
        File.Delete(path);
    }

    private static Guid ReadModuleVersionId(string assemblyPath)
    {
        using var stream = new FileStream(
            assemblyPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        using var pe = new PEReader(stream, PEStreamOptions.PrefetchMetadata);
        if (!pe.HasMetadata)
            throw new BadImageFormatException("The Desktop CLR JIT entry assembly has no managed metadata.");
        var metadata = pe.GetMetadataReader();
        return metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
    }

    private static async Task WriteChunksAsync(
        RuntimeFrameWriter writer,
        RuntimeFrameKind kind,
        byte[] content)
    {
        for (var offset = 0; offset < content.Length; offset += JitFrameChunkSize)
        {
            var length = Math.Min(JitFrameChunkSize, content.Length - offset);
            await writer.WriteAsync(kind, content.AsMemory(offset, length)).ConfigureAwait(false);
        }
    }

    private static ValueTask WriteExitAsync(
        RuntimeFrameWriter writer,
        string status,
        int exitCode,
        double elapsedMilliseconds) =>
        writer.WriteAsync(RuntimeFrameKind.Exit, RuntimeStructuredPayloadCodec.Serialize(new
        {
            status,
            exitCode,
            elapsedMilliseconds
        }));

    private static string BoundedMessage(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ');
        return message.Length <= 512 ? message : message[..512];
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // The process may have exited between HasExited and Kill.
        }
    }
}

internal sealed record DesktopClrJitArguments(string AssemblyPath, string? MethodFilter)
{
    private const string Usage =
        "Usage: SharpLabNext.WineRunner desktop-jit <absolute-entry-assembly> <method-filter>";

    public static DesktopClrJitArguments Parse(string[] args)
    {
        if (args.Length is < 2 or > 3 || !string.Equals(args[0], "desktop-jit", StringComparison.Ordinal))
            throw new ArgumentException(Usage, nameof(args));
        if (string.IsNullOrWhiteSpace(args[1]) || args[1].IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new ArgumentException("The Desktop CLR JIT entry assembly path is invalid.", nameof(args));
        var assemblyPath = Path.GetFullPath(args[1]);
        if (!Path.IsPathFullyQualified(args[1]) || !File.Exists(assemblyPath))
            throw new FileNotFoundException("The Desktop CLR JIT entry assembly was not found.", assemblyPath);
        if (args.Length == 3 && (args[2].Length > 256 || args[2].Any(char.IsControl)))
            throw new ArgumentException("The Desktop CLR JIT method filter is invalid.", nameof(args));
        var filter = args.Length == 3 ? args[2] : null;
        return new DesktopClrJitArguments(assemblyPath, string.IsNullOrWhiteSpace(filter) ? null : filter);
    }
}

internal sealed record DesktopClrJitCapture(
    string RuntimeVersion,
    Guid ModuleVersionId,
    IReadOnlyList<DesktopClrJitMethod> Methods)
{
    private static ReadOnlySpan<byte> Magic => "SLNDCJ01"u8;
    private const uint FormatVersion = 1;
    internal const int MaximumCaptureBytes = 16 * 1024 * 1024;
    private const int MaximumNativeCodeBytes = 8 * 1024 * 1024;
    private const int MaximumMethods = 512;
    private const int MaximumRuntimeVersionBytes = 64;
    private const int MaximumDisplayNameBytes = 1024;
    private const int MaximumMethodCodeBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<DesktopClrJitCapture> ReadAsync(string capturePath)
    {
        if (!string.Equals(capturePath, "/tmp/sharplabnext-desktop-jit.bin", StringComparison.Ordinal))
            throw new InvalidDataException("The Desktop CLR JIT capture path is not the fixed tmpfs path.");
        var info = new FileInfo(capturePath);
        if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("The Desktop CLR JIT helper did not create a regular capture file.");
        if (info.Length is <= 0 or > MaximumCaptureBytes)
            throw new InvalidDataException("The Desktop CLR JIT capture size is outside the protocol limit.");

        var bytes = new byte[checked((int)info.Length)];
        await using var stream = new FileStream(
            capturePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length != bytes.Length)
            throw new InvalidDataException("The Desktop CLR JIT capture changed while it was read.");
        await stream.ReadExactlyAsync(bytes).ConfigureAwait(false);
        if (stream.Length != bytes.Length)
            throw new InvalidDataException("The Desktop CLR JIT capture changed while it was read.");
        return Parse(bytes);
    }

    public static DesktopClrJitCapture Parse(ReadOnlySpan<byte> capture)
    {
        if (capture.Length is 0 or > MaximumCaptureBytes)
            throw new InvalidDataException("The Desktop CLR JIT capture size is outside the protocol limit.");
        var reader = new CaptureReader(capture);
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("The Desktop CLR JIT capture magic is invalid.");
        if (reader.ReadUInt32() != FormatVersion)
            throw new InvalidDataException("The Desktop CLR JIT capture version is not supported.");
        var methodCount = checked((int)reader.ReadUInt32());
        var declaredTotalCodeBytes = checked((int)reader.ReadUInt32());
        if (methodCount is < 0 or > MaximumMethods ||
            declaredTotalCodeBytes is < 0 or > MaximumNativeCodeBytes)
        {
            throw new InvalidDataException("The Desktop CLR JIT capture header is outside the protocol limit.");
        }
        var moduleVersionId = new Guid(reader.ReadBytes(16));
        var runtimeVersion = ReadText(ref reader, reader.ReadUInt16(), MaximumRuntimeVersionBytes, "runtime version");
        if (!Version.TryParse(runtimeVersion, out _))
            throw new InvalidDataException("The Desktop CLR JIT capture runtime version is invalid.");

        var methods = new List<DesktopClrJitMethod>(methodCount);
        var tokens = new HashSet<uint>();
        var ranges = new HashSet<(ulong Address, uint Length)>();
        var totalCodeBytes = 0;
        for (var index = 0; index < methodCount; index++)
        {
            var token = reader.ReadUInt32();
            if ((token & 0xff000000) != 0x06000000 || (token & 0x00ffffff) == 0 || !tokens.Add(token))
                throw new InvalidDataException("The Desktop CLR JIT capture contains an invalid or duplicate method token.");
            var nativeAddress = reader.ReadUInt64();
            var codeLength = reader.ReadUInt32();
            var displayNameLength = reader.ReadUInt16();
            var displayName = ReadText(ref reader, displayNameLength, MaximumDisplayNameBytes, "method display name");
            if (codeLength == 0 ||
                codeLength > MaximumMethodCodeBytes ||
                codeLength > (uint)reader.Remaining ||
                nativeAddress == 0 ||
                nativeAddress > ulong.MaxValue - codeLength ||
                !ranges.Add((nativeAddress, codeLength)))
                throw new InvalidDataException("The Desktop CLR JIT capture method code length is invalid.");
            totalCodeBytes = checked(totalCodeBytes + checked((int)codeLength));
            if (totalCodeBytes > MaximumNativeCodeBytes)
                throw new InvalidDataException("The Desktop CLR JIT capture native code exceeds the protocol limit.");
            methods.Add(new DesktopClrJitMethod(
                token,
                displayName,
                nativeAddress,
                reader.ReadBytes(checked((int)codeLength)).ToArray()));
        }
        if (totalCodeBytes != declaredTotalCodeBytes || reader.Remaining != 0)
            throw new InvalidDataException("The Desktop CLR JIT capture size or trailing bytes are invalid.");
        return new DesktopClrJitCapture(runtimeVersion, moduleVersionId, methods);
    }

    private static string ReadText(ref CaptureReader reader, int length, int maximumLength, string field)
    {
        if (length <= 0 || length > maximumLength)
            throw new InvalidDataException($"The Desktop CLR JIT capture {field} length is invalid.");
        string value;
        try
        {
            value = StrictUtf8.GetString(reader.ReadBytes(length));
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"The Desktop CLR JIT capture {field} is not valid UTF-8.", exception);
        }
        if (value.Any(char.IsControl))
            throw new InvalidDataException($"The Desktop CLR JIT capture {field} contains a control character.");
        return value;
    }

    private ref struct CaptureReader(ReadOnlySpan<byte> value)
    {
        private ReadOnlySpan<byte> _remaining = value;

        public int Remaining => _remaining.Length;

        public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(sizeof(uint)));

        public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(sizeof(ulong)));

        public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(ReadBytes(sizeof(ushort)));

        public ReadOnlySpan<byte> ReadBytes(int length)
        {
            if (length < 0 || length > _remaining.Length)
                throw new InvalidDataException("The Desktop CLR JIT capture is truncated.");
            var result = _remaining[..length];
            _remaining = _remaining[length..];
            return result;
        }
    }
}

internal sealed record DesktopClrJitMethod(
    uint MetadataToken,
    string DisplayName,
    ulong NativeAddress,
    byte[] NativeCode);

internal sealed record DesktopClrJitDocument(string Text, IReadOnlyList<DesktopClrJitMethodResult> Methods);

internal sealed record DesktopClrJitMethodResult(
    string Method,
    string DisplayName,
    string Status,
    string? Address,
    string? Error,
    int NativeCodeSize,
    int InstructionCount,
    IReadOnlyList<object> LinkedRanges,
    string MappingSource);

internal static class DesktopClrJitDisassembly
{
    public static DesktopClrJitDocument Decode(DesktopClrJitCapture capture)
    {
        var text = new StringBuilder();
        var methods = new List<DesktopClrJitMethodResult>(capture.Methods.Count);
        foreach (var method in capture.Methods)
        {
            if (text.Length > 0)
                text.AppendLine().AppendLine();
            var decoded = DecodeMethod(method, capture.RuntimeVersion);
            text.Append(decoded.Text);
            methods.Add(new DesktopClrJitMethodResult(
                $"0x{method.MetadataToken:x8}",
                method.DisplayName,
                "prepared",
                $"0x{method.NativeAddress:x}",
                null,
                method.NativeCode.Length,
                decoded.InstructionCount,
                [],
                "none"));
        }
        return new DesktopClrJitDocument(text.ToString(), methods);
    }

    private static (string Text, int InstructionCount) DecodeMethod(
        DesktopClrJitMethod method,
        string runtimeVersion)
    {
        var decoder = Iced.Intel.Decoder.Create(64, method.NativeCode);
        decoder.IP = method.NativeAddress;
        var formatter = new IntelFormatter();
        var output = new FormatterStringOutput();
        var text = new StringBuilder();
        text.Append("; Assembly listing for method ").AppendLine(method.DisplayName);
        text.Append("; Desktop CLR version ").AppendLine(runtimeVersion);
        text.Append("; Native address 0x")
            .AppendLine(method.NativeAddress.ToString("x", CultureInfo.InvariantCulture));
        text.AppendLine("G_M000_IG00:");
        var instructionCount = 0;
        var decodedBytes = 0;
        while (decodedBytes < method.NativeCode.Length)
        {
            var instruction = decoder.Decode();
            if (decoder.LastError != DecoderError.None ||
                instruction.Code == Code.INVALID ||
                instruction.Length == 0 ||
                instruction.Length > method.NativeCode.Length - decodedBytes)
            {
                throw new InvalidDataException("The Desktop CLR JIT capture contains an invalid or truncated x64 instruction.");
            }
            output.Clear();
            formatter.Format(instruction, output);
            text.Append("       L")
                .Append((instruction.IP - method.NativeAddress).ToString("x4", CultureInfo.InvariantCulture))
                .Append(": ")
                .AppendLine(output.Value);
            instructionCount++;
            decodedBytes += instruction.Length;
        }
        if (instructionCount == 0)
            throw new InvalidDataException("The Desktop CLR JIT capture contains an empty native method.");
        text.Append("; Total bytes of code ")
            .Append(method.NativeCode.Length.ToString(CultureInfo.InvariantCulture));
        return (text.ToString(), instructionCount);
    }

    private sealed class FormatterStringOutput : FormatterOutput
    {
        private readonly StringBuilder _builder = new();

        public string Value => _builder.ToString();

        public void Clear() => _builder.Clear();

        public override void Write(string text, FormatterTextKind kind) => _builder.Append(text);
    }
}
