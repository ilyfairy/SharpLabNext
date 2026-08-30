using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SharpLabNext.ArtifactWorker.Sdk;
using SharpLabNext.Contracts;
using SharpLabNext.Worker.IL.Compiler;

namespace SharpLabNext.Worker.Artifacts.ILAssembler;

internal sealed record IlCompilerInvocationResult(bool Succeeded, byte[] PeImage, IReadOnlyList<IlCompilerDiagnostic> Diagnostics, string? FailureKind);

internal sealed record IlCompilerProcessHealth(bool IsHealthy, string Message);

internal sealed partial class IlCompilerProcessRunner(IlAssemblerWorkerSettings settings, ArtifactWorkerCapabilityManifest capabilityManifest, ILogger<IlCompilerProcessRunner> logger)
{
    private int _startedProcessCount;

    internal int StartedProcessCount => Volatile.Read(ref _startedProcessCount);

    public async Task<IlCompilerInvocationResult> AssembleAsync(ValidatedCilArtifact source, BuildOutputKind outputKind, CancellationToken cancellationToken)
    {
        string? jobRoot = null;
        try
        {
            Directory.CreateDirectory(settings.WorkRoot);
            jobRoot = Path.Combine(settings.WorkRoot, $"assemble-{Guid.NewGuid():N}");
            Directory.CreateDirectory(jobRoot);
            var requestPath = Path.Combine(jobRoot, "request.json");
            var responsePath = Path.Combine(jobRoot, "response.json");
            var outputPath = Path.Combine(jobRoot, "output.dll");
            var request = new IlCompilerRequest(IlCompilerProtocol.Version, outputKind == BuildOutputKind.Library ? "dll" : "exe", capabilityManifest.Limits.MaximumOutputArtifactBytes, [new IlCompilerSource(source.EntryPath, source.SourceText)]);
            var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, IlCompilerProtocol.JsonOptions);
            if (requestBytes.Length > IlCompilerProtocol.MaxRequestBytes)
                throw new ArtifactWorkerLimitExceededException("The IL compiler request exceeds its transfer limit.");
            await File.WriteAllBytesAsync(requestPath, requestBytes, cancellationToken).ConfigureAwait(false);

            var startInfo = CreateStartInfo(jobRoot);
            startInfo.ArgumentList.Add(settings.CompilerAssemblyPath);
            startInfo.ArgumentList.Add("--compile");
            startInfo.ArgumentList.Add(requestPath);
            startInfo.ArgumentList.Add(responsePath);
            startInfo.ArgumentList.Add(outputPath);
            var execution = await ExecuteAsync(startInfo, cancellationToken).ConfigureAwait(false);
            if (execution.OutputLimitExceeded)
                throw new ArtifactWorkerLimitExceededException("The isolated IL compiler exceeded its process output limit.");
            if (execution.ExitCode != 0)
                throw new ArtifactWorkerProcessorException("The isolated IL compiler exited unexpectedly.");

            var responseInfo = new FileInfo(responsePath);
            if (!responseInfo.Exists || responseInfo.Length is <= 0)
                throw new ArtifactWorkerProcessorException("The isolated IL compiler returned no response.");
            if (responseInfo.Length > settings.MaxCompilerResponseBytes)
                throw new ArtifactWorkerLimitExceededException("The isolated IL compiler response exceeded its size limit.");
            await using var responseStream = new FileStream(responsePath, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var response = await JsonSerializer.DeserializeAsync<IlCompilerResponse>(responseStream, IlCompilerProtocol.JsonOptions, cancellationToken).ConfigureAwait(false) ?? throw new ArtifactWorkerProcessorException("The isolated IL compiler response was empty.");
            if (response.ProtocolVersion != IlCompilerProtocol.Version || response.Diagnostics.Count > IlCompilerProtocol.MaxDiagnostics)
            {
                throw new ArtifactWorkerProcessorException("The isolated IL compiler response violated the approved protocol.");
            }
            if (!response.Succeeded)
                return new IlCompilerInvocationResult(false, [], response.Diagnostics, response.FailureKind);

            var outputInfo = new FileInfo(outputPath);
            if (!outputInfo.Exists || outputInfo.Length is <= 0)
                throw new ArtifactWorkerProcessorException("The isolated IL compiler did not produce a managed PE.");
            if (outputInfo.Length > capabilityManifest.Limits.MaximumOutputArtifactBytes)
                throw new ArtifactWorkerLimitExceededException("The assembled PE exceeds the configured output limit.");
            var peImage = await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
            return new IlCompilerInvocationResult(true, peImage, response.Diagnostics, null);
        }
        catch (JsonException exception)
        {
            throw new ArtifactWorkerProcessorException("The isolated IL compiler response was invalid JSON.", exception);
        }
        finally
        {
            if (jobRoot is not null)
                DeleteJobRoot(jobRoot);
        }
    }

