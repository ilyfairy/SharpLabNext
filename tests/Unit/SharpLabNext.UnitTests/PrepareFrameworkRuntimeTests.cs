using System.Diagnostics;
using System.Security.Cryptography;

namespace SharpLabNext.UnitTests;

public sealed class PrepareFrameworkRuntimeTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string ScriptPath = Path.Combine(RepositoryRoot, "eng", "tools", "prepare-framework-runtime.cs");
    private static readonly string RepositoryRevision = ReadRepositoryRevision();

    [Fact]
    public void SharedPreparationScriptExists()
    {
        Assert.True(File.Exists(ScriptPath));
    }

    [Fact]
    public async Task WinetricksTargetBuildsOneDataDrivenCommandWithoutInstallerInput()
    {
        using var secrets = new SecretFiles();

        var result = await RunAsync(ValidArguments("netfx48", secrets, includeInstallerSource: false));

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        Assert.Contains("docker buildx build", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Dockerfile.operator-wine-framework-matrix", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("FRAMEWORK_TARGET_ID=netfx48", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("FRAMEWORK_VERSION=4.8", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("CLR_GENERATION=clr4", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("FRAMEWORK_SEED_GENERATION=clr2", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("FRAMEWORK_SEED_VERSION=3.5", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("FRAMEWORK_SEED_PREFIX=/opt/wine-netfx-clr2", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("FRAMEWORK_SEED_INPUT_SHA256=", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("FRAMEWORK_SEED_IMAGE=operator/framework-seed:cache@sha256:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("FRAMEWORK_WOW64_BASE_IMAGE=operator/root:10.0@sha256:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"SOURCE_REVISION={RepositoryRevision}", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("ROOT_IMAGE=operator/root:10.0@sha256:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("<repository-context>", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("framework-vendored-context=<direct-input-directory>", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("framework-cached-context=<direct-input-directory>", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("framework-installer-context=<direct-input-directory>", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(RepositoryRoot, result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("id=framework-installer-url", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void PreparationUsesDirectInputsWithoutHostStaging()
    {
        var source = File.ReadAllText(ScriptPath);
        Assert.Contains("framework-vendored-context", source, StringComparison.Ordinal);
        Assert.Contains("framework-cached-context", source, StringComparison.Ordinal);
        Assert.Contains("framework-installer-context", source, StringComparison.Ordinal);
        Assert.Contains("--build-context", source, StringComparison.Ordinal);
        Assert.Contains("ContextDirectory", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CommittedSourceContext", source, StringComparison.Ordinal);
        Assert.DoesNotContain("StagedBuildContext", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Guid.NewGuid", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Copy", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wow64BaseBuildHasNoTargetOrPrivateContext()
    {
        var arguments = CommonArguments("wow64-base");

        var result = await RunAsync(arguments);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        Assert.Contains("--target framework-wow64-base", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("FRAMEWORK_WOW64_BASE_IMAGE=operator/root:10.0@sha256:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("FRAMEWORK_SEED_IMAGE=operator/root:10.0@sha256:", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("FRAMEWORK_TARGET_ID=", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("framework-installer-context=", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("clr2", "3.5", "/opt/wine-netfx-clr2")]
    [InlineData("clr4", "4.8", "/opt/wine-netfx-clr4")]
    public async Task CompanionSeedUsesOneDigestPinnedWow64Base(string generation, string version, string prefix)
    {
        var arguments = CommonArguments("companion-seed").ToList();
        arguments.InsertRange(arguments.Count - 1, [
            "--seed-generation", generation,
            "--framework-wow64-base-image",
            $"operator/framework-wow64:cache@sha256:{new string('f', 64)}",
        ]);
        if (generation == "clr2")
        {
            arguments.InsertRange(arguments.Count - 1, [
                "--cached-winetricks-payload-file",
                Path.Combine(Path.GetTempPath(), "dotnetfx35.dry-run.exe"),
            ]);
        }

        var result = await RunAsync(arguments);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        Assert.Contains("--target framework-companion-seed", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"FRAMEWORK_SEED_GENERATION={generation}", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"FRAMEWORK_SEED_VERSION={version}", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"FRAMEWORK_SEED_PREFIX={prefix}", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("FRAMEWORK_WOW64_BASE_IMAGE=operator/framework-wow64:cache@sha256:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("FRAMEWORK_SEED_IMAGE=operator/root:10.0@sha256:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("framework-installer-context=<direct-input-directory>", result.StandardOutput, StringComparison.Ordinal);
        if (generation == "clr2")
            Assert.Contains("FRAMEWORK_INSTALLER_NETWORK=none", result.StandardOutput, StringComparison.Ordinal);
        else
            Assert.Contains("FRAMEWORK_INSTALLER_NETWORK=default", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("--network none", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("netfx30")]
    [InlineData("netfx35")]
    public async Task DotNet35Sp1TargetsRequireTheCachedWinetricksPayload(string targetId)
    {
        using var secrets = new SecretFiles();
        var missing = await RunAsync(ValidArguments(targetId, secrets, includeInstallerSource: false));

        Assert.Equal(64, missing.ExitCode);
        Assert.Contains("--cached-winetricks-payload-file", missing.StandardError, StringComparison.Ordinal);

        var arguments = ValidArguments(targetId, secrets, includeInstallerSource: false).ToList();
        arguments.InsertRange(arguments.Count - 1, [
            "--cached-winetricks-payload-file",
            Path.Combine(secrets.Root, "dotnetfx35.dry-run.exe"),
        ]);
        var accepted = await RunAsync(arguments);

        Assert.Equal(0, accepted.ExitCode);
        Assert.Empty(accepted.StandardError);
        Assert.Contains("FRAMEWORK_INSTALLER_NETWORK=none", accepted.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("--network none", accepted.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("framework-installer-context=<direct-input-directory>", accepted.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(secrets.Root, accepted.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("netfx451", "4.5.1")]
    [InlineData("netfx47", "4.7")]
    public async Task ManualTargetUsesUrlSecretWithoutDisclosingIt(string targetId, string version)
    {
        using var secrets = new SecretFiles();

        var result = await RunAsync(ValidArguments(targetId, secrets, includeInstallerSource: true));

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        Assert.Contains($"FRAMEWORK_VERSION={version}", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("id=framework-installer-url,src=", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(secrets.Url, result.CombinedOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualTargetRequiresExactlyOnePrivateInstallerSource()
    {
        using var secrets = new SecretFiles();

        var result = await RunAsync(ValidArguments("netfx451", secrets, includeInstallerSource: false));

        Assert.Equal(64, result.ExitCode);
        Assert.Contains("requires exactly one", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WinetricksTargetRejectsPrivateInstallerSource()
    {
        using var secrets = new SecretFiles();

        var result = await RunAsync(ValidArguments("netfx48", secrets, includeInstallerSource: true));

        Assert.Equal(64, result.ExitCode);
        Assert.Contains("does not accept an operator installer", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(secrets.Url, result.CombinedOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalManualInstallerDigestMismatchFailsBeforeDocker()
    {
        using var secrets = new SecretFiles();
        var arguments = ValidArguments("netfx451", secrets, includeInstallerSource: false).ToList();
        arguments.InsertRange(arguments.Count - 1, ["--installer-secret-file", secrets.InstallerPath]);

        var result = await RunAsync(arguments);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("does not match the manifest SHA-256", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(secrets.InstallerPath, result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FloatingBaseImageFailsAsUsage()
    {
        using var secrets = new SecretFiles();
        var arguments = ValidArguments("netfx48", secrets, includeInstallerSource: false);
        arguments[Array.IndexOf(arguments, "--base-image") + 1] = "operator/wine:latest";

        var result = await RunAsync(arguments);

        Assert.Equal(64, result.ExitCode);
        Assert.Contains("repository[:tag]@sha256", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FloatingRootImageFailsAsUsage()
    {
        using var secrets = new SecretFiles();
        var arguments = ValidArguments("netfx48", secrets, includeInstallerSource: false);
        arguments[Array.IndexOf(arguments, "--root-image") + 1] = "operator/root:latest";

        var result = await RunAsync(arguments);

        Assert.Equal(64, result.ExitCode);
        Assert.Contains("--root-image must use repository[:tag]@sha256", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingEulaAcceptanceFailsAsUsage()
    {
        using var secrets = new SecretFiles();
        var arguments = ValidArguments("netfx48", secrets, includeInstallerSource: false).Where(argument => argument != "--accept-microsoft-dotnet-framework-eula").ToArray();

        var result = await RunAsync(arguments);

        Assert.Equal(64, result.ExitCode);
        Assert.Contains("--accept-microsoft-dotnet-framework-eula is required", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownTargetFailsBeforeDocker()
    {
        using var secrets = new SecretFiles();
        var arguments = ValidArguments("netfx48", secrets, includeInstallerSource: false);
        arguments[Array.IndexOf(arguments, "--target-id") + 1] = "netfx49";

        var result = await RunAsync(arguments);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("is not present in the Framework installer manifest", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceRevisionMustMatchRepositoryHead()
    {
        using var secrets = new SecretFiles();
        var arguments = ValidArguments("netfx48", secrets, includeInstallerSource: false);
        arguments[Array.IndexOf(arguments, "--source-revision") + 1] = new string('0', 40);

        var result = await RunAsync(arguments, contentIdentity: false);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("does not match Git HEAD", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(VendoredPayloadState.Missing, "Git LFS object is missing")]
    [InlineData(VendoredPayloadState.UnexpandedLfsObject, "unexpanded Git LFS pointer")]
    [InlineData(VendoredPayloadState.Corrupt, "SHA-256 does not match")]
    public async Task VendoredPayloadFailureStopsBeforeDocker(VendoredPayloadState state, string expectedError)
    {
        using var fixture = new FrameworkRepositoryFixture(state);
        using var secrets = new SecretFiles();

        var result = await RunAsync(ValidArguments("netfx48", secrets, includeInstallerSource: false, fixture.Root, fixture.Revision));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(expectedError, result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("docker buildx build", result.StandardOutput, StringComparison.Ordinal);
    }

    private static string[] ValidArguments(string targetId, SecretFiles secrets, bool includeInstallerSource, string? repositoryRoot = null, string? sourceRevision = null)
    {
        repositoryRoot ??= RepositoryRoot;
        sourceRevision ??= RepositoryRevision;
        var arguments = new List<string>
        {
            "--repository-root", repositoryRoot,
            "--target-id", targetId,
            "--base-image", $"operator/wine:9.0@sha256:{new string('c', 64)}",
            "--root-image", $"operator/root:10.0@sha256:{new string('d', 64)}",
            "--framework-seed-image", $"operator/framework-seed:cache@sha256:{new string('e', 64)}",
            "--seed-input-sha256", new string('a', 64),
            "--output-image", $"sharplabnext/operator-{targetId}:test",
            "--source-revision", sourceRevision,
            "--accept-microsoft-dotnet-framework-eula",
        };
        if (includeInstallerSource)
        {
            arguments.Add("--installer-url-secret-file");
            arguments.Add(secrets.UrlPath);
        }
        arguments.Add("--dry-run");
        return arguments.ToArray();
    }

    private static string[] CommonArguments(string buildKind) =>
    [
        "--repository-root", RepositoryRoot,
        "--build-kind", buildKind,
        "--base-image", $"operator/wine:9.0@sha256:{new string('c', 64)}",
        "--root-image", $"operator/root:10.0@sha256:{new string('d', 64)}",
        "--seed-input-sha256", new string('a', 64),
        "--output-image", $"sharplabnext/framework-{buildKind}:test",
        "--source-revision", RepositoryRevision,
        "--accept-microsoft-dotnet-framework-eula",
        "--dry-run",
    ];

    private static async Task<ProcessResult> RunAsync(IEnumerable<string> arguments, bool contentIdentity = true)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryRoot
        };
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        if (contentIdentity) startInfo.Environment["SHARPLABNEXT_SOURCE_IDENTITY_MODE"] = "content";
        else startInfo.Environment.Remove("SHARPLABNEXT_SOURCE_IDENTITY_MODE");
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add(ScriptPath);
        startInfo.ArgumentList.Add("--");
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the Framework preparation script test process.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SharpLabNext.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("SharpLabNext.slnx was not found above the test output directory.");
    }

    private static string ReadRepositoryRevision()
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryRoot
        };
        startInfo.ArgumentList.Add("rev-parse");
        startInfo.ArgumentList.Add("--verify");
        startInfo.ArgumentList.Add("HEAD");
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not read the repository revision.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Could not read the repository revision.");
        return output.Trim();
    }

    private sealed class SecretFiles : IDisposable
    {
        public SecretFiles()
        {
            Root = Path.Combine(Path.GetTempPath(), $"SharpLabNext.FrameworkPreparation.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            UrlPath = Path.Combine(Root, "installer-url.secret");
            InstallerPath = Path.Combine(Root, "installer.secret");
            Url = "https://operator.invalid/framework-installer.exe?token=framework-secret-token";
            File.WriteAllText(UrlPath, Url);
            File.WriteAllBytes(InstallerPath, new byte[(500 * 1024) + 1]);
            InstallerSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(InstallerPath)));
        }

        public string Root { get; }
        public string UrlPath { get; }
        public string InstallerPath { get; }
        public string InstallerSha256 { get; }
        public string Url { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public enum VendoredPayloadState
    {
        Missing,
        UnexpandedLfsObject,
        Corrupt
    }

    private sealed class FrameworkRepositoryFixture : IDisposable
    {
        private const string PayloadRelativePath = "eng/prerequisites/dotnet-framework-2.0/NetFx64.exe";

        public FrameworkRepositoryFixture(VendoredPayloadState state)
        {
            Root = Path.Combine(Path.GetTempPath(), $"SharpLabNext.FrameworkRepository.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            RunGit("init", "--quiet");
            File.WriteAllText(Path.Combine(Root, "SharpLabNext.slnx"), "<Solution />\n");
            File.WriteAllText(Path.Combine(Root, "fixture.txt"), "fixture\n");
            RunGit("add", "fixture.txt");
            RunGit("-c", "user.name=SharpLabNext Tests", "-c", "user.email=tests@sharplabnext.invalid", "commit", "--quiet", "-m", "fixture");
            Revision = RunGit("rev-parse", "--verify", "HEAD").Trim();

            var profiles = Path.Combine(Root, "profiles");
            Directory.CreateDirectory(profiles);
            File.Copy(Path.Combine(RepositoryRoot, "profiles", "runtime-framework-installers.json"), Path.Combine(profiles, "runtime-framework-installers.json"));
            File.Copy(Path.Combine(RepositoryRoot, "profiles", "runtime-matrix.json"), Path.Combine(profiles, "runtime-matrix.json"));
            File.WriteAllText(Path.Combine(Root, ".gitattributes"), $"{PayloadRelativePath} filter=lfs diff=lfs merge=lfs -text\n");

            var payloadPath = Path.Combine(Root, PayloadRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
            switch (state)
            {
                case VendoredPayloadState.Missing:
                    break;
                case VendoredPayloadState.UnexpandedLfsObject:
                    File.WriteAllText(
                        payloadPath,
                        "version https://git-lfs.github.com/spec/v1\n" +
                        "oid sha256:7ea86dca8eeaedcaa4a17370547ca2cea9e9b6774972b8e03d2cb1fb0e798669\n" +
                        "size 47400128\n");
                    break;
                case VendoredPayloadState.Corrupt:
                    using (var stream = new FileStream(payloadPath, FileMode.CreateNew, FileAccess.Write))
                        stream.SetLength(47_400_128);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(state));
            }
        }

        public string Root { get; }
        public string Revision { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private string RunGit(params string[] arguments)
        {
            var startInfo = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = Root
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Git for the Framework fixture.");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Framework fixture Git failed: {error}");
            return output;
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string CombinedOutput => StandardOutput + StandardError;
    }
}
