using System.Diagnostics;
using SharpLabNext.MonoJitInspector;
using SharpLabNext.RunnerFixture;

namespace SharpLabNext.UnitTests;

public sealed class MonoJitInspectorTests
{
    private const string RawOutput = """
        Mono JIT compiler version 6.12.0.182 (tarball Tue Jun 14 22:52:21 UTC 2022)
        method to IR Example.Program:Calculate (int)
        il_seq_point il: 0x0
        processing: register allocation diagnostic that must not escape
        Method int Example.Program:Calculate (int) emitted at 0x40c82fd0 to 0x40c82fdd (code length 13) [Example.exe]

        *** ASM for Example.Program:Calculate (int) ***

        /tmp/mono-jit:     file format elf64-x86-64

        Disassembly of section .text:

        0000000000000000 <Example_Program_Calculate__int_>:
        <BB>:3
           0:  48 8b c7              mov    %rdi,%rax
           3:  83 c0 01              add    $0x1,%eax
        <BB>:1
           6:  48 83 c4 00           add    $0x0,%rsp
           a:  61 00 00
        ***
        """;

    [Fact]
    public void ParserReturnsOnlyBoundedAssemblySection()
    {
        var method = new MonoMethodCandidate("0x06000001", "Example.Program.Calculate", "Example.Program:Calculate(int)");

        var section = MonoJitOutputParser.Parse(RawOutput, method, "6.12.0.182");

        Assert.Equal(13, section.NativeCodeSize);
        Assert.Equal(3, section.InstructionCount);
        Assert.Equal("0x40c82fd0", section.Address);
        Assert.Contains("mov    %rdi,%rax", section.Text, StringComparison.Ordinal);
        Assert.Contains("G_M000_IG00:", section.Text, StringComparison.Ordinal);
        Assert.Contains("; Total bytes of code 13", section.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("61 00 00", section.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("method to IR", section.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("il_seq_point", section.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("register allocation", section.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/mono-jit", section.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Example.Program:Other(int)")]
    [InlineData("Example.Program:Calculate(long)")]
    public void ParserRejectsOutputForAnotherMethod(string selector)
    {
        var method = new MonoMethodCandidate("0x06000001", "Example.Program.Calculate", selector);

        Assert.Throws<InvalidDataException>(() => MonoJitOutputParser.Parse(RawOutput, method, "6.12.0.182"));
    }

    [Fact]
    public void ParserRejectsInconsistentNativeSize()
    {
        var method = new MonoMethodCandidate("0x06000001", "Example.Program.Calculate", "Example.Program:Calculate(int)");
        var corrupted = RawOutput.Replace("code length 13", "code length 14", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => MonoJitOutputParser.Parse(corrupted, method, "6.12.0.182"));
    }

    [Fact]
    public void ParserFailureNeverEchoesRawVerboseOutput()
    {
        const string secret = "RAW-VERBOSE-MUST-NOT-ESCAPE";
        var method = new MonoMethodCandidate("0x06000001", "Example.Program.Other", "Example.Program:Other(int)");

        var exception = Assert.Throws<InvalidDataException>(() => MonoJitOutputParser.Parse(RawOutput + secret, method, "6.12.0.182"));

        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeVersionParserRequiresExactNumericVersion()
    {
        Assert.Equal("6.12.0.182", MonoJitOutputParser.ParseRuntimeVersion(RawOutput));
        Assert.Throws<InvalidDataException>(() => MonoJitOutputParser.ParseRuntimeVersion("Mono runtime version unavailable"));
    }

    [Fact]
    public void MetadataInspectionUsesMethodFilterWithoutLoadingAssembly()
    {
        var assemblyPath = typeof(MonoJitInspectorTests).Assembly.Location;

        var inspection = MonoAssemblyInspection.Read(assemblyPath, "*RuntimeVersionParserRequiresExactNumericVersion");

        var method = Assert.Single(inspection.Methods);
        Assert.EndsWith(".MonoJitInspectorTests.RuntimeVersionParserRequiresExactNumericVersion", method.DisplayName, StringComparison.Ordinal);
        Assert.StartsWith("0x06", method.Identity, StringComparison.Ordinal);
        Assert.Contains(":RuntimeVersionParserRequiresExactNumericVersion", method.Selector, StringComparison.Ordinal);
    }

    [Fact]
    public void ArgumentsRejectMissingFilesAndControlCharacters()
    {
        Assert.Throws<ArgumentException>(() => MonoJitInspectorArguments.Parse([]));
        Assert.Throws<FileNotFoundException>(() => MonoJitInspectorArguments.Parse([Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dll")]));
        Assert.Throws<ArgumentException>(() => MonoJitInspectorArguments.Parse([typeof(MonoJitInspectorTests).Assembly.Location, "bad\nfilter"]));
    }

    [Fact]
    public void SharedOutputBudgetKillsTheChildAtTheFirstOverflow()
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(RunnerFixtureMarker).Assembly.Location);
        start.ArgumentList.Add("compiler-child-hang");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the output-budget fixture.");
        var budget = new ProcessOutputBudget(process, maximumBytes: 4);

        Assert.True(budget.TryReserve(2));
        Assert.True(budget.TryReserve(2));
        Assert.False(budget.TryReserve(1));
        Assert.True(budget.Overflowed);
        Assert.True(process.WaitForExit(5_000), "The overflowed child process was not killed promptly.");
    }
}
