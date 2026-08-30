using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using SharpLabNext.ArtifactProcessing.Protocol;

namespace SharpLabNext.ArtifactWorker;

internal sealed record ProcessorRunResult(ProcessorResponse Response, string OutputPath, string? PortablePdbOutputPath = null);

internal interface IArtifactProcessorRunner
{
    Task<ProcessorRunResult> RunAsync(MaterializedArtifact artifact, ProcessorOperation operation, bool includeSequencePoints, bool includeCompilerGeneratedMembers, bool includeMetadataTokens, int maxCharacters, int maxFindings, DateTimeOffset deadlineUtc, CancellationToken cancellationToken, string? rewriterProfileId = null);
}

internal sealed class ArtifactProcessorProcessRunner(ArtifactWorkerSettings settings) : IArtifactProcessorRunner
{
    private const int MaximumLogCharacters = 64 * 1024;

    public async Task<ProcessorRunResult> RunAsync(MaterializedArtifact artifact, ProcessorOperation operation, bool includeSequencePoints, bool includeCompilerGeneratedMembers, bool includeMetadataTokens, int maxCharacters, int maxFindings, DateTimeOffset deadlineUtc, CancellationToken cancellationToken, string? rewriterProfileId = null)
    {
        var remaining = deadlineUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            return LimitResult(operation, "The artifact processing deadline elapsed before execution.");
        var timeout = TimeSpan.FromMilliseconds(Math.Min(settings.Limits.MaxProcessorMilliseconds, Math.Max(1, remaining.TotalMilliseconds)));

        var requestPath = TemporaryArtifactDirectory.ResolvePath(artifact.RootPath, "processor-request.json");
        var responsePath = TemporaryArtifactDirectory.ResolvePath(artifact.RootPath, "processor-response.json");
        var isTransform = operation == ProcessorOperation.RuntimeInstrumentationV1;
        var outputPath = TemporaryArtifactDirectory.ResolvePath(artifact.RootPath, isTransform ? "processor-output.dll" : "processor-output.txt");
        var portablePdbOutputPath = isTransform && artifact.PortablePdbPath is not null
            ? TemporaryArtifactDirectory.ResolvePath(artifact.RootPath, "processor-output.pdb") : null;
        var request = new ProcessorRequest(
            ProcessorProtocol.Version,
            operation,
            artifact.AssemblyPath,
            artifact.PortablePdbPath,
            outputPath,
            artifact.ReferenceSet?.Paths ?? [],
            artifact.ReferenceSet?.SystemModuleName,
            includeSequencePoints,
            includeCompilerGeneratedMembers,
            includeMetadataTokens,
            maxCharacters,
            maxFindings,
            rewriterProfileId,
            portablePdbOutputPath,
            artifact.Manifest?.ArtifactFormat ?? ArtifactFormatContract.ManagedPe);
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, ProcessorProtocol.JsonOptions), new UTF8Encoding(false), cancellationToken);

        var startInfo = CreateStartInfo(requestPath, responsePath, artifact.RootPath);
        using var process = Process.Start(startInfo) ?? throw new ArtifactProcessorCrashedException("The artifact processor could not be started.");
        var stdout = ReadBoundedAsync(process.StandardOutput, MaximumLogCharacters);
        var stderr = ReadBoundedAsync(process.StandardError, MaximumLogCharacters);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        var memoryExceeded = false;
        try
        {
            var exit = process.WaitForExitAsync(CancellationToken.None);
            while (!exit.IsCompleted)
            {
                await Task.Delay(50, linked.Token);
                try
                {
                    process.Refresh();
                    if (!process.HasExited && process.WorkingSet64 > settings.Limits.MaxProcessorMemoryBytes)
                    {
                        memoryExceeded = true;
                        Kill(process);
                        break;
                    }
                }
                catch (InvalidOperationException)
                {
                    break;
                }
            }
            await exit;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            _ = await stdout;
            _ = await stderr;
            return LimitResult(operation, "The artifact processor exceeded its time limit.", outputPath);
        }

        _ = await stdout;
        _ = await stderr;
        if (memoryExceeded)
            return LimitResult(operation, "The artifact processor exceeded its memory limit.", outputPath);
        if (!File.Exists(responsePath))
        {
            return InvalidArtifactResult(operation, "The artifact processor terminated without a valid response.", outputPath);
        }

        var responseInfo = new FileInfo(responsePath);
        if (responseInfo.Length <= 0 || responseInfo.Length > settings.Limits.MaxProcessorResponseBytes)
        {
            return LimitResult(operation, "The artifact processor response exceeded its limit.", outputPath);
        }
        await using var responseStream = new FileStream(responsePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        ProcessorResponse? response;
        try
        {
            response = await JsonSerializer.DeserializeAsync<ProcessorResponse>(responseStream, ProcessorProtocol.JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            return InvalidArtifactResult(operation, "The artifact processor response was invalid.", outputPath);
        }
        if (response is null || response.ProtocolVersion != ProcessorProtocol.Version)
            return InvalidArtifactResult(operation, "The artifact processor response was invalid.", outputPath);
        if (response.LinkedRanges.Count > settings.Limits.MaxLinkedRanges || response.Findings.Count > settings.Limits.MaxFindings)
        {
            return LimitResult(operation, "The artifact processor response exceeded its item limit.", outputPath);
        }
        var maximumOutputBytes = isTransform
            ? settings.Limits.MaxAssemblyBytes : settings.Limits.MaxOutputBytes;
        if (File.Exists(outputPath) && new FileInfo(outputPath).Length > maximumOutputBytes)
            return LimitResult(operation, "The artifact processor output exceeded its byte limit.", outputPath);
        if (portablePdbOutputPath is not null && File.Exists(portablePdbOutputPath) && new FileInfo(portablePdbOutputPath).Length > settings.Limits.MaxPortablePdbBytes)
        {
            return LimitResult(operation, "The rewritten portable PDB exceeded its byte limit.", outputPath);
        }
        return new ProcessorRunResult(response, outputPath, portablePdbOutputPath);
    }

    private ProcessStartInfo CreateStartInfo(string requestPath, string responsePath, string workRoot)
    {
        var dotnetHost = ResolveExecutable(settings.DotNetHostPath);
        if (!File.Exists(settings.ProcessorAssemblyPath))
            throw new ArtifactProcessorCrashedException("The artifact processor executable is unavailable.");
        var startInfo = new ProcessStartInfo { FileName = dotnetHost, WorkingDirectory = workRoot, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = false, CreateNoWindow = true };
        startInfo.ArgumentList.Add(settings.ProcessorAssemblyPath);
        startInfo.ArgumentList.Add("--request");
        startInfo.ArgumentList.Add(requestPath);
        startInfo.ArgumentList.Add("--response");
        startInfo.ArgumentList.Add(responsePath);

        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        startInfo.Environment.Clear();
        if (!string.IsNullOrWhiteSpace(systemRoot))
        {
            startInfo.Environment["SystemRoot"] = systemRoot;
            startInfo.Environment["WINDIR"] = systemRoot;
        }
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_EnableDiagnostics"] = "0";
        startInfo.Environment["COMPlus_EnableDiagnostics"] = "0";
        startInfo.Environment["DOTNET_GCHeapHardLimit"] =
            settings.Limits.MaxProcessorMemoryBytes.ToString("x", CultureInfo.InvariantCulture);
        startInfo.Environment["TEMP"] = workRoot;
        startInfo.Environment["TMP"] = workRoot;
        startInfo.Environment["TMPDIR"] = workRoot;
        var hostDirectory = Path.GetDirectoryName(dotnetHost);
        if (!string.IsNullOrWhiteSpace(hostDirectory))
            startInfo.Environment["DOTNET_ROOT"] = hostDirectory;
        return startInfo;
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

        var executableName = OperatingSystem.IsWindows() && Path.GetExtension(configuredPath).Length == 0
            ? configuredPath + ".exe" : configuredPath;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, executableName);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }
        throw new ArtifactProcessorCrashedException("The configured .NET host is unavailable.");
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximumCharacters)
    {
        var result = new StringBuilder(Math.Min(4_096, maximumCharacters));
        var buffer = new char[4_096];
        while (await reader.ReadAsync(buffer) is var read && read > 0)
        {
            var remaining = maximumCharacters - result.Length;
            if (remaining > 0)
                result.Append(buffer, 0, Math.Min(read, remaining));
        }
        return result.ToString();
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

    private static ProcessorRunResult LimitResult(ProcessorOperation operation, string message, string outputPath = "") =>
        new(Response(operation, ProcessorOutcome.LimitExceeded, message), outputPath);

    private static ProcessorRunResult InvalidArtifactResult(ProcessorOperation operation, string message, string outputPath) =>
        new(Response(operation, ProcessorOutcome.InvalidArtifact, message), outputPath);

    private static ProcessorResponse Response(ProcessorOperation operation, ProcessorOutcome outcome, string message) => new(
            ProcessorProtocol.Version,
            outcome,
            operation switch
            {
                ProcessorOperation.Verify => "microsoft-ilverification",
                ProcessorOperation.RuntimeInstrumentationV1 => "runtime-instrumentation-v1",
                _ => "icsharpcode-decompiler"
            },
            operation switch
            {
                ProcessorOperation.Verify => ProcessorProtocol.IlVerificationVersion,
                ProcessorOperation.RuntimeInstrumentationV1 => ProcessorProtocol.RuntimeInstrumentationVersion,
                _ => ProcessorProtocol.IlSpyVersion
            },
            operation switch
            {
                ProcessorOperation.Il => "text/x-il",
                ProcessorOperation.DecompiledCSharp => "text/x-csharp",
                ProcessorOperation.RuntimeInstrumentationV1 => "application/vnd.sharplabnext.managed-pe",
                _ => "application/vnd.sharplabnext.il-verification+json"
            },
            0,
            [],
            [],
            outcome == ProcessorOutcome.LimitExceeded,
            message);
}
