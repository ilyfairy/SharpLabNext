using System.Diagnostics;
using System.Globalization;

namespace SharpLabNext.ProfileUpdater;

public interface ISourceDateEpochReader
{
    Task<string?> ReadAsync(string repositoryRoot, string revision, CancellationToken cancellationToken = default);
}

public sealed class GitSourceDateEpochReader : ISourceDateEpochReader
{
    public async Task<string?> ReadAsync(string repositoryRoot, string revision, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo { FileName = "git", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(Path.GetFullPath(repositoryRoot));
        startInfo.ArgumentList.Add("show");
        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add("--format=%ct");
        startInfo.ArgumentList.Add($"{revision}^{{commit}}");

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return null;
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            _ = await errorTask;
            return process.ExitCode == 0 ? (await outputTask).Trim() : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

public static class SourceDateEpochResolver
{
    public const string ContentFallbackUnixSeconds = "0";
    private const string SourceIdentityModeEnvironmentVariable = "SHARPLABNEXT_SOURCE_IDENTITY_MODE";
    private const string ContentSourceIdentityMode = "content";

    public static async Task<string> ResolveAsync(string repositoryRoot, string sourceRevision, SourceIdentityMode sourceIdentityMode = SourceIdentityMode.VerifiedRevision, ISourceDateEpochReader? reader = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRevision);
        var useDefaultReader = reader is null;
        reader ??= new GitSourceDateEpochReader();

        if (sourceIdentityMode == SourceIdentityMode.Content || useDefaultReader && string.Equals(Environment.GetEnvironmentVariable(SourceIdentityModeEnvironmentVariable), ContentSourceIdentityMode, StringComparison.OrdinalIgnoreCase))
        {
            return ContentFallbackUnixSeconds;
        }

        var epoch = await reader.ReadAsync(repositoryRoot, sourceRevision, cancellationToken);
        if (epoch is null)
            throw new BakeEnvironmentValidationException($"Could not resolve SOURCE_DATE_EPOCH from verified source revision '{sourceRevision}'.");

        return Validate(epoch);
    }

    public static string Validate(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Any(static character => character is < '0' or > '9') || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) || epoch < 0)
        {
            throw new BakeEnvironmentValidationException("SOURCE_DATE_EPOCH must be a non-negative Unix timestamp in whole seconds.");
        }

        return epoch.ToString(CultureInfo.InvariantCulture);
    }
}

public enum SourceIdentityMode
{
    VerifiedRevision,
    Content
}
