#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property NuGetLockFilePath=obj/prepare-cppcli-toolchain.packages.lock.json

using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

return await CppCliToolchainPreparation.RunAsync(args);

internal static class CppCliToolchainPreparation
{
    private const string DockerfileRelativePath = "deploy/docker/Dockerfile.operator-cppcli-base";
    private const string ManifestRelativePath = "eng/release-prerequisites.json";
    private const string ContextName = "cppcli-prerequisite-context";
    private const string Usage =
        "Usage: dotnet run eng/tools/prepare-cppcli-toolchain.cs -- " +
        "--framework-seed-image REFERENCE --output-image REFERENCE " +
        "--msvc-wine-source PATH --visual-studio-manifest PATH " +
        "--netfx48-developer-pack PATH --operator-build-input-sha256 HEX " +
        "--accept-microsoft-cpp-build-tools-license --accept-microsoft-dotnet-eula " +
        "[--repository-root PATH] [--docker-command COMMAND] [--dry-run]";

    public static async Task<int> RunAsync(string[] args)
    {
        PreparationOptions options;
        try
        {
            options = Parse(args);
        }
        catch (UsageException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(Usage);
            return 64;
        }

        ValidatedInputs inputs;
        try
        {
            inputs = Validate(options);
        }
        catch (InputValidationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }

        try
        {
            var invocation = CreateDockerInvocation(options, inputs);
            if (options.DryRun)
            {
                Console.WriteLine(invocation.RenderRedacted());
                return 0;
            }
            return await ExecuteAsync(invocation, inputs);
        }
        catch (InputValidationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static PreparationOptions Parse(string[] args)
    {
        string? repositoryRoot = null;
        string? dockerCommand = null;
        string? frameworkSeedImage = null;
        string? outputImage = null;
        string? msvcWineSource = null;
        string? visualStudioManifest = null;
        string? developerPack = null;
        string? operatorBuildInputSha256 = null;
        var acceptCppBuildToolsLicense = false;
        var acceptDotNetEula = false;
        var dryRun = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (!seen.Add(option))
                throw new UsageException($"Option '{option}' was supplied more than once.");
            switch (option)
            {
                case "--repository-root":
                    repositoryRoot = RequiredValue(args, ref index, option);
                    break;
                case "--docker-command":
                    dockerCommand = RequiredValue(args, ref index, option);
                    break;
                case "--framework-seed-image":
                    frameworkSeedImage = RequiredValue(args, ref index, option);
                    break;
                case "--output-image":
                    outputImage = RequiredValue(args, ref index, option);
                    break;
                case "--msvc-wine-source":
                    msvcWineSource = RequiredValue(args, ref index, option);
                    break;
                case "--visual-studio-manifest":
                    visualStudioManifest = RequiredValue(args, ref index, option);
                    break;
                case "--netfx48-developer-pack":
                    developerPack = RequiredValue(args, ref index, option);
                    break;
                case "--operator-build-input-sha256":
                    operatorBuildInputSha256 = RequiredValue(args, ref index, option);
                    break;
                case "--accept-microsoft-cpp-build-tools-license":
                    acceptCppBuildToolsLicense = true;
                    break;
                case "--accept-microsoft-dotnet-eula":
                    acceptDotNetEula = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                default:
                    throw new UsageException($"Unknown argument '{option}'.");
            }
        }

        if (new[]
            {
                frameworkSeedImage,
                outputImage,
                msvcWineSource,
                visualStudioManifest,
                developerPack,
                operatorBuildInputSha256
            }.Any(string.IsNullOrWhiteSpace))
        {
            throw new UsageException("Framework seed, output image, all three source files, and " + "operator build input digest are required.");
        }
        if (!acceptCppBuildToolsLicense || !acceptDotNetEula)
        {
            throw new UsageException("Both --accept-microsoft-cpp-build-tools-license and " + "--accept-microsoft-dotnet-eula are required.");
        }

        return new PreparationOptions(repositoryRoot, dockerCommand ?? "docker", ValidateDigestReference(frameworkSeedImage!, "--framework-seed-image"), ValidateOutputImageReference(outputImage!), msvcWineSource!, visualStudioManifest!, developerPack!, NormalizeSha256(operatorBuildInputSha256!, "--operator-build-input-sha256"), dryRun);
    }

    private static ValidatedInputs Validate(PreparationOptions options)
    {
        var repositoryRoot = ResolveRepositoryRoot(options.RepositoryRoot);
        var dockerfile = Path.Combine(repositoryRoot, DockerfileRelativePath);
        RequireRegularFile(dockerfile, "The C++/CLI operator Dockerfile is missing or invalid.");
        var locks = ReadDownloadLocks(Path.Combine(repositoryRoot, ManifestRelativePath));
        return new ValidatedInputs(repositoryRoot, dockerfile, ValidateSource(options.MsvcWineSource, locks, "msvc-wine-source"), ValidateSource(options.VisualStudioManifest, locks, "visual-studio-manifest"), ValidateSource(options.DeveloperPack, locks, "netfx48-developer-pack"));
    }

    private static Dictionary<string, DownloadLock> ReadDownloadLocks(string manifestPath)
    {
        try
        {
            RequireRegularFile(manifestPath, "The release prerequisite manifest is missing or invalid.");
            using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 3)
            {
                throw new InputValidationException("The release prerequisite manifest schema is unsupported.");
            }
            var result = new Dictionary<string, DownloadLock>(StringComparer.Ordinal);
            foreach (var item in root.GetProperty("downloads").EnumerateArray())
            {
                var id = item.GetProperty("id").GetString();
                if (id is null || !new[]
                    {
                        "msvc-wine-source",
                        "visual-studio-manifest",
                        "netfx48-developer-pack"
                    }.Contains(id, StringComparer.Ordinal))
                {
                    continue;
                }
                if (!StringComparer.Ordinal.Equals(item.GetProperty("kind").GetString(), "file"))
                {
                    throw new InputValidationException($"Prerequisite lock '{id}' is invalid.");
                }
                var size = item.GetProperty("sizeBytes").GetInt64();
                var sha256 = item.GetProperty("sha256").GetString();
                if (size <= 0 || sha256 is null || sha256.Length != 64 || sha256.Any(static character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')) || !result.TryAdd(id, new DownloadLock(id, size, sha256)))
                {
                    throw new InputValidationException($"Prerequisite lock '{id}' is invalid.");
                }
            }
            if (result.Count != 3)
            {
                throw new InputValidationException("The release prerequisite manifest is missing C++/CLI source locks.");
            }
            return result;
        }
        catch (InputValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InputValidationException("The release prerequisite manifest could not be validated.");
        }
    }

    private static ValidatedSource ValidateSource(string configuredPath, IReadOnlyDictionary<string, DownloadLock> locks, string id)
    {
        try
        {
            var path = Path.GetFullPath(configuredPath);
            var info = new FileInfo(path);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InputValidationException($"Prerequisite '{id}' is missing or is not a regular file.");
            }
            var expected = locks[id];
            if (info.Length != expected.SizeBytes)
            {
                throw new InputValidationException($"Prerequisite '{id}' size or SHA-256 is invalid.");
            }
            using var stream = File.OpenRead(path);
            var digest = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (!StringComparer.Ordinal.Equals(digest, expected.Sha256))
            {
                throw new InputValidationException($"Prerequisite '{id}' size or SHA-256 is invalid.");
            }
            return new ValidatedSource(id, path, expected.SizeBytes, expected.Sha256);
        }
        catch (InputValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            throw new InputValidationException($"Prerequisite '{id}' could not be validated.");
        }
    }

    private static DockerInvocation CreateDockerInvocation(PreparationOptions options, ValidatedInputs inputs)
    {
        var contextDirectory = ContextDirectory(inputs);
        var arguments = new List<DockerArgument>
        {
            new("buildx"),
            new("build"),
            new("--file"),
            new(inputs.Dockerfile),
            new("--tag"),
            new(options.OutputImage),
            new("--load"),
            new("--provenance=false"),
            new("--build-arg"),
            new($"FRAMEWORK_SEED_IMAGE={options.FrameworkSeedImage}"),
            new("--build-arg"),
            new($"MSVC_WINE_SOURCE_SHA256={inputs.MsvcWineSource.Sha256}"),
            new("--build-arg"),
            new($"VISUAL_STUDIO_MANIFEST_SHA256=" + inputs.VisualStudioManifest.Sha256),
            new("--build-arg"),
            new($"NETFX48_DEVELOPER_PACK_SHA256=" + inputs.DeveloperPack.Sha256),
            new("--build-arg"),
            new($"OPERATOR_BUILD_INPUT_SHA256={options.OperatorBuildInputSha256}"),
            new("--build-arg"),
            new("ACCEPT_MICROSOFT_CPP_BUILD_TOOLS_LICENSE=true"),
            new("--build-arg"),
            new("ACCEPT_MICROSOFT_DOTNET_EULA=true"),
            new("--build-arg"),
            new($"MSVC_WINE_SOURCE_FILE={Path.GetFileName(inputs.MsvcWineSource.Path)}"),
            new("--build-arg"),
            new($"VISUAL_STUDIO_MANIFEST_FILE={Path.GetFileName(inputs.VisualStudioManifest.Path)}"),
            new("--build-arg"),
            new($"NETFX48_DEVELOPER_PACK_FILE={Path.GetFileName(inputs.DeveloperPack.Path)}"),
            new("--build-context"),
            new($"{ContextName}={contextDirectory}", $"{ContextName}=<direct-input-directory>"),
            new(inputs.RepositoryRoot)
        };
        return new DockerInvocation(options.DockerCommand, arguments);
    }

    private static string ContextDirectory(ValidatedInputs inputs)
    {
        var paths = new[]
        {
            inputs.MsvcWineSource.Path,
            inputs.VisualStudioManifest.Path,
            inputs.DeveloperPack.Path
        };
        var directory = Path.GetDirectoryName(paths[0]);
        if (string.IsNullOrEmpty(directory))
            throw new InputValidationException("The C++/CLI prerequisite directory is invalid.");
        directory = Path.GetFullPath(directory);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (paths.Any(path =>
        {
            var parent = Path.GetDirectoryName(path);
            return string.IsNullOrEmpty(parent) ||
                !String.Equals(Path.GetFullPath(parent), directory, comparison);
        }))
        {
            throw new InputValidationException("The C++/CLI prerequisite files must be in one directory " + "so Docker can map that directory directly.");
        }
        return directory;
    }

    private static async Task<int> ExecuteAsync(DockerInvocation invocation, ValidatedInputs inputs)
    {
        var startInfo = new ProcessStartInfo(invocation.Command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = inputs.RepositoryRoot
        };
        foreach (var argument in invocation.Arguments)
            startInfo.ArgumentList.Add(argument.Value);

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            Console.Error.WriteLine("Could not start Docker Buildx.");
            return 1;
        }
        if (process is null)
        {
            Console.Error.WriteLine("Could not start Docker Buildx.");
            return 1;
        }

        using (process)
        {
            var sensitive = new[]
            {
                inputs.MsvcWineSource.Path,
                inputs.VisualStudioManifest.Path,
                inputs.DeveloperPack.Path,
                ContextDirectory(inputs)
            };
            var standardOutput = ForwardRedactedAsync(process.StandardOutput, Console.Out, sensitive);
            var standardError = ForwardRedactedAsync(process.StandardError, Console.Error, sensitive);
            await process.WaitForExitAsync();
            await Task.WhenAll(standardOutput, standardError);
            if (process.ExitCode != 0)
            {
                Console.Error.WriteLine("Docker Buildx did not create the source-built C++/CLI base image.");
                return 1;
            }
        }
        return 0;
    }

