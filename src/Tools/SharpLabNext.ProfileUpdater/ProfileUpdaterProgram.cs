using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpLabNext.ProfileUpdater;

public static class ProfileUpdaterProgram
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Any(static argument => argument is "--help" or "-h"))
        {
            Console.WriteLine(ProfileUpdaterCommand.Usage);
            return 0;
        }

        try
        {
            var command = ProfileUpdaterCommand.Parse(args);
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(command.HttpTimeoutSeconds) };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SharpLabNext-ProfileUpdater/2.0");
            var workflow = new ProfileUpdateWorkflow(command.RepositoryRoot, command.LockPath, command.StateRoot, new OfficialProfileSourceClient(httpClient), new ProcessProfileUpdateCommandRunner());

            object result;
            var exitCode = 0;
            switch (command.Kind)
            {
                case ProfileUpdaterCommandKind.Check:
                    {
                        var check = await workflow.CheckAsync(command.ReleaseId);
                        result = check;
                        if (command.FailOnChange && check.Changed)
                        {
                            exitCode = 2;
                        }

                        break;
                    }
                case ProfileUpdaterCommandKind.Resolve:
                    {
                        var candidate = await workflow.ResolveAsync(command.ReleaseId, command.OutputPath);
                        result = candidate;
                        if (command.FailOnChange && candidate.Receipt.Changes.Count > 0)
                        {
                            exitCode = 2;
                        }

                        break;
                    }
                case ProfileUpdaterCommandKind.Build:
                    result = await workflow.BuildAsync(command.CandidatePath, command.CandidateDigest, command.Configuration);
                    break;
                case ProfileUpdaterCommandKind.Test:
                    result = await workflow.TestAsync(command.CandidatePath, command.CandidateDigest, command.Configuration, command.TestScope);
                    break;
                case ProfileUpdaterCommandKind.Promote:
                    result = await workflow.PromoteAsync(command.CandidatePath, command.CandidateDigest);
                    break;
                case ProfileUpdaterCommandKind.Pipeline:
                    {
                        var candidate = await workflow.ResolveAsync(command.ReleaseId, command.OutputPath);
                        await workflow.BuildAsync(null, candidate.CandidateDigest, command.Configuration);
                        await workflow.TestAsync(null, candidate.CandidateDigest, command.Configuration, ProfileUpdateTestScope.Full);
                        result = await workflow.PromoteAsync(null, candidate.CandidateDigest);
                        break;
                    }
                default:
                    throw new InvalidOperationException($"Unsupported profile updater command '{command.Kind}'.");
            }

            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return exitCode;
        }
        catch (ProfileUpdaterUsageException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(ProfileUpdaterCommand.Usage);
            return 64;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Profile update failed: {exception.Message}");
            return 1;
        }
    }
}

public enum ProfileUpdaterCommandKind
{
    Check,
    Resolve,
    Build,
    Test,
    Promote,
    Pipeline
}

