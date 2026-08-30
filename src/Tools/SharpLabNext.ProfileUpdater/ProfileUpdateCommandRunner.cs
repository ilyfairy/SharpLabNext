using System.Diagnostics;

namespace SharpLabNext.ProfileUpdater;

public sealed record ProfileUpdateExternalCommand(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string>? Environment = null, bool AlwaysRun = false);

public sealed record ProfileUpdateCommandResult(int ExitCode);

public interface IProfileUpdateCommandRunner
{
    Task<ProfileUpdateCommandResult> RunAsync(ProfileUpdateExternalCommand command, CancellationToken cancellationToken = default);
}

public sealed class ProcessProfileUpdateCommandRunner : IProfileUpdateCommandRunner
{
    public async Task<ProfileUpdateCommandResult> RunAsync(ProfileUpdateExternalCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var startInfo = new ProcessStartInfo { FileName = ResolveExecutable(command.FileName), WorkingDirectory = command.WorkingDirectory, UseShellExecute = false };
        foreach (var argument in command.Arguments)
            startInfo.ArgumentList.Add(argument);

        if (command.Environment is not null)
        {
            foreach (var pair in command.Environment)
                startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start external command '{command.FileName}'.");
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        return new ProfileUpdateCommandResult(process.ExitCode);
    }

    private static string ResolveExecutable(string fileName) => OperatingSystem.IsWindows() && string.Equals(fileName, "npm", StringComparison.OrdinalIgnoreCase) ? "npm.cmd" : fileName;
}
