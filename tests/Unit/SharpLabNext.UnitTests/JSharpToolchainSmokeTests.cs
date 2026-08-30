using System.Diagnostics;

namespace SharpLabNext.UnitTests;

public sealed class JSharpToolchainSmokeTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string ScriptPath = Path.Combine(RepositoryRoot, "eng", "smoke", "jsharp-toolchain.cs");

    [Fact]
    public void ScriptLocksTheIsolatedX64Clr2Contract()
    {
        var source = File.ReadAllText(ScriptPath);

        Assert.Contains("--pull=never", source, StringComparison.Ordinal);
        Assert.Contains("--platform=linux/amd64", source, StringComparison.Ordinal);
        Assert.Contains("--network=none", source, StringComparison.Ordinal);
        Assert.Contains("--read-only", source, StringComparison.Ordinal);
        Assert.Contains("--cap-drop=ALL", source, StringComparison.Ordinal);
        Assert.Contains("--security-opt=no-new-privileges=true", source, StringComparison.Ordinal);
        Assert.Contains("--pids-limit=128", source, StringComparison.Ordinal);
        Assert.Contains("--memory=1024m", source, StringComparison.Ordinal);
        Assert.Contains("--cpus=1.0", source, StringComparison.Ordinal);
        Assert.Contains("--ulimit=nofile=512:512", source, StringComparison.Ordinal);
        Assert.DoesNotContain("--ulimit=nofile=256:256", source, StringComparison.Ordinal);
        Assert.Contains("--tmpfs=/work:", source, StringComparison.Ordinal);
        Assert.Contains("--tmpfs=/opt/wine-jsharp20/drive_c/users/root/Temp:" + "rw,exec,nosuid,nodev,size=256m,mode=1777", source, StringComparison.Ordinal);
        Assert.DoesNotContain("--env=TMP=", source, StringComparison.Ordinal);
        Assert.DoesNotContain("--env=TEMP=", source, StringComparison.Ordinal);
        Assert.DoesNotContain("--env=TMPDIR=", source, StringComparison.Ordinal);
        Assert.Contains("--entrypoint=/bin/bash", source, StringComparison.Ordinal);
        Assert.Contains("WINEPREFIX=/opt/wine-jsharp20", source, StringComparison.Ordinal);
        Assert.Contains("WINEARCH=win64", source, StringComparison.Ordinal);
        Assert.Contains("test -x /usr/lib/wine/wineserver", source, StringComparison.Ordinal);
        Assert.Contains("/usr/lib/wine/wineserver -k", source, StringComparison.Ordinal);
        Assert.DoesNotContain("command -v wineserver", source, StringComparison.Ordinal);
        Assert.Contains("Framework64/v2.0.50727", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.NET/Framework/v2.0.50727/vjc.exe", source, StringComparison.Ordinal);
        Assert.Contains("/platform:x64", source, StringComparison.Ordinal);
        Assert.Contains("expected AMD64", source, StringComparison.Ordinal);
        Assert.Contains("optional header is not PE32+", source, StringComparison.Ordinal);
        Assert.Contains("(flags & 0x2) == 0 && (flags & 0x20000) == 0", source, StringComparison.Ordinal);
        Assert.Contains("test \"${metadata_version}\" = 'v2.0.50727'", source, StringComparison.Ordinal);
        Assert.Contains("cmp -s /work/runtime.expected /work/runtime.stdout", source, StringComparison.Ordinal);
        Assert.DoesNotContain("command -v objdump", source, StringComparison.Ordinal);
        Assert.DoesNotContain("command -v python3", source, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingImageReferenceFailsAsUsageBeforeDocker()
    {
        var result = await RunAsync([]);

        Assert.Equal(64, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("LOCAL_IMAGE_REFERENCE", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--privileged")]
    [InlineData("https://operator.invalid/jsharp-image")]
    [InlineData("image@sha256:abc")]
    public async Task UnsafeOrMalformedImageReferenceFailsBeforeDocker(string imageReference)
    {
        var result = await RunAsync([imageReference]);

        Assert.Equal(64, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("LOCAL_IMAGE_REFERENCE", result.StandardError, StringComparison.Ordinal);
    }

    private static async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments)
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

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the J# x64 smoke script test process.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
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

        return new ProcessResult(process.ExitCode, await output, await error);
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

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
