using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Sdk;
using SharpLabNext.WorkerHost;

namespace SharpLabNext.Worker.PeachPie.Tests;

[Collection(PeachPieTestGroup.Name)]
public sealed class PeachPieBuildServiceTests
{
    [Fact]
    public async Task CompileCheckHonorsPhp85AndDiagnosticsUseWorkspaceRelativePaths()
    {
        var root = PeachPieTestSettings.CreateRoot();
        try
        {
            var service = PeachPieTestSettings.CreateBuildService(root);
            var php85 = PeachPieTestSettings.CreateRequest(
                BuildTarget.CompileCheck,
                "<?php\n$result = 'sharplab' |> strtoupper(...);\necho $result;\n");

            var valid = await service.BuildAsync(php85, TestContext.Current.CancellationToken);

            Assert.True(Assert.IsType<CompilationCheckResult>(valid.Result).CompilationSucceeded);

            var invalid = await service.BuildAsync(
                PeachPieTestSettings.CreateRequest(
                    BuildTarget.CompileCheck,
                    "<?php\nfunction broken( {\n"),
                TestContext.Current.CancellationToken);
            var check = Assert.IsType<CompilationCheckResult>(invalid.Result);
            Assert.False(check.CompilationSucceeded);
            Assert.NotEmpty(check.Diagnostics);
            Assert.All(check.Diagnostics, diagnostic =>
            {
                Assert.Equal("Program.php", diagnostic.FilePath);
                Assert.DoesNotContain(root, diagnostic.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(root, diagnostic.FilePath ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            });
        }
        finally
        {
            PeachPieTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ArtifactContainsManagedPeWithoutPdbAndRecursiveRuntimeClosure()
    {
        var root = PeachPieTestSettings.CreateRoot();
        try
        {
            var service = PeachPieTestSettings.CreateBuildService(root);

            var execution = await service.BuildAsync(
                PeachPieTestSettings.CreateRequest(BuildTarget.Artifact, "<?php echo 42;"),
                TestContext.Current.CancellationToken);

            var result = Assert.IsType<BuildResult>(execution.Result);
            Assert.Equal(BuildOutcome.Succeeded, result.Outcome);
            Assert.Equal(PeachPieToolchain.CompilerVersion, result.Identity.CompilerVersion);
            Assert.Equal(PeachPieToolchain.CompilerCommit, result.Identity.CompilerCommit);
            var envelope = Assert.IsType<LanguageWorkerArtifactEnvelope>(execution.Artifact);
            Assert.Equal(PeachPieToolchain.ArtifactFormat, envelope.ArtifactFormat);
            Assert.NotNull(envelope.FileContentsBase64);
            var paths = envelope.FileContentsBase64.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Contains($"{PeachPieToolchain.AssemblyName}.dll", paths);
            Assert.Contains(PeachPieToolchain.RuntimeAssemblyName, paths);
            Assert.Contains(PeachPieToolchain.LibraryAssemblyName, paths);
            Assert.Contains("Peachpie.Library.RegularExpressions.dll", paths);
            Assert.Contains("Microsoft.Extensions.ObjectPool.dll", paths);
            Assert.Contains("BCrypt.Net-Next.dll", paths);
            Assert.Contains("FluentFTP.dll", paths);
            Assert.Contains("Isopoh.Cryptography.Argon2.dll", paths);
            Assert.Contains("NGettext.dll", paths);
            Assert.Contains("Rationals.dll", paths);
            Assert.Contains(PeachPieToolchain.MonoUnixNativeArtifactPath, paths);
            Assert.DoesNotContain(paths, static path => path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("Peachpie.CodeAnalysis.dll", paths);
            Assert.DoesNotContain("Peachpie.Microsoft.CodeAnalysis.dll", paths);
            Assert.DoesNotContain(paths, static path => path.StartsWith("Peachpie.App", StringComparison.OrdinalIgnoreCase));
            var nativeLibrary = Assert.Single(
                envelope.Manifest.Files,
                static file => file.Role == "native-library");
            Assert.Equal(PeachPieToolchain.MonoUnixNativeArtifactPath, nativeLibrary.Path);
            Assert.Equal("x64", envelope.Manifest.RuntimeRequirement.Architecture);
            var nativeContent = Convert.FromBase64String(
                envelope.FileContentsBase64[PeachPieToolchain.MonoUnixNativeArtifactPath]);
            Assert.Equal(
                PeachPieToolchain.MonoUnixNativeSha256,
                Convert.ToHexStringLower(SHA256.HashData(nativeContent)));
            Assert.Equal("false", envelope.Manifest.Metadata!["portablePdb"]);
            Assert.Equal("8.5", envelope.Manifest.Metadata["phpLanguageVersion"]);
            Assert.Equal(
                $"{PeachPieToolchain.NativeRuntimeIdentifier}:{PeachPieToolchain.MonoUnixNativeArtifactPath}",
                envelope.Manifest.Metadata["nativeLibraryClosure"]);
            Assert.Equal(
                PeachPieToolchain.MonoUnixNativePackagePath,
                envelope.Manifest.Metadata["nativeLibrarySourcePath"]);
        }
        finally
        {
            PeachPieTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ArtifactFailsClosedWhenPinnedLinuxX64NativeAssetIsUnavailable()
    {
        var root = PeachPieTestSettings.CreateRoot();
        try
        {
            var missingPath = Path.Combine(
                root,
                "runtimes",
                PeachPieToolchain.NativeRuntimeIdentifier,
                "native",
                PeachPieToolchain.MonoUnixNativeLibraryName);
            var settings = PeachPieTestSettings.CreateSettings(root, isolated: false) with
            {
                MonoUnixNativeLibraryPath = missingPath
            };
            var service = PeachPieTestSettings.CreateBuildService(settings);

            var exception = await Assert.ThrowsAsync<LanguageWorkerRequestException>(() =>
                service.BuildAsync(
                    PeachPieTestSettings.CreateRequest(BuildTarget.Artifact, "<?php echo 42;"),
                    TestContext.Current.CancellationToken));

            Assert.Equal("compiler-failure", exception.Code);
            Assert.Equal(503, exception.StatusCode);
            Assert.DoesNotContain(root, exception.PublicMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            PeachPieTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ArtifactRejectsUnreviewedLinuxX64NativeAsset()
    {
        var root = PeachPieTestSettings.CreateRoot();
        try
        {
            var nativePath = Path.Combine(
                root,
                "runtimes",
                PeachPieToolchain.NativeRuntimeIdentifier,
                "native",
                PeachPieToolchain.MonoUnixNativeLibraryName);
            Directory.CreateDirectory(Path.GetDirectoryName(nativePath)!);
            var unreviewedElf = new byte[20]
            {
                0x7f, (byte)'E', (byte)'L', (byte)'F',
                2, 1, 1, 0,
                0, 0, 0, 0,
                0, 0, 0, 0,
                3, 0, 0x3e, 0
            };
            await File.WriteAllBytesAsync(
                nativePath,
                unreviewedElf,
                TestContext.Current.CancellationToken);
            var settings = PeachPieTestSettings.CreateSettings(root, isolated: false) with
            {
                MonoUnixNativeLibraryPath = nativePath
            };
            var service = PeachPieTestSettings.CreateBuildService(settings);

            var exception = await Assert.ThrowsAsync<LanguageWorkerRequestException>(() =>
                service.BuildAsync(
                    PeachPieTestSettings.CreateRequest(BuildTarget.Artifact, "<?php echo 42;"),
                    TestContext.Current.CancellationToken));

            Assert.Equal("compiler-failure", exception.Code);
            Assert.Equal(503, exception.StatusCode);
        }
        finally
        {
            PeachPieTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task MultiFileArtifactRunsCoreFunctionsAndBootstrapPreservesExit()
    {
        var root = PeachPieTestSettings.CreateRoot();
        try
        {
            var service = PeachPieTestSettings.CreateBuildService(root);
            var files = new[]
            {
                new WorkspaceFile(
                    "lib/functions.php",
                    1,
                    "<?php\nfunction answer(): int { return 42; }\n"),
                new WorkspaceFile(
                    "src/index.php",
                    1,
                    "<?php\nrequire __DIR__ . '/../lib/functions.php';\n" +
                    "$match = preg_match('/lab/i', 'SharpLab');\n" +
                    "$json = json_encode(['answer' => answer(), 'match' => $match]);\n" +
                    "$hash = password_hash('secret', PASSWORD_BCRYPT);\n" +
                    "echo $json . '|' . (password_verify('secret', $hash) ? 'verified' : 'failed');\n")
            };
            var runBuild = await service.BuildAsync(
                PeachPieTestSettings.CreateRequest(
                    BuildTarget.Artifact,
                    files,
                    ["lib/functions.php", "src/index.php"],
                    "src/index.php"),
                TestContext.Current.CancellationToken);
            var runEnvelope = Assert.IsType<LanguageWorkerArtifactEnvelope>(runBuild.Artifact);

            var run = await RunArtifactAsync(runEnvelope, root, "normal");

            Assert.Equal(0, run.ExitCode);
            Assert.Equal("{\"answer\":42,\"match\":1}|verified", run.StandardOutput);
            Assert.Equal(string.Empty, run.StandardError);

            var exitBuild = await service.BuildAsync(
                PeachPieTestSettings.CreateRequest(BuildTarget.Artifact, "<?php exit(7);"),
                TestContext.Current.CancellationToken);
            var exitEnvelope = Assert.IsType<LanguageWorkerArtifactEnvelope>(exitBuild.Artifact);

            var exited = await RunArtifactAsync(exitEnvelope, root, "exit");

            Assert.Equal(7, exited.ExitCode);
        }
        finally
        {
            PeachPieTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CompilerRunnerStartsFreshOneShotChildForEveryBuild()
    {
        var root = PeachPieTestSettings.CreateRoot();
        using var environment = PeachPieProcessEnvironment.Apply(root);
        try
        {
            var settings = PeachPieTestSettings.CreateSettings(root, isolated: true);
            using var runner = PeachPieTestSettings.CreateCompilerProcessRunner(settings);
            var request = PeachPieTestSettings.CreateRequest(BuildTarget.CompileCheck, "<?php echo 42;");

            var first = await runner.RunAsync<BuildRequest, PeachPieCompilerResponse>(
                PeachPieCompilerChild.ChildArgument,
                request,
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
            var second = await runner.RunAsync<BuildRequest, PeachPieCompilerResponse>(
                PeachPieCompilerChild.ChildArgument,
                request with
                {
                    RequestId = $"request-{Guid.NewGuid():N}",
                    IdempotencyKey = $"idempotency-{Guid.NewGuid():N}"
                },
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);

            Assert.True(first.CompilationSucceeded);
            Assert.True(second.CompilationSucceeded);
            Assert.NotEqual(Environment.ProcessId, first.CompilerProcessId);
            Assert.NotEqual(first.CompilerProcessId, second.CompilerProcessId);
        }
        finally
        {
            PeachPieTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task EmittedPhpFunctionAndClassMethodNamesRemainDiscoverableByName()
    {
        var root = PeachPieTestSettings.CreateRoot();
        try
        {
            var service = PeachPieTestSettings.CreateBuildService(root);
            var execution = await service.BuildAsync(
                PeachPieTestSettings.CreateRequest(
                    BuildTarget.Artifact,
                    "<?php\nfunction global_name(): int { return 1; }\n" +
                    "class Demo { public function method_name(): int { return 2; } }\n" +
                    "echo global_name() + (new Demo())->method_name();\n",
                    "src/index.php"),
                TestContext.Current.CancellationToken);
            var envelope = Assert.IsType<LanguageWorkerArtifactEnvelope>(execution.Artifact);
            Assert.NotNull(envelope.FileContentsBase64);
            var peImage = Convert.FromBase64String(
                envelope.FileContentsBase64[$"{PeachPieToolchain.AssemblyName}.dll"]);
            using var pe = new PEReader(new MemoryStream(peImage, writable: false));
            var metadata = pe.GetMetadataReader();
            var names = metadata.MethodDefinitions
                .Select(handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name))
                .ToArray();

            Assert.Contains(names, static name => name.Contains("global_name", StringComparison.Ordinal));
            Assert.Contains(names, static name => name.Contains("method_name", StringComparison.Ordinal));
        }
        finally
        {
            PeachPieTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RepeatedBuildsProduceTheSameManagedPeAndArtifactIdentity()
    {
        var root = PeachPieTestSettings.CreateRoot();
        try
        {
            var service = PeachPieTestSettings.CreateBuildService(root);
            var request = PeachPieTestSettings.CreateRequest(
                BuildTarget.Artifact,
                "<?php function answer(): int { return 42; } echo answer();");

            var first = await service.BuildAsync(request, TestContext.Current.CancellationToken);
            var second = await service.BuildAsync(
                request with
                {
                    RequestId = $"request-{Guid.NewGuid():N}",
                    IdempotencyKey = $"idempotency-{Guid.NewGuid():N}"
                },
                TestContext.Current.CancellationToken);

            var firstEnvelope = Assert.IsType<LanguageWorkerArtifactEnvelope>(first.Artifact);
            var secondEnvelope = Assert.IsType<LanguageWorkerArtifactEnvelope>(second.Artifact);
            Assert.Equal(firstEnvelope.ArtifactRef, secondEnvelope.ArtifactRef);
            Assert.NotNull(firstEnvelope.FileContentsBase64);
            Assert.NotNull(secondEnvelope.FileContentsBase64);
            Assert.Equal(
                firstEnvelope.FileContentsBase64[$"{PeachPieToolchain.AssemblyName}.dll"],
                secondEnvelope.FileContentsBase64[$"{PeachPieToolchain.AssemblyName}.dll"]);
        }
        finally
        {
            PeachPieTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public void ReservedBootstrapPathIsRejected()
    {
        var request = PeachPieTestSettings.CreateRequest(
            BuildTarget.CompileCheck,
            "<?php echo 42;",
            PeachPieCompiler.BootstrapFileName);

        var exception = Assert.Throws<PeachPieBuildRequestValidationException>(() =>
            PeachPieWorkspaceValidator.Validate(request, PeachPieTestSettings.LoadManifest()));

        Assert.Contains("reserved", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ProcessResult> RunArtifactAsync(
        LanguageWorkerArtifactEnvelope envelope,
        string root,
        string name)
    {
        var directory = Path.Combine(root, $"run-{name}");
        Directory.CreateDirectory(directory);
        Assert.NotNull(envelope.FileContentsBase64);
        foreach (var (path, base64) in envelope.FileContentsBase64)
        {
            var outputPath = Path.Combine(directory, path);
            await File.WriteAllBytesAsync(
                outputPath,
                Convert.FromBase64String(base64),
                TestContext.Current.CancellationToken);
        }
        var runtimeConfig = new
        {
            runtimeOptions = new
            {
                tfm = "net10.0",
                framework = new { name = "Microsoft.NETCore.App", version = "10.0.9" }
            }
        };
        await File.WriteAllTextAsync(
            Path.Combine(directory, $"{PeachPieToolchain.AssemblyName}.runtimeconfig.json"),
            JsonSerializer.Serialize(runtimeConfig),
            TestContext.Current.CancellationToken);
        var startInfo = new ProcessStartInfo(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add($"{PeachPieToolchain.AssemblyName}.dll");
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start emitted PeachPie artifact.");
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        return new ProcessResult(
            process.ExitCode,
            await stdout,
            await stderr);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PeachPieTestGroup
{
    public const string Name = "PeachPie worker tests";
}

internal sealed class PeachPieProcessEnvironment : IDisposable
{
    private readonly IReadOnlyDictionary<string, string?> _previous;

    private PeachPieProcessEnvironment(IReadOnlyDictionary<string, string?> previous) =>
        _previous = previous;

    public static PeachPieProcessEnvironment Apply(string root)
    {
        var previous = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in PeachPieTestSettings.WebHostConfiguration(root))
        {
            var environmentKey = key.Replace(":", "__", StringComparison.Ordinal);
            previous[environmentKey] = Environment.GetEnvironmentVariable(environmentKey);
            Environment.SetEnvironmentVariable(environmentKey, value);
        }
        return new PeachPieProcessEnvironment(previous);
    }

    public void Dispose()
    {
        foreach (var (key, value) in _previous)
            Environment.SetEnvironmentVariable(key, value);
    }
}
