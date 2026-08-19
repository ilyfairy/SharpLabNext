using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Sdk;

namespace SharpLabNext.Worker.JSharp;

internal sealed record JSharpCompilerInvocation(
    bool Succeeded,
    byte[] PeImage,
    IReadOnlyList<Diagnostic> Diagnostics);

internal interface IJSharpCompilerProcess
{
    Task<JSharpCompilerInvocation> CompileAsync(
        ValidatedJSharpWorkspace workspace,
        CancellationToken cancellationToken);
}

public sealed partial class JSharpCompilerProcess : IJSharpCompilerProcess, IDisposable
{
    private readonly JSharpWorkerSettings _settings;
    private readonly LanguageWorkerCapabilityManifest _manifest;
    private readonly ILogger<JSharpCompilerProcess> _logger;
    private readonly SemaphoreSlim _concurrency;
    private int _startedProcessCount;

    public JSharpCompilerProcess(
        JSharpWorkerSettings settings,
        LanguageWorkerCapabilityManifest manifest,
        ILogger<JSharpCompilerProcess> logger)
    {
        _settings = settings;
        _manifest = manifest;
        _logger = logger;
        _concurrency = new SemaphoreSlim(
            manifest.Limits.MaximumConcurrentBuilds,
            manifest.Limits.MaximumConcurrentBuilds);
    }

    async Task<JSharpCompilerInvocation> IJSharpCompilerProcess.CompileAsync(
        ValidatedJSharpWorkspace workspace,
        CancellationToken cancellationToken) =>
        await CompileAsync(workspace, cancellationToken).ConfigureAwait(false);

