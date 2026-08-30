using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.CppCli.Tests;

public sealed class CppCliCompilerProcessTests
{
    [Fact]
    public void CommandUsesFixedCompilerAndOnlyRelativeSourceAndOutputPaths()
    {
        var root = CppCliTestSettings.CreateRoot();
        try
        {
            var settings = CppCliTestSettings.CreateSettings(root);
            var jobRoot = Path.Combine(root, "work", "build-test");
            var command = CppCliCompilerCommand.Create(settings, jobRoot, "Program.cpp", "output/SharpLabNext.User.obj", "output/SharpLabNext.User.exe", optimize: true);
            var arguments = command.ArgumentList.ToArray();

            Assert.Equal(settings.CompilerPath, command.FileName);
            Assert.Equal(jobRoot, command.WorkingDirectory);
            Assert.Equal("-all", command.Environment["WINEDEBUG"]);
            Assert.False(command.Environment.ContainsKey("TMP"));
            Assert.False(command.Environment.ContainsKey("TEMP"));
            Assert.False(command.Environment.ContainsKey("TMPDIR"));
            Assert.Contains("/nologo", arguments);
            Assert.Contains("/EHa", arguments);
            Assert.DoesNotContain("/EHsc", arguments);
            Assert.Contains("/clr", arguments);
            Assert.Contains("/MD", arguments);
            Assert.Contains("/O2", arguments);
            Assert.Contains("/utf-8", arguments);
            Assert.Contains("/diagnostics:column", arguments);
            Assert.Contains("/experimental:deterministic", arguments);
            Assert.Contains("Program.cpp", arguments);
            Assert.Contains("/Fooutput/SharpLabNext.User.obj", arguments);
            Assert.Contains("/Feoutput/SharpLabNext.User.exe", arguments);
            Assert.Equal(["/link", "/Brepro"], arguments[^2..]);
            Assert.DoesNotContain(arguments, argument => argument.Contains(jobRoot, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            CppCliTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public void DiagnosticsMapMsvcAndLinkerOutputAndDropDeterministicCryptoNoise()
    {
        var root = CppCliTestSettings.CreateRoot();
        try
        {
            var diagnostics = CppCliCompilerProcess.ParseDiagnostics("Program.cpp(4,7): error C2065: 'missing': undeclared identifier\n" + "Program.cpp(6,1): fatal error C1121: call to CryptoAPI failed\n", "LINK : fatal error LNK1104: cannot open file 'missing.lib'\n", root, ["Program.cpp"], 7, 3, 100);

            Assert.Equal(2, diagnostics.Count);
            var compiler = Assert.Single(diagnostics, static diagnostic => diagnostic.Code == "C2065");
            Assert.Equal("msvc-cl", compiler.Source);
            Assert.Equal(DiagnosticSeverity.Error, compiler.Severity);
            Assert.Equal("Program.cpp", compiler.FilePath);
            Assert.Equal(new TextRange(3, 6, 3, 7), compiler.Range);
            Assert.Equal(7, compiler.WorkspaceRevision);
            Assert.Equal(3, compiler.SelectionRevision);
            Assert.Contains(diagnostics, static diagnostic => diagnostic.Code == "LNK1104");
            Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Code == "C1121");
        }
        finally
        {
            CppCliTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public void RelativePathGuardRejectsTraversal()
    {
        var root = CppCliTestSettings.CreateRoot();
        try
        {
            var settings = CppCliTestSettings.CreateSettings(root);
            Assert.Throws<ArgumentException>(() => CppCliCompilerCommand.Create(settings, root, "../Program.cpp", "output/User.obj", "output/User.exe", optimize: false));
        }
        finally
        {
            CppCliTestSettings.DeleteRoot(root);
        }
    }
}
