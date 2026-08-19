#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property NuGetLockFilePath=obj/prepare-jsharp-toolchain.packages.lock.json
#:property AllowUnsafeBlocks=true

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

return await JSharpToolchainPreparation.RunAsync(args);

internal static partial class JSharpToolchainPreparation
{
    private const string DockerfileRelativePath = "deploy/docker/Dockerfile.operator-jsharp20";
    private const string Usage =
        "Usage: dotnet run eng/prepare-jsharp-toolchain.cs -- " +
        "--base-image REFERENCE --output-image REFERENCE " +
        "(--clr2-url-secret-file PATH | --clr2-installer-secret-file PATH) --clr2-sha256 HEX " +
        "(--jsharp-url-secret-file PATH | --jsharp-installer-secret-file PATH) --jsharp-sha256 HEX " +
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

        try
        {
            using var stagedContexts = StagedBuildContexts.Create(inputs);
            var invocation = CreateDockerInvocation(options, inputs, stagedContexts);
            if (options.DryRun)
            {
                Console.WriteLine(invocation.RenderRedacted());
                return 0;
            }

            return await ExecuteAsync(invocation, inputs, stagedContexts);
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
        string? baseImage = null;
        string? outputImage = null;
        string? clr2UrlSecretFile = null;
        string? clr2InstallerSecretFile = null;
        string? clr2Sha256 = null;
        string? jsharpUrlSecretFile = null;
        string? jsharpInstallerSecretFile = null;
        string? jsharpSha256 = null;
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
                case "--base-image":
                    baseImage = RequiredValue(args, ref index, option);
                    break;
                case "--output-image":
                    outputImage = RequiredValue(args, ref index, option);
                    break;
                case "--clr2-url-secret-file":
                    clr2UrlSecretFile = RequiredValue(args, ref index, option);
                    break;
                case "--clr2-installer-secret-file":
                    clr2InstallerSecretFile = RequiredValue(args, ref index, option);
                    break;
                case "--clr2-sha256":
                    clr2Sha256 = RequiredValue(args, ref index, option);
                    break;
                case "--jsharp-url-secret-file":
                    jsharpUrlSecretFile = RequiredValue(args, ref index, option);
                    break;
                case "--jsharp-installer-secret-file":
                    jsharpInstallerSecretFile = RequiredValue(args, ref index, option);
                    break;
                case "--jsharp-sha256":
                    jsharpSha256 = RequiredValue(args, ref index, option);
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

        if (string.IsNullOrWhiteSpace(baseImage) ||
            string.IsNullOrWhiteSpace(outputImage) ||
            string.IsNullOrWhiteSpace(clr2Sha256) ||
            string.IsNullOrWhiteSpace(jsharpSha256))
        {
            throw new UsageException(
                "Base/output images and both operator asset SHA-256 digests are required.");
        }
        if (!acceptDotNetEula || !acceptJSharpEula)
        {
            throw new UsageException(
                "Both --accept-microsoft-dotnet-eula and --accept-microsoft-jsharp-eula are required.");
        }

        return new PreparationOptions(
            repositoryRoot,
            dockerCommand ?? "docker",
            ValidateBaseImageReference(baseImage),
            ValidateOutputImageReference(outputImage),
            AssetSourceOptions.Create(
                "CLR2",
                clr2UrlSecretFile,
                clr2InstallerSecretFile,
                "--clr2-url-secret-file",
                "--clr2-installer-secret-file"),
            NormalizeSha256(clr2Sha256, "--clr2-sha256"),
            AssetSourceOptions.Create(
                "Visual J# x64",
                jsharpUrlSecretFile,
                jsharpInstallerSecretFile,
                "--jsharp-url-secret-file",
                "--jsharp-installer-secret-file"),
            NormalizeSha256(jsharpSha256, "--jsharp-sha256"),
            dryRun);
    }

    private static ValidatedInputs Validate(PreparationOptions options)
    {
        var repositoryRoot = ResolveRepositoryRoot(options.RepositoryRoot);
        var clr2Source = ValidateAssetSource(
            options.Clr2Source,
            options.Clr2Sha256,
            "dotnet-clr2-url",
            "dotnet-clr2-installer-context");
        var jsharpSource = ValidateAssetSource(
            options.JSharpSource,
            options.JSharpSha256,
            "visual-jsharp-url",
            "visual-jsharp-installer-context");
        return new ValidatedInputs(repositoryRoot, clr2Source, jsharpSource);
    }