    internal async Task<JSharpCompilerInvocation> CompileAsync(
        ValidatedJSharpWorkspace workspace,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_settings.CompilerHostPath) || !File.Exists(_settings.CompilerPath))
        {
            throw new LanguageWorkerRequestException(
                "compiler-unavailable",
                "The operator-supplied J# compiler is unavailable.",
                StatusCodes.Status503ServiceUnavailable);
        }
        if (!await _concurrency.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new LanguageWorkerRequestException(
                "compiler-capacity-exhausted",
                "J# compiler process capacity is exhausted.",
                StatusCodes.Status429TooManyRequests);
        }

        string? jobRoot = null;
        try
        {
            Directory.CreateDirectory(_settings.WorkRoot);
            jobRoot = Path.Combine(_settings.WorkRoot, $"build-{Guid.NewGuid():N}");
            Directory.CreateDirectory(jobRoot);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    jobRoot,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var sourcePath = workspace.SourceFile.Path;
            var fullSourcePath = Path.GetFullPath(Path.Combine(
                jobRoot,
                sourcePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullSourcePath.StartsWith(jobRoot + Path.DirectorySeparatorChar, PathComparison()))
                throw new InvalidOperationException("Validated J# path escaped its private job directory.");
            Directory.CreateDirectory(Path.GetDirectoryName(fullSourcePath)!);
            await File.WriteAllTextAsync(
                fullSourcePath,
                workspace.SourceFile.Text,
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);

            const string outputDirectory = "output";
            const string outputPath = outputDirectory + "/" + JSharpToolchain.OutputFileName;
            Directory.CreateDirectory(Path.Combine(jobRoot, outputDirectory));
            var startInfo = JSharpCompilerCommand.Create(
                _settings,
                jobRoot,
                sourcePath,
                outputPath,
                workspace.Options.Optimize);
            var execution = await ExecuteAsync(startInfo, cancellationToken).ConfigureAwait(false);
            if (execution.OutputLimitExceeded)
            {
                throw new LanguageWorkerRequestException(
                    "compiler-output-limit",
                    "The J# compiler exceeded its process output limit.",
                    StatusCodes.Status413PayloadTooLarge);
            }

            var diagnostics = ParseDiagnostics(
                execution.StandardOutput,
                execution.StandardError,
                jobRoot,
                [sourcePath],
                workspace.Snapshot.Revision,
                workspace.Snapshot.SelectionRevision,
                _settings.ProcessLimits.MaximumDiagnostics);
            if (execution.ExitCode != 0)
            {
                if (diagnostics.Count == 0)
                {
                    diagnostics =
                    [
                        CreateDiagnostic(
                            "VJC9999",
                            DiagnosticSeverity.Error,
                            PublicText(
                                string.IsNullOrWhiteSpace(execution.StandardError)
                                    ? execution.StandardOutput
                                    : execution.StandardError,
                                jobRoot),
                            null,
                            null,
                            workspace.Snapshot.Revision,
                            workspace.Snapshot.SelectionRevision)
                    ];
                }
                return new JSharpCompilerInvocation(false, [], diagnostics);
            }

            var pe = await ReadBoundedAsync(
                Path.Combine(jobRoot, outputPath.Replace('/', Path.DirectorySeparatorChar)),
                _manifest.Limits.MaximumArtifactBytes,
                cancellationToken).ConfigureAwait(false);
            return new JSharpCompilerInvocation(true, pe, diagnostics);
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

    private async Task<ProcessExecution> ExecuteAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
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
            throw new LanguageWorkerRequestException(
                "compiler-unavailable",
                "The operator-supplied J# compiler could not be started.",
                StatusCodes.Status503ServiceUnavailable,
                exception);
        }

        var outputBudget = new ProcessOutputBudget(
            _settings.ProcessLimits.MaximumProcessOutputBytes,
            () => Kill(process));
        var stdoutTask = CaptureAsync(process.StandardOutput.BaseStream, outputBudget);
        var stderrTask = CaptureAsync(process.StandardError.BaseStream, outputBudget);
        var exitTask = process.WaitForExitAsync(CancellationToken.None);
        try
        {
            while (!exitTask.IsCompleted)
            {
                await Task.WhenAny(
                    exitTask,
                    Task.Delay(
                        _settings.ProcessLimits.MemoryPollIntervalMilliseconds,
                        cancellationToken)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (exitTask.IsCompleted)
                    break;
                try
                {
                    process.Refresh();
                    if (process.WorkingSet64 > _settings.ProcessLimits.MaximumProcessWorkingSetBytes)
                    {
                        Kill(process);
                        throw new LanguageWorkerRequestException(
                            "compiler-memory-limit",
                            "The J# compiler exceeded its memory limit.",
                            StatusCodes.Status429TooManyRequests);
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
            await ((Task)Task.WhenAll(stdoutTask, stderrTask))
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw;
        }

        return new ProcessExecution(
            process.ExitCode,
            await stdoutTask.ConfigureAwait(false),
            await stderrTask.ConfigureAwait(false),
            outputBudget.Exceeded);
    }

    private static async Task<string> CaptureAsync(Stream stream, ProcessOutputBudget budget)
    {
        using var result = new MemoryStream(Math.Min(budget.MaximumBytes, 16 * 1024));
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
                break;
            budget.Observe(read);
            if (result.Length < budget.MaximumBytes)
            {
                result.Write(
                    buffer,
                    0,
                    Math.Min(read, budget.MaximumBytes - checked((int)result.Length)));
            }
        }
        return Encoding.UTF8.GetString(result.GetBuffer(), 0, checked((int)result.Length));
    }

    internal static List<Diagnostic> ParseDiagnostics(
        string standardOutput,
        string standardError,
        string jobRoot,
        IReadOnlyList<string> sourcePaths,
        long workspaceRevision,
        long selectionRevision,
        int maximumDiagnostics)
    {
        var diagnostics = new List<Diagnostic>();
        foreach (var line in EnumerateLines(standardOutput).Concat(EnumerateLines(standardError)))
        {
            if (diagnostics.Count >= maximumDiagnostics)
                break;
            var match = LocatedDiagnosticRegex().Match(line);
            if (match.Success)
            {
                var startLine = Math.Max(0, ParseCoordinate(match, "line") - 1);
                var startCharacter = Math.Max(0, ParseCoordinate(match, "column") - 1);
                diagnostics.Add(CreateDiagnostic(
                    match.Groups["code"].Value,
                    Severity(match.Groups["severity"].Value),
                    PublicText(match.Groups["message"].Value, jobRoot, 4096),
                    MapPath(match.Groups["path"].Value, sourcePaths),
                    new TextRange(startLine, startCharacter, startLine, startCharacter + 1),
                    workspaceRevision,
                    selectionRevision));
                continue;
            }

            match = LocationlessDiagnosticRegex().Match(line);
            if (match.Success)
            {
                diagnostics.Add(CreateDiagnostic(
                    match.Groups["code"].Value,
                    Severity(match.Groups["severity"].Value),
                    PublicText(match.Groups["message"].Value, jobRoot, 4096),
                    null,
                    null,
                    workspaceRevision,
                    selectionRevision));
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

    private static string? MapPath(string rawPath, IReadOnlyList<string> sourcePaths)
    {
        var normalized = rawPath.Trim().Trim('"').Replace('\\', '/');
        foreach (var sourcePath in sourcePaths)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(normalized, sourcePath) ||
                normalized.EndsWith('/' + sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                return sourcePath;
            }
        }
        var fileName = normalized.Split('/').LastOrDefault();
        return sourcePaths.FirstOrDefault(path =>
            StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(path), fileName));
    }

    private static Diagnostic CreateDiagnostic(
        string code,
        DiagnosticSeverity severity,
        string message,
        string? filePath,
        TextRange? range,
        long workspaceRevision,
        long selectionRevision) => new(
        "vjc",
        code,
        severity,
        message,
        filePath,
        range,
        [],
        [],
        workspaceRevision,
        selectionRevision);

    private static DiagnosticSeverity Severity(string value) => value.ToLowerInvariant() switch
    {
        "warning" => DiagnosticSeverity.Warning,
        "error" or "fatal error" => DiagnosticSeverity.Error,
        _ => DiagnosticSeverity.Information
    };

    private static int ParseCoordinate(Match match, string name) =>
        int.TryParse(match.Groups[name].Value, out var value) ? value : 1;

    private static string PublicText(string value, string jobRoot, int maximumCharacters = 1024)
    {
        var compact = value
            .Replace(jobRoot, ".", PathComparison())
            .Replace(jobRoot.Replace('\\', '/'), ".", PathComparison())
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return compact.Length <= maximumCharacters ? compact : compact[..maximumCharacters];
    }

    private static async Task<byte[]> ReadBoundedAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0)
        {
            throw new LanguageWorkerRequestException(
                "compiler-invalid-output",
                "The J# compiler did not produce a managed executable.",
                StatusCodes.Status503ServiceUnavailable);
        }
        if (info.Length > maximumBytes)
        {
            throw new LanguageWorkerRequestException(
                "artifact-too-large",
                "The J# compiler output exceeds the configured artifact limit.",
                StatusCodes.Status413PayloadTooLarge);
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

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    [GeneratedRegex(
        "^(?<path>.+)\\((?<line>[0-9]+)(?:,(?<column>[0-9]+))?\\)\\s*:\\s*(?<severity>fatal error|error|warning)\\s+(?<code>[A-Z]+[0-9]+)\\s*:\\s*(?<message>.+)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LocatedDiagnosticRegex();

    [GeneratedRegex(
        "^(?:(?:vjc|[^:]+)\\s*:\\s*)?(?<severity>fatal error|error|warning)\\s+(?<code>[A-Z]+[0-9]+)\\s*:\\s*(?<message>.+)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LocationlessDiagnosticRegex();

    private sealed record ProcessExecution(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        bool OutputLimitExceeded);

    private sealed class ProcessOutputBudget(int maximumBytes, Action exceeded)
    {
        private int _observedBytes;
        private int _exceeded;

        public int MaximumBytes { get; } = maximumBytes;
        public bool Exceeded => Volatile.Read(ref _exceeded) != 0;

        public void Observe(int bytes)
        {
            if (Interlocked.Add(ref _observedBytes, bytes) <= MaximumBytes ||
                Interlocked.Exchange(ref _exceeded, 1) != 0)
            {
                return;
            }
            exceeded();
        }
    }

    [LoggerMessage(
        EventId = 6501,
        Level = LogLevel.Warning,
        Message = "Failed to remove J# compiler job directory {JobRoot}.")]
    private static partial void CompilerJobCleanupFailed(
        ILogger logger,
        Exception exception,
        string jobRoot);
}
