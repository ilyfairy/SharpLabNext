using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SharpLabNext.Contracts;
using SharpLabNext.Worker.IL.Compiler;

namespace SharpLabNext.Worker.IL;

public sealed record IlAssemblerInvocationResult(
    bool Succeeded,
    byte[] PeImage,
    IReadOnlyList<IlCompilerDiagnostic> Diagnostics,
    string? FailureKind);

public sealed record IlAssemblerHealth(bool IsHealthy, string Message);

public sealed class IlAssemblerProcess : IDisposable
{
    private readonly IlWorkerSettings _settings;
    private readonly ILogger<IlAssemblerProcess> _logger;
    private readonly SemaphoreSlim _concurrency;
    private int _startedProcessCount;

    public IlAssemblerProcess(IlWorkerSettings settings, ILogger<IlAssemblerProcess> logger)
    {
        _settings = settings;
        _logger = logger;
        _concurrency = new SemaphoreSlim(
            settings.CompilationLimits.MaxConcurrentBuilds,
            settings.CompilationLimits.MaxConcurrentBuilds);
    }

    internal async Task<IlAssemblerInvocationResult> AssembleAsync(
        ValidatedIlWorkspace workspace,
        CancellationToken cancellationToken)
    {
        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? jobRoot = null;
        try
        {
            Directory.CreateDirectory(_settings.WorkRoot);
            jobRoot = Path.Combine(_settings.WorkRoot, $"build-{Guid.NewGuid():N}");
            Directory.CreateDirectory(jobRoot);
            var requestPath = Path.Combine(jobRoot, "request.json");
            var responsePath = Path.Combine(jobRoot, "response.json");
            var outputPath = Path.Combine(jobRoot, "output.dll");
            var request = new IlCompilerRequest(
                IlCompilerProtocol.Version,
                workspace.Options.OutputKind == BuildOutputKind.Library ? "dll" : "exe",
                _settings.CompilationLimits.MaxPeBytes,
                workspace.OrderedFiles.Select(static file => new IlCompilerSource(file.Path, file.Text)).ToArray());
            var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, IlCompilerProtocol.JsonOptions);
            if (requestBytes.Length > _settings.CompilationLimits.MaxTotalSourceUtf8Bytes + 256 * 1024)
                throw new IlBuildOutputLimitExceededException("The compiler request exceeds the configured transfer limit.");
            await File.WriteAllBytesAsync(requestPath, requestBytes, cancellationToken).ConfigureAwait(false);

            var startInfo = CreateStartInfo(jobRoot);
            startInfo.ArgumentList.Add(_settings.CompilerAssemblyPath);
            startInfo.ArgumentList.Add("--compile");
            startInfo.ArgumentList.Add(requestPath);
            startInfo.ArgumentList.Add(responsePath);
            startInfo.ArgumentList.Add(outputPath);
            var execution = await ExecuteAsync(startInfo, cancellationToken).ConfigureAwait(false);
            if (execution.OutputLimitExceeded)
                throw new IlBuildOutputLimitExceededException("The isolated IL compiler exceeded its output limit.");
            if (execution.ExitCode != 0)
            {
                throw new IlAssemblerUnavailableException(
                    $"The isolated IL compiler exited with code {execution.ExitCode}. {PublicOutput(execution.StandardError)}");
            }

            var responseInfo = new FileInfo(responsePath);
            if (!responseInfo.Exists || responseInfo.Length is <= 0)
                throw new IlAssemblerUnavailableException("The isolated IL compiler returned an invalid response file.");
            if (responseInfo.Length > _settings.CompilationLimits.MaxCompilerResponseBytes)
                throw new IlBuildOutputLimitExceededException("The isolated IL compiler response exceeded its size limit.");
            await using var responseStream = new FileStream(
                responsePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var response = await JsonSerializer.DeserializeAsync<IlCompilerResponse>(
                responseStream,
                IlCompilerProtocol.JsonOptions,
                cancellationToken).ConfigureAwait(false)
                ?? throw new IlAssemblerUnavailableException("The isolated IL compiler response was empty.");
            if (response.ProtocolVersion != IlCompilerProtocol.Version ||
                response.Diagnostics.Count > IlCompilerProtocol.MaxDiagnostics)
            {
                throw new IlAssemblerUnavailableException("The isolated IL compiler response violated the worker protocol.");
            }

            if (!response.Succeeded)
                return new IlAssemblerInvocationResult(false, [], response.Diagnostics, response.FailureKind);
            var outputInfo = new FileInfo(outputPath);
            if (!outputInfo.Exists || outputInfo.Length is <= 0)
                throw new IlAssemblerUnavailableException("The isolated IL compiler did not produce a bounded PE image.");
            if (outputInfo.Length > _settings.CompilationLimits.MaxPeBytes)
                throw new IlBuildOutputLimitExceededException("The assembled PE exceeded its size limit.");
            var peImage = await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
            return new IlAssemblerInvocationResult(true, peImage, response.Diagnostics, null);
        }
        catch (JsonException exception)
        {
            throw new IlAssemblerUnavailableException("The isolated IL compiler response was invalid JSON.", exception);
        }
        finally
        {
            if (jobRoot is not null)
                DeleteJobRoot(jobRoot);
            _concurrency.Release();
        }
    }

