using System.Diagnostics;
using System.Reflection.Metadata;

namespace SharpLabNext.IntegrationTests;

public sealed class RuntimeCapabilityProbeTests
{
    [Fact]
    public void ProbeProducerUsesBuildCompatibleLockedRestoreProperty()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "eng",
            "runtime-capability-probe.cs"));

        Assert.Contains("start.ArgumentList.Add(\"-p:RestoreLockedMode=true\")", source, StringComparison.Ordinal);
        Assert.DoesNotContain("start.ArgumentList.Add(\"--locked-mode\")", source, StringComparison.Ordinal);
        Assert.Contains("#:property JsonSerializerIsReflectionEnabledByDefault=true", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CapabilityPreflightBindsCandidateProfileToCanonicalPath()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "eng",
            "runtime-capability-preflight.cs"));

        Assert.Contains(
            "profiles/runtimes/candidates/{context.ProfileId}.json",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "profilePath,\n            $\"profiles/runtimes/candidates/{context.ProfileId}.json\",",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeBuildProducesCoreClrAndDesktopClrArtifacts()
    {
        var root = FindRepositoryRoot();
        var configuration = Configuration();

        Assert.True(File.Exists(Path.Combine(
            root,
            "tests",
            "Fixtures",
            "SharpLabNext.RuntimeCapabilityProbe",
            "bin",
            configuration,
            "netcoreapp2.0",
            "SharpLabNext.RuntimeCapabilityProbe.dll")));
        Assert.True(File.Exists(Path.Combine(
            root,
            "tests",
            "Fixtures",
            "SharpLabNext.RuntimeCapabilityProbe",
            "bin",
            configuration,
            "net20",
            "SharpLabNext.RuntimeCapabilityProbe.exe")));
    }

    [Fact]
    public async Task CoreClrProbeReturnsMarkersAndNestedExceptionMaterial()
    {
        var probe = CoreClrProbePath();
        using (var success = Start(probe, "success-security"))
        {
            var stdout = success.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = success.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await success.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.Equal(0, success.ExitCode);
            Assert.Contains("SLN-CAPABILITY-STDOUT-V1", await stdout, StringComparison.Ordinal);
            Assert.Contains("SLN-CAPABILITY-STDERR-V1", await stderr, StringComparison.Ordinal);
        }

        using (var failure = Start(probe, "user-exception"))
        {
            var stderr = failure.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await failure.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.NotEqual(0, failure.ExitCode);
            var text = await stderr;
            Assert.Contains("outer capability probe failure", text, StringComparison.Ordinal);
            Assert.Contains("inner capability probe failure", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PortablePdbContainsMultipleVisibleSequencePointsForJitProbe()
    {
        var pdbPath = Path.ChangeExtension(CoreClrProbePath(), ".pdb");
        using var stream = File.OpenRead(pdbPath);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        var reader = provider.GetMetadataReader();
        var maximumVisiblePoints = 0;
        foreach (var handle in reader.MethodDebugInformation)
        {
            var visible = reader.GetMethodDebugInformation(handle)
                .GetSequencePoints()
                .Count(static point => !point.IsHidden);
            maximumVisiblePoints = Math.Max(maximumVisiblePoints, visible);
        }

        Assert.True(maximumVisiblePoints >= 4, $"Observed only {maximumVisiblePoints} visible sequence points.");
    }

    private static Process Start(string probe, string mode)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(probe);
        start.ArgumentList.Add(mode);
        return Process.Start(start)
            ?? throw new InvalidOperationException("Runtime capability probe did not start.");
    }

    private static string CoreClrProbePath() => Path.Combine(
        FindRepositoryRoot(),
        "tests",
        "Fixtures",
        "SharpLabNext.RuntimeCapabilityProbe",
        "bin",
        Configuration(),
        "netcoreapp2.0",
        "SharpLabNext.RuntimeCapabilityProbe.dll");

    private static string Configuration() =>
        new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
        ?? throw new InvalidOperationException("Test build configuration could not be resolved.");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SharpLabNext.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("SharpLabNext.slnx was not found above the test output directory.");
    }
}
