using SharpLabNext.Contracts;

namespace SharpLabNext.CompatibilityCli;

public enum CompatibilityCommandKind
{
    Validate,
    Resolve
}

public enum CompatibilityOutputFormat
{
    Json,
    Markdown
}

public sealed record CompatibilityCommand(
    CompatibilityCommandKind Kind,
    string CatalogPath,
    string LockPath,
    CompatibilityOutputFormat Format,
    string? OutputPath,
    string? LanguageId,
    string? ToolchainId,
    string? ReferenceSetId,
    string? OutputId,
    string? RuntimeId,
    BuildConfiguration BuildMode)
{
    public const string Usage = """
        Usage:
          SharpLabNext.CompatibilityCli validate [--catalog PATH] [--lock PATH] [--format json|markdown] [--output PATH]
          SharpLabNext.CompatibilityCli resolve --language ID --output ID [--toolchain ID] [--reference-set ID] [--runtime ID] [--mode debug|release] [--catalog PATH] [--lock PATH]
        """;

    public static CompatibilityCommand Parse(string[] args)
    {
        var repositoryRoot = FindRepositoryRoot();
        var kind = CompatibilityCommandKind.Validate;
        var index = 0;
        if (args.Length > 0 && args[0] is "validate" or "resolve")
        {
            kind = args[0] == "resolve" ? CompatibilityCommandKind.Resolve : CompatibilityCommandKind.Validate;
            index = 1;
        }

        var catalogPath = Path.Combine(repositoryRoot, "profiles", "catalog", "catalog.json");
        var lockPath = Path.Combine(repositoryRoot, "profiles", "lock.json");
        var format = CompatibilityOutputFormat.Json;
        string? outputPath = null;
        string? languageId = null;
        string? toolchainId = null;
        string? referenceSetId = null;
        string? outputId = null;
        string? runtimeId = null;
        var mode = BuildConfiguration.Release;
        for (; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--catalog":
                    catalogPath = Path.GetFullPath(RequiredValue(args, ref index));
                    break;
                case "--lock":
                    lockPath = Path.GetFullPath(RequiredValue(args, ref index));
                    break;
                case "--format":
                    format = RequiredValue(args, ref index) switch
                    {
                        "json" => CompatibilityOutputFormat.Json,
                        "markdown" => CompatibilityOutputFormat.Markdown,
                        var value => throw new CompatibilityUsageException($"Unknown output format '{value}'.")
                    };
                    break;
                case "--output":
                    outputPath = Path.GetFullPath(RequiredValue(args, ref index));
                    break;
                case "--language":
                    languageId = RequiredValue(args, ref index);
                    break;
                case "--toolchain":
                    toolchainId = RequiredValue(args, ref index);
                    break;
                case "--reference-set":
                    referenceSetId = RequiredValue(args, ref index);
                    break;
                case "--runtime":
                    runtimeId = RequiredValue(args, ref index);
                    break;
                case "--mode":
                    mode = RequiredValue(args, ref index) switch
                    {
                        "debug" => BuildConfiguration.Debug,
                        "release" => BuildConfiguration.Release,
                        var value => throw new CompatibilityUsageException($"Unknown build mode '{value}'.")
                    };
                    break;
                case "--help" or "-h":
                    throw new CompatibilityUsageException(Usage);
                default:
                    throw new CompatibilityUsageException($"Unknown argument '{args[index]}'.");
            }
        }

        if (kind == CompatibilityCommandKind.Resolve && (string.IsNullOrWhiteSpace(languageId) || string.IsNullOrWhiteSpace(outputId)))
        {
            throw new CompatibilityUsageException("resolve requires --language and --output.");
        }

        return new CompatibilityCommand(kind, Path.GetFullPath(catalogPath), Path.GetFullPath(lockPath), format, outputPath, languageId, toolchainId, referenceSetId, outputId, runtimeId, mode);
    }

    private static string RequiredValue(string[] args, ref int index)
    {
        index++;
        return index < args.Length && !string.IsNullOrWhiteSpace(args[index])
            ? args[index] : throw new CompatibilityUsageException("An option value is missing.");
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

        throw new CompatibilityUsageException("SharpLabNext.slnx was not found above the current directory.");
    }
}

public sealed class CompatibilityUsageException(string message) : Exception(message);
