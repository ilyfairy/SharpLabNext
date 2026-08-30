using SharpLabNext.Contracts;
using SharpLabNext.Worker.FSharp.Compiler;

namespace SharpLabNext.Worker.FSharp.Tests;

public sealed class FSharpLanguageSessionTests
{
    [Fact]
    public async Task SingleFileConsoleSessionAllowsImplicitModule()
    {
        var root = FSharpTestSettings.CreateRoot();
        try
        {
            var settings = FSharpTestSettings.Create(root);
            await using var manager = new FSharpLanguageSessionManager(new FSharpReferenceSetProvider(settings.ReferenceSets), new FSharpCompilerFacade(), settings);
            const string text = "open System\n\nprintfn \"Hello from SharpLabNext\"\n";
            var request = CreateOpenRequest(text, BuildOutputKind.Console);
            var descriptor = await manager.OpenAsync(request, TestContext.Current.CancellationToken);
            Assert.True(manager.TryGet(descriptor.SessionId, out var session));
            Assert.NotNull(session);

            var diagnostics = await session.GetDiagnosticsAsync("sharplabnext:///Program.fs", TestContext.Current.CancellationToken);

            Assert.DoesNotContain(diagnostics.Diagnostics, static item => item.Code == "FS0222");
            Assert.DoesNotContain(diagnostics.Diagnostics, static item => item.Severity == 1);
        }
        finally
        {
            FSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task SessionProvidesFcsDiagnosticsCompletionHoverSignatureAndSymbols()
    {
        var root = FSharpTestSettings.CreateRoot();
        try
        {
            var settings = FSharpTestSettings.Create(root);
            await using var manager = new FSharpLanguageSessionManager(new FSharpReferenceSetProvider(settings.ReferenceSets), new FSharpCompilerFacade(), settings);
            var text = """
                module Program
                open System
                let values = [1; 2; 3] |> List.map (fun value -> value + 1)
                let write () = Console.WriteLine("hello")
                """;
            var request = CreateOpenRequest(text);
            var descriptor = await manager.OpenAsync(request, TestContext.Current.CancellationToken);
            Assert.True(manager.TryGet(descriptor.SessionId, out var session));
            Assert.NotNull(session);
            var uri = "sharplabnext:///Program.fs";

            var diagnostics = await session.GetDiagnosticsAsync(uri, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(diagnostics.Diagnostics, static item => item.Severity == 1);

            var completionLine = "let write () = Console.Wri";
            await session.DidChangeAsync(new FSharpLspDidChangeParams(new FSharpLspVersionedTextDocumentIdentifier(uri, 2), [new FSharpLspTextChange(null, null, string.Join('\n', text.Split('\n')[..3]) + "\n" + completionLine + "\n")]), TestContext.Current.CancellationToken);
            var completions = await session.GetCompletionsAsync(new FSharpLspCompletionParams(new FSharpLspTextDocumentIdentifier(uri), new FSharpLspPosition(3, completionLine.Length), null), TestContext.Current.CancellationToken);
            Assert.Contains(completions.Items, static item => item.Label == "WriteLine");

            await session.DidChangeAsync(new FSharpLspDidChangeParams(new FSharpLspVersionedTextDocumentIdentifier(uri, 3), [new FSharpLspTextChange(null, null, text)]), TestContext.Current.CancellationToken);
            var mapColumn = text.Split('\n')[2].IndexOf("map", StringComparison.Ordinal) + 1;
            var hover = await session.GetHoverAsync(new FSharpLspTextDocumentPositionParams(new FSharpLspTextDocumentIdentifier(uri), new FSharpLspPosition(2, mapColumn)), TestContext.Current.CancellationToken);
            Assert.NotNull(hover);
            Assert.Contains("map", hover.Contents.Value, StringComparison.OrdinalIgnoreCase);

            var writeLine = text.Split('\n')[3];
            var signature = await session.GetSignatureHelpAsync(new FSharpLspSignatureHelpParams(new FSharpLspTextDocumentIdentifier(uri), new FSharpLspPosition(3, writeLine.LastIndexOf('(') + 1), null), TestContext.Current.CancellationToken);
            Assert.NotNull(signature);
            Assert.Contains(signature.Signatures, static item => item.Label.Contains("WriteLine", StringComparison.Ordinal));

            var symbols = await session.GetDocumentSymbolsAsync(new FSharpLspDocumentSymbolParams(new FSharpLspTextDocumentIdentifier(uri)), TestContext.Current.CancellationToken);
            Assert.Contains(symbols, static item => item.Name.Contains("Program", StringComparison.Ordinal));

            const string invalid = "module Program\nlet value: int = \"wrong\"\n";
            await session.DidChangeAsync(new FSharpLspDidChangeParams(new FSharpLspVersionedTextDocumentIdentifier(uri, 4), [new FSharpLspTextChange(null, null, invalid)]), TestContext.Current.CancellationToken);
            var invalidDiagnostics = await session.GetDiagnosticsAsync(uri, TestContext.Current.CancellationToken);
            Assert.Contains(invalidDiagnostics.Diagnostics, static item => item.Code == "FS0001");

            Assert.True(await manager.CloseAsync(descriptor.SessionId));
            Assert.False(manager.TryGet(descriptor.SessionId, out _));
        }
        finally
        {
            FSharpTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task SemanticTokensAndUnusedOpenActionsUseUtf16AcrossMultiFileWorkspace()
    {
        var root = FSharpTestSettings.CreateRoot();
        try
        {
            var settings = FSharpTestSettings.Create(root);
            await using var manager = new FSharpLanguageSessionManager(new FSharpReferenceSetProvider(settings.ReferenceSets), new FSharpCompilerFacade(), settings);
            const string definitions = "namespace Demo\nmodule Shared =\n    let answer = 42\n";
            const string program = "module Program\nopen Demo\nopen System.Text\nlet value = (\"😀\", Shared.answer)\n";
            var request = CreateOpenRequest(
                [
                    new WorkspaceFile("Definitions.fs", 1, definitions),
                    new WorkspaceFile("Program.fs", 2, program)
                ],
                ["Definitions.fs", "Program.fs"],
                "Program.fs");
            var descriptor = await manager.OpenAsync(request, TestContext.Current.CancellationToken);
            Assert.True(manager.TryGet(descriptor.SessionId, out var session));
            Assert.NotNull(session);

            var definitionsTokens = await session.GetSemanticTokensAsync(new FSharpLspSemanticTokensParams(new FSharpLspTextDocumentIdentifier("sharplabnext:///Definitions.fs")), TestContext.Current.CancellationToken);
            var programTokens = await session.GetSemanticTokensAsync(new FSharpLspSemanticTokensParams(new FSharpLspTextDocumentIdentifier("sharplabnext:///Program.fs")), TestContext.Current.CancellationToken);
            var decodedDefinitions = Decode(definitionsTokens.Data);
            var decodedProgram = Decode(programTokens.Data);

            Assert.NotEmpty(decodedDefinitions);
            Assert.NotEmpty(decodedProgram);
            Assert.Equal("2:1", programTokens.ResultId);
            var programLine = program.Split('\n')[3];
            var sharedColumn = programLine.IndexOf("Shared", StringComparison.Ordinal);
            Assert.Contains(decodedProgram, token => token.Line == 3 && token.Character == sharedColumn && token.Length == "Shared".Length);
            Assert.True(sharedColumn > programLine.IndexOf("😀", StringComparison.Ordinal));
            Assert.All(decodedProgram, token => Assert.InRange(token.Type, 0, FSharpLanguageSession.SemanticTokenTypes.Length - 1));

            var quickFixes = await session.GetCodeActionsAsync(
                new FSharpLspCodeActionParams(
                    new FSharpLspTextDocumentIdentifier("sharplabnext:///Program.fs"),
                    new FSharpLspRange(new FSharpLspPosition(2, 0), new FSharpLspPosition(2, "open System.Text".Length)),
                    new FSharpLspCodeActionContext([], ["quickfix"])),
                TestContext.Current.CancellationToken);
            var quickFix = Assert.Single(quickFixes);
            Assert.Equal("quickfix", quickFix.Kind);
            var quickFixEdit = Assert.Single(quickFix.Edit.Changes["sharplabnext:///Program.fs"]);
            Assert.Equal(2, quickFixEdit.Range.Start.Line);
            Assert.Equal(string.Empty, quickFixEdit.NewText);

            var organizeActions = await session.GetCodeActionsAsync(
                new FSharpLspCodeActionParams(
                    new FSharpLspTextDocumentIdentifier("sharplabnext:///Program.fs"),
                    new FSharpLspRange(new FSharpLspPosition(0, 0), new FSharpLspPosition(3, programLine.Length)),
                    new FSharpLspCodeActionContext([], ["source.organizeImports"])),
                TestContext.Current.CancellationToken);
            var organize = Assert.Single(organizeActions);
            Assert.Equal("source.organizeImports", organize.Kind);
            Assert.Single(organize.Edit.Changes["sharplabnext:///Program.fs"]);

            var unsupportedFormatting = await session.GetCodeActionsAsync(
                new FSharpLspCodeActionParams(
                    new FSharpLspTextDocumentIdentifier("sharplabnext:///Program.fs"),
                    new FSharpLspRange(new FSharpLspPosition(0, 0), new FSharpLspPosition(3, programLine.Length)),
                    new FSharpLspCodeActionContext([], ["source.formatDocument"])),
                TestContext.Current.CancellationToken);
            Assert.Empty(unsupportedFormatting);
        }
        finally
        {
            FSharpTestSettings.DeleteRoot(root);
        }
    }

    private static OpenLanguageSessionRequest CreateOpenRequest(string text, BuildOutputKind outputKind = BuildOutputKind.Library) => CreateOpenRequest([new WorkspaceFile("Program.fs", 1, text)], ["Program.fs"], "Program.fs", outputKind);

    private static OpenLanguageSessionRequest CreateOpenRequest(IReadOnlyList<WorkspaceFile> files, IReadOnlyList<string> sourceOrder, string activeFilePath, BuildOutputKind outputKind = BuildOutputKind.Library)
    {
        var options = new BuildOptions(BuildConfiguration.Debug, Optimize: false, outputKind, AllowUnsafe: false, EmitPortablePdb: true, NullableContextMode.Disable, LanguageVersion: "9.0");
        var workspace = new WorkspaceSnapshot(ContractSchemaVersions.WorkspaceSnapshot, 1, 1, "fsharp", files, activeFilePath, sourceOrder, "net10-ref", options);
        return new OpenLanguageSessionRequest("request-session", "pipeline-session", "fsharp", "fsharp-stable", "net10-ref", workspace);
    }

    private static List<DecodedSemanticToken> Decode(IReadOnlyList<int> data)
    {
        Assert.Equal(0, data.Count % 5);
        var result = new List<DecodedSemanticToken>(data.Count / 5);
        var line = 0;
        var character = 0;
        for (var index = 0; index < data.Count; index += 5)
        {
            line += data[index];
            character = data[index] == 0 ? character + data[index + 1] : data[index + 1];
            result.Add(new DecodedSemanticToken(line, character, data[index + 2], data[index + 3]));
        }
        return result;
    }

    private sealed record DecodedSemanticToken(int Line, int Character, int Length, int Type);
}
