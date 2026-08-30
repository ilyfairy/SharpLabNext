using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Sdk;

namespace SharpLabNext.Worker.GSharp;

internal sealed record GSharpCompilerInvocation(bool Succeeded, byte[] PeImage, byte[] PortablePdb, IReadOnlyList<Diagnostic> Diagnostics);

public sealed partial class GSharpCompilerProcess : IDisposable
{
    private const int MaximumDiagnostics = 1000;
    private readonly GSharpWorkerSettings _settings;
    private readonly LanguageWorkerCapabilityManifest _manifest;
    private readonly ILogger<GSharpCompilerProcess> _logger;
    private readonly SemaphoreSlim _concurrency;
    private int _startedProcessCount;

    public GSharpCompilerProcess(GSharpWorkerSettings settings, LanguageWorkerCapabilityManifest manifest, ILogger<GSharpCompilerProcess> logger)
    {
        _settings = settings;
        _manifest = manifest;
        _logger = logger;
        _concurrency = new SemaphoreSlim(manifest.Limits.MaximumConcurrentBuilds, manifest.Limits.MaximumConcurrentBuilds);
    }

    internal async Task<GSharpCompilerInvocation> CompileAsync(ValidatedGSharpWorkspace workspace, LoadedGSharpReferenceSet referenceSet, GSharpToolchainProfile toolchain, CancellationToken cancellationToken)
    {
        if (!File.Exists(toolchain.CompilerAssemblyPath))
        {
            throw new LanguageWorkerRequestException("compiler-unavailable", "The fixed G# compiler is unavailable.", StatusCodes.Status503ServiceUnavailable);
        }
        if (!await _concurrency.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new LanguageWorkerRequestException("compiler-capacity-exhausted", "G# compiler process capacity is exhausted.", StatusCodes.Status429TooManyRequests);
        }

        string? jobRoot = null;
        try
        {
            Directory.CreateDirectory(_settings.WorkRoot);
            jobRoot = Path.Combine(_settings.WorkRoot, $"build-{Guid.NewGuid():N}");
            Directory.CreateDirectory(jobRoot);
            var sourcePaths = new List<string>(workspace.OrderedFiles.Count);
            var pathMap = new Dictionary<string, string>(PathComparer());
            foreach (var file in workspace.OrderedFiles)
            {
                var path = Path.GetFullPath(Path.Combine(jobRoot, file.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(jobRoot + Path.DirectorySeparatorChar, PathComparison()))
                    throw new InvalidOperationException("Validated G# path escaped its job directory.");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, file.Text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                sourcePaths.Add(path);
                pathMap[path] = file.Path;
            }

            var outputDirectory = Path.Combine(jobRoot, "output");
            Directory.CreateDirectory(outputDirectory);
            var pePath = Path.Combine(outputDirectory, $"{GSharpToolchain.AssemblyName}.dll");
            var pdbPath = Path.Combine(outputDirectory, $"{GSharpToolchain.AssemblyName}.pdb");
            var startInfo = GSharpProcessEnvironment.Create(_settings, jobRoot);
            startInfo.ArgumentList.Add(toolchain.CompilerAssemblyPath);
            foreach (var sourcePath in sourcePaths)
                startInfo.ArgumentList.Add(sourcePath);
            startInfo.ArgumentList.Add($"/out:{pePath}");
            startInfo.ArgumentList.Add($"/pdb:{pdbPath}");
            startInfo.ArgumentList.Add("/debug:portable");
            startInfo.ArgumentList.Add("/deterministic+");
            startInfo.ArgumentList.Add("/nowarn:GS9100");
            startInfo.ArgumentList.Add($"/assemblyname:{GSharpToolchain.AssemblyName}");
            startInfo.ArgumentList.Add(workspace.Options.OutputKind == BuildOutputKind.Library ? "/target:library" : "/target:exe");
            startInfo.ArgumentList.Add($"/targetframework:{referenceSet.Definition.TargetFramework}");
            foreach (var referencePath in referenceSet.ReferenceAssemblyPaths)
                startInfo.ArgumentList.Add($"/r:{referencePath}");

            var execution = await ExecuteAsync(startInfo, cancellationToken).ConfigureAwait(false);
            if (execution.OutputLimitExceeded)
            {
                throw new LanguageWorkerRequestException("compiler-output-limit", "The G# compiler exceeded its process output limit.", StatusCodes.Status413PayloadTooLarge);
            }

            var diagnostics = ParseDiagnostics(execution.StandardOutput, execution.StandardError, pathMap, workspace.Snapshot.Revision, workspace.Snapshot.SelectionRevision);
            if (execution.ExitCode != 0)
            {
                if (diagnostics.Count == 0)
                {
                    diagnostics =
                    [
                        CreateDiagnostic("GS9999", DiagnosticSeverity.Error, GSharpProcessEnvironment.PublicText(string.IsNullOrWhiteSpace(execution.StandardError) ? execution.StandardOutput : execution.StandardError), null, null, workspace.Snapshot.Revision, workspace.Snapshot.SelectionRevision)
                    ];
                }
                return new GSharpCompilerInvocation(false, [], [], diagnostics);
            }

            var pe = await ReadBoundedAsync(pePath, _manifest.Limits.MaximumArtifactBytes, "The G# compiler did not produce a bounded managed PE.", cancellationToken).ConfigureAwait(false);
            var remaining = _manifest.Limits.MaximumArtifactBytes - pe.Length;
            var pdb = await ReadBoundedAsync(pdbPath, remaining, "The G# compiler did not produce a bounded Portable PDB.", cancellationToken).ConfigureAwait(false);
            return new GSharpCompilerInvocation(true, pe, pdb, diagnostics);
        }
        finally
        {
            if (jobRoot is not null)
                DeleteJobRoot(jobRoot);
            _concurrency.Release();
        }
    }

    internal int StartedProcessCount => Volatile.Read(ref _startedProcessCount);

    public void Dispose() => _concurrency.Dispose();

    private async Task<ProcessExecution> ExecuteAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Process.Start returned false.");
            process.StandardInput.Close();
            Interlocked.Increment(ref _startedProcessCount);
        }
        catch (Exception exception)
        {
            throw new LanguageWorkerRequestException("compiler-unavailable", "The fixed G# compiler could not be started.", StatusCodes.Status503ServiceUnavailable, exception);
        }