    public async Task<IlAssemblerHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settings.CompilerAssemblyPath))
            return new IlAssemblerHealth(false, "The isolated IL compiler assembly is missing.");
        try
        {
            Directory.CreateDirectory(_settings.WorkRoot);
            var startInfo = CreateStartInfo(_settings.WorkRoot);
            startInfo.ArgumentList.Add(_settings.CompilerAssemblyPath);
            startInfo.ArgumentList.Add("--describe");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var execution = await ExecuteAsync(startInfo, timeout.Token).ConfigureAwait(false);
            if (execution.ExitCode != 0 || execution.OutputLimitExceeded)
                return new IlAssemblerHealth(false, "The isolated IL compiler preflight failed.");
            var descriptor = JsonSerializer.Deserialize<IlCompilerDescriptor>(
                execution.StandardOutput,
                IlCompilerProtocol.JsonOptions);
            var healthy = descriptor is not null &&
                descriptor.ProtocolVersion == IlCompilerProtocol.Version &&
                StringComparer.Ordinal.Equals(descriptor.Toolchain, "Mobius.ILasm") &&
                StringComparer.Ordinal.Equals(descriptor.PackageVersion, _settings.Identity.CompilerVersion);
            return healthy
                ? new IlAssemblerHealth(true, $"Mobius.ILasm {descriptor!.PackageVersion} ({descriptor.AssemblyVersion}) is isolated and ready.")
                : new IlAssemblerHealth(false, "The isolated IL compiler identity is not approved.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new IlAssemblerHealth(false, $"The isolated IL compiler is unavailable: {PublicOutput(exception.Message)}");
        }
    }

    public void Dispose() => _concurrency.Dispose();

    internal int StartedProcessCount => Volatile.Read(ref _startedProcessCount);

    private ProcessStartInfo CreateStartInfo(string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _settings.DotNetHostPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var inheritedEnvironment = startInfo.Environment.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        startInfo.Environment.Clear();
        CopyEnvironment("PATH");
        CopyEnvironment("SystemRoot");
        CopyEnvironment("WINDIR");
        CopyEnvironment("DOTNET_ROOT");
        CopyEnvironment("DOTNET_ROOT_X64");
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_EnableDiagnostics"] = "0";
        startInfo.Environment["COMPlus_EnableDiagnostics"] = "0";
        startInfo.Environment["LANG"] = "C.UTF-8";
        startInfo.Environment["LC_ALL"] = "C.UTF-8";
        startInfo.Environment["HOME"] = workingDirectory;
        startInfo.Environment["TMP"] = workingDirectory;
        startInfo.Environment["TEMP"] = workingDirectory;
        startInfo.Environment["TMPDIR"] = workingDirectory;
        return startInfo;

        void CopyEnvironment(string name)
        {
            if (inheritedEnvironment.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value))
                startInfo.Environment[name] = value;
        }
    }

    private async Task<ProcessExecution> ExecuteAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new IlAssemblerUnavailableException("The isolated IL compiler could not be started.");
            Interlocked.Increment(ref _startedProcessCount);
        }
        catch (Exception exception) when (exception is not IlAssemblerUnavailableException)
        {
            throw new IlAssemblerUnavailableException("The isolated IL compiler could not be started.", exception);
        }

        var outputExceeded = 0;
        void MarkOutputExceeded()
        {
            Interlocked.Exchange(ref outputExceeded, 1);
            Kill(process);
        }
        var stdoutTask = CaptureAsync(
            process.StandardOutput.BaseStream,
            _settings.CompilationLimits.MaxProcessOutputBytes,
            MarkOutputExceeded);
        var stderrTask = CaptureAsync(
            process.StandardError.BaseStream,
            _settings.CompilationLimits.MaxProcessOutputBytes,
            MarkOutputExceeded);
        var exitTask = process.WaitForExitAsync(CancellationToken.None);
        try
        {
            while (!exitTask.IsCompleted)
            {
                await Task.WhenAny(exitTask, Task.Delay(50, cancellationToken)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (exitTask.IsCompleted)
                    break;
                try
                {
                    process.Refresh();
                    if (process.WorkingSet64 > _settings.CompilationLimits.MaxProcessWorkingSetBytes)
                    {
                        Kill(process);
                        throw new IlBuildOutputLimitExceededException("The isolated IL compiler exceeded its memory limit.");
                    }
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                }
            }
            await exitTask.ConfigureAwait(false);
        }
        catch
        {
            Kill(process);
            await exitTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new ProcessExecution(
            process.ExitCode,
            stdout,
            stderr,
            Volatile.Read(ref outputExceeded) != 0);
    }

    private static async Task<string> CaptureAsync(
        Stream stream,
        int maximumBytes,
        Action limitExceeded)
    {
        using var result = new MemoryStream(Math.Min(maximumBytes, 16 * 1024));
        var buffer = new byte[4 * 1024];
        var observedBytes = 0;
        var marked = false;
        while (true)
        {
            var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
                break;
            observedBytes = checked(observedBytes + read);
            if (observedBytes > maximumBytes && !marked)
            {
                marked = true;
                limitExceeded();
            }
            if (result.Length < maximumBytes)
                result.Write(buffer, 0, Math.Min(read, maximumBytes - checked((int)result.Length)));
        }
        return Encoding.UTF8.GetString(result.GetBuffer(), 0, checked((int)result.Length));
    }

    private void DeleteJobRoot(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            IlWorkerLog.TemporaryDirectoryCleanupFailed(_logger, exception, path);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string PublicOutput(string value)
    {
        var compact = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length <= 512 ? compact : compact[..512];
    }

    private sealed record ProcessExecution(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        bool OutputLimitExceeded);
}
