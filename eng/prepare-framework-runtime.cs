#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property NuGetLockFilePath=obj/prepare-framework-runtime.packages.lock.json
#:property AllowUnsafeBlocks=true

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

return await FrameworkRuntimePreparation.RunAsync(args);

internal static partial class FrameworkRuntimePreparation
{
    private const string DockerfileRelativePath =
        "deploy/docker/Dockerfile.operator-wine-framework-matrix";
    private const string ManifestRelativePath =
        "profiles/runtime-framework-installers.json";
    private const string MatrixRelativePath = "profiles/runtime-matrix.json";
    private const long MaximumInstallerBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumCommittedSourceFileBytes = 32 * 1024 * 1024;
    private static readonly string[] CommittedSourceFiles =
    [
        DockerfileRelativePath,
        ManifestRelativePath,
        MatrixRelativePath,
        "deploy/docker/wine-netfx-framework-preflight.sh",
        "deploy/docker/dedupe-wine-prefixes.py",
        "deploy/docker/certificates/microsoft-tls-rsa-root-g2-xsign.crt",
        "deploy/docker/certificates/microsoft-tls-g2-rsa-ca-ocsp-04.crt"
    ];
    private const string Usage =
        "Usage: dotnet run eng/prepare-framework-runtime.cs -- " +
        "--target-id ID --base-image REFERENCE --root-image REFERENCE --output-image REFERENCE " +
        "--source-revision COMMIT " +
        "[--installer-url-secret-file PATH | --installer-secret-file PATH] " +
        "--accept-microsoft-dotnet-framework-eula " +
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
            WriteUsageError(exception);
            return 64;
        }

        try
        {
            var repositoryRoot = ResolveRepositoryRoot(options.RepositoryRoot);
            ValidateSourceState(repositoryRoot, options.SourceRevision, options.DryRun);
            using var sourceContext = CommittedSourceContext.Create(
                repositoryRoot,
                options.SourceRevision);
            var inputs = Validate(options, repositoryRoot, sourceContext.Directory);
            using var stagedContext = StagedBuildContext.Create(inputs.AssetSource);
            var invocation = CreateDockerInvocation(
                options,
                inputs,
                sourceContext,
                stagedContext);
            if (options.DryRun)
            {
                Console.WriteLine(invocation.RenderRedacted());
                return 0;
            }

            return await ExecuteAsync(
                invocation,
                inputs,
                sourceContext,
                stagedContext,
                options.SourceRevision);
        }
        catch (UsageException exception)
        {
            WriteUsageError(exception);
            return 64;
        }
        catch (InputValidationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static void WriteUsageError(UsageException exception)
    {
        Console.Error.WriteLine(exception.Message);
        Console.Error.WriteLine(Usage);
    }

    private static PreparationOptions Parse(string[] args)
    {
        string? repositoryRoot = null;
        string? dockerCommand = null;
        string? targetId = null;
        string? baseImage = null;
        string? rootImage = null;
        string? outputImage = null;
        string? sourceRevision = null;
        string? installerUrlSecretFile = null;
        string? installerSecretFile = null;
        var acceptEula = false;
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
                case "--target-id":
                    targetId = RequiredValue(args, ref index, option);
                    break;
                case "--base-image":
                    baseImage = RequiredValue(args, ref index, option);
                    break;
                case "--root-image":
                    rootImage = RequiredValue(args, ref index, option);
                    break;
                case "--output-image":
                    outputImage = RequiredValue(args, ref index, option);
                    break;
                case "--source-revision":
                    sourceRevision = RequiredValue(args, ref index, option);
                    break;
                case "--installer-url-secret-file":
                    installerUrlSecretFile = RequiredValue(args, ref index, option);
                    break;
                case "--installer-secret-file":
                    installerSecretFile = RequiredValue(args, ref index, option);
                    break;
                case "--accept-microsoft-dotnet-framework-eula":
                    acceptEula = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                default:
                    throw new UsageException($"Unknown argument '{option}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(targetId) ||
            string.IsNullOrWhiteSpace(baseImage) ||
            string.IsNullOrWhiteSpace(rootImage) ||
            string.IsNullOrWhiteSpace(outputImage) ||
            string.IsNullOrWhiteSpace(sourceRevision))
        {
            throw new UsageException(
                "Target ID, base/root/output images, and source revision are required.");
        }
        if (!acceptEula)
            throw new UsageException("--accept-microsoft-dotnet-framework-eula is required.");
        if (installerUrlSecretFile is not null && installerSecretFile is not null)
        {
            throw new UsageException(
                "An operator installer requires exactly one URL secret or local installer secret file.");
        }

        return new PreparationOptions(
            repositoryRoot,
            dockerCommand ?? "docker",
            targetId,
            ValidateDigestPinnedImageReference(baseImage, "--base-image"),
            ValidateDigestPinnedImageReference(rootImage, "--root-image"),
            ValidateOutputImageReference(outputImage),
            ValidateSourceRevision(sourceRevision),
            installerUrlSecretFile,
            installerSecretFile,
            dryRun);
    }

    private static ValidatedInputs Validate(
        PreparationOptions options,
        string repositoryRoot,
        string sourceRoot)
    {
        var manifestPath = Path.Combine(sourceRoot, ManifestRelativePath);
        byte[] manifestBytes;
        InstallerManifest manifest;
        try
        {
            manifestBytes = File.ReadAllBytes(manifestPath);
            manifest = JsonSerializer.Deserialize(
                manifestBytes,
                InstallerManifestJsonContext.Default.InstallerManifest)
                ?? throw new JsonException();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InputValidationException("The Framework installer manifest is invalid or unreadable.");
        }

        ValidateManifest(manifest, sourceRoot);
        var target = manifest.Targets.SingleOrDefault(
            candidate => StringComparer.Ordinal.Equals(candidate.Id, options.TargetId));
        if (target is null)
        {
            throw new InputValidationException(
                $"Target '{options.TargetId}' is not present in the Framework installer manifest.");
        }

        var hasSource = options.InstallerUrlSecretFile is not null || options.InstallerSecretFile is not null;
        if (target.Recipe.Kind == "operator-installer" && !hasSource)
        {
            throw new UsageException(
                $"Target '{target.Id}' requires exactly one operator installer URL or local secret file.");
        }
        if (target.Recipe.Kind == "winetricks" && hasSource)
            throw new UsageException($"Target '{target.Id}' does not accept an operator installer source.");

        var source = target.Recipe.Kind == "operator-installer"
            ? ValidateAssetSource(options, target.Recipe)
            : ValidatedAssetSource.None();
        var manifestSha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
        return new ValidatedInputs(repositoryRoot, manifestSha256, target, source);
    }

    private static void ValidateManifest(InstallerManifest manifest, string repositoryRoot)
    {
        if (manifest.SchemaVersion != 1 || manifest.WinetricksVersion != "20240105")
            throw new InputValidationException("The Framework installer manifest version is unsupported.");
        if (manifest.CompanionPrefixes.Clr2 != new CompanionPrefix("/opt/wine-netfx-clr2", "dotnet35sp1") ||
            manifest.CompanionPrefixes.Clr4 != new CompanionPrefix("/opt/wine-netfx-clr4", "dotnet48"))
        {
            throw new InputValidationException("The Framework companion-prefix recipes are invalid.");
        }
        if (manifest.Targets.Count == 0 ||
            manifest.Targets.Select(target => target.Id).Distinct(StringComparer.Ordinal).Count() != manifest.Targets.Count ||
            manifest.Targets.Select(target => target.Version).Distinct(StringComparer.Ordinal).Count() != manifest.Targets.Count)
        {
            throw new InputValidationException("The Framework installer manifest contains duplicate or no targets.");
        }

        foreach (var target in manifest.Targets)
        {
            if (!IsIdentifier(target.Id) || !IsVersion(target.Version))
                throw new InputValidationException("The Framework installer manifest contains an invalid target identity.");
            var expectedPrefix = target.ClrGeneration switch
            {
                "clr2" => "/opt/wine-netfx-clr2",
                "clr4" => "/opt/wine-netfx-clr4",
                _ => throw new InputValidationException("The Framework installer manifest contains an invalid CLR generation.")
            };
            if (!StringComparer.Ordinal.Equals(target.Prefix, expectedPrefix))
                throw new InputValidationException("The Framework installer manifest contains an invalid prefix mapping.");
            ValidateRecipe(target.Recipe);
        }

        ValidateMatrixParity(manifest, Path.Combine(repositoryRoot, MatrixRelativePath));
    }

    private static void ValidateRecipe(InstallerRecipe recipe)
    {
        if (recipe.Kind == "winetricks")
        {
            if (!IsWinetricksVerb(recipe.Verb) ||
                recipe.FileName is not null ||
                recipe.Sha256 is not null ||
                recipe.PrerequisiteVerb is not null ||
                recipe.Arguments is not null ||
                recipe.SharedClr2FeaturePack is false)
            {
                throw new InputValidationException("The Framework installer manifest contains an invalid Winetricks recipe.");
            }
            return;
        }

        if (recipe.Kind != "operator-installer" ||
            recipe.Verb is not null ||
            recipe.SharedClr2FeaturePack is not null ||
            !IsInstallerFileName(recipe.FileName) ||
            !IsSha256(recipe.Sha256) ||
            !IsWinetricksVerb(recipe.PrerequisiteVerb) ||
            recipe.Arguments is not { Length: >= 1 and <= 8 } ||
            recipe.Arguments.Any(argument => !IsInstallerArgument(argument)))
        {
            throw new InputValidationException("The Framework installer manifest contains an invalid operator recipe.");
        }
    }

    private static void ValidateMatrixParity(InstallerManifest manifest, string matrixPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(matrixPath));
            var targets = document.RootElement.GetProperty("framework").GetProperty("targets");
            if (targets.GetArrayLength() != manifest.Targets.Count)
                throw new InputValidationException("The Framework installer manifest does not match the runtime matrix.");

            var index = 0;
            foreach (var matrixTarget in targets.EnumerateArray())
            {
                var installerTarget = manifest.Targets[index++];
                if (!StringComparer.Ordinal.Equals(matrixTarget.GetProperty("id").GetString(), installerTarget.Id) ||
                    !StringComparer.Ordinal.Equals(matrixTarget.GetProperty("version").GetString(), installerTarget.Version) ||
                    !StringComparer.Ordinal.Equals(matrixTarget.GetProperty("clrGeneration").GetString(), installerTarget.ClrGeneration) ||
                    !StringComparer.Ordinal.Equals(matrixTarget.GetProperty("prefix").GetString(), installerTarget.Prefix))
                {
                    throw new InputValidationException("The Framework installer manifest does not match the runtime matrix.");
                }
            }
        }
        catch (InputValidationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InputValidationException("The runtime matrix could not be checked against the installer manifest.");
        }
    }

    private static ValidatedAssetSource ValidateAssetSource(
        PreparationOptions options,
        InstallerRecipe recipe)
    {
        if (options.InstallerUrlSecretFile is not null)
            return ReadUrlSecret(options.InstallerUrlSecretFile);
        return ReadInstaller(options.InstallerSecretFile!, recipe.Sha256!);
    }

    private static ValidatedAssetSource ReadUrlSecret(string configuredPath)
    {
        string path;
        string content;
        try
        {
            path = Path.GetFullPath(configuredPath);
            if (!File.Exists(path))
                throw new InputValidationException("The Framework installer URL secret file does not exist.");
            content = File.ReadAllText(path).Trim();
        }
        catch (InputValidationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            throw new InputValidationException("The Framework installer URL secret file could not be read.");
        }

        if (string.IsNullOrWhiteSpace(content) ||
            content.Any(char.IsWhiteSpace) ||
            !Uri.TryCreate(content, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new InputValidationException(
                "The Framework installer URL secret file must contain one absolute HTTP(S) URL.");
        }

        return new ValidatedAssetSource(AssetSourceKind.Url, path, content);
    }

    private static ValidatedAssetSource ReadInstaller(string configuredPath, string expectedSha256)
    {
        string path;
        try
        {
            path = Path.GetFullPath(configuredPath);
            if (!File.Exists(path))
                throw new InputValidationException("The Framework installer secret file does not exist.");
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaximumInstallerBytes)
                throw new InputValidationException("The Framework installer secret file has an invalid size.");
            using var stream = File.OpenRead(path);
            var actualSha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (!StringComparer.Ordinal.Equals(actualSha256, expectedSha256))
            {
                throw new InputValidationException(
                    "The Framework installer secret file does not match the manifest SHA-256 digest.");
            }
        }
        catch (InputValidationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            throw new InputValidationException("The Framework installer secret file could not be read.");
        }

        return new ValidatedAssetSource(AssetSourceKind.Installer, path, SensitiveContent: null);
    }

    private static DockerInvocation CreateDockerInvocation(
        PreparationOptions options,
        ValidatedInputs inputs,
        CommittedSourceContext sourceContext,
        StagedBuildContext stagedContext)
    {
        var dockerfile = Path.Combine(sourceContext.Directory, DockerfileRelativePath);
        var arguments = new List<DockerArgument>
        {
            new("buildx"),
            new("build"),
            new("--file"),
            new(
                dockerfile,
                $"<committed-source-context>/{DockerfileRelativePath}"),
            new("--tag"),
            new(options.OutputImage),
            new("--load"),
            new("--provenance=false"),
            new("--build-arg"),
            new($"BASE_IMAGE={options.BaseImage}"),
            new("--build-arg"),
            new($"ROOT_IMAGE={options.RootImage}"),
            new("--build-arg"),
            new($"FRAMEWORK_TARGET_ID={inputs.Target.Id}"),
            new("--build-arg"),
            new($"FRAMEWORK_VERSION={inputs.Target.Version}"),
            new("--build-arg"),
            new($"CLR_GENERATION={inputs.Target.ClrGeneration}"),
            new("--build-arg"),
            new($"INSTALLER_MANIFEST_SHA256={inputs.ManifestSha256}"),
            new("--build-arg"),
            new($"SOURCE_REVISION={options.SourceRevision}"),
            new("--build-arg"),
            new("ACCEPT_MICROSOFT_DOTNET_FRAMEWORK_EULA=true")
        };
        if (inputs.AssetSource.Kind == AssetSourceKind.Url)
        {
            arguments.Add(new("--secret"));
            arguments.Add(new($"id=framework-installer-url,src={inputs.AssetSource.Path}"));
        }
        arguments.Add(new("--build-context"));
        arguments.Add(new(
            $"framework-installer-context={stagedContext.Directory}",
            "framework-installer-context=<staged-local-context>"));
        arguments.Add(new(sourceContext.Directory, "<committed-source-context>"));
        return new DockerInvocation(options.DockerCommand, arguments);
    }

    private static async Task<int> ExecuteAsync(
        DockerInvocation invocation,
        ValidatedInputs inputs,
        CommittedSourceContext sourceContext,
        StagedBuildContext stagedContext,
        string sourceRevision)
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
                inputs.AssetSource.Path,
                inputs.AssetSource.SensitiveContent,
                sourceContext.Directory,
                stagedContext.Directory
            }.Where(static value => !string.IsNullOrEmpty(value)).Select(static value => value!).ToArray();
            var output = ForwardRedactedAsync(process.StandardOutput, Console.Out, sensitiveValues);
            var error = ForwardRedactedAsync(process.StandardError, Console.Error, sensitiveValues);
            await process.WaitForExitAsync();
            await Task.WhenAll(output, error);
            if (process.ExitCode != 0)
            {
                Console.Error.WriteLine("Docker Buildx did not create the operator Framework runtime image.");
                return 1;
            }
        }

        ValidateSourceState(inputs.RepositoryRoot, sourceRevision, dryRun: false);
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
        catch (InputValidationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            throw new InputValidationException("The repository root is invalid.");
        }

        throw new InputValidationException("SharpLabNext.slnx was not found above the current directory.");
    }

    private static string ValidateDigestPinnedImageReference(string value, string option)
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
                $"{option} must use repository[:tag]@sha256:<64 lowercase hex>.");
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

    private static string ValidateSourceRevision(string value)
    {
        if (value.Length is not (40 or 64) ||
            value.Any(static character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new UsageException(
                "--source-revision must be a complete lowercase Git commit identity.");
        }
        return value;
    }

    private static void ValidateSourceState(
        string repositoryRoot,
        string sourceRevision,
        bool dryRun)
    {
        var head = RunGit(repositoryRoot, "rev-parse", "--verify", "HEAD").Trim();
        if (!StringComparer.Ordinal.Equals(head, sourceRevision))
        {
            throw new InputValidationException(
                $"The source revision '{sourceRevision}' does not match Git HEAD '{head}'.");
        }

        var status = RunGit(
            repositoryRoot,
            "status",
            "--porcelain=v1",
            "--untracked-files=normal");
        if (!dryRun && status.Length != 0)
        {
            throw new InputValidationException(
                "The Framework operator source worktree must be clean.");
        }
    }

    private static string RunGit(string repositoryRoot, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = repositoryRoot
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InputValidationException(
                    "Could not inspect the Framework operator Git source.");
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InputValidationException(
                    "Could not inspect the Framework operator Git source.");
            }
            return output;
        }
        catch (InputValidationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            throw new InputValidationException(
                "Could not inspect the Framework operator Git source.");
        }
    }

    private static byte[] ReadCommittedSourceFile(
        string repositoryRoot,
        string sourceRevision,
        string relativePath)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = repositoryRoot
        };
        startInfo.ArgumentList.Add("show");
        startInfo.ArgumentList.Add("--no-ext-diff");
        startInfo.ArgumentList.Add("--no-textconv");
        startInfo.ArgumentList.Add($"{sourceRevision}:{relativePath}");

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InputValidationException(
                    $"Could not read committed Framework operator source '{relativePath}'.");
            var error = process.StandardError.ReadToEndAsync();
            using var output = new MemoryStream();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = process.StandardOutput.BaseStream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;
                output.Write(buffer, 0, read);
                if (output.Length > MaximumCommittedSourceFileBytes)
                {
                    process.Kill(entireProcessTree: true);
                    throw new InputValidationException(
                        $"Committed Framework operator source '{relativePath}' is too large.");
                }
            }
            process.WaitForExit();
            _ = error.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || output.Length == 0)
            {
                throw new InputValidationException(
                    $"Could not read committed Framework operator source '{relativePath}'.");
            }
            return output.ToArray();
        }
        catch (InputValidationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception or IOException)
        {
            throw new InputValidationException(
                $"Could not read committed Framework operator source '{relativePath}'.");
        }
    }

    private static string RequiredValue(string[] args, ref int index, string option)
    {
        index++;
        if (index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            throw new UsageException($"{option} requires a non-empty value.");
        return args[index];
    }

    private static bool IsIdentifier(string? value) =>
        value is { Length: >= 1 and <= 128 } &&
        value.All(static character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');

    private static bool IsVersion(string? value) =>
        value is { Length: >= 3 and <= 16 } &&
        value.All(static character => character is >= '0' and <= '9' or '.');

    private static bool IsWinetricksVerb(string? value) =>
        value is { Length: >= 8 and <= 32 } &&
        value.StartsWith("dotnet", StringComparison.Ordinal) &&
        value[6..].All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'z');

    private static bool IsInstallerFileName(string? value) =>
        value is { Length: >= 5 and <= 128 } &&
        value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        value.All(static character =>
            character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-');

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsInstallerArgument(string value) =>
        value is { Length: >= 2 and <= 64 } &&
        value[0] == '/' &&
        value[1..].All(static character =>
            character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or ':' or '.' or '_' or '=' or '-');

    private sealed record PreparationOptions(
        string? RepositoryRoot,
        string DockerCommand,
        string TargetId,
        string BaseImage,
        string RootImage,
        string OutputImage,
        string SourceRevision,
        string? InstallerUrlSecretFile,
        string? InstallerSecretFile,
        bool DryRun);

    private sealed record ValidatedInputs(
        string RepositoryRoot,
        string ManifestSha256,
        InstallerTarget Target,
        ValidatedAssetSource AssetSource);

    private enum AssetSourceKind
    {
        None,
        Url,
        Installer
    }

    private sealed record ValidatedAssetSource(
        AssetSourceKind Kind,
        string? Path,
        string? SensitiveContent)
    {
        public static ValidatedAssetSource None() =>
            new(AssetSourceKind.None, Path: null, SensitiveContent: null);
    }

    private sealed class CommittedSourceContext : IDisposable
    {
        private CommittedSourceContext(string directory) => Directory = directory;

        public string Directory { get; }

        public static CommittedSourceContext Create(
            string repositoryRoot,
            string sourceRevision)
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                $".sharplabnext-framework-source-{Guid.NewGuid():N}");
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(
                        directory,
                        UnixFileMode.UserRead |
                        UnixFileMode.UserWrite |
                        UnixFileMode.UserExecute);
                }
                foreach (var relativePath in CommittedSourceFiles)
                {
                    var destination = Path.Combine(
                        directory,
                        relativePath.Replace('/', Path.DirectorySeparatorChar));
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.WriteAllBytes(
                        destination,
                        ReadCommittedSourceFile(repositoryRoot, sourceRevision, relativePath));
                }
                return new CommittedSourceContext(directory);
            }
            catch (InputValidationException)
            {
                TryDelete(directory, failClosed: false);
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                TryDelete(directory, failClosed: false);
                throw new InputValidationException(
                    "The committed Framework operator source context could not be created.");
            }
        }

        public void Dispose() => TryDelete(Directory, failClosed: true);

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
                        "The committed Framework operator source context could not be removed.");
                }
            }
        }
    }

    private sealed partial class StagedBuildContext : IDisposable
    {
        private StagedBuildContext(string directory) => Directory = directory;

        public string Directory { get; }

        public static StagedBuildContext Create(ValidatedAssetSource source)
        {
            var parent = source.Kind == AssetSourceKind.Installer
                ? Path.GetDirectoryName(source.Path)
                : Path.GetTempPath();
            if (string.IsNullOrEmpty(parent))
                throw new InputValidationException("The operator installer staging directory is invalid.");

            var directory = Path.Combine(
                parent,
                $".sharplabnext-framework-context-{Guid.NewGuid():N}");
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                var stagedInstaller = Path.Combine(directory, "installer.bin");
                if (source.Kind == AssetSourceKind.Installer)
                    CreateHardLink(stagedInstaller, source.Path!);
                else
                    File.Create(stagedInstaller).Dispose();
                return new StagedBuildContext(directory);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                TryDelete(directory, failClosed: false);
                throw new InputValidationException(
                    "The operator installer could not be staged as a private local build context.");
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
                    "Could not create the private operator installer hard link.",
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
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (failClosed)
                {
                    throw new InputValidationException(
                        "The private operator installer staging context could not be removed.");
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

    private sealed record InstallerManifest(
        int SchemaVersion,
        string WinetricksVersion,
        CompanionPrefixes CompanionPrefixes,
        IReadOnlyList<InstallerTarget> Targets);

    private sealed record CompanionPrefixes(CompanionPrefix Clr2, CompanionPrefix Clr4);

    private sealed record CompanionPrefix(string Prefix, string WinetricksVerb);

    private sealed record InstallerTarget(
        string Id,
        string Version,
        string ClrGeneration,
        string Prefix,
        InstallerRecipe Recipe);

    private sealed record InstallerRecipe(
        string Kind,
        string? Verb,
        bool? SharedClr2FeaturePack,
        string? FileName,
        string? Sha256,
        string? PrerequisiteVerb,
        string[]? Arguments);

    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
    [JsonSerializable(typeof(InstallerManifest))]
    private sealed partial class InstallerManifestJsonContext : JsonSerializerContext;

    private sealed class UsageException(string message) : Exception(message);

    private sealed class InputValidationException(string message) : Exception(message);
}
