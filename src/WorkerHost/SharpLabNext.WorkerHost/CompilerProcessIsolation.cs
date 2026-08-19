using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SharpLabNext.Contracts;

namespace SharpLabNext.WorkerHost;

public sealed record CompilerProcessIsolationOptions(
    bool Enabled,
    int MaximumConcurrentProcesses,
    long MaximumWorkingSetBytes,
    int MaximumRequestBytes,
    int MaximumResponseBytes,
    int MaximumStandardErrorBytes,
    int MemoryPollIntervalMilliseconds)
{
    public static CompilerProcessIsolationOptions Default { get; } = new(
        Enabled: true,
        MaximumConcurrentProcesses: 2,
        MaximumWorkingSetBytes: 512L * 1024 * 1024,
        MaximumRequestBytes: 2 * 1024 * 1024,
        MaximumResponseBytes: 64 * 1024 * 1024,
        MaximumStandardErrorBytes: 64 * 1024,
        MemoryPollIntervalMilliseconds: 25);

    public void Validate()
    {
        if (MaximumConcurrentProcesses is < 1 or > 32)
            throw new InvalidOperationException("MaximumConcurrentProcesses must be between 1 and 32.");
        if (MaximumWorkingSetBytes is < 64L * 1024 * 1024 or > 8L * 1024 * 1024 * 1024)
            throw new InvalidOperationException("MaximumWorkingSetBytes must be between 64 MiB and 8 GiB.");
        if (MaximumRequestBytes is < 64 * 1024 or > 16 * 1024 * 1024)
            throw new InvalidOperationException("MaximumRequestBytes must be between 64 KiB and 16 MiB.");
        if (MaximumResponseBytes is < 1024 * 1024 or > 256 * 1024 * 1024)
            throw new InvalidOperationException("MaximumResponseBytes must be between 1 MiB and 256 MiB.");
        if (MaximumStandardErrorBytes is < 1024 or > 1024 * 1024)
            throw new InvalidOperationException("MaximumStandardErrorBytes must be between 1 KiB and 1 MiB.");
        if (MemoryPollIntervalMilliseconds is < 10 or > 1000)
            throw new InvalidOperationException("MemoryPollIntervalMilliseconds must be between 10 and 1000.");
    }
}

public sealed record CompilerProcessCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? Environment = null);

public interface ICompilerProcessRunner
{
    Task<TResponse> RunAsync<TRequest, TResponse>(
        string childArgument,
        TRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        where TRequest : class
        where TResponse : class;
}

public sealed class CompilerProcessRunner : ICompilerProcessRunner, IDisposable
{
    private readonly CompilerProcessIsolationOptions _options;
    private readonly CompilerProcessCommand? _commandOverride;
    private readonly SemaphoreSlim _processSlots;

    public CompilerProcessRunner(
        CompilerProcessIsolationOptions options,
        CompilerProcessCommand? commandOverride = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _commandOverride = commandOverride;
        _processSlots = new SemaphoreSlim(options.MaximumConcurrentProcesses, options.MaximumConcurrentProcesses);
    }

