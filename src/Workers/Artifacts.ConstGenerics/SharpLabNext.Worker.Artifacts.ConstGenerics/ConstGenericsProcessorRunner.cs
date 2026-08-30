using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using SharpLabNext.ArtifactWorker.Sdk;
using SharpLabNext.Worker.Artifacts.ConstGenerics.Protocol;

namespace SharpLabNext.Worker.Artifacts.ConstGenerics;

internal sealed record ConstGenericsProcessorRunResult(ConstGenericsProcessorResponse Response, string OutputPath);

internal sealed record ConstGenericsProcessorHealth(bool IsHealthy, string Message);

internal sealed class ConstGenericsProcessorRunner(ConstGenericsArtifactWorkerSettings settings, ArtifactWorkerCapabilityManifest capabilityManifest)
{
    private static readonly string[] ExpectedOperations = ["decompiled-csharp", "il", "verify"];
    private int _startedProcessCount;

    internal int StartedProcessCount => Volatile.Read(ref _startedProcessCount);

    public async Task<ConstGenericsProcessorRunResult> RunAsync(MaterializedConstGenericsArtifact artifact, ConstGenericsProcessorOperation operation, bool includeSequencePoints, bool includeCompilerGeneratedMembers, bool includeMetadataTokens, int maxCharacters, int maxFindings, CancellationToken cancellationToken)
    {
        var requestPath = ConstGenericsTemporaryDirectory.ResolvePath(artifact.RootPath, "processor-request.json");
        var responsePath = ConstGenericsTemporaryDirectory.ResolvePath(artifact.RootPath, "processor-response.json");
        var outputPath = ConstGenericsTemporaryDirectory.ResolvePath(artifact.RootPath, "processor-output.txt");
        var request = new ConstGenericsProcessorRequest(
            ConstGenericsProcessorProtocol.Version,
            operation,
            artifact.AssemblyPath,
            artifact.PortablePdbPath,
            outputPath,
            new[] { settings.ReferenceRoot, settings.RuntimeReferenceRoot }
                .Distinct(StringComparer.Ordinal).ToArray(),
            settings.SystemModuleName,
            includeSequencePoints,
            includeCompilerGeneratedMembers,
            includeMetadataTokens,
            Math.Min(maxCharacters, capabilityManifest.Limits.MaximumOutputArtifactBytes),
            Math.Min(maxFindings, ConstGenericsProcessorProtocol.MaximumFindings));
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, ConstGenericsProcessorProtocol.JsonOptions);
        if (requestBytes.Length > ConstGenericsProcessorProtocol.MaximumRequestBytes)
            throw new ArtifactWorkerLimitExceededException("The isolated processor request exceeded its limit.");
        await File.WriteAllBytesAsync(requestPath, requestBytes, cancellationToken).ConfigureAwait(false);

        var startInfo = CreateStartInfo(artifact.RootPath);
        startInfo.ArgumentList.Add(settings.ProcessorAssemblyPath);
        startInfo.ArgumentList.Add("--request");
        startInfo.ArgumentList.Add(requestPath);
        startInfo.ArgumentList.Add("--response");
        startInfo.ArgumentList.Add(responsePath);
        var execution = await ExecuteAsync(startInfo, cancellationToken).ConfigureAwait(false);
        if (execution.OutputLimitExceeded)
            throw new ArtifactWorkerLimitExceededException("The isolated processor exceeded its log limit.");
        if (execution.ExitCode != 0 && !File.Exists(responsePath))
            throw new ArtifactWorkerProcessorException("The isolated processor exited unexpectedly.");

        var responseInfo = new FileInfo(responsePath);
        if (!responseInfo.Exists || responseInfo.Length is <= 0)
            throw new ArtifactWorkerProcessorException("The isolated processor returned no response.");
        if (responseInfo.Length > ConstGenericsProcessorProtocol.MaximumResponseBytes)
            throw new ArtifactWorkerLimitExceededException("The isolated processor response exceeded its limit.");
        ConstGenericsProcessorResponse response;
        try
        {
            await using var responseStream = new FileStream(responsePath, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            response = await JsonSerializer.DeserializeAsync<ConstGenericsProcessorResponse>(responseStream, ConstGenericsProcessorProtocol.JsonOptions, cancellationToken).ConfigureAwait(false) ?? throw new ArtifactWorkerProcessorException("The isolated processor response was empty.");
        }
        catch (JsonException exception)
        {
            throw new ArtifactWorkerProcessorException("The isolated processor response was invalid.", exception);
        }

        ValidateResponse(response, operation);
        if (response.LinkedRanges.Count > ConstGenericsProcessorProtocol.MaximumLinkedRanges || response.Findings.Count > ConstGenericsProcessorProtocol.MaximumFindings)
        {
            throw new ArtifactWorkerLimitExceededException("The isolated processor returned too many items.");
        }
        if (File.Exists(outputPath) && new FileInfo(outputPath).Length > capabilityManifest.Limits.MaximumOutputArtifactBytes)
        {
            throw new ArtifactWorkerLimitExceededException("The isolated processor output exceeded its byte limit.");
        }
        return new ConstGenericsProcessorRunResult(response, outputPath);
    }

