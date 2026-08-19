namespace SharpLabNext.ProfileUpdater;

public interface IProfileCandidateWorkspaceManager
{
    Task PrepareAsync(
        string repositoryRoot,
        string workspaceRoot,
        CancellationToken cancellationToken = default);
}

public sealed class GitProfileCandidateWorkspaceManager(
    IProfileUpdateCommandRunner? commandRunner = null) : IProfileCandidateWorkspaceManager
{
    private readonly IProfileUpdateCommandRunner commandRunner =
        commandRunner ?? new ProcessProfileUpdateCommandRunner();

    public async Task PrepareAsync(
        string repositoryRoot,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var workspace = Path.GetFullPath(workspaceRoot);
        if (Directory.Exists(workspace))
        {
            if (File.Exists(Path.Combine(workspace, ".git")) || Directory.Exists(Path.Combine(workspace, ".git")))
            {
                await PrepareSubmodulesAsync(workspace, cancellationToken);
                return;
            }
            throw new ProfileUpdateValidationException(
                $"Candidate workspace '{workspace}' exists but is not a Git worktree.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(workspace)!);
        await RunGitAsync(
            root,
            ["worktree", "add", "--detach", workspace, "HEAD"],
            "candidate worktree creation",
            cancellationToken);
        await PrepareSubmodulesAsync(workspace, cancellationToken);
    }

    private async Task PrepareSubmodulesAsync(string workspace, CancellationToken cancellationToken)
    {
        await RunGitAsync(
            workspace,
            ["submodule", "sync", "--recursive"],
            "candidate submodule synchronization",
            cancellationToken);
        await RunGitAsync(
            workspace,
            ["submodule", "update", "--init", "--recursive", "--checkout", "--force"],
            "candidate submodule checkout",
            cancellationToken);
    }

    private async Task RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        string operation,
        CancellationToken cancellationToken)
    {
        ProfileUpdateCommandResult result;
        try
        {
            result = await commandRunner.RunAsync(
                new ProfileUpdateExternalCommand("git", arguments, workingDirectory),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ProfileUpdateValidationException(
                $"Git {operation} could not start: {exception.Message}");
        }
        if (result.ExitCode != 0)
        {
            throw new ProfileUpdateValidationException(
                $"Git {operation} failed with exit code {result.ExitCode}.");
        }
    }
}