    public async Task<TResponse> RunAsync<TRequest, TResponse>(
        string childArgument,
        TRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        where TRequest : class
        where TResponse : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childArgument);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (timeout <= TimeSpan.Zero)
            throw new CompilerProcessTimeoutException("The compiler process deadline elapsed.");
        if (!await _processSlots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new CompilerProcessCapacityExceededException();

        try
        {
            return await RunCoreAsync<TRequest, TResponse>(
                childArgument,
                request,
                timeout,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _processSlots.Release();
        }
    }

    public void Dispose() => _processSlots.Dispose();

    private async Task<TResponse> RunCoreAsync<TRequest, TResponse>(
        string childArgument,
        TRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        where TRequest : class
        where TResponse : class
    {
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        using var process = new Process
        {
            StartInfo = CreateStartInfo(childArgument),
            EnableRaisingEvents = true
        };
        try
        {
            if (!process.Start())
                throw new CompilerProcessCrashedException(null, "The compiler process could not be started.");
        }
        catch (CompilerProcessException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new CompilerProcessCrashedException(null, "The compiler process could not be started.", exception);
        }

        var stdoutTask = ReadBoundedAsync(
            process.StandardOutput.BaseStream,
            _options.MaximumResponseBytes,
            CancellationToken.None);
        var stderrTask = ReadBoundedTextAsync(
            process.StandardError.BaseStream,
            _options.MaximumStandardErrorBytes,
            CancellationToken.None);

        Exception? inputFailure = null;
        try
        {
            var requestBytes = CompilerChildProtocol.SerializeRequest(request);
            if (requestBytes.Length > _options.MaximumRequestBytes)
            {
                throw new CompilerProcessProtocolException(
                    "The compiler process request exceeded its configured byte limit.");
            }

            await process.StandardInput.BaseStream.WriteAsync(requestBytes, linked.Token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(linked.Token).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await KillAndDrainAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            await KillAndDrainAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            throw new CompilerProcessTimeoutException("The compiler process deadline elapsed.");
        }
        catch (CompilerProcessException)
        {
            await KillAndDrainAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            inputFailure = exception;
        }

        try
        {
            await WaitForExitAsync(process, stdoutTask, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await KillAndDrainAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            await KillAndDrainAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            throw new CompilerProcessTimeoutException("The compiler process deadline elapsed.");
        }
        catch (CompilerProcessException)
        {
            await KillAndDrainAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }

        var standardError = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            await ObserveAsync(stdoutTask).ConfigureAwait(false);
            throw new CompilerProcessCrashedException(
                process.ExitCode,
                "The compiler process exited unexpectedly.",
                inputFailure);
        }
        if (inputFailure is not null)
        {
            await ObserveAsync(stdoutTask).ConfigureAwait(false);
            throw new CompilerProcessProtocolException(
                "The compiler process stopped before accepting its request.",
                inputFailure);
        }

        byte[] responseBytes;
        try
        {
            responseBytes = await stdoutTask.ConfigureAwait(false);
        }
        catch (CompilerProcessProtocolException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new CompilerProcessProtocolException(
                "The compiler process response could not be read.",
                exception);
        }

        CompilerChildResponse<TResponse> response;
        try
        {
            response = CompilerChildProtocol.DeserializeResponse<TResponse>(responseBytes);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            throw new CompilerProcessProtocolException(
                "The compiler process returned an invalid response.",
                exception);
        }

        if (response.Failure is { } failure)
            throw new CompilerChildReportedException(failure.Kind, failure.PublicMessage);
        return response.Result
            ?? throw new CompilerProcessProtocolException(
                $"The compiler process returned an empty response. stderr bytes: {Encoding.UTF8.GetByteCount(standardError)}.");
    }

    private ProcessStartInfo CreateStartInfo(string childArgument)
    {
        var command = _commandOverride ?? CreateCurrentProcessCommand(childArgument);
        var startInfo = new ProcessStartInfo(command.FileName)
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = command.WorkingDirectory ?? AppContext.BaseDirectory
        };
        foreach (var argument in command.Arguments)
            startInfo.ArgumentList.Add(argument);
        foreach (var pair in command.Environment ?? new Dictionary<string, string?>())
        {
            if (pair.Value is null)
                startInfo.Environment.Remove(pair.Key);
            else
                startInfo.Environment[pair.Key] = pair.Value;
        }
        startInfo.Environment["DOTNET_GCHeapHardLimit"] =
            _options.MaximumWorkingSetBytes.ToString("x", CultureInfo.InvariantCulture);
        return startInfo;
    }

    private static CompilerProcessCommand CreateCurrentProcessCommand(string childArgument)
    {
        var processPath = Environment.ProcessPath
            ?? throw new CompilerProcessCrashedException(null, "The current worker executable path is unavailable.");
        var arguments = new List<string>();
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyPath = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(assemblyPath))
                throw new CompilerProcessCrashedException(null, "The current worker assembly path is unavailable.");
            arguments.Add(assemblyPath);
        }
        arguments.Add(childArgument);
        return new CompilerProcessCommand(processPath, arguments, AppContext.BaseDirectory);
    }

    private async Task WaitForExitAsync(
        Process process,
        Task<byte[]> stdoutTask,
        CancellationToken cancellationToken)
    {
        var exitTask = process.WaitForExitAsync(CancellationToken.None);
        while (!exitTask.IsCompleted)
        {
            if (stdoutTask.IsFaulted)
                await stdoutTask.ConfigureAwait(false);
            var delay = Task.Delay(_options.MemoryPollIntervalMilliseconds, cancellationToken);
            if (await Task.WhenAny(exitTask, delay).ConfigureAwait(false) == exitTask)
                break;
            await delay.ConfigureAwait(false);
            try
            {
                process.Refresh();
                if (process.HasExited)
                    continue;
                var workingSet = process.WorkingSet64;
                if (workingSet > _options.MaximumWorkingSetBytes)
                {
                    throw new CompilerProcessMemoryLimitExceededException(
                        _options.MaximumWorkingSetBytes,
                        workingSet);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }
        await exitTask.ConfigureAwait(false);
    }

    private static async Task KillAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
        }
    }

    private static async Task KillAndDrainAsync(
        Process process,
        Task<byte[]> stdoutTask,
        Task<string> stderrTask)
    {
        await KillAsync(process).ConfigureAwait(false);
        await ObserveAsync(stdoutTask).ConfigureAwait(false);
        await ObserveAsync(stderrTask).ConfigureAwait(false);
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return output.ToArray();
            if (output.Length + read > maximumBytes)
                throw new CompilerProcessProtocolException(
                    "The compiler process response exceeded its configured byte limit.");
            output.Write(buffer, 0, read);
        }
    }

    private static async Task<string> ReadBoundedTextAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maximumBytes, 16 * 1024));
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            var retained = (int)Math.Min(read, maximumBytes - output.Length);
            if (retained > 0)
                output.Write(buffer, 0, retained);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
}

