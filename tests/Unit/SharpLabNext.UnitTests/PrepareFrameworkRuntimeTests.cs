using System.Diagnostics;
using System.Security.Cryptography;

namespace SharpLabNext.UnitTests;

public sealed class PrepareFrameworkRuntimeTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string ScriptPath = Path.Combine(
        RepositoryRoot,
        "eng",
        "prepare-framework-runtime.cs");
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
        Assert.Contains($"SOURCE_REVISION={RepositoryRevision}", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("ROOT_IMAGE=operator/root:10.0@sha256:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("<committed-source-context>", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("framework-installer-context=<staged-local-context>", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(RepositoryRoot, result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("id=framework-installer-url", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void CommittedSourceContextHasAnExplicitMinimalAllowlist()
    {
        var source = File.ReadAllText(ScriptPath);
        string[] requiredFiles =
        [
            "deploy/docker/Dockerfile.operator-wine-framework-matrix",
            "profiles/runtime-framework-installers.json",
            "profiles/runtime-matrix.json",
            "deploy/docker/wine-netfx-framework-preflight.sh",
            "deploy/docker/dedupe-wine-prefixes.py",
            "deploy/docker/certificates/microsoft-tls-rsa-root-g2-xsign.crt",
            "deploy/docker/certificates/microsoft-tls-g2-rsa-ca-ocsp-04.crt"
        ];

        Assert.Contains("CommittedSourceFiles", source, StringComparison.Ordinal);
        Assert.Contains("git", source, StringComparison.Ordinal);
        Assert.Contains("show", source, StringComparison.Ordinal);
        Assert.Contains("--no-textconv", source, StringComparison.Ordinal);
        foreach (var file in requiredFiles)
            Assert.Contains(file, source, StringComparison.Ordinal);
        Assert.Contains(
            "ValidateSourceState(inputs.RepositoryRoot, sourceRevision, dryRun: false)",
            source,
            StringComparison.Ordinal);
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
        var arguments = ValidArguments("netfx48", secrets, includeInstallerSource: false)
            .Where(argument => argument != "--accept-microsoft-dotnet-framework-eula")
            .ToArray();

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

        var result = await RunAsync(arguments);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("does not match Git HEAD", result.StandardError, StringComparison.Ordinal);
    }

    private static string[] ValidArguments(
        string targetId,
        SecretFiles secrets,
        bool includeInstallerSource)
    {
        var arguments = new List<string>
        {
            "--repository-root", RepositoryRoot,
            "--target-id", targetId,
            "--base-image", $"operator/wine:9.0@sha256:{new string('c', 64)}",
            "--root-image", $"operator/root:10.0@sha256:{new string('d', 64)}",
            "--output-image", $"sharplabnext/operator-{targetId}:test",
            "--source-revision", RepositoryRevision,
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
            ?? throw new InvalidOperationException("Could not start the Framework preparation script test process.");
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
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not read the repository revision.");
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
