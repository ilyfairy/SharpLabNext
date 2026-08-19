using System.Text.Json;
using SharpLabNext.Contracts;
using SharpLabNext.RuntimeProfile.Sdk;
using SharpLabNext.RuntimeSupervisor;

namespace SharpLabNext.UnitTests;

public sealed class WineCoreClrMatrixContractTests
{
    private const string WineHost = "/usr/lib/wine/wine64";
    private const string WinePrefix = "/opt/wine-dotnet";
    private const string WindowsDotNet = @"Z:\opt\wine-dotnet\drive_c\dotnet\dotnet.exe";
    private const string WindowsHelper = @"Z:\opt\sharplabnext\SharpLabNext.LegacyJitInspector.dll";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] RunCapabilities = ["run"];
    private static readonly string[] RunAndJitCapabilities = ["run", "jit-asm"];

    public static TheoryData<string, string, bool> ExactRows => new()
    {
        { "wine-dotnet-5-linux-x64.json", "5.0.17", false },
        { "wine-dotnet-6-linux-x64.json", "6.0.36", false },
        { "wine-dotnet-7-linux-x64.json", "7.0.20", true },
        { "wine-dotnet-8-linux-x64.json", "8.0.29", true },
        { "wine-dotnet-9-linux-x64.json", "9.0.18", true },
        { "wine-dotnet-10-linux-x64.json", "10.0.10", true },
        { "wine-dotnet-11-preview-linux-x64.json", "11.0.0-preview.6.26359.118", true }
    };

    [Theory]
    [MemberData(nameof(ExactRows))]
    public void ExactProfileKeepsTheProvenWineCapabilityBoundary(
        string fileName,
        string runtimeVersion,
        bool supportsJit)
    {
        var profile = LoadProfile(fileName);

        Assert.Empty(RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false));
        Assert.Equal("coreclr-wine", profile.Family);
        Assert.Equal(runtimeVersion, profile.RuntimeVersion);
        Assert.Equal(runtimeVersion, profile.JitVersion);
        Assert.Equal(["coreclr-wine", "coreclr"], profile.AcceptedRuntimeFamilies);
        Assert.Equal(
            supportsJit ? RunAndJitCapabilities : RunCapabilities,
            profile.Capabilities);
        Assert.Equal(RuntimeContainerIsolationKinds.Wine, profile.Container.IsolationKind);
        Assert.Equal(RuntimeContainerEnvironmentKinds.Wine, profile.Container.EnvironmentKind);
        Assert.Equal(RuntimeContainerExecutionUsers.NonRoot, profile.Container.ExecutionUser);
        Assert.Equal(WinePrefix, profile.Container.WinePrefixPath);
        Assert.Equal(RuntimeRunnerKinds.WineCoreClr, profile.Layout.RunnerKind);
        Assert.Equal(WinePrefix, profile.Layout.WinePrefixPath);

        Assert.Equal(
            [
                WineHost,
                WindowsDotNet,
                "exec",
                "--fx-version",
                runtimeVersion,
                WindowsHelper,
                "--runtime-version",
                runtimeVersion,
                "run",
                @"Z:\workspace\app.dll",
                "--",
                "argument"
            ],
            RuntimeProfileCommandBuilder.CreateRunCommand(profile, "app.dll", ["argument"]));

        if (!supportsJit)
        {
            Assert.Null(profile.Operations?.Jit);
            Assert.Throws<NotSupportedException>(() =>
                RuntimeProfileCommandBuilder.CreateJitCommand(profile, "app.dll", "Program.Main"));
            return;
        }

        Assert.Equal(
            RuntimeOperationImplementationIds.LegacyJitInspector,
            profile.Operations?.Jit?.ImplementationId);
        Assert.Equal(RuntimeJitSourceMappingKinds.None, profile.Operations?.Jit?.SourceMappingKind);
        Assert.Null(profile.Operations?.Jit?.ProfilerPath);
        Assert.Equal(
            [
                WineHost,
                WindowsDotNet,
                "exec",
                "--fx-version",
                runtimeVersion,
                WindowsHelper,
                "--runtime-version",
                runtimeVersion,
                "jit",
                @"Z:\workspace\app.dll",
                "Program.Main"
            ],
            RuntimeProfileCommandBuilder.CreateJitCommand(profile, "app.dll", "Program.Main"));
    }

    [Theory]
    [MemberData(nameof(ExactRows))]
    public void SupervisorUsesWindowsJitDisasmWithoutProfilerOrMappingClaims(
        string fileName,
        string runtimeVersion,
        bool supportsJit)
    {
        if (!supportsJit)
            return;

        var profile = LoadProfile(fileName);
        var request = new JitRequest(
            "request-1",
            "idempotency-1",
            "resolution-1",
            new ArtifactRef($"sha256:{new string('a', 64)}"),
            profile.Id,
            new JitOptions(
                "WindowsAbi",
                "tier0-diffable",
                "disabled",
                "coreclr-jitdisasm",
                "runtime-job-default"),
            DateTimeOffset.UtcNow.AddMinutes(1));

        var environment = RuntimeJobExecutor.CreateJitEnvironment(
            profile,
            request,
            "SharpLabNext.User.dll");

        Assert.Equal("*WindowsAbi*", environment["COMPlus_JitDisasm"]);
        Assert.Equal("SharpLabNext.User", environment["COMPlus_JitDisasmAssemblies"]);
        Assert.Equal(@"Z:\tmp\sharplabnext-jit.asm", environment["COMPlus_JitStdOutFile"]);
        Assert.Equal(@"Z:\tmp\sharplabnext-jit.asm", environment["SHARPLABNEXT_JIT_OUTPUT_PATH"]);
        Assert.Equal(WinePrefix, environment["WINEPREFIX"]);
        Assert.Equal("win64", environment["WINEARCH"]);
        Assert.Equal("-all", environment["WINEDEBUG"]);
        Assert.Equal("0", environment["DOTNET_EnableDiagnostics"]);
        Assert.DoesNotContain("CORECLR_ENABLE_PROFILING", environment);
        Assert.DoesNotContain("CORECLR_PROFILER_PATH", environment);
        Assert.DoesNotContain("SHARPLABNEXT_JIT_MAP_PATH", environment);
        Assert.DoesNotContain("SHARPLABNEXT_JIT_RICH_MAP_PATH", environment);
        Assert.Equal(runtimeVersion, profile.Operations!.Jit!.Command.Argv[3]);
    }

    private static RuntimeProfileOptions LoadProfile(string fileName)
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "profiles",
            "runtimes",
            "candidates",
            fileName);
        return JsonSerializer.Deserialize<RuntimeProfileOptions>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException($"Runtime profile '{fileName}' is invalid.");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "profiles", "runtime-matrix.json")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
