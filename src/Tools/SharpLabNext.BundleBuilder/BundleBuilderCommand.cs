namespace SharpLabNext.BundleBuilder;

public sealed record BundleBuilderCommand(
    string RepositoryRoot,
    string CatalogPath,
    string LockPath,
    string DeploymentImagesPath,
    string LicensePolicyPath,
    string ComposePath,
    string NoticesPath,
    string OutputDirectory,
    string DockerCommand,
    string OpenSslCommand,
    string? SigningKeyPath,
    string? SigningPublicKeyPath,
    string? SigningKeyId,
    string? ImagePrefix,
    bool MetadataOnly,
    IReadOnlyDictionary<string, string> ImageOverrides,
    string? SourceRevision = null,
    bool AllowUncommittedSourceForDevelopment = false,
    string? ProfileUpdateStatusPath = null,
    string? RuntimeProfilesPath = null)
{
    public const string Usage =
        "Usage: SharpLabNext.BundleBuilder [--repository-root PATH] [--catalog PATH] [--lock PATH] " +
        "[--deployment-images PATH] [--license-policy PATH] [--compose PATH] [--notices PATH] [--output PATH] " +
        "[--docker COMMAND] [--openssl COMMAND] [--signing-key PATH --signing-public-key PATH] " +
        "[--signing-key-id ID] [--image-prefix PREFIX] [--image ID=REFERENCE] [--metadata-only] " +
        "[--source-revision REVISION] [--allow-uncommitted-source-for-development] " +
        "[--profile-update-status PATH] [--runtime-profiles PATH]";

    public static BundleBuilderCommand Parse(string[] args)
    {
        var repositoryRoot = FindRepositoryRoot();
        string? catalogPath = null;
        string? lockPath = null;
        string? deploymentImagesPath = null;
        string? licensePolicyPath = null;
        string? composePath = null;
        string? noticesPath = null;
        string? outputDirectory = null;
        var dockerCommand = "docker";
        var openSslCommand = "openssl";
        string? signingKeyPath = null;
        string? signingPublicKeyPath = null;
        string? signingKeyId = null;
        string? imagePrefix = null;
        var metadataOnly = false;
        string? sourceRevision = null;
        string? profileUpdateStatusPath = null;
        string? runtimeProfilesPath = null;
        var allowUncommittedSourceForDevelopment = false;
        var imageOverrides = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--repository-root":
                    repositoryRoot = Path.GetFullPath(RequiredValue(args, ref index));
                    break;
                case "--catalog":
                    catalogPath = Path.GetFullPath(RequiredValue(args, ref index));
                    break;
                case "--lock":
                    lockPath = Path.GetFullPath(RequiredValue(args, ref index));
                    break;
                case "--deployment-images":
                    deploymentImagesPath = Path.GetFullPath(RequiredValue(args, ref index));
                    break;
                case "--license-policy":
                    licensePolicyPath = Path.GetFullPath(RequiredValue(args, ref index));
                    break;
                case "--compose":
                    composePath = Path.GetFullPath(RequiredValue(args, ref index));
                    break;
                case "--notices":
                    noticesPath = Path.GetFullPath(RequiredValue(args, ref index));
                    break;
                case "--output":
                    outputDirectory = Path.GetFullPath(RequiredValue(args, ref index));
                    break;
                case "--docker":
                    dockerCommand = RequiredValue(args, ref index);
                    break;
                case "--openssl":
                    openSslCommand = RequiredValue(args, ref index);
                    break;
                case "--signing-key":
                    signingKeyPath = Path.GetFullPath(RequiredValue(args, ref index));
                    break;
                case "--signing-public-key":
                    signingPublicKeyPath = Path.GetFullPath(RequiredValue(args, ref index));
                    break;
                case "--signing-key-id":
                    signingKeyId = RequiredValue(args, ref index);
                    break;
                case "--image-prefix":
                    imagePrefix = RequiredValue(args, ref index);
                    break;
                case "--image":
                    AddImageOverride(imageOverrides, RequiredValue(args, ref index));
                    break;
                case "--metadata-only":
                    metadataOnly = true;
                    break;
                case "--source-revision":
                    sourceRevision = RequiredValue(args, ref index);
                    break;
                case "--allow-uncommitted-source-for-development":
                    allowUncommittedSourceForDevelopment = true;
                    break;
                case "--profile-update-status":
                    profileUpdateStatusPath = Path.GetFullPath(RequiredValue(args, ref index));
                    break;
                case "--runtime-profiles":
                    runtimeProfilesPath = Path.GetFullPath(RequiredValue(args, ref index));
                    break;
                case "--help" or "-h":
                    throw new BundleBuilderUsageException(Usage);
                default:
                    throw new BundleBuilderUsageException($"Unknown argument '{args[index]}'.");
            }
        }

        repositoryRoot = Path.GetFullPath(repositoryRoot);
        if (!File.Exists(Path.Combine(repositoryRoot, "SharpLabNext.slnx")))
        {
            throw new BundleBuilderUsageException("The repository root does not contain SharpLabNext.slnx.");
        }

        if ((signingKeyPath is null) != (signingPublicKeyPath is null))
        {
            throw new BundleBuilderUsageException(
                "--signing-key and --signing-public-key must be supplied together.");
        }
        if (signingKeyId is not null && signingKeyPath is null)
        {
            throw new BundleBuilderUsageException("--signing-key-id requires signing keys.");
        }
        if (signingKeyId is not null && !IsKeyId(signingKeyId))
        {
            throw new BundleBuilderUsageException("--signing-key-id contains unsupported characters.");
        }
        if (imagePrefix is not null)
        {
            imagePrefix = NormalizeImagePrefix(imagePrefix);
        }

        catalogPath ??= Path.Combine(repositoryRoot, "profiles", "catalog", "catalog.json");
        lockPath ??= Path.Combine(repositoryRoot, "profiles", "lock.json");
        deploymentImagesPath ??= Path.Combine(repositoryRoot, "deploy", "images.json");
        licensePolicyPath ??= Path.Combine(repositoryRoot, "profiles", "license-policy.json");
        composePath ??= Path.Combine(repositoryRoot, "deploy", "compose.prod.yaml");
        noticesPath ??= Path.Combine(repositoryRoot, "THIRD-PARTY-NOTICES.md");
        outputDirectory ??= Path.Combine(repositoryRoot, "artifacts", "bundles", "candidate");
        runtimeProfilesPath ??= Path.Combine(repositoryRoot, "profiles", "runtimes");

        return new BundleBuilderCommand(
            repositoryRoot,
            Path.GetFullPath(catalogPath),
            Path.GetFullPath(lockPath),
            Path.GetFullPath(deploymentImagesPath),
            Path.GetFullPath(licensePolicyPath),
            Path.GetFullPath(composePath),
            Path.GetFullPath(noticesPath),
            Path.GetFullPath(outputDirectory),
            dockerCommand,
            openSslCommand,
            signingKeyPath,
            signingPublicKeyPath,
            signingKeyId,
            imagePrefix,
            metadataOnly,
            imageOverrides,
            sourceRevision,
            allowUncommittedSourceForDevelopment,
            profileUpdateStatusPath,
            Path.GetFullPath(runtimeProfilesPath));
    }

    private static void AddImageOverride(Dictionary<string, string> overrides, string value)
    {
        var separator = value.IndexOf('=');
        if (separator <= 0 || separator == value.Length - 1)
        {
            throw new BundleBuilderUsageException("--image must use ID=REFERENCE.");
        }

        var id = value[..separator];
        var reference = value[(separator + 1)..];
        if (!IsId(id) || string.IsNullOrWhiteSpace(reference) || reference.Contains('\0'))
        {
            throw new BundleBuilderUsageException("--image contains an invalid ID or image reference.");
        }

        if (!overrides.TryAdd(id, reference))
        {
            throw new BundleBuilderUsageException($"Image '{id}' was overridden more than once.");
        }
    }

    private static bool IsId(string value) =>
        value.Length is > 0 and <= 128 &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static bool IsKeyId(string value) =>
        value.Length is > 0 and <= 160 &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');

    private static string NormalizeImagePrefix(string value)
    {
        var normalized = value.Trim().TrimEnd('/');
        if (normalized.Length is 0 or > 240 ||
            normalized.Any(static character => char.IsWhiteSpace(character) || character == '\0') ||
            normalized.StartsWith('/') || normalized.EndsWith(':'))
        {
            throw new BundleBuilderUsageException("--image-prefix is not a valid Docker repository prefix.");
        }

        return normalized;
    }

    private static string RequiredValue(string[] args, ref int index)
    {
        index++;
        return index < args.Length && !string.IsNullOrWhiteSpace(args[index])
            ? args[index]
            : throw new BundleBuilderUsageException("An option value is missing.");
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

        throw new BundleBuilderUsageException("SharpLabNext.slnx was not found above the current directory.");
    }
}
