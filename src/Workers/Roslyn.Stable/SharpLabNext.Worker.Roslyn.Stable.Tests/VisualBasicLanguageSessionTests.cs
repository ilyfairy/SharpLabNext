using SharpLabNext.Contracts;
using SharpLabNext.Worker.Roslyn;

namespace SharpLabNext.Worker.Roslyn.Stable.Tests;

public sealed class VisualBasicLanguageSessionTests
{
    [Fact]
    public async Task AutomaticOutputKindInitializesVisualBasicSessionAsLibrary()
    {
        const string source = "Public Class Calculator\nEnd Class";
        await using var manager = CreateManager();
        var contract = await manager.OpenAsync(LanguageSessionTests.CreateVisualBasicOpenRequest("vb-automatic-output-kind", source, outputKind: BuildOutputKind.Auto), TestContext.Current.CancellationToken);
        var session = manager.GetRequired(contract.SessionId);

        var snapshot = await session.GetDocumentSnapshotAsync("file:///Program.vb", TestContext.Current.CancellationToken);

        Assert.Equal(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary, snapshot.Document.Project.CompilationOptions?.OutputKind);
    }

    [Fact]
    public async Task VisualBasicCompletionHoverSignatureTokensSymbolsAndActionsWork()
    {
        const string source = "Imports System.Text\nImports System\nPublic Class Demo\nPublic Sub Run()\n        Dim text As String = \"\"\n        Dim length = text.Length\n    End Sub\nEnd Class";
        const string completionSource = "Imports System\nPublic Class Demo\n    Public Sub Run()\n        Console.\n";
        const string signatureSource = "Imports System\nPublic Class Demo\n    Public Sub Run()\n        Console.WriteLine(";
        await using var manager = CreateManager();
        var contract = await manager.OpenAsync(LanguageSessionTests.CreateVisualBasicOpenRequest("vb-features", source), TestContext.Current.CancellationToken);
        var session = manager.GetRequired(contract.SessionId);
        await session.DidOpenAsync(new LspDidOpenTextDocumentParams(new LspTextDocumentItem("file:///Program.vb", "visual-basic", 1, source)), TestContext.Current.CancellationToken);

        var hover = await session.GetHoverAsync(new LspTextDocumentPositionParams(new LspTextDocumentIdentifier("file:///Program.vb"), new LspPosition(5, 28)), TestContext.Current.CancellationToken);
        var tokens = await session.GetSemanticTokensAsync(new LspSemanticTokensParams(new LspTextDocumentIdentifier("file:///Program.vb")), TestContext.Current.CancellationToken);
        var symbols = await session.GetDocumentSymbolsAsync(new LspDocumentSymbolParams(new LspTextDocumentIdentifier("file:///Program.vb")), TestContext.Current.CancellationToken);
        var actions = await session.GetCodeActionsAsync(
            new LspCodeActionParams(
                new LspTextDocumentIdentifier("file:///Program.vb"),
                new LspRange(new LspPosition(0, 0), new LspPosition(7, 9)),
                new LspCodeActionContext([], null)),
            TestContext.Current.CancellationToken);

        await session.DidChangeAsync(
            new LspDidChangeTextDocumentParams(
                new LspVersionedTextDocumentIdentifier("file:///Program.vb", 2),
                [new LspTextDocumentContentChangeEvent(null, null, completionSource)]),
            TestContext.Current.CancellationToken);
         var completions = await session.GetCompletionsAsync(new LspCompletionParams(new LspTextDocumentIdentifier("file:///Program.vb"), new LspPosition(3, 16), new LspCompletionContext(1, null)), TestContext.Current.CancellationToken);
        var writeLine = Assert.Single(completions.Items, static item => item.Label == "WriteLine");
        var resolved = await session.ResolveCompletionAsync(writeLine, TestContext.Current.CancellationToken);

        await session.DidChangeAsync(
            new LspDidChangeTextDocumentParams(
                new LspVersionedTextDocumentIdentifier("file:///Program.vb", 3),
                [new LspTextDocumentContentChangeEvent(null, null, signatureSource)]),
            TestContext.Current.CancellationToken);
         var signature = await session.GetSignatureHelpAsync(new LspSignatureHelpParams(new LspTextDocumentIdentifier("file:///Program.vb"), new LspPosition(3, 26), null), TestContext.Current.CancellationToken);

        Assert.Equal("visual-basic", contract.LanguageId);
        Assert.Contains("WriteLine", resolved.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(hover);
        Assert.Contains("Length", hover.Contents.Value, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(signature);
        Assert.Contains(signature.Signatures, static item => item.Label.Contains("WriteLine", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(tokens.Data);
        var demo = Assert.Single(symbols, static symbol => symbol.Name == "Demo");
        Assert.Contains(demo.Children, static symbol => symbol.Name == "Run");
        Assert.Contains(actions, static action => action.Kind == "source.organizeImports");
        Assert.Contains(actions, static action => action.Kind == "source.formatDocument");
    }

    [Theory]
    [InlineData("Con")]
    [InlineData("Console")]
    public async Task VisualBasicCompletionPreservesAllRoslynPrefixMatches(string prefix)
    {
        var source = $$"""
            Imports System
            Public Class Demo
                Public Sub Run()
                    {{prefix}}
                End Sub
            End Class
            """;
        await using var manager = CreateManager();
        var contract = await manager.OpenAsync(LanguageSessionTests.CreateVisualBasicOpenRequest($"vb-completion-prefix-{prefix}", source), TestContext.Current.CancellationToken);
        var session = manager.GetRequired(contract.SessionId);
        await session.DidOpenAsync(new LspDidOpenTextDocumentParams(new LspTextDocumentItem("file:///Program.vb", "visual-basic", 1, source)), TestContext.Current.CancellationToken);

        var completions = await session.GetCompletionsAsync(new LspCompletionParams(new LspTextDocumentIdentifier("file:///Program.vb"), new LspPosition(3, 8 + prefix.Length), new LspCompletionContext(1, null)), TestContext.Current.CancellationToken);

        Assert.Contains(completions.Items, static item => item.Label.Equals("Console", StringComparison.OrdinalIgnoreCase));
        if (prefix == "Con")
            Assert.Contains(completions.Items, static item => item.Label.Equals("Const", StringComparison.OrdinalIgnoreCase));
        Assert.False(completions.IsIncomplete);
    }

    [Fact]
    public async Task CSharpAndVisualBasicSessionsRemainIsolatedWhenConcurrent()
    {
        await using var manager = CreateManager();
        var csharpContract = await manager.OpenAsync(LanguageSessionTests.CreateOpenRequest("parallel-csharp", "int value = 1;"), TestContext.Current.CancellationToken);
        var visualBasicContract = await manager.OpenAsync(LanguageSessionTests.CreateVisualBasicOpenRequest("parallel-vb", "Module Program\n    Dim value As Integer = \"bad\"\nEnd Module"), TestContext.Current.CancellationToken);
        var csharp = manager.GetRequired(csharpContract.SessionId);
        var visualBasic = manager.GetRequired(visualBasicContract.SessionId);
         await csharp.DidOpenAsync(new LspDidOpenTextDocumentParams(new LspTextDocumentItem("file:///Program.cs", "csharp", 1, "int value = 1;")), TestContext.Current.CancellationToken);
         await visualBasic.DidOpenAsync(new LspDidOpenTextDocumentParams(new LspTextDocumentItem("file:///Program.vb", "visual-basic", 1, "Module Program\n    Dim value As Integer = \"bad\"\nEnd Module")), TestContext.Current.CancellationToken);

        var csharpDiagnosticsTask = csharp.GetDiagnosticsAsync("file:///Program.cs", 1, TestContext.Current.CancellationToken);
        var visualBasicDiagnosticsTask = visualBasic.GetDiagnosticsAsync("file:///Program.vb", 1, TestContext.Current.CancellationToken);
        await Task.WhenAll(csharpDiagnosticsTask, visualBasicDiagnosticsTask);
        var csharpDiagnostics = await csharpDiagnosticsTask;
        var visualBasicDiagnostics = await visualBasicDiagnosticsTask;

        Assert.Equal("csharp", csharpContract.LanguageId);
        Assert.Equal("visual-basic", visualBasicContract.LanguageId);
        Assert.NotNull(csharpDiagnostics);
        Assert.NotNull(visualBasicDiagnostics);
        Assert.DoesNotContain(csharpDiagnostics.Diagnostics, static diagnostic => diagnostic.Code.StartsWith("BC", StringComparison.Ordinal));
        Assert.Contains(visualBasicDiagnostics.Diagnostics, static diagnostic => diagnostic.Code.StartsWith("BC", StringComparison.Ordinal));
    }

    private static RoslynLanguageSessionManager CreateManager() =>
        new(new ReferenceSetProvider([new ReferenceSetDefinition("net10-ref", CSharpBuildServiceTests.GetNet10ReferencePathForHost(), "net10.0", CSharpBuildServiceTests.GetNet10ReferenceVersionForHost())]), new RoslynWorkerIdentity("development", "roslyn-stable", "5.6.0", null, "development-worker-image"), CompilationLimits.Default, LspLimits.Default);
}
