#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property NuGetLockFilePath=obj/prepare-jsharp-toolchain.packages.lock.json

using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

return await JSharpToolchainPreparation.RunAsync(args);

internal static class JSharpToolchainPreparation
{
    private const string DockerfileRelativePath =
        "deploy/docker/Dockerfile.operator-jsharp20";
    private const string InstallerRelativePath =
        "eng/prerequisites/visual-jsharp-2.0-se-x64/vjredist64.exe";
    private const long InstallerSize = 6_110_048;
    private const string InstallerSha256 =
        "3a7a6ff79eeb5d51f8bf4cab188f74de0a220722e3d9d97858092ea3ef41b2b0";
    private const string InstallerContextName = "visual-jsharp-installer-context";
    private const string Usage =
        "Usage: dotnet run eng/prepare-jsharp-toolchain.cs -- " +
        "--framework-seed-image REFERENCE --output-image REFERENCE " +
        "--operator-build-input-sha256 HEX " +
        "--accept-microsoft-dotnet-eula --accept-microsoft-jsharp-eula " +
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

        var invocation = CreateDockerInvocation(options, inputs);
        if (options.DryRun)
        {
            Console.WriteLine(invocation.RenderRedacted());
            return 0;
        }
        return await ExecuteAsync(invocation, inputs);
    }

    private static PreparationOptions Parse(string[] args)
    {
        string? repositoryRoot = null;
        string? dockerCommand = null;
        string? frameworkSeedImage = null;
        string? outputImage = null;
        string? operatorBuildInputSha256 = null;
        var acceptDotNetEula = false;
        var acceptJSharpEula = false;
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
                case "--operator-build-input-sha256":
                    operatorBuildInputSha256 = RequiredValue(args, ref index, option);
                    break;
                case "--accept-microsoft-dotnet-eula":
                    acceptDotNetEula = true;
                    break;
                case "--accept-microsoft-jsharp-eula":
                    acceptJSharpEula = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                default:
                    throw new UsageException($"Unknown argument '{option}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(frameworkSeedImage) ||
            string.IsNullOrWhiteSpace(outputImage) ||
            string.IsNullOrWhiteSpace(operatorBuildInputSha256))
        {
            throw new UsageException(
                "Framework seed, output image, and operator build input digest are required.");
        }
        if (!acceptDotNetEula || !acceptJSharpEula)
        {
            throw new UsageException(
                "Both --accept-microsoft-dotnet-eula and " +
                "--accept-microsoft-jsharp-eula are required.");
        }

        return new PreparationOptions(
            repositoryRoot,
            dockerCommand ?? "docker",
            ValidateDigestReference(frameworkSeedImage, "--framework-seed-image"),
            ValidateOutputImageReference(outputImage),
            NormalizeSha256(
                operatorBuildInputSha256,
                "--operator-build-input-sha256"),
            dryRun);
    }

    private static ValidatedInputs Validate(PreparationOptions options)
    {
        var repositoryRoot = ResolveRepositoryRoot(options.RepositoryRoot);
        var dockerfile = Path.Combine(repositoryRoot, DockerfileRelativePath);
        RequireRegularFile(dockerfile, "The J# operator Dockerfile is missing or invalid.");

        var installer = Path.Combine(
            repositoryRoot,
            InstallerRelativePath.Replace('/', Path.DirectorySeparatorChar));
        FileInfo info;
        try
        {
            info = new FileInfo(installer);
            if (!info.Exists)
            {
                throw new InputValidationException(
                    "The Visual J# Git LFS object is missing. Run git lfs pull before building.");
            }
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InputValidationException(
                    "The Visual J# Git LFS input must be one regular non-link file.");
            }
            if (IsLfsPointer(installer, info.Length))
            {
                throw new InputValidationException(
                    "The Visual J# input is an unexpanded Git LFS pointer. " +
                    "Run git lfs pull before building.");
            }
            ValidateLfsAttribute(repositoryRoot);
            if (info.Length != InstallerSize)
            {
                throw new InputValidationException(
                    "The Visual J# Git LFS input size or SHA-256 is invalid.");
            }
            using var stream = File.OpenRead(installer);
            var digest = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (!StringComparer.Ordinal.Equals(digest, InstallerSha256))
            {
                throw new InputValidationException(
                    "The Visual J# Git LFS input size or SHA-256 is invalid.");
            }
        }
        catch (InputValidationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or
                NotSupportedException or UnauthorizedAccessException)
        {
            throw new InputValidationException(
                "The Visual J# Git LFS input could not be validated.");
        }

        var context = Path.GetDirectoryName(installer);
        if (string.IsNullOrEmpty(context))
            throw new InputValidationException("The Visual J# LFS context is invalid.");
        return new ValidatedInputs(repositoryRoot, dockerfile, installer, context);
    }

    private static bool IsLfsPointer(string path, long size)
    {
        if (size is < 1 or > 1024)
            return false;
        var text = File.ReadAllText(path);
        return text.StartsWith(
            "version https://git-lfs.github.com/spec/v1\n",
            StringComparison.Ordinal);
    }

    private static void ValidateLfsAttribute(string repositoryRoot)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[]
        {
            "check-attr",
            "filter",
            "--",
            InstallerRelativePath
        })
        {
            startInfo.ArgumentList.Add(argument);
        }
        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InputValidationException(
                    "Git could not validate the Visual J# LFS attribute.");
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0 ||
                !output.TrimEnd().EndsWith(": filter: lfs", StringComparison.Ordinal))
            {
                throw new InputValidationException(
                    "The Visual J# installer path is not covered by a Git LFS filter rule.");
            }
        }
        catch (InputValidationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            throw new InputValidationException(
                "Git could not validate the Visual J# LFS attribute.");
        }
    }

    private static DockerInvocation CreateDockerInvocation(
        PreparationOptions options,
        ValidatedInputs inputs)
    {
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
            new($"JSHARP_INSTALLER_SHA256={InstallerSha256}"),
            new("--build-arg"),
            new($"OPERATOR_BUILD_INPUT_SHA256={options.OperatorBuildInputSha256}"),
            new("--build-arg"),
            new("ACCEPT_MICROSOFT_DOTNET_EULA=true"),
            new("--build-arg"),
            new("ACCEPT_MICROSOFT_JSHARP_EULA=true"),
            new("--build-context"),
            new(
                $"{InstallerContextName}={inputs.InstallerContext}",
                $"{InstallerContextName}=<repository-lfs-context>"),
            new(inputs.RepositoryRoot)
        };
        return new DockerInvocation(options.DockerCommand, arguments);
    }

    private static async Task<int> ExecuteAsync(
        DockerInvocation invocation,
        ValidatedInputs inputs)
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
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
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
            var sensitive = new[] { inputs.Installer, inputs.InstallerContext };
            var standardOutput = ForwardRedactedAsync(
                process.StandardOutput,
                Console.Out,
                sensitive);
            var standardError = ForwardRedactedAsync(
                process.StandardError,
                Console.Error,
                sensitive);
            await process.WaitForExitAsync();
            await Task.WhenAll(standardOutput, standardError);
            if (process.ExitCode != 0)
            {
                Console.Error.WriteLine(
                    "Docker Buildx did not create the source-built J# operator image.");
                return 1;
            }
        }
        return 0;
    }

    private static async Task ForwardRedactedAsync(
        StreamReader source,
        TextWriter destination,
        IReadOnlyList<string> sensitiveValues)
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
        catch (Exception exception) when (
            exception is ArgumentException or IOException or
                NotSupportedException or UnauthorizedAccessException)
        {
            throw new InputValidationException("The repository root is invalid.");
        }
        throw new InputValidationException(
            "SharpLabNext.slnx was not found above the current directory.");
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
        catch (Exception exception) when (
            exception is ArgumentException or IOException or
                NotSupportedException or UnauthorizedAccessException)
        {
            throw new InputValidationException(message);
        }
    }

    private static string NormalizeSha256(string value, string option)
    {
        if (value.Length != 64 ||
            value.Any(static character => !char.IsAsciiHexDigit(character)))
        {
            throw new UsageException(
                $"{option} must contain exactly 64 hexadecimal characters.");
        }
        return value.ToLowerInvariant();
    }

    private static string ValidateDigestReference(string value, string option)
    {
        const string marker = "@sha256:";
        var separator = value.LastIndexOf(marker, StringComparison.Ordinal);
        if (value.Length > 512 || separator <= 0 ||
            separator + marker.Length + 64 != value.Length ||
            value[..separator].Any(static character =>
                char.IsWhiteSpace(character) ||
                char.IsControl(character) ||
                character == '@') ||
            value[(separator + marker.Length)..].Any(static character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new UsageException(
                $"{option} must use repository[:tag]@sha256:<64 lowercase hex>.");
        }
        return value;
    }

    private static string ValidateOutputImageReference(string value)
    {
        if (value.Length > 512 || value.Contains('@') ||
            !value.Contains(':', StringComparison.Ordinal) ||
            value.Any(static character =>
                char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw new UsageException(
                "--output-image must contain one bounded taggable Docker image reference.");
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
        string OperatorBuildInputSha256,
        bool DryRun);

    private sealed record ValidatedInputs(
        string RepositoryRoot,
        string Dockerfile,
        string Installer,
        string InstallerContext);

    private sealed record DockerArgument(string Value, string? RedactedValue = null);

    private sealed record DockerInvocation(
        string Command,
        IReadOnlyList<DockerArgument> Arguments)
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
            if (value.Length > 0 && value.All(static character =>
                    !char.IsWhiteSpace(character) &&
                    character is not ('"' or '\\')))
            {
                return value;
            }
            return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
        }
    }

    private sealed class UsageException(string message) : Exception(message);
    private sealed class InputValidationException(string message) : Exception(message);
}
