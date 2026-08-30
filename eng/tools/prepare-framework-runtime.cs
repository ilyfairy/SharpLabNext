#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property NuGetLockFilePath=obj/prepare-framework-runtime.packages.lock.json
#:property AllowUnsafeBlocks=true

using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

return await FrameworkRuntimePreparation.RunAsync(args);

internal static partial class FrameworkRuntimePreparation
{
    private const string DockerfileRelativePath = "deploy/docker/Dockerfile.operator-wine-framework-matrix";
    private const string ManifestRelativePath = "profiles/runtime-framework-installers.json";
    private const string MatrixRelativePath = "profiles/runtime-matrix.json";
    private const string VendoredContextName = "framework-vendored-context";
    private const string CachedContextName = "framework-cached-context";
    private const string InstallerContextName = "framework-installer-context";
    private const string SourceIdentityModeEnvironmentVariable = "SHARPLABNEXT_SOURCE_IDENTITY_MODE";
    private const string ContentSourceIdentityMode = "content";
    private const long MaximumInstallerBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumGitLfsPointerBytes = 1024;
    private const string Usage =
        "Usage: dotnet run eng/tools/prepare-framework-runtime.cs -- " +
        "[--build-kind operator|wow64-base|companion-seed] " +
        "[--target-id ID] [--seed-generation clr2|clr4] " +
        "--base-image REFERENCE --root-image REFERENCE --output-image REFERENCE " +
        "--seed-input-sha256 SHA256 " +
        "[--framework-seed-image REFERENCE | --framework-wow64-base-image REFERENCE] " +
        "--source-revision COMMIT " +
        "[--installer-url-secret-file PATH | --installer-secret-file PATH] " +
        "[--cached-winetricks-payload-file PATH] " +
        "--accept-microsoft-dotnet-framework-eula " +
        "[--allow-uncommitted-source-for-development] " +
        "[--repository-root PATH] [--docker-command COMMAND] [--dry-run]";

