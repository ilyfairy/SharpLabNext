using System.Diagnostics;

namespace SharpLabNext.BundleBuilder;

public sealed record RepositorySourceState(bool IsGitRepository, string? HeadRevision, bool IsDirty);

public sealed record RepositorySourceProvenance(string Revision, string? HeadRevision, bool IsDirty, bool IsVerified);

public interface IRepositorySourceInspector
{
    Task<RepositorySourceState> InspectAsync(string repositoryRoot, CancellationToken cancellationToken = default);
}

public sealed class ContentRepositorySourceInspector : IRepositorySourceInspector
{
    public Task<RepositorySourceState> InspectAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(repositoryRoot);
        return Task.FromResult(Directory.Exists(root) ? new RepositorySourceState(IsGitRepository: false, HeadRevision: null, IsDirty: true) : throw new BundleValidationException($"Source directory '{root}' does not exist."));
    }
}

public sealed class GitRepositorySourceInspector : IRepositorySourceInspector
{
    private readonly bool allowFallback;

    public GitRepositorySourceInspector(bool allowFallback = true)
    {
        this.allowFallback = allowFallback;
    }

    public async Task<RepositorySourceState> InspectAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var root = Path.GetFullPath(repositoryRoot);
        if (allowFallback && string.Equals(Environment.GetEnvironmentVariable(RepositorySourceProvenanceResolver.SourceIdentityModeEnvironmentVariable), RepositorySourceProvenanceResolver.ContentSourceIdentityMode, StringComparison.OrdinalIgnoreCase))
        {
            return FallbackState(root);
        }
        GitResult workTree;
        try
        {
            workTree = await RunGitAsync(root, ["rev-parse", "--is-inside-work-tree"], cancellationToken);
        }
        catch (BundleValidationException) when (allowFallback)
        {
            return FallbackState(root);
        }
        if (workTree.ExitCode != 0 || !string.Equals(workTree.StandardOutput.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            if (allowFallback)
                return FallbackState(root);
            throw new BundleValidationException("Source provenance requires a Git worktree.");
        }

        GitResult head;
        try
        {
            head = await RunGitAsync(root, ["rev-parse", "--verify", "HEAD"], cancellationToken);
        }
        catch (BundleValidationException) when (allowFallback)
        {
            return FallbackState(root);
        }
        var revision = head.ExitCode == 0 && !string.IsNullOrWhiteSpace(head.StandardOutput)
            ? head.StandardOutput.Trim() : null;
        if (revision is null)
        {
            if (allowFallback)
                return FallbackState(root);
            throw new BundleValidationException("Source provenance requires an existing Git HEAD commit.");
        }

        GitResult status;
        try
        {
            status = await RunGitAsync(root, ["status", "--porcelain=v1", "--untracked-files=all"], cancellationToken);
        }
        catch (BundleValidationException) when (allowFallback)
        {
            return FallbackState(root);
        }
        if (status.ExitCode != 0)
        {
            if (allowFallback)
                return FallbackState(root);
            throw new BundleValidationException("Could not inspect Git worktree status.");
        }

        return new RepositorySourceState(true, revision, !string.IsNullOrWhiteSpace(status.StandardOutput));
    }

    private static RepositorySourceState FallbackState(string root) => Directory.Exists(root) ? new(false, null, true) : throw new BundleValidationException($"Source directory '{root}' does not exist.");

    private static async Task<GitResult> RunGitAsync(string repositoryRoot, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo { FileName = "git", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(repositoryRoot);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo) ?? throw new BundleValidationException("Could not start Git to inspect source provenance.");
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

    private sealed record GitResult(int ExitCode, string StandardOutput, string StandardError);
}

public static class RepositorySourceProvenanceResolver
{
    public const string ImageLabel = "io.sharplabnext.source.revision";
    public const string SourceIdentityModeEnvironmentVariable = "SHARPLABNEXT_SOURCE_IDENTITY_MODE";
    public const string ContentSourceIdentityMode = "content";
    // Keep the 64-hex shape required by low-level image contracts, but use it
    // only as a stable local label; Docker/BuildKit owns cache invalidation.
    public const string LocalBuildRevision = "0f92ac96a34a11b45d5a836a4a602b79e9b4e5ba607fce7965d0ee46cea8e408";

    public static async Task<RepositorySourceProvenance> ResolveAsync(string repositoryRoot, string? requestedRevision, IRepositorySourceInspector? inspector = null, CancellationToken cancellationToken = default)
    {
        inspector ??= new GitRepositorySourceInspector();
        var state = await inspector.InspectAsync(repositoryRoot, cancellationToken);
        return Resolve(state, requestedRevision);
    }

    public static RepositorySourceProvenance Resolve(RepositorySourceState state, string? requestedRevision)
    {
        ArgumentNullException.ThrowIfNull(state);
        var requested = string.IsNullOrWhiteSpace(requestedRevision) ? null : requestedRevision.Trim();
        var localBuildMode = string.Equals(Environment.GetEnvironmentVariable(SourceIdentityModeEnvironmentVariable), ContentSourceIdentityMode, StringComparison.OrdinalIgnoreCase);
        if (requested is not null && IsReservedUnknown(requested))
        {
            throw new BundleValidationException("Source revision cannot be 'unknown'; use a Git revision or the local build label.");
        }
        if (localBuildMode)
        {
            // Local builds keep one stable label while Docker observes the
            // actual working-tree inputs and decides which layers to rebuild.
            requested ??= LocalBuildRevision;
        }

        if (!localBuildMode && state.IsGitRepository && state.HeadRevision is not null && requested is not null && !string.Equals(requested, state.HeadRevision, StringComparison.OrdinalIgnoreCase))
        {
            throw new BundleValidationException($"Requested source revision '{requested}' does not match Git HEAD '{state.HeadRevision}'.");
        }

        var revision = requested ?? state.HeadRevision ?? LocalBuildRevision;
        if (!IsRevisionLabel(revision))
        {
            throw new BundleValidationException("Source revision must be 1-128 ASCII letters, digits, '.', '_', '-', or ':'.");
        }

        if (state.HeadRevision is not null && string.Equals(revision, state.HeadRevision, StringComparison.OrdinalIgnoreCase))
        {
            revision = state.HeadRevision;
        }

        var verified = state.IsGitRepository &&
                       state.HeadRevision is not null &&
                       !state.IsDirty &&
                       string.Equals(revision, state.HeadRevision, StringComparison.Ordinal);
        return new RepositorySourceProvenance(revision, state.HeadRevision, state.IsDirty, verified);
    }

    private static bool IsReservedUnknown(string value) =>
        value.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("none", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("null", StringComparison.OrdinalIgnoreCase);

    private static bool IsRevisionLabel(string value) =>
        value.Length is > 0 and <= 128 &&
        value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');
}