    private static DockerInvocation CreateDockerInvocation(
        PreparationOptions options,
        ValidatedInputs inputs,
        StagedBuildContexts stagedContexts)
    {
        var dockerfile = Path.Combine(inputs.RepositoryRoot, DockerfileRelativePath);
        var arguments = new List<DockerArgument>
        {
            new("buildx"),
            new("build"),
            new("--file"),
            new(dockerfile),
            new("--tag"),
            new(options.OutputImage),
            new("--load"),
            new("--provenance=false"),
            new("--build-arg"),
            new($"BASE_IMAGE={options.BaseImage}"),
            new("--build-arg"),
            new($"CLR2_INSTALLER_SHA256={options.Clr2Sha256}"),
            new("--build-arg"),
            new($"JSHARP_INSTALLER_SHA256={options.JSharpSha256}"),
            new("--build-arg"),
            new("ACCEPT_MICROSOFT_DOTNET_EULA=true"),
            new("--build-arg"),
            new("ACCEPT_MICROSOFT_JSHARP_EULA=true"),
        };
        AddSourceArguments(arguments, inputs.Clr2Source, stagedContexts.Clr2Context);
        AddSourceArguments(arguments, inputs.JSharpSource, stagedContexts.JSharpContext);
        arguments.Add(new(inputs.RepositoryRoot));
        return new DockerInvocation(options.DockerCommand, arguments);
    }

    private static void AddSourceArguments(
        List<DockerArgument> arguments,
        ValidatedAssetSource source,
        StagedBuildContext context)
    {
        if (source.Kind == AssetSourceKind.Url)
        {
            arguments.Add(new("--secret"));
            arguments.Add(new($"id={source.DockerSourceId},src={source.Path}"));
        }
        arguments.Add(new("--build-context"));
        arguments.Add(new(
            $"{context.Name}={context.Directory}",
            $"{context.Name}=<staged-local-context>"));
    }

    private static async Task<int> ExecuteAsync(
        DockerInvocation invocation,
        ValidatedInputs inputs,
        StagedBuildContexts stagedContexts)
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
            var sensitiveValues = new[]
            {
                inputs.Clr2Source.Path,
                inputs.Clr2Source.SensitiveContent,
                inputs.JSharpSource.Path,
                inputs.JSharpSource.SensitiveContent,
                stagedContexts.Clr2Context.Directory,
                stagedContexts.JSharpContext.Directory
            }.Where(static value => !string.IsNullOrEmpty(value)).Select(static value => value!).ToArray();
            var standardOutput = ForwardRedactedAsync(process.StandardOutput, Console.Out, sensitiveValues);
            var standardError = ForwardRedactedAsync(process.StandardError, Console.Error, sensitiveValues);
            await process.WaitForExitAsync();
            await Task.WhenAll(standardOutput, standardError);
            if (process.ExitCode != 0)
            {
                Console.Error.WriteLine("Docker Buildx did not create the operator J# toolchain image.");
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
            foreach (var sensitiveValue in sensitiveValues)
                line = line.Replace(sensitiveValue, "<redacted>", StringComparison.Ordinal);
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
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            throw new InputValidationException("The repository root is invalid.");
        }

