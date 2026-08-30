extern alias SharpLabNextWineRunner;

using ProcessBridgeArguments = SharpLabNextWineRunner::ProcessBridgeArguments;

namespace SharpLabNext.UnitTests;

public sealed class ProcessBridgeArgumentsTests
{
    [Fact]
    public void ParseKeepsFixedAndUserArgumentsInSeparateVectors()
    {
        var parsed = ProcessBridgeArguments.Parse([
            "bridge",
            "mono",
            "--debug",
            "/workspace/Program.exe",
            "--",
            "--user-option",
            "value with spaces",
            "--"
        ]);

        Assert.Equal("mono", parsed.Executable);
        Assert.Equal(["--debug", "/workspace/Program.exe"], parsed.FixedArguments);
        Assert.Equal(["--user-option", "value with spaces", "--"], parsed.UserArguments);
        Assert.False(parsed.FiltersWineNoise);
    }

    [Theory]
    [InlineData("wine")]
    [InlineData("WINE64")]
    [InlineData("/usr/bin/wine")]
    [InlineData("/usr/bin/wine-stable")]
    [InlineData("/opt/wine/bin/wine64.exe")]
    public void WineExecutableNamesEnableWineNoiseFiltering(string executable)
    {
        Assert.True(ProcessBridgeArguments.IsWineExecutable(executable));
    }

    [Theory]
    [InlineData("dotnet")]
    [InlineData("mono")]
    [InlineData("winetricks")]
    [InlineData("/usr/bin/dotnet")]
    public void OrdinaryExecutableNamesDoNotEnableWineNoiseFiltering(string executable)
    {
        Assert.False(ProcessBridgeArguments.IsWineExecutable(executable));
    }

    [Theory]
    [InlineData("dotnet", "fixture.dll")]
    [InlineData("mono", "/workspace/Program.exe")]
    public void ParseAcceptsDotnetAndMonoStyleCommands(string executable, string managedEntryPoint)
    {
        var parsed = ProcessBridgeArguments.Parse([
            "bridge", executable, managedEntryPoint, "--", "argument"
        ]);

        Assert.Equal(executable, parsed.Executable);
        Assert.Equal([managedEntryPoint], parsed.FixedArguments);
        Assert.Equal(["argument"], parsed.UserArguments);
    }

    [Theory]
    [InlineData("dotnet", "fixture.dll")]
    [InlineData("bridge", "dotnet")]
    [InlineData("bridge", "dotnet", "fixture.dll")]
    public void ParseRequiresBridgeCommandAndSeparator(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => ProcessBridgeArguments.Parse(args));
    }

    [Theory]
    [InlineData("../dotnet")]
    [InlineData("bin/dotnet")]
    [InlineData(".\\dotnet")]
    [InlineData("dotnet --info")]
    [InlineData("--dotnet")]
    [InlineData("dotnet\0--info")]
    public void ParseRejectsExecutablePathInjection(string executable)
    {
        Assert.Throws<ArgumentException>(() => ProcessBridgeArguments.Parse(["bridge", executable, "--"]));
    }

    [Fact]
    public void ParseRejectsMissingAbsoluteExecutable()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}", "dotnet.exe");

        var exception = Assert.Throws<FileNotFoundException>(() => ProcessBridgeArguments.Parse(["bridge", missing, "--"]));

        Assert.Equal(Path.GetFullPath(missing), exception.FileName);
    }

    [Fact]
    public void ParseRejectsTraversalInsideAnAbsoluteExecutablePath()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The test host executable path is unavailable.");
        var injected = Path.Combine(Path.GetDirectoryName(executable)!, "unused-directory", "..", Path.GetFileName(executable));

        Assert.Throws<ArgumentException>(() => ProcessBridgeArguments.Parse(["bridge", injected, "--"]));
    }

    [Fact]
    public void ParseAcceptsAnExistingAbsoluteExecutable()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The test host executable path is unavailable.");

        var parsed = ProcessBridgeArguments.Parse(["bridge", executable, "--"]);

        Assert.Equal(Path.GetFullPath(executable), parsed.Executable);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ParseRejectsNullCharactersInEitherArgumentVector(bool fixedArgument)
    {
        var args = fixedArgument
            ? new[] { "bridge", "dotnet", "invalid\0fixed", "--" }
            : new[] { "bridge", "dotnet", "--", "invalid\0user" };

        Assert.Throws<ArgumentException>(() => ProcessBridgeArguments.Parse(args));
    }
}
