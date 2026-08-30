using System.Diagnostics;
using System.Text.Json;
using SharpLabNext.Contracts;
using SharpLabNext.RunnerFixture;
using SharpLabNext.WorkerHost;

namespace SharpLabNext.IntegrationTests;

public sealed class CompilerProcessIsolationTests
{
    [Theory]
    [InlineData("src/Workers/Roslyn.Stable/SharpLabNext.Worker.Roslyn.Stable", "SharpLabNext.Worker.Roslyn.Stable.dll", "--sharplabnext-roslyn-build-child", "csharp", "roslyn-stable", "Program.cs", "System.Console.WriteLine(42);")]
    [InlineData("src/Workers/FSharp/SharpLabNext.Worker.FSharp", "SharpLabNext.Worker.FSharp.dll", "--sharplabnext-fsharp-build-child", "fsharp", "fsharp-stable", "Program.fs", "module Program\nprintfn \"hello\"\n")]
    public async Task RealCompilerChildReturnsTypedCompileCheckResult(string projectDirectory, string assemblyName, string childArgument, string languageId, string toolchainId, string fileName, string source)
    {
        var root = FindRepositoryRoot();
        var outputDirectory = Path.Combine(root, projectDirectory, "bin", "Release", "net10.0");
        var assemblyPath = Path.Combine(outputDirectory, assemblyName);
        Assert.True(File.Exists(assemblyPath), assemblyPath);
        var referencePath = FindReferencePath("net10-ref", "10.0.9", "net10.0");
        var workRoot = Path.Combine(Path.GetTempPath(), "SharpLabNext-CompilerChild", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workRoot);
        try
        {
            var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Staging",
                ["DOTNET_ENVIRONMENT"] = "Staging",
                ["InternalServiceAuth__Required"] = "false",
                ["ReferenceSets__net10-ref__Path"] = referencePath,
                ["FSharpWorker__WorkRoot"] = workRoot,
                ["PeachPie__WorkRoot"] = workRoot
            };
            using var runner = new CompilerProcessRunner(
                CompilerProcessIsolationOptions.Default with { MaximumConcurrentProcesses = 1 },
                new CompilerProcessCommand(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", [assemblyPath, childArgument], outputDirectory, environment));
            var options = new BuildOptions(
                BuildConfiguration.Release,
                Optimize: true,
                BuildOutputKind.Console,
                AllowUnsafe: false,
                EmitPortablePdb: true,
                languageId is "fsharp" or "php" ? NullableContextMode.Disable : NullableContextMode.Enable,
                LanguageVersion: languageId switch
                {
                    "fsharp" => "9.0",
                    "php" => "8.5",
                    _ => "14.0"
                });
            var request = new BuildRequest($"{languageId}-child-request", $"{languageId}-child-key", $"{languageId}-child-pipeline", toolchainId, "net10-ref", new WorkspaceSnapshot(ContractSchemaVersions.WorkspaceSnapshot, 1, 1, languageId, [new WorkspaceFile(fileName, 1, source)], fileName, [fileName], "net10-ref", options), DateTimeOffset.UtcNow.AddSeconds(30), options, BuildTarget.CompileCheck);

            var execution = await runner.RunAsync<BuildRequest, RawBuildExecution>("unused", request, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            var result = execution.Result;
            Assert.Equal("compile-check", result.GetProperty("ResultType").GetString());
            Assert.True(result.GetProperty("CompilationSucceeded").GetBoolean());
            Assert.Equal(toolchainId, result.GetProperty("Identity").GetProperty("ToolchainId").GetString());
        }
        finally
        {
            if (Directory.Exists(workRoot))
                Directory.Delete(workRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PeachPieCompilerChildReturnsSuccessfulCompileCheckResult()
    {
        var root = FindRepositoryRoot();
        var outputDirectory = Path.Combine(root, "src/Workers/PeachPie/SharpLabNext.Worker.PeachPie/bin/Release/net10.0");
        var assemblyPath = Path.Combine(outputDirectory, "SharpLabNext.Worker.PeachPie.dll");
        Assert.True(File.Exists(assemblyPath), assemblyPath);
        var workRoot = Path.Combine(Path.GetTempPath(), "SharpLabNext-CompilerChild", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workRoot);
        try
        {
            var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Staging",
                ["DOTNET_ENVIRONMENT"] = "Staging",
                ["InternalServiceAuth__Required"] = "false",
                ["ReferenceSets__net10-ref__Path"] = FindReferencePath("net10-ref", "10.0.9", "net10.0"),
                ["PeachPie__WorkRoot"] = workRoot
            };
            using var runner = new CompilerProcessRunner(
                CompilerProcessIsolationOptions.Default with { MaximumConcurrentProcesses = 1 },
                new CompilerProcessCommand(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", [assemblyPath, "--sharplabnext-peachpie-compiler-child"], outputDirectory, environment));
            var options = new BuildOptions(BuildConfiguration.Release, Optimize: true, BuildOutputKind.Console, AllowUnsafe: false, EmitPortablePdb: false, NullableContextMode.Disable, LanguageVersion: "8.5");
            var request = new BuildRequest("php-child-request", "php-child-key", "php-child-pipeline", "peachpie-stable", "net10-ref", new WorkspaceSnapshot(ContractSchemaVersions.WorkspaceSnapshot, 1, 1, "php", [new WorkspaceFile("index.php", 1, "<?php function square($value) { return $value * $value; } echo square(7);")], "index.php", ["index.php"], "net10-ref", options), DateTimeOffset.UtcNow.AddSeconds(30), options, BuildTarget.CompileCheck);

            var execution = await runner.RunAsync<BuildRequest, RawPeachPieCompilerResponse>("unused", request, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.True(execution.CompilerProcessId > 0);
            Assert.True(execution.CompilationSucceeded);
            Assert.False(execution.EmitSucceeded);
            Assert.Empty(execution.PeImage);
            Assert.Empty(execution.Diagnostics);
        }
        finally
        {
            if (Directory.Exists(workRoot))
                Directory.Delete(workRoot, recursive: true);
        }
    }

    [Fact]
    public async Task NonZeroChildExitIsClassifiedAsCompilerCrash()
    {
        using var runner = CreateRunner("compiler-child-exit");

        var exception = await Assert.ThrowsAsync<CompilerProcessCrashedException>(() =>
            runner.RunAsync<object, string>(
                "unused",
                new { request = "crash" },
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));

        Assert.Equal(134, exception.ExitCode);
    }

    [Fact]
    public async Task HardDeadlineKillsHungCompilerChild()
    {
        using var runner = CreateRunner("compiler-child-hang");
        var started = Stopwatch.StartNew();

        await Assert.ThrowsAsync<CompilerProcessTimeoutException>(() =>
            runner.RunAsync<object, string>(
                "unused",
                new { request = "hang" },
                TimeSpan.FromMilliseconds(200),
                TestContext.Current.CancellationToken));

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"Elapsed: {started.Elapsed}.");
    }

    [Fact]
    public async Task WorkingSetWatermarkKillsMemoryHeavyCompilerChild()
    {
        using var runner = CreateRunner(
            "compiler-child-memory",
            CompilerProcessIsolationOptions.Default with { MaximumConcurrentProcesses = 1, MaximumWorkingSetBytes = 64L * 1024 * 1024, MemoryPollIntervalMilliseconds = 10 });

        var exception = await Assert.ThrowsAsync<CompilerProcessMemoryLimitExceededException>(() =>
            runner.RunAsync<object, string>(
                "unused",
                new { request = "memory" },
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));

        Assert.True(exception.ObservedBytes > exception.LimitBytes);
    }

    [Fact]
    public async Task ConcurrentCompilerProcessCountIsFailClosed()
    {
        using var runner = CreateRunner(
            "compiler-child-hang",
            CompilerProcessIsolationOptions.Default with { MaximumConcurrentProcesses = 1 });
        using var firstCancellation = new CancellationTokenSource();
        var first = runner.RunAsync<object, string>("unused", new { request = "first" }, TimeSpan.FromSeconds(10), firstCancellation.Token);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<CompilerProcessCapacityExceededException>(() =>
            runner.RunAsync<object, string>(
                "unused",
                new { request = "second" },
                TimeSpan.FromSeconds(1),
                TestContext.Current.CancellationToken));

        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    [Fact]
    public async Task StandardErrorBeyondCaptureLimitIsDrainedWithoutDeadlock()
    {
        using var runner = CreateRunner(
            "compiler-child-stderr",
            CompilerProcessIsolationOptions.Default with { MaximumConcurrentProcesses = 1, MaximumStandardErrorBytes = 1024 });
        var started = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<CompilerProcessCrashedException>(() =>
            runner.RunAsync<object, string>(
                "unused",
                new { request = "stderr" },
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));

        Assert.Equal(134, exception.ExitCode);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"Elapsed: {started.Elapsed}.");
    }

    [Fact]
    public async Task RequestAndResponseByteLimitsFailClosed()
    {
        using (var requestRunner = CreateRunner(
                   "compiler-child-hang",
                   CompilerProcessIsolationOptions.Default with { MaximumConcurrentProcesses = 1, MaximumRequestBytes = 64 * 1024 }))
        {
            await Assert.ThrowsAsync<CompilerProcessProtocolException>(() =>
                requestRunner.RunAsync<object, string>(
                    "unused",
                    new { source = new string('x', 128 * 1024) },
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken));
        }

        using var responseRunner = CreateRunner(
            "compiler-child-stdout",
            CompilerProcessIsolationOptions.Default with { MaximumConcurrentProcesses = 1, MaximumResponseBytes = 1024 * 1024 });
        await Assert.ThrowsAsync<CompilerProcessProtocolException>(() =>
            responseRunner.RunAsync<object, string>(
                "unused",
                new { request = "response-limit" },
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));
    }

    private static CompilerProcessRunner CreateRunner(string mode, CompilerProcessIsolationOptions? options = null)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var fixture = typeof(RunnerFixtureMarker).Assembly.Location;
        return new CompilerProcessRunner(
            options ?? CompilerProcessIsolationOptions.Default with { MaximumConcurrentProcesses = 1 },
            new CompilerProcessCommand(dotnet, [fixture, mode], Path.GetDirectoryName(fixture)));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SharpLabNext.slnx")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("The repository root was not found.");
    }

    private static string FindReferencePath(string id, string version, string targetFramework)
    {
        var materializedRoot = Environment.GetEnvironmentVariable("SHARPLABNEXT_TEST_CORECLR_REFERENCE_SETS");
        if (!string.IsNullOrWhiteSpace(materializedRoot))
        {
            var materialized = Path.Combine(materializedRoot, id);
            if (Directory.Exists(materialized))
                return materialized;
        }

        var roots = new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
            "/usr/share/dotnet",
            "/usr/local/share/dotnet"
        };
        foreach (var root in roots.Where(static value => !string.IsNullOrWhiteSpace(value)))
        {
            var candidate = Path.Combine(root!, "packs", "Microsoft.NETCore.App.Ref", version, "ref", targetFramework);
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException($"The .NET {version} reference set was not found.");
    }

    private sealed record RawBuildExecution(JsonElement Result);

    private sealed record RawPeachPieCompilerResponse(int CompilerProcessId, bool CompilationSucceeded, bool EmitSucceeded, byte[] PeImage, IReadOnlyList<JsonElement> Diagnostics);
}