    private static async Task ForwardRedactedAsync(StreamReader source, TextWriter destination, IReadOnlyList<string> sensitiveValues)
    {
        while (await source.ReadLineAsync() is { } line)
        {
            foreach (var value in sensitiveValues)
                line = line.Replace(value, "<redacted>", StringComparison.OrdinalIgnoreCase);
            await destination.WriteLineAsync(line);
        }
    }

    private static string ResolveRepositoryRoot(string? configured)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var root = Path.GetFullPath(configured);
                if (File.Exists(Path.Combine(root, "SharpLabNext.slnx")))
                    return root;
                throw new InputValidationException("The repository root is invalid.");
            }
            var directory = new DirectoryInfo(Environment.CurrentDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "SharpLabNext.slnx")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }
        catch (InputValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            throw new InputValidationException("The repository root is invalid.");
        }
        throw new InputValidationException("SharpLabNext.slnx was not found above the current directory.");
    }

    private static void RequireRegularFile(string path, string message)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InputValidationException(message);
        }
        catch (InputValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            throw new InputValidationException(message);
        }
    }

    private static string NormalizeSha256(string value, string option)
    {
        if (value.Length != 64 || value.Any(static character => !char.IsAsciiHexDigit(character)))
        {
            throw new UsageException($"{option} must contain exactly 64 hexadecimal characters.");
        }
        return value.ToLowerInvariant();
    }

    private static string ValidateDigestReference(string value, string option)
    {
        const string marker = "@sha256:";
        var separator = value.LastIndexOf(marker, StringComparison.Ordinal);
        if (value.Length > 512 || separator <= 0 || separator + marker.Length + 64 != value.Length || value[..separator].Any(static character => char.IsWhiteSpace(character) || char.IsControl(character) || character == '@') || value[(separator + marker.Length)..].Any(static character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new UsageException($"{option} must use repository[:tag]@sha256:<64 lowercase hex>.");
        }
        return value;
    }

    private static string ValidateOutputImageReference(string value)
    {
        if (value.Length > 512 || value.Contains('@') || !value.Contains(':', StringComparison.Ordinal) || value.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw new UsageException("--output-image must contain one bounded taggable Docker image reference.");
        }
        return value;
    }

    private static string RequiredValue(string[] args, ref int index, string option)
    {
        index++;
        if (index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            throw new UsageException($"{option} requires a non-empty value.");
        return args[index];
    }

    private sealed record PreparationOptions(
        string? RepositoryRoot,
        string DockerCommand,
        string FrameworkSeedImage,
        string OutputImage,
        string MsvcWineSource,
        string VisualStudioManifest,
        string DeveloperPack,
        string OperatorBuildInputSha256,
        bool DryRun);

    private sealed record DownloadLock(string Id, long SizeBytes, string Sha256);
    private sealed record ValidatedSource(string Id, string Path, long SizeBytes, string Sha256);
    private sealed record ValidatedInputs(string RepositoryRoot, string Dockerfile, ValidatedSource MsvcWineSource, ValidatedSource VisualStudioManifest, ValidatedSource DeveloperPack);

    private sealed record DockerArgument(string Value, string? RedactedValue = null);

    private sealed record DockerInvocation(string Command, IReadOnlyList<DockerArgument> Arguments)
    {
        public string RenderRedacted()
        {
            var builder = new StringBuilder(Quote(Command));
            foreach (var argument in Arguments)
            {
                builder.Append(' ');
                builder.Append(Quote(argument.RedactedValue ?? argument.Value));
            }
            return builder.ToString();
        }

        private static string Quote(string value)
        {
            if (value.Length > 0 && value.All(static character => !char.IsWhiteSpace(character) && character is not ('"' or '\\')))
            {
                return value;
            }
            return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
        }
    }

    private sealed class UsageException(string message) : Exception(message);
    private sealed class InputValidationException : Exception
    {
        public InputValidationException(string message) : base(message) { }

        public InputValidationException(string message, Exception? innerException) : base(message, innerException) { }
    }
}