public sealed record ProfileUpdaterCommand(
    ProfileUpdaterCommandKind Kind,
    string RepositoryRoot,
    string LockPath,
    string StateRoot,
    string? OutputPath,
    string? CandidatePath,
    string? CandidateDigest,
    string? ReleaseId,
    bool FailOnChange,
    int HttpTimeoutSeconds,
    string Configuration,
    ProfileUpdateTestScope TestScope)
{
    public const string Usage = """
        Usage:
          SharpLabNext.ProfileUpdater check [--release-id ID] [--fail-on-change] [common options]
          SharpLabNext.ProfileUpdater resolve [--release-id ID] [--output PATH] [common options]
          SharpLabNext.ProfileUpdater build [--candidate PATH | --candidate-digest SHA256] [--configuration Debug|Release] [common options]
          SharpLabNext.ProfileUpdater test [--candidate PATH | --candidate-digest SHA256] [--test-scope affected|full] [--configuration Debug|Release] [common options]
          SharpLabNext.ProfileUpdater promote [--candidate PATH | --candidate-digest SHA256] [common options]

        Common options: --lock PATH --state-root PATH --http-timeout SECONDS

        Compatibility: omitting a command is the same as resolve. The legacy --apply option runs
        resolve, build, full test, and promote; it never bypasses the promotion gates. The command
        names are also accepted as --check, --resolve, --build, --test, and --promote.
        """;

    public static ProfileUpdaterCommand Parse(string[] args)
    {
        var repositoryRoot = FindRepositoryRoot();
        var lockPath = Path.Combine(repositoryRoot, "profiles", "lock.json");
        var stateRoot = Path.Combine(repositoryRoot, "artifacts", "profile-updater");
        string? outputPath = null;
        string? candidatePath = null;
        string? candidateDigest = null;
        string? releaseId = null;
        var failOnChange = false;
        var apply = false;
        var timeout = 60;
        var configuration = "Release";
        var testScope = ProfileUpdateTestScope.Full;
        var kind = ProfileUpdaterCommandKind.Resolve;
        var commandWasExplicit = false;
        var index = 0;
        if (args.Length > 0 && TryParseKind(args[0], out var initialKind))
        {
            kind = initialKind;
            commandWasExplicit = true;
            index = 1;
        }

        for (; index < args.Length; index++)
        {
            if (TryParseKind(args[index], out var optionKind))
            {
                if (commandWasExplicit)
                {
                    throw new ProfileUpdaterUsageException("Only one updater command may be specified.");
                }

                kind = optionKind;
                commandWasExplicit = true;
                continue;
            }

            switch (args[index])
            {
                case "--lock":
                    lockPath = Path.GetFullPath(RequiredValue(args, ref index));
                    break;
                case "--state-root":
                    stateRoot = Path.GetFullPath(RequiredValue(args, ref index));
                    break;
                case "--output":
                    outputPath = Path.GetFullPath(RequiredValue(args, ref index));
                    break;
                case "--candidate":
                    candidatePath = Path.GetFullPath(RequiredValue(args, ref index));
                    break;
                case "--candidate-digest":
                    candidateDigest = RequiredValue(args, ref index);
                    break;
                case "--release-id":
                    releaseId = RequiredValue(args, ref index);
                    break;
                case "--apply":
                    apply = true;
                    break;
                case "--fail-on-change":
                    failOnChange = true;
                    break;
                case "--http-timeout":
                    if (!int.TryParse(RequiredValue(args, ref index), out timeout) || timeout is < 5 or > 600)
                    {
                        throw new ProfileUpdaterUsageException("HTTP timeout must be between 5 and 600 seconds.");
                    }

                    break;
                case "--configuration":
                    configuration = RequiredValue(args, ref index) switch
                    {
                        "Debug" or "debug" => "Debug",
                        "Release" or "release" => "Release",
                        var value => throw new ProfileUpdaterUsageException($"Unknown configuration '{value}'; expected Debug or Release.")
                    };
                    break;
                case "--test-scope":
                    testScope = RequiredValue(args, ref index) switch
                    {
                        "affected" => ProfileUpdateTestScope.Affected,
                        "full" => ProfileUpdateTestScope.Full,
                        var value => throw new ProfileUpdaterUsageException($"Unknown test scope '{value}'; expected affected or full.")
                    };
                    break;
                default:
                    throw new ProfileUpdaterUsageException($"Unknown argument '{args[index]}'.");
            }
        }

        if (candidatePath is not null && candidateDigest is not null)
        {
            throw new ProfileUpdaterUsageException("--candidate and --candidate-digest are mutually exclusive.");
        }

        if (apply)
        {
            if (commandWasExplicit)
            {
                throw new ProfileUpdaterUsageException("--apply is only supported by the legacy command-less invocation.");
            }

            kind = ProfileUpdaterCommandKind.Pipeline;
        }

        if (outputPath is not null && kind is not (ProfileUpdaterCommandKind.Resolve or ProfileUpdaterCommandKind.Pipeline))
        {
            throw new ProfileUpdaterUsageException("--output is only valid for resolve.");
        }

        if (failOnChange && kind is not (ProfileUpdaterCommandKind.Check or ProfileUpdaterCommandKind.Resolve))
        {
            throw new ProfileUpdaterUsageException("--fail-on-change is only valid for check or resolve.");
        }

        return new ProfileUpdaterCommand(kind, repositoryRoot, Path.GetFullPath(lockPath), Path.GetFullPath(stateRoot), outputPath, candidatePath, candidateDigest, releaseId, failOnChange, timeout, configuration, testScope);
    }

    private static bool TryParseKind(string value, out ProfileUpdaterCommandKind kind)
    {
        kind = value switch
        {
            "check" or "--check" => ProfileUpdaterCommandKind.Check,
            "resolve" or "--resolve" => ProfileUpdaterCommandKind.Resolve,
            "build" or "--build" => ProfileUpdaterCommandKind.Build,
            "test" or "--test" => ProfileUpdaterCommandKind.Test,
            "promote" or "--promote" => ProfileUpdaterCommandKind.Promote,
            _ => default
        };
        return value is "check" or "--check" or "resolve" or "--resolve" or "build" or "--build" or
            "test" or "--test" or "promote" or "--promote";
    }

    private static string RequiredValue(string[] args, ref int index)
    {
        index++;
        return index < args.Length && !string.IsNullOrWhiteSpace(args[index])
            ? args[index] : throw new ProfileUpdaterUsageException("An option value is missing.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SharpLabNext.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new ProfileUpdaterUsageException("SharpLabNext.slnx was not found above the current directory.");
    }
}

public sealed class ProfileUpdaterUsageException(string message) : Exception(message);

internal static class AtomicFile
{
    public static async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("The output path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content.ToArray(), cancellationToken);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static async Task ReplaceSetAsync(IReadOnlyList<(string Path, ReadOnlyMemory<byte> Content)> replacements, CancellationToken cancellationToken)
    {
        var entries = new List<ReplacementEntry>(replacements.Count);
        try
        {
            foreach (var replacement in replacements)
            {
                var path = Path.GetFullPath(replacement.Path);
                var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("The replacement path has no parent directory.");
                Directory.CreateDirectory(directory);
                var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
                var backup = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.bak");
                await File.WriteAllBytesAsync(temporary, replacement.Content.ToArray(), cancellationToken);
                entries.Add(new ReplacementEntry(path, temporary, backup, File.Exists(path)));
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Existed)
                    File.Move(entry.Path, entry.Backup);
                File.Move(entry.Temporary, entry.Path);
                entry.Applied = true;
            }

            foreach (var entry in entries.Where(static entry => entry.Existed))
                File.Delete(entry.Backup);
        }
        catch
        {
            foreach (var entry in entries.AsEnumerable().Reverse())
            {
                if (entry.Applied && File.Exists(entry.Path))
                    File.Delete(entry.Path);
                if (entry.Existed && File.Exists(entry.Backup))
                    File.Move(entry.Backup, entry.Path, overwrite: true);
            }
            throw;
        }
        finally
        {
            foreach (var entry in entries)
            {
                if (File.Exists(entry.Temporary))
                    File.Delete(entry.Temporary);
                if (File.Exists(entry.Backup))
                    File.Delete(entry.Backup);
            }
        }
    }

    private sealed class ReplacementEntry(string path, string temporary, string backup, bool existed)
    {
        public string Path { get; } = path;
        public string Temporary { get; } = temporary;
        public string Backup { get; } = backup;
        public bool Existed { get; } = existed;
        public bool Applied { get; set; }
    }
}
