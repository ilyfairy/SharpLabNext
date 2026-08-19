using System.Diagnostics;
using System.Text;
using SharpLabNext.ArtifactWorker.Sdk;

namespace SharpLabNext.Worker.Artifacts.JSIL;

internal sealed record JsilTranslationResult(
    bool Succeeded,
    string? JavaScript,
    string? PublicMessage,
    string? Detail);

internal interface IJsilProcessRunner
{
    Task<JsilTranslationResult> TranslateAsync(
        JsilMaterializedArtifact artifact,
        int maximumCharacters,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken);
}

internal sealed class JsilProcessRunner(
    JsilWorkerSettings settings,
    ArtifactWorkerCapabilityManifest capabilityManifest) : IJsilProcessRunner
{
    public async Task<JsilTranslationResult> TranslateAsync(
        JsilMaterializedArtifact artifact,
        int maximumCharacters,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.Combine(artifact.RootPath, "javascript");
        Directory.CreateDirectory(outputDirectory);
        using var process = new Process
        {
            StartInfo = CreateStartInfo(artifact.AssemblyPath, outputDirectory),
            EnableRaisingEvents = true
        };
        if (!process.Start())
            throw new ArtifactWorkerProcessorException("The isolated JSIL process could not be started.");

        var stdout = ReadBoundedAsync(
            process.StandardOutput.BaseStream,
            settings.MaximumProcessOutputBytes,
            CancellationToken.None);
        var stderr = ReadBoundedAsync(
            process.StandardError.BaseStream,
            settings.MaximumProcessOutputBytes,
            CancellationToken.None);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = deadlineUtc - DateTimeOffset.UtcNow;
        var operationLimit = TimeSpan.FromMilliseconds(capabilityManifest.Limits.MaximumOperationMilliseconds);
        if (remaining <= TimeSpan.Zero)
            throw new ArtifactWorkerDeadlineExceededException("The JSIL translation deadline elapsed.");
        deadline.CancelAfter(remaining < operationLimit ? remaining : operationLimit);

        var memoryExceeded = false;
        try
        {
            while (!process.HasExited)
            {
                deadline.Token.ThrowIfCancellationRequested();
                process.Refresh();
                if (process.WorkingSet64 > settings.MaximumProcessWorkingSetBytes)
                {
                    memoryExceeded = true;
                    Kill(process);
                    break;
                }
                await Task.Delay(25, deadline.Token).ConfigureAwait(false);
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw new ArtifactWorkerDeadlineExceededException("JSIL translation exceeded its deadline.");
        }

        var stdoutResult = await stdout.ConfigureAwait(false);
        var stderrResult = await stderr.ConfigureAwait(false);
        if (memoryExceeded)
            throw new ArtifactWorkerLimitExceededException("JSIL translation exceeded its memory limit.");
        if (stdoutResult.Truncated || stderrResult.Truncated)
            throw new ArtifactWorkerLimitExceededException("JSIL process diagnostics exceeded the output limit.");

        var outputFiles = Directory.EnumerateFiles(outputDirectory, "*.js", SearchOption.TopDirectoryOnly)
            .Where(static path => !path.EndsWith(".manifest.js", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (process.ExitCode != 0 || outputFiles.Length != 1)
        {
            return new JsilTranslationResult(
                false,
                null,
                "JSIL could not translate this managed assembly.",
                SanitizeDetail(stderrResult.Text, stdoutResult.Text));
        }

        var output = outputFiles[0];
        var length = new FileInfo(output).Length;
        if (length <= 0 || length > capabilityManifest.Limits.MaximumOutputArtifactBytes)
            throw new ArtifactWorkerLimitExceededException("Generated JavaScript exceeded the output limit.");
        var javascript = await File.ReadAllTextAsync(output, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        if (javascript.Length > maximumCharacters)
            throw new ArtifactWorkerLimitExceededException("Generated JavaScript exceeded the requested character limit.");
        return new JsilTranslationResult(true, javascript, null, null);
    }

    private ProcessStartInfo CreateStartInfo(string assemblyPath, string outputDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = settings.MonoPath,
            WorkingDirectory = Path.GetDirectoryName(settings.CompilerPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(settings.CompilerPath);
        startInfo.ArgumentList.Add("--quiet");
        startInfo.ArgumentList.Add("--nothreads");
        startInfo.ArgumentList.Add("--nodeps");
        startInfo.ArgumentList.Add("--out");
        startInfo.ArgumentList.Add(outputDirectory);
        startInfo.ArgumentList.Add(assemblyPath);
        startInfo.Environment["MONO_GC_PARAMS"] =
            $"max-heap-size={settings.MaximumProcessWorkingSetBytes}";
        startInfo.Environment["HOME"] = Path.GetDirectoryName(assemblyPath)!;
        return startInfo;
    }

    private static async Task<BoundedText> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var content = new MemoryStream(Math.Min(maximumBytes, 16 * 1024));
        var buffer = new byte[8 * 1024];
        var truncated = false;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            var remaining = maximumBytes - checked((int)content.Length);
            if (remaining > 0)
                content.Write(buffer, 0, Math.Min(read, remaining));
            if (read > remaining)
                truncated = true;
        }
        return new BoundedText(Encoding.UTF8.GetString(content.ToArray()), truncated);
    }

    private static string? SanitizeDetail(params string[] values)
    {
        var value = string.Join(' ', values)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (value.Length == 0)
            return null;
        return value.Length <= 2_048 ? value : value[..2_048];
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

    private sealed record BoundedText(string Text, bool Truncated);
}