public static class CompilerChildProtocol
{
    public const int SchemaVersion = 1;
    // Compiler children are SharpLabNext-owned interaction endpoints. Keep
    // their envelope in the same strict PascalCase shape as the host API.
    private static readonly JsonSerializerOptions JsonOptions = ContractJson.CreateSerializerOptions();

    public static byte[] SerializeRequest<TRequest>(TRequest request) where TRequest : class =>
        JsonSerializer.SerializeToUtf8Bytes(
            new CompilerChildRequest<TRequest>(SchemaVersion, request),
            JsonOptions);

    public static async Task<TRequest> ReadRequestAsync<TRequest>(
        Stream input,
        int maximumBytes,
        CancellationToken cancellationToken)
        where TRequest : class
    {
        var bytes = await ReadAllBoundedAsync(input, maximumBytes, cancellationToken).ConfigureAwait(false);
        var envelope = JsonSerializer.Deserialize<CompilerChildRequest<TRequest>>(bytes, JsonOptions)
            ?? throw new InvalidDataException("The compiler child request was empty.");
        if (envelope.SchemaVersion != SchemaVersion)
            throw new InvalidDataException("The compiler child request schema is unsupported.");
        return envelope.Request
            ?? throw new InvalidDataException("The compiler child request payload was empty.");
    }

    public static async Task WriteSuccessAsync<TResponse>(
        Stream output,
        TResponse response,
        int maximumBytes,
        CancellationToken cancellationToken)
        where TResponse : class =>
        await WriteResponseAsync(
            output,
            new CompilerChildResponse<TResponse>(SchemaVersion, response, null),
            maximumBytes,
            cancellationToken).ConfigureAwait(false);

    public static async Task WriteFailureAsync<TResponse>(
        Stream output,
        CompilerChildFailureKind kind,
        string publicMessage,
        int maximumBytes,
        CancellationToken cancellationToken)
        where TResponse : class =>
        await WriteResponseAsync(
            output,
            new CompilerChildResponse<TResponse>(
                SchemaVersion,
                null,
                new CompilerChildFailure(kind, publicMessage)),
            maximumBytes,
            cancellationToken).ConfigureAwait(false);

    public static CompilerChildResponse<TResponse> DeserializeResponse<TResponse>(byte[] bytes)
        where TResponse : class
    {
        var response = JsonSerializer.Deserialize<CompilerChildResponse<TResponse>>(bytes, JsonOptions)
            ?? throw new InvalidDataException("The compiler child response was empty.");
        if (response.SchemaVersion != SchemaVersion)
            throw new InvalidDataException("The compiler child response schema is unsupported.");
        if ((response.Result is null) == (response.Failure is null))
            throw new InvalidDataException("The compiler child response must contain exactly one outcome.");
        return response;
    }

    private static async Task WriteResponseAsync<TResponse>(
        Stream output,
        CompilerChildResponse<TResponse> response,
        int maximumBytes,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        if (bytes.Length > maximumBytes)
            throw new InvalidDataException("The compiler child response exceeded its byte limit.");
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadAllBoundedAsync(
        Stream input,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return output.ToArray();
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("The compiler child input exceeded its byte limit.");
            output.Write(buffer, 0, read);
        }
    }
}

public sealed record CompilerChildRequest<TRequest>(int SchemaVersion, TRequest Request)
    where TRequest : class;

public sealed record CompilerChildResponse<TResponse>(
    int SchemaVersion,
    TResponse? Result,
    CompilerChildFailure? Failure)
    where TResponse : class;

public sealed record CompilerChildFailure(
    CompilerChildFailureKind Kind,
    string PublicMessage);

public enum CompilerChildFailureKind
{
    InvalidRequest,
    ReferenceSetUnavailable,
    CompilerIdentityMismatch,
    OutputLimitExceeded,
    DeadlineExceeded,
    CompilerFailure,
    Internal
}

public abstract class CompilerProcessException : Exception
{
    protected CompilerProcessException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class CompilerProcessCapacityExceededException()
    : CompilerProcessException("The compiler process capacity is exhausted.");

public sealed class CompilerProcessTimeoutException(string message)
    : CompilerProcessException(message);

public sealed class CompilerProcessCrashedException(
    int? exitCode,
    string message,
    Exception? innerException = null) : CompilerProcessException(message, innerException)
{
    public int? ExitCode { get; } = exitCode;
}

public sealed class CompilerProcessMemoryLimitExceededException(
    long limitBytes,
    long observedBytes)
    : CompilerProcessException("The compiler process exceeded its memory limit.")
{
    public long LimitBytes { get; } = limitBytes;
    public long ObservedBytes { get; } = observedBytes;
}

public sealed class CompilerProcessProtocolException(
    string message,
    Exception? innerException = null) : CompilerProcessException(message, innerException);

public sealed class CompilerChildReportedException(
    CompilerChildFailureKind kind,
    string publicMessage) : CompilerProcessException(publicMessage)
{
    public CompilerChildFailureKind Kind { get; } = kind;
    public string PublicMessage { get; } = publicMessage;
}
