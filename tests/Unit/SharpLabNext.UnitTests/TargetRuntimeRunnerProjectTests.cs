using System.Text.Json;
using System.Xml.Linq;

namespace SharpLabNext.UnitTests;

public sealed class TargetRuntimeRunnerProjectTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string ProjectDirectory = Path.Combine(RepositoryRoot, "src", "RuntimeJobs", "SharpLabNext.TargetRuntimeRunner");

    [Fact]
    public void ProjectKeepsPortableNet20AnyCpuContract()
    {
        var project = XDocument.Load(Path.Combine(ProjectDirectory, "SharpLabNext.TargetRuntimeRunner.csproj"));
        var properties = project.Root!.Elements("PropertyGroup").Elements().ToLookup(static element => element.Name.LocalName, StringComparer.Ordinal);

        Assert.Equal("net20", Assert.Single(properties["TargetFramework"]).Value);
        Assert.Equal("AnyCPU", Assert.Single(properties["PlatformTarget"]).Value);
        Assert.Empty(properties["RuntimeIdentifier"]);
        Assert.Empty(properties["RuntimeIdentifiers"]);
    }

    [Fact]
    public void PackageLockContainsNoRuntimeSpecificDependencyGraph()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ProjectDirectory, "packages.lock.json")));
        var graphNames = document.RootElement.GetProperty("dependencies").EnumerateObject().Select(static graph => graph.Name).ToArray();

        Assert.Equal([".NETFramework,Version=v2.0"], graphNames);
        Assert.DoesNotContain(graphNames, static name => name.Contains('/', StringComparison.Ordinal));
    }

    [Fact]
    public void WineCaptureDirectoryIsRestrictedToTheSharedTmpfsPath()
    {
        var source = File.ReadAllText(Path.Combine(ProjectDirectory, "RunOutputCapture.cs"));

        Assert.Contains("Environment.GetEnvironmentVariable(\"SHARPLABNEXT_CAPTURE_DIRECTORY\")", source, StringComparison.Ordinal);
        Assert.Contains("!string.Equals(configuredDirectory, @\"Z:\\tmp\", StringComparison.OrdinalIgnoreCase)", source, StringComparison.Ordinal);
        Assert.Contains("return Path.GetTempPath();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void UserEntryPointUsesDirectGeneratedCallInsteadOfReflectionInvoke()
    {
        var source = File.ReadAllText(Path.Combine(ProjectDirectory, "UserAssemblyRunner.cs"));

        Assert.Contains("new DynamicMethod(", source, StringComparison.Ordinal);
        Assert.Contains("CreateEntryPointInvoker(entryPoint)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("entryPoint.Invoke(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("getAwaiter.Invoke(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("getResult.Invoke(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("wait.Invoke(", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SharpLabNext.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the SharpLabNext repository root.");
    }
}
