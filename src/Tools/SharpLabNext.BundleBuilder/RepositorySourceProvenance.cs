using System.Diagnostics;

namespace SharpLabNext.BundleBuilder;

public sealed record RepositorySourceState(
    bool IsGitRepository,
    string? HeadRevision,
    bool IsDirty);

public sealed record RepositorySourceProvenance(
    string Revision,
    string? HeadRevision,
    bool IsDirty,
    bool IsVerified,
    bool DevelopmentOverrideUsed);

public interface IRepositorySourceInspector
{
    Task<RepositorySourceState> InspectAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default);
}

public sealed class GitRepositorySourceInspector : IRepositorySourceInspector
{
    public async Task<RepositorySourceState> InspectAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var root = Path.GetFullPath(repositoryRoot);
        var workTree = await RunGitAsync(root, ["rev-parse", "--is-inside-work-tree"], cancellationToken);
        if (workTree.ExitCode != 0 ||
            !string.Equals(workTree.StandardOutput.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            return new RepositorySourceState(false, null, true);
        }

        var head = await RunGitAsync(root, ["rev-parse", "--verify", "HEAD"], cancellationToken);
        var revision = head.ExitCode == 0 && !string.IsNullOrWhiteSpace(head.StandardOutput)
            ? head.StandardOutput.Trim()
            : null;
        var status = await RunGitAsync(
            root,
            ["status", "--porcelain=v1", "--untracked-files=all"],
            cancellationToken);
        if (status.ExitCode != 0)
        {
            throw new BundleValidationException(
                $"Could not inspect Git worktree status: {SingleLine(status.StandardError)}");
        }

        return new RepositorySourceState(
            true,
            revision,
            !string.IsNullOrWhiteSpace(status.StandardOutput));
    }

    private static async Task<GitResult> RunGitAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(repositoryRoot);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new BundleValidationException("Could not start Git to inspect source provenance.");
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new GitResult(process.ExitCode, await outputTask, await errorTask);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new BundleValidationException($"Git is required to verify source provenance: {exception.Message}");
        }
    }

    private static string SingleLine(string value) =>
        string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();

    private sealed record GitResult(int ExitCode, string StandardOutput, string StandardError);
}

public static class RepositorySourceProvenanceResolver
{
    public const string ImageLabel = "io.sharplabnext.source.revision";
    public const string LocalUncommittedRevision = "local-uncommitted";

    public static async Task<RepositorySourceProvenance> ResolveAsync(
        string repositoryRoot,
        string? requestedRevision,
        bool allowUncommittedSourceForDevelopment,
        IRepositorySourceInspector? inspector = null,
        CancellationToken cancellationToken = default)
    {
        inspector ??= new GitRepositorySourceInspector();
        var state = await inspector.InspectAsync(repositoryRoot, cancellationToken);
        return Resolve(state, requestedRevision, allowUncommittedSourceForDevelopment);
    }

    public static RepositorySourceProvenance Resolve(
        RepositorySourceState state,
        string? requestedRevision,
        bool allowUncommittedSourceForDevelopment)
    {
        ArgumentNullException.ThrowIfNull(state);
        var requested = string.IsNullOrWhiteSpace(requestedRevision) ? null : requestedRevision.Trim();
        if (requested is not null && IsReservedUnknown(requested))
        {
            throw new BundleValidationException(
                "Source revision cannot be 'unknown'; use a Git commit or an explicit local development revision.");
        }

        if (!allowUncommittedSourceForDevelopment)
        {
            if (!state.IsGitRepository)
            {
                throw new BundleValidationException(
                    "Release bundle creation requires a Git worktree. " +
                    "Use the development-only override only for local tests.");
            }
            if (string.IsNullOrWhiteSpace(state.HeadRevision))
            {
                throw new BundleValidationException(
                    "Release bundle creation requires an existing Git HEAD commit.");
            }
            if (state.IsDirty)
            {
                throw new BundleValidationException(
                    "Release bundle creation requires a clean Git worktree, including no untracked files.");
            }
            if (requested is not null &&
                !string.Equals(requested, state.HeadRevision, StringComparison.OrdinalIgnoreCase))
            {
                throw new BundleValidationException(
                    $"Requested source revision '{requested}' does not match Git HEAD '{state.HeadRevision}'.");
            }
        }

        var revision = requested ?? state.HeadRevision ?? LocalUncommittedRevision;
        if (!IsRevisionLabel(revision))
        {
            throw new BundleValidationException(
                "Source revision must be 1-128 ASCII letters, digits, '.', '_', '-', or ':'.");
        }

        if (state.HeadRevision is not null &&
            string.Equals(revision, state.HeadRevision, StringComparison.OrdinalIgnoreCase))
        {
            revision = state.HeadRevision;
        }

        var verified = state.IsGitRepository &&
                       state.HeadRevision is not null &&
                       !state.IsDirty &&
                       string.Equals(revision, state.HeadRevision, StringComparison.Ordinal);
        return new RepositorySourceProvenance(
            revision,
            state.HeadRevision,
            state.IsDirty,
            verified,
            allowUncommittedSourceForDevelopment);
    }

    private static bool IsReservedUnknown(string value) =>
        value.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("none", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("null", StringComparison.OrdinalIgnoreCase);

    private static bool IsRevisionLabel(string value) =>
        value.Length is > 0 and <= 128 &&
        value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');
}
