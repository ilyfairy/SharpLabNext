using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace SharpLabNext.UnitTests;

public sealed class PrepareCppCliToolchainTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string ScriptPath = Path.Combine(RepositoryRoot, "eng", "tools", "prepare-cppcli-toolchain.cs");
    private static readonly string DockerfilePath = Path.Combine(RepositoryRoot, "deploy", "docker", "Dockerfile.operator-cppcli-base");

    [Fact]
    public async Task DryRunPassesVerifiedInputsOnlyAsDockerContexts()
    {
        using var fixture = new RepositoryFixture();

        var result = await RunAsync(fixture.ValidArguments());

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        Assert.Contains("docker buildx build", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Dockerfile.operator-cppcli-base", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"FRAMEWORK_SEED_IMAGE=localhost:5000/framework-clr4@sha256:{new string('a', 64)}", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("cppcli-prerequisite-context=<direct-input-directory>", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("MSVC_WINE_SOURCE_FILE=msvc-wine-source.bin", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("VISUAL_STUDIO_MANIFEST_FILE=visual-studio-manifest.bin", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("NETFX48_DEVELOPER_PACK_FILE=netfx48-developer-pack.bin", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.MsvcWinePath, result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.VisualStudioManifestPath, result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.DeveloperPackPath, result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".exe --", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("--accept-microsoft-cpp-build-tools-license")]
    [InlineData("--accept-microsoft-dotnet-eula")]
    public async Task BothMicrosoftLicenseAcceptancesAreRequired(string omitted)
    {
        using var fixture = new RepositoryFixture();

        var result = await RunAsync(fixture.ValidArguments().Where(argument => argument != omitted));

        Assert.Equal(64, result.ExitCode);
        Assert.Contains("required", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DownloadDigestMismatchFailsBeforeDocker()
    {
        using var fixture = new RepositoryFixture();
        File.WriteAllBytes(fixture.DeveloperPackPath, [0xff]);

        var result = await RunAsync(fixture.ValidArguments());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("netfx48-developer-pack", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("SHA-256", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("docker buildx", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NuGetPackageCannotSatisfyCppCliFileLock()
    {
        using var fixture = new RepositoryFixture();
        fixture.WriteManifest(nonFileId: "msvc-wine-source");

        var result = await RunAsync(fixture.ValidArguments());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("msvc-wine-source", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("invalid", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker buildx", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FloatingFrameworkSeedIsRejected()
    {
        using var fixture = new RepositoryFixture();
        var arguments = fixture.ValidArguments();
        arguments[Array.IndexOf(arguments, "--framework-seed-image") + 1] =
            "localhost:5000/framework-clr4:latest";

        var result = await RunAsync(arguments);

        Assert.Equal(64, result.ExitCode);
        Assert.Contains("--framework-seed-image", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void DockerfileDownloadsPackagesFromLockedManifestAndNeverRunsHostInstallers()
    {
        var source = File.ReadAllText(DockerfilePath);

        Assert.Contains("ARG FRAMEWORK_SEED_IMAGE", source, StringComparison.Ordinal);
        Assert.Contains("FROM ${FRAMEWORK_SEED_IMAGE} AS toolchain-build", source, StringComparison.Ordinal);
        Assert.Contains("from=cppcli-prerequisite-context", source, StringComparison.Ordinal);
        Assert.Contains("MSVC_WINE_SOURCE_FILE", source, StringComparison.Ordinal);
        Assert.Contains("VISUAL_STUDIO_MANIFEST_FILE", source, StringComparison.Ordinal);
        Assert.Contains("NETFX48_DEVELOPER_PACK_FILE", source, StringComparison.Ordinal);
        Assert.Contains("vsdownload.py", source, StringComparison.Ordinal);
        Assert.Contains("--manifest", source, StringComparison.Ordinal);
        Assert.Contains("Microsoft.VisualStudio.Component.VC.14.51.x86.x64", source, StringComparison.Ordinal);
        Assert.Contains("Microsoft.VC.14.51.CLI.X64", source, StringComparison.Ordinal);
        Assert.Contains("Win11SDK_10.0.26100", source, StringComparison.Ordinal);
        Assert.Contains("type=cache,id=sharplabnext-msvc-19.51.36248", source, StringComparison.Ordinal);
        Assert.Contains("command -v wine-stable", source, StringComparison.Ordinal);
        Assert.Contains("source /opt/msvc/bin/x64/msvcenv.sh", source, StringComparison.Ordinal);
        Assert.Contains("test -f \"${BINDIR}/cl.exe\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/VC/Tools/MSVC/14.51.36248/", source, StringComparison.Ordinal);
        Assert.Contains("cabextract", source, StringComparison.Ordinal);
        Assert.Contains("msiextract", source, StringComparison.Ordinal);
        Assert.Contains("mv /opt/wine-netfx-clr4 /opt/wine-dotnet", source, StringComparison.Ordinal);
        Assert.Contains("/clr", source, StringComparison.Ordinal);
        Assert.DoesNotContain("wine msiexec", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NDP48-DevPack-ENU.exe /", source, StringComparison.OrdinalIgnoreCase);
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

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the C++/CLI preparation script.");
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
            Root = Path.Combine(Path.GetTempPath(), $"SharpLabNext.CppCliPreparation.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(Root, "eng"));
            Directory.CreateDirectory(Path.Combine(Root, "deploy", "docker"));
            Directory.CreateDirectory(Path.Combine(Root, "downloads"));
            File.WriteAllText(Path.Combine(Root, "SharpLabNext.slnx"), string.Empty);
            File.WriteAllText(Path.Combine(Root, "deploy", "docker", "Dockerfile.operator-cppcli-base"), "FROM scratch\n");
            MsvcWinePath = WriteInput("msvc-wine-source", 0x11);
            VisualStudioManifestPath = WriteInput("visual-studio-manifest", 0x22);
            DeveloperPackPath = WriteInput("netfx48-developer-pack", 0x33);
            WriteManifest();
        }

        public string Root { get; }
        public string MsvcWinePath { get; }
        public string VisualStudioManifestPath { get; }
        public string DeveloperPackPath { get; }

        public string[] ValidArguments() =>
        [
            "--repository-root", Root,
            "--framework-seed-image", $"localhost:5000/framework-clr4@sha256:{new string('a', 64)}",
            "--output-image", "sharplabnext/msvc-cppcli-prepared-base:source-v2",
            "--msvc-wine-source", MsvcWinePath,
            "--visual-studio-manifest", VisualStudioManifestPath,
            "--netfx48-developer-pack", DeveloperPackPath,
            "--operator-build-input-sha256", new string('b', 64),
            "--accept-microsoft-cpp-build-tools-license",
            "--accept-microsoft-dotnet-eula",
            "--dry-run"
        ];

        private string WriteInput(string id, byte value)
        {
            var path = Path.Combine(Root, "downloads", $"{id}.bin");
            File.WriteAllBytes(path, [value]);
            return path;
        }

        public void WriteManifest(string? nonFileId = null)
        {
            object Download(string id, string path) => new { kind = id == nonFileId ? "nuget-package" : "file", id, path = $"downloads/{Path.GetFileName(path)}", url = id == "msvc-wine-source" ? "https://codeload.github.com/mstorsjo/msvc-wine/tar.gz/test" : $"https://download.microsoft.com/{id}", sizeBytes = 1, sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), license = "test license" };
            var manifest = new
            {
                schemaVersion = 3,
                localRegistry = new { image = $"registry@sha256:{new string('c', 64)}", imageId = $"sha256:{new string('c', 64)}", containerName = "sharplabnext-release-registry", host = "127.0.0.1", port = 5000 },
                downloads = new[]
                {
                    Download("msvc-wine-source", MsvcWinePath),
                    Download("visual-studio-manifest", VisualStudioManifestPath),
                    Download("netfx48-developer-pack", DeveloperPackPath)
                },
                repositoryFiles = Array.Empty<object>(),
                generatedImages = new[]
                {
                    new { id = "jsharp20-development-base", reference = "sharplabnext/operator-jsharp20:source-v2", buildKind = "jsharp20", license = "test" },
                    new { id = "cppcli-prepared-base", reference = "sharplabnext/msvc-cppcli-prepared-base:source-v2", buildKind = "cppcli", license = "test" }
                }
            };
            File.WriteAllText(Path.Combine(Root, "eng", "release-prerequisites.json"), JsonSerializer.Serialize(manifest));
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

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string CombinedOutput => StandardOutput + StandardError;
    }
}
