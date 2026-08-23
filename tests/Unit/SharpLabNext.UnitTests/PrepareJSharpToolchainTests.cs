using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SharpLabNext.UnitTests;

public sealed class PrepareJSharpToolchainTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string ScriptPath = Path.Combine(
        RepositoryRoot,
        "eng",
        "prepare-jsharp-toolchain.cs");
    private static readonly string DockerfilePath = Path.Combine(
        RepositoryRoot,
        "deploy",
        "docker",
        "Dockerfile.operator-jsharp20");

    [Fact]
    public async Task DryRunBuildsRedactedOperatorImageCommand()
    {
        using var secrets = new SecretFiles();
        var result = await RunAsync(ValidArguments(secrets));

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        Assert.Contains("docker buildx build", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Dockerfile.operator-jsharp20", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--load", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--provenance=false", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            $"BASE_IMAGE=codex/msvc-wine:cppcli@sha256:{new string('c', 64)}",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            "--tag sharplabnext/operator-jsharp20:test",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            $"CLR2_INSTALLER_SHA256={new string('a', 64)}",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            $"JSHARP_INSTALLER_SHA256={new string('b', 64)}",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains("id=dotnet-clr2-url,src=", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("id=visual-jsharp-url,src=", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "dotnet-clr2-installer-context=<staged-local-context>",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            "visual-jsharp-installer-context=<staged-local-context>",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.DoesNotContain(secrets.Clr2Url, result.CombinedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(secrets.JSharpUrl, result.CombinedOutput, StringComparison.Ordinal);
        Assert.Contains(secrets.Clr2Path, result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(secrets.JSharpPath, result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LargeLocalInstallersUseRedactedIndependentBuildContextsAndAreCleanedUp()
    {
        using var secrets = new SecretFiles();

        var result = await RunAsync(LocalInstallerArguments(secrets));

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        Assert.True(new FileInfo(secrets.Clr2InstallerPath).Length > 500 * 1024);
        Assert.True(new FileInfo(secrets.JSharpInstallerPath).Length > 500 * 1024);
        Assert.Contains("--provenance=false", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "dotnet-clr2-installer-context=<staged-local-context>",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            "visual-jsharp-installer-context=<staged-local-context>",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.DoesNotContain("id=dotnet-clr2-installer", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("id=visual-jsharp-installer", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(secrets.Clr2InstallerPath, result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secrets.JSharpInstallerPath, result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secrets.Clr2Url, result.CombinedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(secrets.JSharpUrl, result.CombinedOutput, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateDirectories(
            secrets.Root,
            ".sharplabnext-jsharp-context-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task LocalInstallerContextsAreCleanedWhenDockerCannotStart()
    {
        using var secrets = new SecretFiles();
        var arguments = LocalInstallerArguments(secrets).ToList();
        arguments.Remove("--dry-run");
        arguments.InsertRange(2, [
            "--docker-command",
            Path.Combine(secrets.Root, "missing-docker-command")
        ]);

        var result = await RunAsync(arguments);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Could not start Docker Buildx", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(secrets.Clr2InstallerPath, result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secrets.JSharpInstallerPath, result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateDirectories(
            secrets.Root,
            ".sharplabnext-jsharp-context-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task LocalInstallerDigestMismatchFailsBeforeDocker()
    {
        using var secrets = new SecretFiles();
        var arguments = LocalInstallerArguments(secrets);
        arguments[Array.IndexOf(arguments, "--jsharp-sha256") + 1] = new string('d', 64);

        var result = await RunAsync(arguments);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("does not match its required SHA-256 digest", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(secrets.JSharpInstallerPath, result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UrlAndLocalInstallerForOneAssetAreMutuallyExclusive()
    {
        using var secrets = new SecretFiles();
        var arguments = ValidArguments(secrets).ToList();
        arguments.InsertRange(
            arguments.Count - 1,
            ["--jsharp-installer-secret-file", secrets.JSharpInstallerPath]);

        var result = await RunAsync(arguments);

        Assert.Equal(64, result.ExitCode);
        Assert.Contains("requires exactly one of", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(secrets.JSharpUrl, result.CombinedOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FloatingBaseImageFailsAsUsage()
    {
        using var secrets = new SecretFiles();
        var arguments = ValidArguments(secrets);
        arguments[Array.IndexOf(arguments, "--base-image") + 1] = "codex/msvc-wine:cppcli";

        var result = await RunAsync(arguments);

        Assert.Equal(64, result.ExitCode);
        Assert.Contains("repository[:tag]@sha256", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void OperatorDockerfileLocksPrivateX64BootstrapContract()
    {
        var source = File.ReadAllText(DockerfilePath);

        Assert.Contains("ARG BASE_IMAGE", source, StringComparison.Ordinal);
        Assert.Contains("FROM ${BASE_IMAGE}", source, StringComparison.Ordinal);
        Assert.Contains("@sha256:[0-9a-f]{64}", source, StringComparison.Ordinal);
        Assert.Contains("id=dotnet-clr2-url", source, StringComparison.Ordinal);
        Assert.Contains("id=visual-jsharp-url", source, StringComparison.Ordinal);
        Assert.Contains("from=dotnet-clr2-installer-context", source, StringComparison.Ordinal);
        Assert.Contains("from=visual-jsharp-installer-context", source, StringComparison.Ordinal);
        Assert.DoesNotContain("id=dotnet-clr2-installer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("id=visual-jsharp-installer", source, StringComparison.Ordinal);
        Assert.Contains("sha256sum --check --status", source, StringComparison.Ordinal);
        Assert.Contains("${W_CACHE}/dotnet35sp1/dotnetfx35.exe", source, StringComparison.Ordinal);
        Assert.Contains("[jsharp-bootstrap] stage=%s", source, StringComparison.Ordinal);
        Assert.Contains("tail -c 16384", source, StringComparison.Ordinal);
        Assert.Contains("tail -n 80", source, StringComparison.Ordinal);
        Assert.Contains("<redacted-url>", source, StringComparison.Ordinal);
        Assert.Contains("run_logged install-clr2-winetricks", source, StringComparison.Ordinal);
        Assert.Contains("timeout --signal=KILL 900 xvfb-run -a", source, StringComparison.Ordinal);
        Assert.Contains("winetricks --optout --unattended dotnet35sp1", source, StringComparison.Ordinal);
        Assert.Contains("run_logged install-jsharp-bootstrap install_jsharp", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "winetricks --optout --unattended dotnet35sp1 >/dev/null",
            source,
            StringComparison.Ordinal);
        Assert.Contains("test \"${ACCEPT_MICROSOFT_DOTNET_EULA}\" = \"true\"", source, StringComparison.Ordinal);
        Assert.Contains("test \"${ACCEPT_MICROSOFT_JSHARP_EULA}\" = \"true\"", source, StringComparison.Ordinal);
        Assert.Contains("WINEPREFIX=/opt/wine-jsharp20", source, StringComparison.Ordinal);
        Assert.Contains("WINEARCH=win64", source, StringComparison.Ordinal);
        Assert.Contains("Framework64/v2.0.50727", source, StringComparison.Ordinal);
        Assert.Contains("windows/assembly/GAC_64", source, StringComparison.Ordinal);
        Assert.Contains("architecture: i386:x86-64", source, StringComparison.Ordinal);
        Assert.Contains("vjc.exe", source, StringComparison.Ordinal);
        Assert.Contains("vjslib.dll", source, StringComparison.Ordinal);
        Assert.Contains("vjscor.dll", source, StringComparison.Ordinal);
        Assert.Contains("vjsnativ.dll", source, StringComparison.Ordinal);
        Assert.Contains("framework64-vjslib", source, StringComparison.Ordinal);
        Assert.Contains("framework64-vjscor", source, StringComparison.Ordinal);
        Assert.Contains("gac64-vjslib-runtime", source, StringComparison.Ordinal);
        Assert.DoesNotContain("gac64-vjscor", source, StringComparison.Ordinal);
        Assert.Contains("arch-vjslib", source, StringComparison.Ordinal);
        Assert.Contains("neutral-ilonly-vjscor", source, StringComparison.Ordinal);
        Assert.Contains("flags & (0x2 | 0x10 | 0x20000)", source, StringComparison.Ordinal);
        Assert.Contains("arch-vjsnativ", source, StringComparison.Ordinal);
        Assert.Contains("private-installer-scan", source, StringComparison.Ordinal);
        Assert.Contains("[jsharp-verify] failed=%s", source, StringComparison.Ordinal);
        Assert.Contains("stage cleanup-private-assets", source, StringComparison.Ordinal);
        Assert.Contains("operator_toolchain=/opt/sharplabnext/jsharp20", source, StringComparison.Ordinal);
        Assert.Contains("link_export export-vjc", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/opt/sharplabnext/operator/jsharp20", source, StringComparison.Ordinal);
        Assert.Contains("trap cleanup EXIT", source, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(new Regex(@"COPY[^\r\n]*\.(?:exe|msi|cab)\b", RegexOptions.IgnoreCase), source);
        var firstLineEnd = source.IndexOf('\n');
        Assert.True(firstLineEnd >= 0);
        var instructions = source[(firstLineEnd + 1)..];
        Assert.DoesNotMatch(new Regex(@"sha256:[0-9a-f]{64}"), instructions);

        var installComplete = source.IndexOf(
            "run_logged install-jsharp-wineserver wineserver -w",
            StringComparison.Ordinal);
        var cachedLayerEnd = source.IndexOf("\nSH\n", installComplete, StringComparison.Ordinal);
        var verifyLayer = source.IndexOf("RUN set -euo pipefail <<'SH'", cachedLayerEnd, StringComparison.Ordinal);
        Assert.True(installComplete >= 0 && cachedLayerEnd > installComplete && verifyLayer > cachedLayerEnd);
        var verifySource = source[verifyLayer..source.IndexOf("\nLABEL ", verifyLayer, StringComparison.Ordinal)];
        Assert.DoesNotContain("--mount=", verifySource, StringComparison.Ordinal);
        Assert.DoesNotContain("id=dotnet-clr2-url", verifySource, StringComparison.Ordinal);
        Assert.DoesNotContain("id=visual-jsharp-url", verifySource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--accept-microsoft-dotnet-eula")]
    [InlineData("--accept-microsoft-jsharp-eula")]
    public async Task MissingEulaAcceptanceFailsAsUsage(string omitted)
    {
        using var secrets = new SecretFiles();
        var arguments = ValidArguments(secrets).Where(argument => argument != omitted).ToArray();

        var result = await RunAsync(arguments);

        Assert.Equal(64, result.ExitCode);
        Assert.Contains("Both --accept-microsoft-dotnet-eula", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(secrets.Clr2Url, result.CombinedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(secrets.JSharpUrl, result.CombinedOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--clr2-sha256")]
    [InlineData("--jsharp-sha256")]
    [InlineData("--base-image")]
    [InlineData("--output-image")]
    public async Task MissingRequiredAssetArgumentFailsAsUsage(string omitted)
    {
        using var secrets = new SecretFiles();
        var arguments = ValidArguments(secrets).ToList();
        var optionIndex = arguments.IndexOf(omitted);
        arguments.RemoveRange(optionIndex, 2);

        var result = await RunAsync(arguments);

        Assert.Equal(64, result.ExitCode);
        Assert.Contains("Base/output images", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(secrets.Clr2Url, result.CombinedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(secrets.JSharpUrl, result.CombinedOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--clr2-url-secret-file", "CLR2")]
    [InlineData("--jsharp-url-secret-file", "Visual J# x64")]
    public async Task MissingAssetSourceFailsAsUsage(string omitted, string assetName)
    {
        using var secrets = new SecretFiles();
        var arguments = ValidArguments(secrets).ToList();
        var optionIndex = arguments.IndexOf(omitted);
        arguments.RemoveRange(optionIndex, 2);

        var result = await RunAsync(arguments);

        Assert.Equal(64, result.ExitCode);
        Assert.Contains($"{assetName} requires exactly one of", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(secrets.Clr2Url, result.CombinedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(secrets.JSharpUrl, result.CombinedOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BlankRequiredValueFailsAsUsage()
    {
        using var secrets = new SecretFiles();
        var arguments = ValidArguments(secrets);
        arguments[Array.IndexOf(arguments, "--clr2-sha256") + 1] = "   ";

        var result = await RunAsync(arguments);

        Assert.Equal(64, result.ExitCode);
        Assert.Contains("requires a non-empty value", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(secrets.Clr2Url, result.CombinedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(secrets.JSharpUrl, result.CombinedOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--clr2-sha256", "abc")]
    [InlineData("--jsharp-sha256", "gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public async Task InvalidSha256FailsAsUsage(string option, string invalidDigest)
    {
        using var secrets = new SecretFiles();
        var arguments = ValidArguments(secrets);
        arguments[Array.IndexOf(arguments, option) + 1] = invalidDigest;

        var result = await RunAsync(arguments);

        Assert.Equal(64, result.ExitCode);
        Assert.Contains("exactly 64 hexadecimal", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(secrets.Clr2Url, result.CombinedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(secrets.JSharpUrl, result.CombinedOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingUrlSecretFileFailsClosedWithoutRevealingPath()
    {
        using var secrets = new SecretFiles();
        var missingPath = Path.Combine(secrets.Root, "missing-clr2-url.secret");
        var arguments = ValidArguments(secrets);
        arguments[Array.IndexOf(arguments, "--clr2-url-secret-file") + 1] = missingPath;

        var result = await RunAsync(arguments);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("CLR2 URL secret file does not exist", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(missingPath, result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secrets.JSharpUrl, result.CombinedOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyUrlSecretFileFailsClosed()
    {
        using var secrets = new SecretFiles(clr2Url: "   \r\n");

        var result = await RunAsync(ValidArguments(secrets));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("must contain one absolute HTTP(S) URL", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(secrets.JSharpUrl, result.CombinedOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownArgumentFailsAsUsageBeforeSecretsAreRead()
    {
        using var secrets = new SecretFiles();
        var arguments = ValidArguments(secrets).Append("--unexpected").ToArray();

        var result = await RunAsync(arguments);

        Assert.Equal(64, result.ExitCode);
        Assert.Contains("Unknown argument", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(secrets.Clr2Url, result.CombinedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(secrets.JSharpUrl, result.CombinedOutput, StringComparison.Ordinal);
    }

    private static string[] ValidArguments(SecretFiles secrets) =>
    [
        "--repository-root", RepositoryRoot,
        "--base-image", $"codex/msvc-wine:cppcli@sha256:{new string('c', 64)}",
        "--output-image", "sharplabnext/operator-jsharp20:test",
        "--clr2-url-secret-file", secrets.Clr2Path,
        "--clr2-sha256", new string('a', 64),
        "--jsharp-url-secret-file", secrets.JSharpPath,
        "--jsharp-sha256", new string('b', 64),
        "--accept-microsoft-dotnet-eula",
        "--accept-microsoft-jsharp-eula",
        "--dry-run"
    ];

    private static string[] LocalInstallerArguments(SecretFiles secrets) =>
    [
        "--repository-root", RepositoryRoot,
        "--base-image", $"codex/msvc-wine:cppcli@sha256:{new string('c', 64)}",
        "--output-image", "sharplabnext/operator-jsharp20:test",
        "--clr2-installer-secret-file", secrets.Clr2InstallerPath,
        "--clr2-sha256", secrets.Clr2InstallerSha256,
        "--jsharp-installer-secret-file", secrets.JSharpInstallerPath,
        "--jsharp-sha256", secrets.JSharpInstallerSha256,
        "--accept-microsoft-dotnet-eula",
        "--accept-microsoft-jsharp-eula",
        "--dry-run"
    ];

    private static async Task<ProcessResult> RunAsync(IEnumerable<string> arguments)
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
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add(ScriptPath);
        startInfo.ArgumentList.Add("--");
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the J# preparation script test process.");
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

    private sealed class SecretFiles : IDisposable
    {
        public SecretFiles(
            string clr2Url = "https://operator.invalid/clr2-installer.exe?token=clr2-secret-token",
            string jsharpUrl = "https://operator.invalid/jsharp-installer.exe?token=jsharp-secret-token")
        {
            Root = Path.Combine(Path.GetTempPath(), $"SharpLabNext.JSharpPreparation.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Clr2Path = Path.Combine(Root, "clr2-url.secret");
            JSharpPath = Path.Combine(Root, "jsharp-url.secret");
            Clr2InstallerPath = Path.Combine(Root, "clr2-installer.secret");
            JSharpInstallerPath = Path.Combine(Root, "jsharp-installer.secret");
            Clr2Url = clr2Url;
            JSharpUrl = jsharpUrl;
            File.WriteAllText(Clr2Path, clr2Url);
            File.WriteAllText(JSharpPath, jsharpUrl);
            File.WriteAllBytes(Clr2InstallerPath, new byte[(500 * 1024) + 1]);
            File.WriteAllBytes(JSharpInstallerPath, Enumerable.Repeat((byte)0x5a, (500 * 1024) + 1).ToArray());
            Clr2InstallerSha256 = FileSha256(Clr2InstallerPath);
            JSharpInstallerSha256 = FileSha256(JSharpInstallerPath);
        }

        public string Root { get; }
        public string Clr2Path { get; }
        public string JSharpPath { get; }
        public string Clr2InstallerPath { get; }
        public string JSharpInstallerPath { get; }
        public string Clr2InstallerSha256 { get; }
        public string JSharpInstallerSha256 { get; }
        public string Clr2Url { get; }
        public string JSharpUrl { get; }

        private static string FileSha256(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string CombinedOutput => StandardOutput + StandardError;
    }
}