        throw new InputValidationException("SharpLabNext.slnx was not found above the current directory.");
    }

    private static ValidatedAssetSource ValidateAssetSource(
        AssetSourceOptions source,
        string expectedSha256,
        string urlSecretId,
        string installerContextName)
    {
        return source.Kind switch
        {
            AssetSourceKind.Url => ReadUrlSecret(
                source.Path,
                source.AssetName,
                urlSecretId,
                installerContextName),
            AssetSourceKind.Installer => ReadInstaller(
                source.Path,
                source.AssetName,
                installerContextName,
                expectedSha256),
            _ => throw new InvalidOperationException("Unsupported operator asset source kind.")
        };
    }

    private static ValidatedAssetSource ReadUrlSecret(
        string configuredPath,
        string assetName,
        string secretId,
        string installerContextName)
    {
        string path;
        string content;
        try
        {
            path = Path.GetFullPath(configuredPath);
            if (!File.Exists(path))
                throw new InputValidationException($"The {assetName} URL secret file does not exist.");
            content = File.ReadAllText(path).Trim();
        }
        catch (InputValidationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            throw new InputValidationException($"The {assetName} URL secret file could not be read.");
        }

        if (string.IsNullOrWhiteSpace(content) ||
            content.Any(char.IsWhiteSpace) ||
            !Uri.TryCreate(content, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new InputValidationException(
                $"The {assetName} URL secret file must contain one absolute HTTP(S) URL.");
        }

        return new ValidatedAssetSource(
            AssetSourceKind.Url,
            path,
            secretId,
            installerContextName,
            content);
    }

    private static ValidatedAssetSource ReadInstaller(
        string configuredPath,
        string assetName,
        string installerContextName,
        string expectedSha256)
    {
        string path;
        try
        {
            path = Path.GetFullPath(configuredPath);
            if (!File.Exists(path))
                throw new InputValidationException($"The {assetName} installer secret file does not exist.");
            var info = new FileInfo(path);
            if (info.Length <= 0)
                throw new InputValidationException($"The {assetName} installer secret file is empty.");
            using var stream = File.OpenRead(path);
            var actualSha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (!StringComparer.Ordinal.Equals(actualSha256, expectedSha256))
            {
                throw new InputValidationException(
                    $"The {assetName} installer secret file does not match its required SHA-256 digest.");
            }
        }
        catch (InputValidationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            throw new InputValidationException($"The {assetName} installer secret file could not be read.");
        }

        return new ValidatedAssetSource(
            AssetSourceKind.Installer,
            path,
            installerContextName,
            installerContextName,
            SensitiveContent: null);
    }

    private static string NormalizeSha256(string value, string option)
    {
        if (value.Length != 64 || value.Any(static character => !char.IsAsciiHexDigit(character)))
            throw new UsageException($"{option} must contain exactly 64 hexadecimal characters.");
        return value.ToLowerInvariant();
    }

    private static string ValidateBaseImageReference(string value)
    {
        const string digestMarker = "@sha256:";
        var separator = value.LastIndexOf(digestMarker, StringComparison.Ordinal);
        if (value.Length > 512 ||
            separator <= 0 ||
            separator + digestMarker.Length + 64 != value.Length ||
            value[..separator].Any(static character =>
                char.IsWhiteSpace(character) || char.IsControl(character) || character == '@') ||
            value[(separator + digestMarker.Length)..].Any(static character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new UsageException(
                "--base-image must use repository[:tag]@sha256:<64 lowercase hex>.");
        }
        return value;
    }

    private static string ValidateOutputImageReference(string value)
    {
        if (value.Length > 512 ||
            value.Contains('@') ||
            value.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character)))
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
        string BaseImage,
        string OutputImage,
        AssetSourceOptions Clr2Source,
        string Clr2Sha256,
        AssetSourceOptions JSharpSource,
        string JSharpSha256,
        bool DryRun);

    private sealed record ValidatedInputs(
        string RepositoryRoot,
        ValidatedAssetSource Clr2Source,
        ValidatedAssetSource JSharpSource);

    private enum AssetSourceKind
    {
        Url,
        Installer
    }

    private sealed record AssetSourceOptions(string AssetName, AssetSourceKind Kind, string Path)
    {
        public static AssetSourceOptions Create(
            string assetName,
            string? urlSecretFile,
            string? installerSecretFile,
            string urlOption,
            string installerOption)
        {
            var hasUrl = !string.IsNullOrWhiteSpace(urlSecretFile);
            var hasInstaller = !string.IsNullOrWhiteSpace(installerSecretFile);
            if (hasUrl == hasInstaller)
            {
                throw new UsageException(
                    $"{assetName} requires exactly one of {urlOption} or {installerOption}.");
            }
            return hasUrl
                ? new AssetSourceOptions(assetName, AssetSourceKind.Url, urlSecretFile!)
                : new AssetSourceOptions(assetName, AssetSourceKind.Installer, installerSecretFile!);
        }
    }

    private sealed record ValidatedAssetSource(
        AssetSourceKind Kind,
        string Path,
        string DockerSourceId,
        string InstallerContextName,
        string? SensitiveContent);

    private sealed class StagedBuildContexts : IDisposable
    {
        private StagedBuildContexts(StagedBuildContext clr2Context, StagedBuildContext jsharpContext)
        {
            Clr2Context = clr2Context;
            JSharpContext = jsharpContext;
        }

        public StagedBuildContext Clr2Context { get; }
        public StagedBuildContext JSharpContext { get; }

        public static StagedBuildContexts Create(ValidatedInputs inputs)
        {
            StagedBuildContext? clr2Context = null;
            try
            {
                clr2Context = StagedBuildContext.Create(inputs.Clr2Source);
                var jsharpContext = StagedBuildContext.Create(inputs.JSharpSource);
                return new StagedBuildContexts(clr2Context, jsharpContext);
            }
            catch
            {
                clr2Context?.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            InputValidationException? cleanupFailure = null;
            try
            {
                JSharpContext.Dispose();
            }
            catch (InputValidationException exception)
            {
                cleanupFailure = exception;
            }
            try
            {
                Clr2Context.Dispose();
            }
            catch (InputValidationException exception)
            {
                cleanupFailure ??= exception;
            }
            if (cleanupFailure is not null)
                throw cleanupFailure;
        }
    }

    private sealed partial class StagedBuildContext : IDisposable
    {
        private StagedBuildContext(string name, string directory)
        {
            Name = name;
            Directory = directory;
        }

        public string Name { get; }
        public string Directory { get; }

        public static StagedBuildContext Create(ValidatedAssetSource source)
        {
            var parent = source.Kind == AssetSourceKind.Installer
                ? Path.GetDirectoryName(source.Path)
                : Path.GetTempPath();
            if (string.IsNullOrEmpty(parent))
                throw new InputValidationException("The operator asset staging directory is invalid.");

            var directory = Path.Combine(
                parent,
                $".sharplabnext-jsharp-context-{Guid.NewGuid():N}");
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                var stagedInstaller = Path.Combine(directory, "installer.bin");
                if (source.Kind == AssetSourceKind.Installer)
                    CreateHardLink(stagedInstaller, source.Path);
                else
                    File.Create(stagedInstaller).Dispose();
                return new StagedBuildContext(source.InstallerContextName, directory);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                TryDelete(directory, failClosed: false);
                throw new InputValidationException(
                    "The operator asset could not be staged as a private local build context.");
            }
        }

        public void Dispose() => TryDelete(Directory, failClosed: true);

        private static void CreateHardLink(string newPath, string existingPath)
        {
            var succeeded = OperatingSystem.IsWindows()
                ? CreateHardLinkWindows(newPath, existingPath, IntPtr.Zero)
                : CreateHardLinkUnix(existingPath, newPath) == 0;
            if (!succeeded)
            {
                throw new IOException(
                    "Could not create the private operator asset hard link.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }
        }

        [LibraryImport(
            "kernel32.dll",
            EntryPoint = "CreateHardLinkW",
            StringMarshalling = StringMarshalling.Utf16,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CreateHardLinkWindows(
            string fileName,
            string existingFileName,
            IntPtr securityAttributes);

        [LibraryImport(
            "libc",
            EntryPoint = "link",
            StringMarshalling = StringMarshalling.Utf8,
            SetLastError = true)]
        private static partial int CreateHardLinkUnix(string existingPath, string newPath);

        private static void TryDelete(string directory, bool failClosed)
        {
            try
            {
                if (System.IO.Directory.Exists(directory))
                    System.IO.Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                if (failClosed)
                {
                    throw new InputValidationException(
                        "The private operator asset staging context could not be removed.");
                }
            }
        }
    }

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
            if (value.Length > 0 && value.All(static character =>
                    !char.IsWhiteSpace(character) && character is not ('\"' or '\\')))
            {
                return value;
            }
            return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
        }
    }

    private sealed class UsageException(string message) : Exception(message);

    private sealed class InputValidationException(string message) : Exception(message);
}