    public static async Task<int> RunAsync(string[] args)
    {
        PreparationOptions options;
        try
        {
            options = Parse(args);
            if (IsContentSourceIdentity())
            {
                options = options with { AllowUncommittedSourceForDevelopment = true };
            }
        }
        catch (UsageException exception)
        {
            WriteUsageError(exception);
            return 64;
        }

        try
        {
            var repositoryRoot = ResolveRepositoryRoot(options.RepositoryRoot);
            ValidateSourceState(repositoryRoot, options.SourceRevision, options.DryRun, options.AllowUncommittedSourceForDevelopment);
            var inputs = Validate(options, repositoryRoot);
            var invocation = CreateDockerInvocation(options, inputs);
            if (options.DryRun)
            {
                Console.WriteLine(invocation.RenderRedacted());
                return 0;
            }

            return await ExecuteAsync(invocation, inputs, options.SourceRevision, options.AllowUncommittedSourceForDevelopment);
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
        var buildKind = BuildKind.Operator;
        string? targetId = null;
        string? seedGeneration = null;
        string? frameworkSeedImage = null;
        string? frameworkWow64BaseImage = null;
        string? seedInputSha256 = null;
        string? baseImage = null;
        string? rootImage = null;
        string? outputImage = null;
        string? sourceRevision = null;
        string? installerUrlSecretFile = null;
        string? installerSecretFile = null;
        string? cachedWinetricksPayloadFile = null;
        var acceptEula = false;
        var allowUncommittedSourceForDevelopment = false;
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
                case "--build-kind":
                    buildKind = RequiredValue(args, ref index, option) switch
                    {
                        "operator" => BuildKind.Operator,
                        "wow64-base" => BuildKind.Wow64Base,
                        "companion-seed" => BuildKind.CompanionSeed,
                        _ => throw new UsageException("--build-kind must be operator, wow64-base, or companion-seed.")
                    };
                    break;
                case "--target-id":
                    targetId = RequiredValue(args, ref index, option);
                    break;
                case "--seed-generation":
                    seedGeneration = RequiredValue(args, ref index, option);
                    break;
                case "--framework-seed-image":
                    frameworkSeedImage = RequiredValue(args, ref index, option);
                    break;
                case "--framework-wow64-base-image":
                    frameworkWow64BaseImage = RequiredValue(args, ref index, option);
                    break;
                case "--seed-input-sha256":
                    seedInputSha256 = RequiredValue(args, ref index, option);
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
                case "--cached-winetricks-payload-file":
                    cachedWinetricksPayloadFile = RequiredValue(args, ref index, option);
                    break;
                case "--accept-microsoft-dotnet-framework-eula":
                    acceptEula = true;
                    break;
                case "--allow-uncommitted-source-for-development":
                    allowUncommittedSourceForDevelopment = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                default:
                    throw new UsageException($"Unknown argument '{option}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(baseImage) || string.IsNullOrWhiteSpace(rootImage) || string.IsNullOrWhiteSpace(outputImage) || string.IsNullOrWhiteSpace(sourceRevision) || string.IsNullOrWhiteSpace(seedInputSha256))
        {
            throw new UsageException("Base/root/output images, source revision, and seed input SHA-256 are required.");
        }
        if (!acceptEula)
            throw new UsageException("--accept-microsoft-dotnet-framework-eula is required.");
        if (installerUrlSecretFile is not null && installerSecretFile is not null)
        {
            throw new UsageException("An operator installer requires exactly one URL secret or local installer secret file.");
        }

        switch (buildKind)
        {
            case BuildKind.Operator when
                string.IsNullOrWhiteSpace(targetId) ||
                string.IsNullOrWhiteSpace(frameworkSeedImage) ||
                seedGeneration is not null ||
                frameworkWow64BaseImage is not null:
                throw new UsageException("Operator builds require target ID and Framework seed image only.");
            case BuildKind.Wow64Base when
                targetId is not null ||
                seedGeneration is not null ||
                frameworkSeedImage is not null ||
                frameworkWow64BaseImage is not null ||
                installerUrlSecretFile is not null ||
                installerSecretFile is not null ||
                cachedWinetricksPayloadFile is not null:
                throw new UsageException("WoW64 base builds do not accept target, seed, or installer inputs.");
            case BuildKind.CompanionSeed when
                seedGeneration is not ("clr2" or "clr4") ||
                string.IsNullOrWhiteSpace(frameworkWow64BaseImage) ||
                targetId is not null ||
                frameworkSeedImage is not null ||
                installerUrlSecretFile is not null ||
                installerSecretFile is not null:
                throw new UsageException("Companion seed builds require one generation and WoW64 base image only.");
        }

        return new PreparationOptions(
            repositoryRoot,
            dockerCommand ?? "docker",
            buildKind,
            targetId,
            seedGeneration,
            frameworkSeedImage is null
                ? null : ValidateDigestPinnedImageReference(frameworkSeedImage, "--framework-seed-image"),
            frameworkWow64BaseImage is null
                ? null : ValidateDigestPinnedImageReference(frameworkWow64BaseImage, "--framework-wow64-base-image"),
            ValidateSha256(seedInputSha256, "--seed-input-sha256"),
            ValidateDigestPinnedImageReference(baseImage, "--base-image"),
            ValidateDigestPinnedImageReference(rootImage, "--root-image"),
            ValidateOutputImageReference(outputImage),
            ValidateSourceRevision(sourceRevision),
            installerUrlSecretFile,
            installerSecretFile,
            cachedWinetricksPayloadFile,
            allowUncommittedSourceForDevelopment,
            dryRun);
    }

    private static ValidatedInputs Validate(PreparationOptions options, string repositoryRoot)
    {
        var manifestPath = Path.Combine(repositoryRoot, ManifestRelativePath);
        byte[] manifestBytes;
        InstallerManifest manifest;
        try
        {
            manifestBytes = File.ReadAllBytes(manifestPath);
            manifest = JsonSerializer.Deserialize(manifestBytes, InstallerManifestJsonContext.Default.InstallerManifest) ?? throw new JsonException();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InputValidationException("The Framework installer manifest is invalid or unreadable.");
        }

        ValidateManifest(manifest, repositoryRoot);
        var target = options.BuildKind == BuildKind.Operator
            ? manifest.Targets.SingleOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.Id, options.TargetId)) : null;
        if (options.BuildKind == BuildKind.Operator && target is null)
        {
            throw new InputValidationException($"Target '{options.TargetId}' is not present in the Framework installer manifest.");
        }

        var vendoredPayload = options.BuildKind == BuildKind.Wow64Base
            ? null : ValidateVendoredPayload(repositoryRoot, manifest.VendoredWinetricksPayloads.Single(), options.AllowUncommittedSourceForDevelopment);
        var cachedPayloadLock = manifest.CachedWinetricksPayloads.Single();
        var cachedPayloadRequired = options.BuildKind switch
        {
            BuildKind.CompanionSeed => options.SeedGeneration == "clr2",
            BuildKind.Operator =>
                target!.Recipe.Verb == cachedPayloadLock.Verb ||
                target.Recipe.PrerequisiteVerb == cachedPayloadLock.Verb,
            _ => false
        };
        if (cachedPayloadRequired != (options.CachedWinetricksPayloadFile is not null))
        {
            throw new UsageException(cachedPayloadRequired ? "This Framework build requires --cached-winetricks-payload-file." : "This Framework build does not accept --cached-winetricks-payload-file.");
        }
        var cachedPayload = cachedPayloadRequired
            ? ValidateCachedPayload(options.CachedWinetricksPayloadFile!, cachedPayloadLock, options.DryRun) : null;

        var hasSource = options.InstallerUrlSecretFile is not null || options.InstallerSecretFile is not null;
        if (target?.Recipe.Kind == "operator-installer" && !hasSource)
        {
            throw new UsageException($"Target '{target.Id}' requires exactly one operator installer URL or local secret file.");
        }
        if (target?.Recipe.Kind == "winetricks" && hasSource)
            throw new UsageException($"Target '{target.Id}' does not accept an operator installer source.");

        var source = target?.Recipe.Kind == "operator-installer"
            ? ValidateAssetSource(options, target.Recipe) : ValidatedAssetSource.None();
        var manifestSha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
        return new ValidatedInputs(repositoryRoot, manifestSha256, target, source, vendoredPayload, cachedPayload);
    }