    public async Task<IlCompilerProcessHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(settings.CompilerAssemblyPath))
            return new IlCompilerProcessHealth(false, "The isolated IL compiler assembly is missing.");
        try
        {
            Directory.CreateDirectory(settings.WorkRoot);
            var startInfo = CreateStartInfo(settings.WorkRoot);
            startInfo.ArgumentList.Add(settings.CompilerAssemblyPath);
            startInfo.ArgumentList.Add("--describe");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var execution = await ExecuteAsync(startInfo, timeout.Token).ConfigureAwait(false);
            if (execution.ExitCode != 0 || execution.OutputLimitExceeded)
                return new IlCompilerProcessHealth(false, "The isolated IL compiler preflight failed.");
            var descriptor = JsonSerializer.Deserialize<IlCompilerDescriptor>(execution.StandardOutput, IlCompilerProtocol.JsonOptions);
            var healthy = descriptor is not null &&
                descriptor.ProtocolVersion == IlCompilerProtocol.Version &&
                string.Equals(descriptor.Toolchain, "Mobius.ILasm", StringComparison.Ordinal) &&
                string.Equals(descriptor.PackageVersion, settings.CompilerVersion, StringComparison.Ordinal);
            return healthy
                ? new IlCompilerProcessHealth(true, $"Mobius.ILasm {descriptor!.PackageVersion} ({descriptor.AssemblyVersion}) is isolated and ready.") : new IlCompilerProcessHealth(false, "The isolated IL compiler identity is not approved.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new IlCompilerProcessHealth(false, "The isolated IL compiler is unavailable.");
        }
    }

    private ProcessStartInfo CreateStartInfo(string workingDirectory)
    {
        var startInfo = new ProcessStartInfo { FileName = settings.DotNetHostPath, WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        var inherited = startInfo.Environment.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
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
            if (inherited.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value))
                startInfo.Environment[name] = value;
        }
    }

    private async Task<ProcessExecution> ExecuteAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new ArtifactWorkerProcessorException("The isolated IL compiler could not be started.");
            Interlocked.Increment(ref _startedProcessCount);
        }
        catch (Exception exception) when (exception is not ArtifactWorkerProcessorException)
        {
            throw new ArtifactWorkerProcessorException("The isolated IL compiler could not be started.", exception);
        }

        var outputExceeded = 0;
        void MarkOutputExceeded()
        {
            Interlocked.Exchange(ref outputExceeded, 1);
            Kill(process);
        }
        var stdoutTask = CaptureAsync(process.StandardOutput.BaseStream, settings.MaxProcessOutputBytes, MarkOutputExceeded);
        var stderrTask = CaptureAsync(process.StandardError.BaseStream, settings.MaxProcessOutputBytes, MarkOutputExceeded);
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
                    if (process.WorkingSet64 > settings.MaxProcessWorkingSetBytes)
                    {
                        Kill(process);
                        throw new ArtifactWorkerLimitExceededException("The isolated IL compiler exceeded its memory limit.");
                    }
                }
                catch (InvalidOperationException) when (process.HasExited) { }
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
        _ = await stderrTask.ConfigureAwait(false);
        return new ProcessExecution(process.ExitCode, stdout, Volatile.Read(ref outputExceeded) != 0);
    }

    private static async Task<string> CaptureAsync(Stream stream, int maximumBytes, Action limitExceeded)
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
            TemporaryDirectoryCleanupFailed(logger, path);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
    }

    [LoggerMessage(EventId = 4300, Level = LogLevel.Warning, Message = "Could not delete IL assembler temporary directory {Path}.")]
    private static partial void TemporaryDirectoryCleanupFailed(ILogger logger, string path);

    private sealed record ProcessExecution(int ExitCode, string StandardOutput, bool OutputLimitExceeded);
}