    public async Task<ConstGenericsProcessorHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(settings.ProcessorAssemblyPath))
            return new ConstGenericsProcessorHealth(false, "The isolated ConstGenerics processor is missing.");
        try
        {
            Directory.CreateDirectory(settings.WorkRoot);
            var startInfo = CreateStartInfo(settings.WorkRoot);
            startInfo.ArgumentList.Add(settings.ProcessorAssemblyPath);
            startInfo.ArgumentList.Add("--describe");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var execution = await ExecuteAsync(startInfo, timeout.Token).ConfigureAwait(false);
            if (execution.ExitCode != 0 || execution.OutputLimitExceeded)
                return new ConstGenericsProcessorHealth(false, "The isolated ConstGenerics processor preflight failed.");
            var descriptor = JsonSerializer.Deserialize<ConstGenericsProcessorDescriptor>(execution.StandardOutput, ConstGenericsProcessorProtocol.JsonOptions);
            var healthy = descriptor is not null &&
                descriptor.ProtocolVersion == ConstGenericsProcessorProtocol.Version &&
                string.Equals(descriptor.IlSpyCommit, ConstGenericsProcessorProtocol.IlSpyCommit, StringComparison.Ordinal) &&
                string.Equals(descriptor.RuntimeCommit, ConstGenericsProcessorProtocol.RuntimeCommit, StringComparison.Ordinal) &&
                string.Equals(descriptor.MetadataFeatureTag, ConstGenericsProcessorProtocol.MetadataFeatureTag, StringComparison.Ordinal) &&
                string.Equals(descriptor.CompatibilityGroup, ConstGenericsProcessorProtocol.CompatibilityGroup, StringComparison.Ordinal) &&
                descriptor.Operations.Order(StringComparer.Ordinal).SequenceEqual(ExpectedOperations, StringComparer.Ordinal);
            return healthy
                ? new ConstGenericsProcessorHealth(true, $"ILSpy {descriptor!.IlSpyCommit[..12]} and matching metadata verifier are isolated and ready.") : new ConstGenericsProcessorHealth(false, "The isolated ConstGenerics processor identity is not approved.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new ConstGenericsProcessorHealth(false, "The isolated ConstGenerics processor is unavailable.");
        }
    }

    private ProcessStartInfo CreateStartInfo(string workingDirectory)
    {
        var startInfo = new ProcessStartInfo { FileName = ResolveExecutable(settings.ProcessorDotNetHostPath), WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = false, CreateNoWindow = true };
        var inherited = startInfo.Environment.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        startInfo.Environment.Clear();
        Copy("PATH");
        Copy("SystemRoot");
        Copy("WINDIR");
        var hostDirectory = Path.GetDirectoryName(startInfo.FileName);
        if (!string.IsNullOrWhiteSpace(hostDirectory))
            startInfo.Environment["DOTNET_ROOT"] = hostDirectory;
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_EnableDiagnostics"] = "0";
        startInfo.Environment["COMPlus_EnableDiagnostics"] = "0";
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        startInfo.Environment["DOTNET_GCHeapHardLimit"] =
            settings.MaximumProcessorWorkingSetBytes.ToString("x", CultureInfo.InvariantCulture);
        startInfo.Environment["LANG"] = "C.UTF-8";
        startInfo.Environment["LC_ALL"] = "C.UTF-8";
        startInfo.Environment["HOME"] = workingDirectory;
        startInfo.Environment["TMP"] = workingDirectory;
        startInfo.Environment["TEMP"] = workingDirectory;
        startInfo.Environment["TMPDIR"] = workingDirectory;
        return startInfo;

        void Copy(string name)
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
                throw new ArtifactWorkerProcessorException("The isolated processor could not be started.");
            Interlocked.Increment(ref _startedProcessCount);
        }
        catch (Exception exception) when (exception is not ArtifactWorkerProcessorException)
        {
            throw new ArtifactWorkerProcessorException("The isolated processor could not be started.", exception);
        }

        var outputExceeded = 0;
        void MarkOutputExceeded()
        {
            Interlocked.Exchange(ref outputExceeded, 1);
            Kill(process);
        }
        var stdoutTask = CaptureAsync(process.StandardOutput.BaseStream, settings.MaximumProcessOutputBytes, MarkOutputExceeded);
        var stderrTask = CaptureAsync(process.StandardError.BaseStream, settings.MaximumProcessOutputBytes, MarkOutputExceeded);
        var exitTask = process.WaitForExitAsync(CancellationToken.None);
        try
        {
            while (!exitTask.IsCompleted)
            {
                await Task.WhenAny(exitTask, Task.Delay(50, cancellationToken)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (exitTask.IsCompleted)
                    break;
                if (TryGetWorkingSet(process, out var workingSet) && workingSet > settings.MaximumProcessorWorkingSetBytes)
                {
                    Kill(process);
                    throw new ArtifactWorkerLimitExceededException("The isolated processor exceeded its memory limit.");
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
        _ = await stderrTask.ConfigureAwait(false);
        return new ProcessExecution(process.ExitCode, stdout, Volatile.Read(ref outputExceeded) != 0);
    }

    private static bool TryGetWorkingSet(Process process, out long workingSet)
    {
        try
        {
            process.Refresh();
            workingSet = process.WorkingSet64;
            return true;
        }
        catch (InvalidOperationException)
        {
            // A short-lived child can exit between the exit-task check and this sample.
            workingSet = 0;
            return false;
        }
    }

    private static void ValidateResponse(ConstGenericsProcessorResponse response, ConstGenericsProcessorOperation operation)
    {
        if (response.ProtocolVersion != ConstGenericsProcessorProtocol.Version)
            throw new ArtifactWorkerProcessorException("The isolated processor protocol identity is invalid.");
        var expectedId = operation switch
        {
            ConstGenericsProcessorOperation.Il => "ilspy-const-generics-il",
            ConstGenericsProcessorOperation.DecompiledCSharp => "ilspy-const-generics-csharp",
            _ => "ilverification-const-generics"
        };
        var expectedVersion = operation == ConstGenericsProcessorOperation.Verify
            ? ConstGenericsProcessorProtocol.VerificationProcessorVersion : ConstGenericsProcessorProtocol.IlSpyProcessorVersion;
        if (!string.Equals(response.ProcessorId, expectedId, StringComparison.Ordinal) || !string.Equals(response.ProcessorVersion, expectedVersion, StringComparison.Ordinal))
        {
            throw new ArtifactWorkerProcessorException("The isolated processor component identity is invalid.");
        }
    }

    private static string ResolveExecutable(string configuredPath)
    {
        if (Path.IsPathFullyQualified(configuredPath))
            return Path.GetFullPath(configuredPath);
        var currentProcess = Environment.ProcessPath;
        if (string.Equals(configuredPath, "dotnet", StringComparison.OrdinalIgnoreCase) && currentProcess is not null && string.Equals(Path.GetFileNameWithoutExtension(currentProcess), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return currentProcess;
        }
        var executable = OperatingSystem.IsWindows() && Path.GetExtension(configuredPath).Length == 0
            ? configuredPath + ".exe" : configuredPath;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }
        throw new ArtifactWorkerProcessorException("The configured processor .NET host is unavailable.");
    }

    private static async Task<string> CaptureAsync(Stream stream, int maximumBytes, Action limitExceeded)
    {
        using var result = new MemoryStream(Math.Min(maximumBytes, 16 * 1024));
        var buffer = new byte[4 * 1024];
        var observed = 0;
        var marked = false;
        while (true)
        {
            var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
                break;
            observed = checked(observed + read);
            if (observed > maximumBytes && !marked)
            {
                marked = true;
                limitExceeded();
            }
            if (result.Length < maximumBytes)
                result.Write(buffer, 0, Math.Min(read, maximumBytes - checked((int)result.Length)));
        }
        return Encoding.UTF8.GetString(result.GetBuffer(), 0, checked((int)result.Length));
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

    private sealed record ProcessExecution(int ExitCode, string StandardOutput, bool OutputLimitExceeded);
}