    private static void ValidateManifest(InstallerManifest manifest, string repositoryRoot)
    {
        if (manifest.SchemaVersion != 1 || manifest.WinetricksVersion != "20240105")
            throw new InputValidationException("The Framework installer manifest version is unsupported.");
        ValidateVendoredWinetricksPayloads(manifest.VendoredWinetricksPayloads);
        ValidateCachedWinetricksPayloads(manifest.CachedWinetricksPayloads);
        ValidateBootstrapTools(manifest.BootstrapTools);
        ValidateClassicWow64Installer(manifest.ClassicWow64Installer);
        if (manifest.CompanionPrefixes.Clr2 != new CompanionPrefix("/opt/wine-netfx-clr2", "dotnet35sp1") || manifest.CompanionPrefixes.Clr4 != new CompanionPrefix("/opt/wine-netfx-clr4", "dotnet48"))
        {
            throw new InputValidationException("The Framework companion-prefix recipes are invalid.");
        }
        if (manifest.Targets.Count == 0 || manifest.Targets.Select(target => target.Id).Distinct(StringComparer.Ordinal).Count() != manifest.Targets.Count || manifest.Targets.Select(target => target.Version).Distinct(StringComparer.Ordinal).Count() != manifest.Targets.Count)
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

    private static void ValidateVendoredWinetricksPayloads(IReadOnlyList<VendoredWinetricksPayload>? payloads)
    {
        var expected = new VendoredWinetricksPayload("dotnet20-x64", "dotnet20", "eng/prerequisites/dotnet-framework-2.0/NetFx64.exe", "dotnet20/NetFx64.exe", 47_400_128, "7ea86dca8eeaedcaa4a17370547ca2cea9e9b6774972b8e03d2cb1fb0e798669");
        if (payloads is not { Count: 1 } || payloads[0] != expected)
        {
            throw new InputValidationException("The vendored Winetricks payload lock is invalid.");
        }
    }

    private static void ValidateCachedWinetricksPayloads(IReadOnlyList<CachedWinetricksPayload>? payloads)
    {
        var expected = new CachedWinetricksPayload("dotnet35sp1-full", "dotnet35sp1", "netfx35sp1-installer", "dotnet35sp1/dotnetfx35.exe", 242_743_296, "0582515bde321e072f8673e829e175ed2e7a53e803127c50253af76528e66bc1");
        if (payloads is not { Count: 1 } || payloads[0] != expected)
        {
            throw new InputValidationException("The cached Winetricks payload lock is invalid.");
        }
    }

    private static void ValidateBootstrapTools(FrameworkBootstrapTools? tools)
    {
        BootstrapDirectPackage[] expectedDirectPackages =
        [
            new("python3", "3.12.3-0ubuntu2.1"),
            new("cabextract", "1.11-2"),
            new("winetricks", "20240105-2")
        ];
        if (tools is null || tools.ArchiveSnapshotId != "20260810T000000Z" || !tools.DirectPackages.SequenceEqual(expectedDirectPackages) || !HasValidPackageLock(tools.ResolvedPackages, 28, tools.ResolvedPackageListSha256, "f5fddc3a5d79452068b4633aa98e95156bca47bf8285bcab0e7b69c5a546830d"))
        {
            throw new InputValidationException("The Framework bootstrap tool package closure is invalid.");
        }

        foreach (var package in tools.DirectPackages)
        {
            if (!tools.ResolvedPackages.Contains($"{package.Name}={package.Version}", StringComparer.Ordinal))
                throw new InputValidationException("The Framework bootstrap direct package is not locked.");
        }

    }

    private static void ValidateClassicWow64Installer(ClassicWow64Installer? installer)
    {
        var expectedDirectPackage = new ClassicWow64DirectPackage("wine32", "i386", "9.0~repack-4build3");
        if (installer is null ||
            installer.ArchiveSnapshotId != "20260810T000000Z" ||
            installer.ForeignArchitecture != "i386" ||
            installer.DirectPackage != expectedDirectPackage ||
            !HasValidPackageLock(installer.ReplacedPackages, 4, installer.ReplacedPackageListSha256, "4a69f0e49c3ffd2cd0a5ef4001395cc5df87748ceb903a5595dea5872c3d1a45") ||
            !HasValidPackageLock(installer.ResolvedPackages, 109, installer.ResolvedPackageListSha256, "e96dce12a7d0347874522dce2a520588fe4f3860feafd55a5736f11241b0ec8e") ||
            !installer.ResolvedPackages.Contains("wine32:i386=9.0~repack-4build3", StringComparer.Ordinal))
        {
            throw new InputValidationException("The Framework classic WoW64 installer package closure is invalid.");
        }
    }

    private static bool HasValidPackageLock(IReadOnlyList<string> packages, int expectedCount, string digest, string expectedDigest)
    {
        if (packages.Count != expectedCount || !packages.SequenceEqual(packages.OrderBy(static package => package, StringComparer.Ordinal)) || packages.Distinct(StringComparer.Ordinal).Count() != packages.Count || packages.Any(static package => !IsPackageLockEntry(package)) || !StringComparer.Ordinal.Equals(digest, expectedDigest))
        {
            return false;
        }

        var canonical = string.Concat(packages.Select(static package => $"{package}\n"));
        var actualDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return StringComparer.Ordinal.Equals(actualDigest, digest);
    }

    private static void ValidateRecipe(InstallerRecipe recipe)
    {
        if (recipe.Kind == "winetricks")
        {
            if (!IsWinetricksVerb(recipe.Verb) || recipe.FileName is not null || recipe.Sha256 is not null || recipe.PrerequisiteVerb is not null || recipe.Arguments is not null || recipe.SharedClr2FeaturePack is false)
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
                if (!StringComparer.Ordinal.Equals(matrixTarget.GetProperty("id").GetString(), installerTarget.Id) || !StringComparer.Ordinal.Equals(matrixTarget.GetProperty("version").GetString(), installerTarget.Version) || !StringComparer.Ordinal.Equals(matrixTarget.GetProperty("clrGeneration").GetString(), installerTarget.ClrGeneration) || !StringComparer.Ordinal.Equals(matrixTarget.GetProperty("prefix").GetString(), installerTarget.Prefix))
                {
                    throw new InputValidationException("The Framework installer manifest does not match the runtime matrix.");
                }
            }
        }
        catch (InputValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InputValidationException("The runtime matrix could not be checked against the installer manifest.");
        }
    }