        var outputExceeded = 0;
        void MarkOutputExceeded()
        {
            Interlocked.Exchange(ref outputExceeded, 1);
            GSharpProcessEnvironment.Kill(process);
        }
        var stdoutTask = CaptureAsync(process.StandardOutput.BaseStream, _settings.ProcessLimits.MaximumProcessOutputBytes, MarkOutputExceeded);
        var stderrTask = CaptureAsync(process.StandardError.BaseStream, _settings.ProcessLimits.MaximumProcessOutputBytes, MarkOutputExceeded);
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
                    if (process.WorkingSet64 > _settings.ProcessLimits.MaximumProcessWorkingSetBytes)
                    {
                        GSharpProcessEnvironment.Kill(process);
                        throw new LanguageWorkerRequestException("compiler-memory-limit", "The G# compiler exceeded its memory limit.", StatusCodes.Status429TooManyRequests);
                    }
                }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            await exitTask.ConfigureAwait(false);
        }
        catch
        {
            GSharpProcessEnvironment.Kill(process);
            await exitTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw;
        }

        return new ProcessExecution(process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false), Volatile.Read(ref outputExceeded) != 0);
    }

    private static async Task<string> CaptureAsync(Stream stream, int maximumBytes, Action limitExceeded)
    {
        using var result = new MemoryStream(Math.Min(maximumBytes, 16 * 1024));
        var buffer = new byte[4096];
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

    private static List<Diagnostic> ParseDiagnostics(string standardOutput, string standardError, Dictionary<string, string> pathMap, long workspaceRevision, long selectionRevision)
    {
        var diagnostics = new List<Diagnostic>();
        foreach (var line in EnumerateLines(standardOutput).Concat(EnumerateLines(standardError)))
        {
            if (diagnostics.Count >= MaximumDiagnostics)
                break;
            var match = LocatedDiagnosticRegex().Match(line);
            if (match.Success)
            {
                var rawPath = match.Groups["path"].Value;
                var fullPath = Path.GetFullPath(rawPath);
                var path = pathMap.TryGetValue(fullPath, out var mapped) ? mapped : Path.GetFileName(rawPath);
                var startLine = Math.Max(0, ParseCoordinate(match, "startLine") - 1);
                var startCharacter = Math.Max(0, ParseCoordinate(match, "startCharacter") - 1);
                var endLine = Math.Max(startLine, ParseCoordinate(match, "endLine") - 1);
                var endCharacter = Math.Max(startCharacter, ParseCoordinate(match, "endCharacter") - 1);
                diagnostics.Add(CreateDiagnostic(match.Groups["code"].Value, Severity(match.Groups["severity"].Value), match.Groups["message"].Value, path, new TextRange(startLine, startCharacter, endLine, endCharacter), workspaceRevision, selectionRevision));
                continue;
            }
            match = LocationlessDiagnosticRegex().Match(line);
            if (match.Success)
            {
                diagnostics.Add(CreateDiagnostic(match.Groups["code"].Value, Severity(match.Groups["severity"].Value), match.Groups["message"].Value, null, null, workspaceRevision, selectionRevision));
            }
        }
        return diagnostics;
    }

    private static IEnumerable<string> EnumerateLines(string value)
    {
        using var reader = new StringReader(value);
        while (reader.ReadLine() is { } line)
            yield return line.Trim();
    }

    private static Diagnostic CreateDiagnostic(string code, DiagnosticSeverity severity, string message, string? filePath, TextRange? range, long workspaceRevision, long selectionRevision) => new("gsc", code, severity, GSharpProcessEnvironment.PublicText(message, 4096), filePath, range, [], [], workspaceRevision, selectionRevision);

    private static DiagnosticSeverity Severity(string value) => value switch
    {
        "error" => DiagnosticSeverity.Error,
        "warning" => DiagnosticSeverity.Warning,
        _ => DiagnosticSeverity.Information
    };

    private static int ParseCoordinate(Match match, string name) => int.TryParse(match.Groups[name].Value, out var value) ? value : 1;

    private static async Task<byte[]> ReadBoundedAsync(string path, int maximumBytes, string failureMessage, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0)
        {
            throw new LanguageWorkerRequestException("compiler-invalid-output", failureMessage, StatusCodes.Status503ServiceUnavailable);
        }
        if (maximumBytes <= 0 || info.Length > maximumBytes)
        {
            throw new LanguageWorkerRequestException("artifact-too-large", "The G# compiler output exceeds the configured artifact limit.", StatusCodes.Status413PayloadTooLarge);
        }
        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private void DeleteJobRoot(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            CompilerJobCleanupFailed(_logger, exception, path);
        }
    }

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    [GeneratedRegex("^(?<path>.+)\\((?<startLine>[0-9]+),(?<startCharacter>[0-9]+),(?<endLine>[0-9]+),(?<endCharacter>[0-9]+)\\): (?<severity>error|warning|info) (?<code>GS[0-9]{4}): (?<message>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex LocatedDiagnosticRegex();

    [GeneratedRegex("^(?:(?:gsc|[^:]+): )?(?<severity>error|warning|info) (?<code>GS[0-9]{4}): (?<message>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex LocationlessDiagnosticRegex();

    private sealed record ProcessExecution(int ExitCode, string StandardOutput, string StandardError, bool OutputLimitExceeded);

    [LoggerMessage(EventId = 6101, Level = LogLevel.Warning, Message = "Failed to remove G# compiler job directory {JobRoot}.")]
    private static partial void CompilerJobCleanupFailed(ILogger logger, Exception exception, string jobRoot);
}
