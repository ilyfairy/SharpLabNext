using Microsoft.Extensions.Logging.Abstractions;
using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Sdk;

namespace SharpLabNext.Worker.JSharp.Tests;

public sealed class JSharpCompilerProcessTests
{
    [Fact]
    public void CommandUsesFixedHostCompilerAndX64PlatformOnly()
    {
        var root = JSharpTestSettings.CreateRoot();
        try
        {
            var settings = JSharpTestSettings.CreateSettings(root);
            var jobRoot = Path.Combine(root, "work", "build-test");
            var command = JSharpCompilerCommand.Create(settings, jobRoot, "Program.jsl", "output/SharpLabNext.User.exe", optimize: true);
            var arguments = command.ArgumentList.ToArray();

            Assert.Equal(settings.CompilerHostPath, command.FileName);
            Assert.Equal(settings.CompilerPath, arguments[0]);
            Assert.Equal(jobRoot, command.WorkingDirectory);
            Assert.Equal(JSharpToolchain.WinePrefixPath, command.Environment["WINEPREFIX"]);
            Assert.Equal(JSharpToolchain.WineArchitecture, command.Environment["WINEARCH"]);
            Assert.Equal("-all", command.Environment["WINEDEBUG"]);
            Assert.False(command.Environment.ContainsKey("TMP"));
            Assert.False(command.Environment.ContainsKey("TEMP"));
            Assert.False(command.Environment.ContainsKey("TMPDIR"));
            Assert.Contains("/target:exe", arguments);
            Assert.Contains("/platform:x64", arguments);
            Assert.DoesNotContain("/platform:anycpu", arguments);
            Assert.DoesNotContain("/platform:x86", arguments);
            Assert.Contains("/utf8output", arguments);
            Assert.Contains("/optimize+", arguments);
            Assert.Contains("/out:output/SharpLabNext.User.exe", arguments);
            Assert.Equal("Program.jsl", arguments[^1]);
            Assert.DoesNotContain(arguments, argument => argument.Contains(jobRoot, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            JSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public void DiagnosticsMapVjcOutputAndRespectLimit()
    {
        var root = JSharpTestSettings.CreateRoot();
        try
        {
            var diagnostics = JSharpCompilerProcess.ParseDiagnostics("Program.jsl(4,7): error VJS1234: missing symbol\n" + "Program.jsl(5,1): warning VJS2000: warning text\n", "vjc : error VJS9999: locationless failure\n", root, ["Program.jsl"], 7, 3, maximumDiagnostics: 2);

            Assert.Equal(2, diagnostics.Count);
            var compiler = Assert.Single(diagnostics, static diagnostic => diagnostic.Code == "VJS1234");
            Assert.Equal("vjc", compiler.Source);
            Assert.Equal(DiagnosticSeverity.Error, compiler.Severity);
            Assert.Equal("Program.jsl", compiler.FilePath);
            Assert.Equal(new TextRange(3, 6, 3, 7), compiler.Range);
            Assert.Equal(7, compiler.WorkspaceRevision);
            Assert.Equal(3, compiler.SelectionRevision);
            Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Code == "VJS9999");
        }
        finally
        {
            JSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CombinedProcessOutputLimitKillsCompilerTree()
    {
        var root = JSharpTestSettings.CreateRoot();
        try
        {
            var settings = JSharpTestSettings.CreateSettings(root, new JSharpProcessLimits(4 * 1024, 512L * 1024 * 1024, 100, 10));
            using var compiler = new JSharpCompilerProcess(settings, JSharpTestSettings.LoadManifest(), NullLogger<JSharpCompilerProcess>.Instance);

            var exception = await Assert.ThrowsAsync<LanguageWorkerRequestException>(() => compiler.CompileAsync(JSharpTestSettings.Validate("OUTPUT_LIMIT"), TestContext.Current.CancellationToken));

            Assert.Equal("compiler-output-limit", exception.Code);
            Assert.Equal(1, compiler.StartedProcessCount);
        }
        finally
        {
            JSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task WorkingSetLimitKillsCompilerTree()
    {
        var root = JSharpTestSettings.CreateRoot();
        try
        {
            var settings = JSharpTestSettings.CreateSettings(root, new JSharpProcessLimits(1024 * 1024, 64L * 1024 * 1024, 100, 10));
            using var compiler = new JSharpCompilerProcess(settings, JSharpTestSettings.LoadManifest(), NullLogger<JSharpCompilerProcess>.Instance);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var exception = await Assert.ThrowsAsync<LanguageWorkerRequestException>(() => compiler.CompileAsync(JSharpTestSettings.Validate("MEMORY_LIMIT"), timeout.Token));

            Assert.Equal("compiler-memory-limit", exception.Code);
            Assert.Equal(1, compiler.StartedProcessCount);
        }
        finally
        {
            JSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CapacityIsNonBlockingAndCancellationTerminatesActiveCompiler()
    {
        var root = JSharpTestSettings.CreateRoot();
        try
        {
            using var compiler = new JSharpCompilerProcess(JSharpTestSettings.CreateSettings(root), JSharpTestSettings.LoadManifest(), NullLogger<JSharpCompilerProcess>.Instance);
            using var cancellation = new CancellationTokenSource();
            var first = compiler.CompileAsync(JSharpTestSettings.Validate("SLEEP"), cancellation.Token);
            await WaitForAsync(() => compiler.StartedProcessCount == 1);

            var exception = await Assert.ThrowsAsync<LanguageWorkerRequestException>(() => compiler.CompileAsync(JSharpTestSettings.Validate("public class Second { }"), TestContext.Current.CancellationToken));
            Assert.Equal("compiler-capacity-exhausted", exception.Code);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        }
        finally
        {
            JSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public void RelativePathGuardRejectsTraversal()
    {
        var root = JSharpTestSettings.CreateRoot();
        try
        {
            var settings = JSharpTestSettings.CreateSettings(root);
            Assert.Throws<ArgumentException>(() => JSharpCompilerCommand.Create(settings, root, "../Program.jsl", "output/User.exe", optimize: false));
        }
        finally
        {
            JSharpTestSettings.DeleteRoot(root);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