    private static ValidatedVendoredPayload ValidateVendoredPayload(string repositoryRoot, VendoredWinetricksPayload payload, bool allowUncommittedSourceForDevelopment)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var path = Path.GetFullPath(Path.Combine(normalizedRoot, payload.RepositoryPath.Replace('/', Path.DirectorySeparatorChar)));
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, pathComparison))
        {
            throw new InputValidationException("The vendored Winetricks payload path escapes the repository root.");
        }

        var contentIdentityMode = IsContentSourceIdentity() &&
            allowUncommittedSourceForDevelopment;
        string? attribute = null;
        if (!contentIdentityMode && HasGitMetadata(normalizedRoot))
        {
            try
            {
                attribute = RunGit(normalizedRoot, "check-attr", "filter", "--", payload.RepositoryPath).Trim();
            }
            catch (InputValidationException) when (allowUncommittedSourceForDevelopment)
            {
                // The byte-level checks below remain authoritative when the
                // optional Git metadata cannot be inspected.
            }
        }
        else if (!contentIdentityMode && !allowUncommittedSourceForDevelopment)
        {
            throw new InputValidationException("The Framework source metadata is unavailable; use the local development build entry point.");
        }
        if (attribute is not null && !attribute.EndsWith(": filter: lfs", StringComparison.Ordinal))
        {
            throw new InputValidationException("The vendored .NET Framework 2.0 payload is not tracked by Git LFS.");
        }

        try
        {
            if (!File.Exists(path))
            {
                throw new InputValidationException("The vendored .NET Framework 2.0 Git LFS object is missing. " + $"Run 'git lfs pull --include={payload.RepositoryPath}'.");
            }

            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InputValidationException("The vendored .NET Framework 2.0 payload must be a regular file.");
            }
            if (info.Length <= MaximumGitLfsPointerBytes && File.ReadAllText(path).StartsWith("version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal))
            {
                throw new InputValidationException("The vendored .NET Framework 2.0 payload is an unexpanded Git LFS pointer. " + $"Run 'git lfs pull --include={payload.RepositoryPath}'.");
            }
            if (info.Length != payload.SizeBytes)
            {
                throw new InputValidationException("The vendored .NET Framework 2.0 payload size does not match the manifest lock.");
            }

            using var stream = File.OpenRead(path);
            var actualSha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (!StringComparer.Ordinal.Equals(actualSha256, payload.Sha256))
            {
                throw new InputValidationException("The vendored .NET Framework 2.0 payload SHA-256 does not match the manifest lock.");
            }
        }
        catch (InputValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InputValidationException("The vendored .NET Framework 2.0 payload could not be read.");
        }

        return new ValidatedVendoredPayload(payload, path);
    }

    private static ValidatedCachedPayload ValidateCachedPayload(string configuredPath, CachedWinetricksPayload payload, bool dryRun)
    {
        string path;
        try
        {
            path = Path.GetFullPath(configuredPath);
            if (dryRun)
                return new ValidatedCachedPayload(payload, path);
            if (!File.Exists(path))
            {
                throw new InputValidationException("The cached Winetricks payload file does not exist.");
            }

            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length != payload.SizeBytes)
            {
                throw new InputValidationException("The cached Winetricks payload is not the expected regular file size.");
            }
            using var stream = File.OpenRead(path);
            var actualSha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (!StringComparer.Ordinal.Equals(actualSha256, payload.Sha256))
            {
                throw new InputValidationException("The cached Winetricks payload SHA-256 does not match the manifest lock.");
            }
        }
        catch (InputValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            throw new InputValidationException("The cached Winetricks payload could not be read.");
        }

        return new ValidatedCachedPayload(payload, path);
    }

    private static ValidatedAssetSource ValidateAssetSource(PreparationOptions options, InstallerRecipe recipe)
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
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            throw new InputValidationException("The Framework installer URL secret file could not be read.");
        }

        if (string.IsNullOrWhiteSpace(content) || content.Any(char.IsWhiteSpace) || !Uri.TryCreate(content, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new InputValidationException("The Framework installer URL secret file must contain one absolute HTTP(S) URL.");
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
                throw new InputValidationException("The Framework installer secret file does not match the manifest SHA-256 digest.");
            }
        }
        catch (InputValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            throw new InputValidationException("The Framework installer secret file could not be read.");
        }

        return new ValidatedAssetSource(AssetSourceKind.Installer, path, SensitiveContent: null);
    }

    private static DockerInvocation CreateDockerInvocation(PreparationOptions options, ValidatedInputs inputs)
    {
        var dockerfile = Path.Combine(inputs.RepositoryRoot, DockerfileRelativePath);
        var frameworkWow64BaseImage = options.BuildKind == BuildKind.CompanionSeed
            ? options.FrameworkWow64BaseImage! : options.RootImage;
        var frameworkSeedImage = options.BuildKind == BuildKind.Operator
            ? options.FrameworkSeedImage! : options.RootImage;
        var arguments = new List<DockerArgument>
        {
            new("buildx"),
            new("build"),
            new("--file"),
            new(dockerfile, $"<repository-context>/{DockerfileRelativePath}"),
            new("--tag"),
            new(options.OutputImage),
            new("--load"),
            new("--provenance=false"),
            new("--build-arg"),
            new($"BASE_IMAGE={options.BaseImage}"),
            new("--build-arg"),
            new($"ROOT_IMAGE={options.RootImage}"),
            new("--build-arg"),
            new($"FRAMEWORK_WOW64_BASE_IMAGE={frameworkWow64BaseImage}"),
            new("--build-arg"),
            new($"FRAMEWORK_SEED_IMAGE={frameworkSeedImage}"),
            new("--build-arg"),
            new($"INSTALLER_MANIFEST_SHA256={inputs.ManifestSha256}"),
            new("--build-arg"),
            new($"FRAMEWORK_SEED_INPUT_SHA256={options.SeedInputSha256}"),
            new("--build-arg"),
            new("ACCEPT_MICROSOFT_DOTNET_FRAMEWORK_EULA=true")
        };
        AddBuildArgument(arguments, "FRAMEWORK_INSTALLER_NETWORK", inputs.CachedPayload is null ? "default" : "none");

        if (options.BuildKind == BuildKind.Wow64Base)
        {
            arguments.Add(new("--target"));
            arguments.Add(new("framework-wow64-base"));
        }
        else if (options.BuildKind == BuildKind.CompanionSeed)
        {
            var seed = CompanionSeedForGeneration(options.SeedGeneration!);
            arguments.Add(new("--target"));
            arguments.Add(new("framework-companion-seed"));
            AddBuildArgument(arguments, "FRAMEWORK_SEED_GENERATION", seed.Generation);
            AddBuildArgument(arguments, "FRAMEWORK_SEED_VERSION", seed.Version);
            AddBuildArgument(arguments, "FRAMEWORK_SEED_PREFIX", seed.Prefix);
        }
        else
        {
            var target = inputs.Target!;
            var seed = CompanionSeedForGeneration(target.ClrGeneration == "clr2" ? "clr4" : "clr2");
            AddBuildArgument(arguments, "FRAMEWORK_TARGET_ID", target.Id);
            AddBuildArgument(arguments, "FRAMEWORK_VERSION", target.Version);
            AddBuildArgument(arguments, "CLR_GENERATION", target.ClrGeneration);
            AddBuildArgument(arguments, "FRAMEWORK_SEED_GENERATION", seed.Generation);
            AddBuildArgument(arguments, "FRAMEWORK_SEED_VERSION", seed.Version);
            AddBuildArgument(arguments, "FRAMEWORK_SEED_PREFIX", seed.Prefix);
            AddBuildArgument(arguments, "SOURCE_REVISION", options.SourceRevision);
        }

        if (inputs.AssetSource.Kind == AssetSourceKind.Url)
        {
            arguments.Add(new("--secret"));
            arguments.Add(new($"id=framework-installer-url,src={inputs.AssetSource.Path}"));
        }
        if (options.BuildKind != BuildKind.Wow64Base)
        {
            var vendoredContext = ContextDirectory(inputs.VendoredPayload!.Path);
            var cachedContext = inputs.CachedPayload is null
                ? vendoredContext : ContextDirectory(inputs.CachedPayload.Path);
            var installerContext = inputs.AssetSource.Kind == AssetSourceKind.Installer
                ? ContextDirectory(inputs.AssetSource.Path!) : vendoredContext;
            AddBuildContext(arguments, VendoredContextName, vendoredContext);
            AddBuildContext(arguments, CachedContextName, cachedContext);
            AddBuildContext(arguments, InstallerContextName, installerContext);
            AddBuildArgument(arguments, "FRAMEWORK_INSTALLER_FILE", inputs.AssetSource.Kind == AssetSourceKind.Installer ? InputFileName(inputs.AssetSource.Path!) : inputs.Target?.Recipe.FileName ?? string.Empty);
        }
        arguments.Add(new(inputs.RepositoryRoot, "<repository-context>"));
        return new DockerInvocation(options.DockerCommand, arguments);
    }

    private static void AddBuildContext(ICollection<DockerArgument> arguments, string name, string directory)
    {
        arguments.Add(new("--build-context"));
        arguments.Add(new($"{name}={directory}", $"{name}=<direct-input-directory>"));
    }

    private static string ContextDirectory(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InputValidationException("A Framework Docker input does not have a parent directory.");
        }
        return directory;
    }

    private static string InputFileName(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || fileName.Any(static character => character is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-')))
        {
            throw new InputValidationException("The Framework installer file name is invalid for a direct Docker context.");
        }
        return fileName;
    }

    private static void AddBuildArgument(ICollection<DockerArgument> arguments, string name, string value)
    {
        arguments.Add(new("--build-arg"));
        arguments.Add(new($"{name}={value}"));
    }

    private static CompanionSeedDefinition CompanionSeedForGeneration(string generation) =>
        generation switch
        {
            "clr2" => new("clr2", "3.5", "/opt/wine-netfx-clr2"),
            "clr4" => new("clr4", "4.8", "/opt/wine-netfx-clr4"),
            _ => throw new InputValidationException("The Framework companion seed generation is invalid.")
        };

    private static async Task<int> ExecuteAsync(DockerInvocation invocation, ValidatedInputs inputs, string sourceRevision, bool allowUncommittedSourceForDevelopment)
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
                inputs.CachedPayload?.Path,
                inputs.VendoredPayload?.Path,
                inputs.Target?.Recipe.FileName
            }.Where(static value => !string.IsNullOrEmpty(value)).Select(static value => value!).ToArray();
            var output = ForwardRedactedAsync(process.StandardOutput, Console.Out, sensitiveValues);
            var error = ForwardRedactedAsync(process.StandardError, Console.Error, sensitiveValues);
            await process.WaitForExitAsync();
            await Task.WhenAll(output, error);
            if (process.ExitCode != 0)
            {
                Console.Error.WriteLine("Docker Buildx did not create the requested Framework build image.");
                return 1;
            }
        }

        ValidateSourceState(inputs.RepositoryRoot, sourceRevision, dryRun: false, allowUncommittedSourceForDevelopment);
        return 0;
    }

    private static async Task ForwardRedactedAsync(StreamReader source, TextWriter destination, IReadOnlyList<string> sensitiveValues)
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
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            throw new InputValidationException("The repository root is invalid.");
        }

        throw new InputValidationException("SharpLabNext.slnx was not found above the current directory.");
    }

    private static string ValidateDigestPinnedImageReference(string value, string option)
    {
        const string digestMarker = "@sha256:";
        var separator = value.LastIndexOf(digestMarker, StringComparison.Ordinal);
        if (value.Length > 512 || separator <= 0 || separator + digestMarker.Length + 64 != value.Length || value[..separator].Any(static character => char.IsWhiteSpace(character) || char.IsControl(character) || character == '@') || value[(separator + digestMarker.Length)..].Any(static character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new UsageException($"{option} must use repository[:tag]@sha256:<64 lowercase hex>.");
        }
        return value;
    }

    private static string ValidateOutputImageReference(string value)
    {
        if (value.Length > 512 || value.Contains('@') || value.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw new UsageException("--output-image must contain one bounded taggable Docker image reference.");
        }
        return value;
    }

    private static string ValidateSourceRevision(string value)
    {
        if (value.Length is not (40 or 64) || value.Any(static character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new UsageException("--source-revision must be a 40- or 64-character lowercase source identity.");
        }
        return value;
    }

    private static string ValidateSha256(string value, string option)
    {
        if (!IsSha256(value))
            throw new UsageException($"{option} must contain 64 lowercase hexadecimal characters.");
        return value;
    }

    private static void ValidateSourceState(string repositoryRoot, string sourceRevision, bool dryRun, bool allowUncommittedSourceForDevelopment)
    {
        if (IsContentSourceIdentity() && allowUncommittedSourceForDevelopment)
            return;
        if (!HasGitMetadata(repositoryRoot))
        {
            if (allowUncommittedSourceForDevelopment)
                return;
            throw new InputValidationException("The Framework source metadata is unavailable; use the local development build entry point.");
        }

        string head;
        try
        {
            head = RunGit(repositoryRoot, "rev-parse", "--verify", "HEAD").Trim();
        }
        catch (InputValidationException) when (allowUncommittedSourceForDevelopment)
        {
            return;
        }
        if (!StringComparer.Ordinal.Equals(head, sourceRevision))
        {
            throw new InputValidationException($"The source revision '{sourceRevision}' does not match Git HEAD '{head}'.");
        }

        string status;
        try
        {
            status = RunGit(repositoryRoot, "status", "--porcelain=v1", "--untracked-files=normal");
        }
        catch (InputValidationException) when (allowUncommittedSourceForDevelopment)
        {
            return;
        }
        if (!dryRun && status.Length != 0 && !allowUncommittedSourceForDevelopment)
        {
            throw new InputValidationException("The Framework operator source worktree must be clean.");
        }
    }

    private static bool HasGitMetadata(string repositoryRoot) => File.Exists(Path.Combine(repositoryRoot, ".git")) || Directory.Exists(Path.Combine(repositoryRoot, ".git"));

    private static bool IsContentSourceIdentity() => string.Equals(Environment.GetEnvironmentVariable(SourceIdentityModeEnvironmentVariable), ContentSourceIdentityMode, StringComparison.OrdinalIgnoreCase);

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
            using var process = Process.Start(startInfo) ?? throw new InputValidationException("Could not inspect the Framework operator Git source.");
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InputValidationException("Could not inspect the Framework operator Git source.");
            }
            return output;
        }
        catch (InputValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            throw new InputValidationException("Could not inspect the Framework operator Git source.");
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
        value.All(static character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');

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
        value.All(static character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-');

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsInstallerArgument(string value) =>
        value is { Length: >= 2 and <= 64 } &&
        value[0] == '/' &&
        value[1..].All(static character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or ':' or '.' or '_' or '=' or '-');

    private static bool IsPackageLockEntry(string value)
    {
        if (value is not { Length: >= 3 and <= 192 })
            return false;
        var separator = value.IndexOf('=');
        if (separator <= 0 || separator != value.LastIndexOf('=') || separator == value.Length - 1)
            return false;
        var name = value[..separator];
        var version = value[(separator + 1)..];
        return (name[0] is >= 'a' and <= 'z' or >= '0' and <= '9') &&
            name.All(static character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '+' or '-' or ':') &&
            (version[0] is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9') &&
            version.All(static character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '+' or ':' or '~' or '_' or '-');
    }

    private sealed record PreparationOptions(
        string? RepositoryRoot,
        string DockerCommand,
        BuildKind BuildKind,
        string? TargetId,
        string? SeedGeneration,
        string? FrameworkSeedImage,
        string? FrameworkWow64BaseImage,
        string SeedInputSha256,
        string BaseImage,
        string RootImage,
        string OutputImage,
        string SourceRevision,
        string? InstallerUrlSecretFile,
        string? InstallerSecretFile,
        string? CachedWinetricksPayloadFile,
        bool AllowUncommittedSourceForDevelopment,
        bool DryRun);

    private sealed record ValidatedInputs(string RepositoryRoot, string ManifestSha256, InstallerTarget? Target, ValidatedAssetSource AssetSource, ValidatedVendoredPayload? VendoredPayload, ValidatedCachedPayload? CachedPayload);

    private sealed record CompanionSeedDefinition(string Generation, string Version, string Prefix);

    private enum BuildKind
    {
        Operator,
        Wow64Base,
        CompanionSeed
    }

    private sealed record ValidatedVendoredPayload(VendoredWinetricksPayload Lock, string Path);

    private sealed record ValidatedCachedPayload(CachedWinetricksPayload Lock, string Path);

    private enum AssetSourceKind
    {
        None,
        Url,
        Installer
    }

    private sealed record ValidatedAssetSource(AssetSourceKind Kind, string? Path, string? SensitiveContent)
    {
        public static ValidatedAssetSource None() => new(AssetSourceKind.None, Path: null, SensitiveContent: null);
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
            if (value.Length > 0 && value.All(static character => !char.IsWhiteSpace(character) && character is not ('\"' or '\\')))
            {
                return value;
            }
            return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
        }
    }

    private sealed record InstallerManifest(
        int SchemaVersion,
        string WinetricksVersion,
        IReadOnlyList<VendoredWinetricksPayload> VendoredWinetricksPayloads,
        IReadOnlyList<CachedWinetricksPayload> CachedWinetricksPayloads,
        FrameworkBootstrapTools BootstrapTools,
        ClassicWow64Installer ClassicWow64Installer,
        CompanionPrefixes CompanionPrefixes,
        IReadOnlyList<InstallerTarget> Targets);

    private sealed record VendoredWinetricksPayload(string Id, string Verb, string RepositoryPath, string CachePath, long SizeBytes, string Sha256);

    private sealed record CachedWinetricksPayload(string Id, string Verb, string PrerequisiteId, string CachePath, long SizeBytes, string Sha256);

    private sealed record FrameworkBootstrapTools(string ArchiveSnapshotId, IReadOnlyList<BootstrapDirectPackage> DirectPackages, IReadOnlyList<string> ResolvedPackages, string ResolvedPackageListSha256);

    private sealed record BootstrapDirectPackage(string Name, string Version);

    private sealed record ClassicWow64Installer(
        string ArchiveSnapshotId,
        string ForeignArchitecture,
        ClassicWow64DirectPackage DirectPackage,
        IReadOnlyList<string> ReplacedPackages,
        string ReplacedPackageListSha256,
        IReadOnlyList<string> ResolvedPackages,
        string ResolvedPackageListSha256);

    private sealed record ClassicWow64DirectPackage(string Name, string Architecture, string Version);

    private sealed record CompanionPrefixes(CompanionPrefix Clr2, CompanionPrefix Clr4);

    private sealed record CompanionPrefix(string Prefix, string WinetricksVerb);

    private sealed record InstallerTarget(string Id, string Version, string ClrGeneration, string Prefix, InstallerRecipe Recipe);

    private sealed record InstallerRecipe(string Kind, string? Verb, bool? SharedClr2FeaturePack, string? FileName, string? Sha256, string? PrerequisiteVerb, string[]? Arguments);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
    [JsonSerializable(typeof(InstallerManifest))]
    private sealed partial class InstallerManifestJsonContext : JsonSerializerContext { }

    private sealed class UsageException(string message) : Exception(message);

    private sealed class InputValidationException : Exception
    {
        public InputValidationException(string message) : base(message) { }

        public InputValidationException(string message, Exception? innerException) : base(message, innerException) { }
    }
}
