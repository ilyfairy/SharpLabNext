using System.Diagnostics;

namespace SharpLabNext.UnitTests;

public sealed class PrepareJSharpToolchainTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string ScriptPath = Path.Combine(RepositoryRoot, "eng", "tools", "prepare-jsharp-toolchain.cs");
    private static readonly string DockerfilePath = Path.Combine(RepositoryRoot, "deploy", "docker", "Dockerfile.operator-jsharp20");

    [Fact]
    public async Task DryRunUsesOneFixedLfsInstallerAndClr2FrameworkSeed()
    {
        var result = await RunAsync(ValidArguments());

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        Assert.Contains("docker buildx build", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Dockerfile.operator-jsharp20", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--load", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--provenance=false", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"FRAMEWORK_SEED_IMAGE=localhost:5000/framework-clr2@sha256:{new string('a', 64)}", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("visual-jsharp-installer-context=<repository-lfs-context>", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"OPERATOR_BUILD_INPUT_SHA256={new string('b', 64)}", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("url", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clr2-installer", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.Combine(RepositoryRoot, "eng", "prerequisites"), result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("--accept-microsoft-dotnet-eula")]
    [InlineData("--accept-microsoft-jsharp-eula")]
    public async Task BothMicrosoftLicenseAcceptancesAreRequired(string omitted)
    {
        var arguments = ValidArguments().Where(argument => argument != omitted);

        var result = await RunAsync(arguments);

        Assert.Equal(64, result.ExitCode);
        Assert.Contains("required", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FloatingFrameworkSeedFailsBeforeReadingLfsInput()
    {
        var arguments = ValidArguments().ToArray();
        arguments[Array.IndexOf(arguments, "--framework-seed-image") + 1] =
            "localhost:5000/framework-clr2:latest";

        var result = await RunAsync(arguments);

        Assert.Equal(64, result.ExitCode);
        Assert.Contains("--framework-seed-image", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingLfsObjectFailsBeforeDocker()
    {
        using var fixture = new RepositoryFixture();

        var result = await RunAsync(ValidArguments(fixture.Root));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Git LFS object is missing", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("docker buildx", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpandedLfsPointerFailsBeforeDocker()
    {
        using var fixture = new RepositoryFixture();
        fixture.WriteInstaller(
            "version https://git-lfs.github.com/spec/v1\n" +
            $"oid sha256:{new string('c', 64)}\n" +
            "size 6110048\n");

        var result = await RunAsync(ValidArguments(fixture.Root));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("unexpanded Git LFS pointer", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("docker buildx", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorruptLfsPayloadFailsBeforeDocker()
    {
        using var fixture = new RepositoryFixture();
        fixture.WriteInstaller(new byte[6_110_048]);

        var result = await RunAsync(ValidArguments(fixture.Root));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("SHA-256", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("docker buildx", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void OperatorDockerfileInstallsOnlyJSharpIntoTheClr2Seed()
    {
        var source = File.ReadAllText(DockerfilePath);

        Assert.Contains("ARG FRAMEWORK_SEED_IMAGE", source, StringComparison.Ordinal);
        Assert.Contains("FROM ${FRAMEWORK_SEED_IMAGE} AS final", source, StringComparison.Ordinal);
        Assert.Contains("from=visual-jsharp-installer-context", source, StringComparison.Ordinal);
        Assert.Contains("vjredist64.exe", source, StringComparison.Ordinal);
        Assert.Contains("seed_prefix=/opt/wine-netfx-clr2", source, StringComparison.Ordinal);
        Assert.Contains("WINEPREFIX=\"${seed_prefix}\" timeout", source, StringComparison.Ordinal);
        Assert.Contains("sharplabnext-wine-netfx-preflight \"${seed_prefix}\" 3.5", source, StringComparison.Ordinal);
        Assert.Contains("mv \"${seed_prefix}\" \"${WINEPREFIX}\"", source, StringComparison.Ordinal);
        Assert.True(source.IndexOf("stage preflight-framework-seed", StringComparison.Ordinal) < source.IndexOf("stage bind-jsharp-prefix", StringComparison.Ordinal));
        Assert.Contains("stage install-jsharp", source, StringComparison.Ordinal);
        Assert.Contains("run_logged install-jsharp-bootstrap", source, StringComparison.Ordinal);
        Assert.Contains("test -x /usr/lib/wine/wineserver", source, StringComparison.Ordinal);
        Assert.Contains("env WINEPREFIX=\"${seed_prefix}\" /usr/lib/wine/wineserver -w", source, StringComparison.Ordinal);
        Assert.DoesNotContain("run_logged install-jsharp-wineserver wineserver -w", source, StringComparison.Ordinal);
        Assert.Contains("[jsharp-verify] status=ok", source, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet-clr2-url", source, StringComparison.Ordinal);
        Assert.DoesNotContain("visual-jsharp-url", source, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnetfx35.exe", source, StringComparison.Ordinal);
        Assert.DoesNotContain("winetricks", source, StringComparison.Ordinal);
    }

    private static string[] ValidArguments(string? repositoryRoot = null) =>
    [
        "--repository-root", repositoryRoot ?? RepositoryRoot,
        "--framework-seed-image", $"localhost:5000/framework-clr2@sha256:{new string('a', 64)}",
        "--output-image", "sharplabnext/operator-jsharp20:source-v2",
        "--operator-build-input-sha256", new string('b', 64),
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

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the J# preparation script test process.");
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

    private sealed class RepositoryFixture : IDisposable
    {
        public RepositoryFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"SharpLabNext.JSharpLfs.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(Root, "eng", "prerequisites", "visual-jsharp-2.0-se-x64"));
            Directory.CreateDirectory(Path.Combine(Root, "deploy", "docker"));
            File.WriteAllText(Path.Combine(Root, "SharpLabNext.slnx"), string.Empty);
            File.WriteAllText(Path.Combine(Root, "deploy", "docker", "Dockerfile.operator-jsharp20"), "FROM scratch\n");
            File.WriteAllText(Path.Combine(Root, ".gitattributes"), "eng/prerequisites/visual-jsharp-2.0-se-x64/vjredist64.exe " + "filter=lfs diff=lfs merge=lfs -text\n");
            RunGit("init", "--quiet");
        }

        public string Root { get; }

        public void WriteInstaller(string content) => File.WriteAllText(InstallerPath, content);

        public void WriteInstaller(byte[] content) => File.WriteAllBytes(InstallerPath, content);

        private string InstallerPath => Path.Combine(Root, "eng", "prerequisites", "visual-jsharp-2.0-se-x64", "vjredist64.exe");

        private void RunGit(params string[] arguments)
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = Root,
                UseShellExecute = false,
                RedirectStandardError = true
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Git for the test fixture.");
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(process.StandardError.ReadToEnd());
        }

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

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
