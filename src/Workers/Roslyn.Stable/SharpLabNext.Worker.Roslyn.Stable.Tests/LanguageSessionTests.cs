using System.Net.WebSockets;
using Microsoft.CodeAnalysis.Text;
using SharpLabNext.Contracts;
using SharpLabNext.Worker.Roslyn;

namespace SharpLabNext.Worker.Roslyn.Stable.Tests;

public sealed class LanguageSessionTests
{
    [Fact]
    public async Task SessionsAreIsolatedAndChangesRequireIncreasingVersions()
    {
        await using var manager = CreateManager();
        var firstContract = await manager.OpenAsync(
            CreateOpenRequest("first", "int value = 1;"),
            TestContext.Current.CancellationToken);
        var secondContract = await manager.OpenAsync(
            CreateOpenRequest("second", "int value = \"bad\";"),
            TestContext.Current.CancellationToken);
        var first = manager.GetRequired(firstContract.SessionId);
        var second = manager.GetRequired(secondContract.SessionId);

        await first.DidOpenAsync(
            new LspDidOpenTextDocumentParams(new LspTextDocumentItem("file:///Program.cs", "csharp", 1, "int value = 1;")),
            TestContext.Current.CancellationToken);
        await second.DidOpenAsync(
            new LspDidOpenTextDocumentParams(new LspTextDocumentItem("file:///Program.cs", "csharp", 1, "int value = \"bad\";")),
            TestContext.Current.CancellationToken);

        var firstDiagnostics = await first.GetDiagnosticsAsync("file:///Program.cs", 1, TestContext.Current.CancellationToken);
        var secondDiagnostics = await second.GetDiagnosticsAsync("file:///Program.cs", 1, TestContext.Current.CancellationToken);

        Assert.NotNull(firstDiagnostics);
        Assert.DoesNotContain(firstDiagnostics.Diagnostics, static diagnostic => diagnostic.Code == "CS0029");
        Assert.NotNull(secondDiagnostics);
        Assert.Contains(secondDiagnostics.Diagnostics, static diagnostic => diagnostic.Code == "CS0029");
        await Assert.ThrowsAsync<LspContentModifiedException>(() => first.DidChangeAsync(
            new LspDidChangeTextDocumentParams(
                new LspVersionedTextDocumentIdentifier("file:///Program.cs", 1),
                [new LspTextDocumentContentChangeEvent(null, null, "int value = 2;")]),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DiagnosticsEchoCurrentDocumentAndWorkspaceRevisions()
    {
        await using var manager = CreateManager();
        var contract = await manager.OpenAsync(
            CreateOpenRequest("diagnostics", "int value = 1;", revision: 20, selectionRevision: 4),
            TestContext.Current.CancellationToken);
        var session = manager.GetRequired(contract.SessionId);
        await session.DidOpenAsync(
            new LspDidOpenTextDocumentParams(new LspTextDocumentItem("file:///Program.cs", "csharp", 1, "int value = 1;")),
            TestContext.Current.CancellationToken);

        var changed = await session.DidChangeAsync(
            new LspDidChangeTextDocumentParams(
                new LspVersionedTextDocumentIdentifier("file:///Program.cs", 2),
                [new LspTextDocumentContentChangeEvent(null, null, "int value = \"bad\";")]),
            TestContext.Current.CancellationToken);
        var stale = await session.GetDiagnosticsAsync("file:///Program.cs", 1, TestContext.Current.CancellationToken);
        var current = await session.GetDiagnosticsAsync("file:///Program.cs", 2, TestContext.Current.CancellationToken);

        Assert.Equal(21, changed.WorkspaceRevision);
        Assert.Null(stale);
        Assert.NotNull(current);
        Assert.Equal(2, current.Version);
        Assert.Equal(21, current.WorkspaceRevision);
        Assert.Equal(4, current.SelectionRevision);
        Assert.All(current.Diagnostics, diagnostic =>
        {
            Assert.Equal(21, diagnostic.Data.WorkspaceRevision);
            Assert.Equal(4, diagnostic.Data.SelectionRevision);
            Assert.Equal(2, diagnostic.Data.DocumentVersion);
        });
    }

    [Fact]
    public async Task AutomaticOutputKindTracksTopLevelStatementsAcrossSessionChanges()
    {
        const string ordinarySource = "public sealed class Calculator { }";
        const string topLevelSource = "System.Console.WriteLine(42);";
        await using var manager = CreateManager();
        var contract = await manager.OpenAsync(
            CreateOpenRequest(
                "automatic-output-kind",
                ordinarySource,
                outputKind: BuildOutputKind.Auto),
            TestContext.Current.CancellationToken);
        var session = manager.GetRequired(contract.SessionId);

        var initial = await session.GetDocumentSnapshotAsync(
            "file:///Program.cs",
            TestContext.Current.CancellationToken);
        Assert.Equal(
            Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary,
            initial.Document.Project.CompilationOptions?.OutputKind);
        var initialDiagnostics = await session.GetDiagnosticsAsync(
            "file:///Program.cs",
            1,
            TestContext.Current.CancellationToken);
        Assert.NotNull(initialDiagnostics);
        Assert.DoesNotContain(initialDiagnostics.Diagnostics, static diagnostic => diagnostic.Code == "CS5001");

        await session.DidOpenAsync(
            new LspDidOpenTextDocumentParams(
                new LspTextDocumentItem("file:///Program.cs", "csharp", 1, topLevelSource)),
            TestContext.Current.CancellationToken);
        var topLevel = await session.GetDocumentSnapshotAsync(
            "file:///Program.cs",
            TestContext.Current.CancellationToken);
        Assert.Equal(
            Microsoft.CodeAnalysis.OutputKind.ConsoleApplication,
            topLevel.Document.Project.CompilationOptions?.OutputKind);
        var topLevelDiagnostics = await session.GetDiagnosticsAsync(
            "file:///Program.cs",
            1,
            TestContext.Current.CancellationToken);
        Assert.NotNull(topLevelDiagnostics);
        Assert.DoesNotContain(topLevelDiagnostics.Diagnostics, static diagnostic => diagnostic.Code == "CS8805");

        await session.DidChangeAsync(
            new LspDidChangeTextDocumentParams(
                new LspVersionedTextDocumentIdentifier("file:///Program.cs", 2),
                [new LspTextDocumentContentChangeEvent(null, null, ordinarySource)]),
            TestContext.Current.CancellationToken);
        var ordinary = await session.GetDocumentSnapshotAsync(
            "file:///Program.cs",
            TestContext.Current.CancellationToken);
        Assert.Equal(
            Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary,
            ordinary.Document.Project.CompilationOptions?.OutputKind);
        var ordinaryDiagnostics = await session.GetDiagnosticsAsync(
            "file:///Program.cs",
            2,
            TestContext.Current.CancellationToken);
        Assert.NotNull(ordinaryDiagnostics);
        Assert.DoesNotContain(ordinaryDiagnostics.Diagnostics, static diagnostic => diagnostic.Code == "CS5001");
    }

    [Fact]
    public async Task CompletionResolveHoverAndSignatureHelpUseRoslynFeatures()
    {
        const string source = "using System;\nclass Demo\n{\n    void Run()\n    {\n        Console.WriteL\n        string text = \"\";\n        var length = text.Length;\n        Console.WriteLine(\n    }\n}";
        await using var manager = CreateManager();
        var contract = await manager.OpenAsync(CreateOpenRequest("features", source), TestContext.Current.CancellationToken);
        var session = manager.GetRequired(contract.SessionId);
        await session.DidOpenAsync(
            new LspDidOpenTextDocumentParams(new LspTextDocumentItem("file:///Program.cs", "csharp", 1, source)),
            TestContext.Current.CancellationToken);

        var completions = await session.GetCompletionsAsync(
            new LspCompletionParams(
                new LspTextDocumentIdentifier("file:///Program.cs"),
                new LspPosition(5, 22),
                new LspCompletionContext(1, null)),
            TestContext.Current.CancellationToken);
        var writeLine = Assert.Single(completions.Items, static item => item.Label == "WriteLine");
        var resolved = await session.ResolveCompletionAsync(writeLine, TestContext.Current.CancellationToken);
        var hover = await session.GetHoverAsync(
            new LspTextDocumentPositionParams(
                new LspTextDocumentIdentifier("file:///Program.cs"),
                new LspPosition(7, 27)),
            TestContext.Current.CancellationToken);
        var signature = await session.GetSignatureHelpAsync(
            new LspSignatureHelpParams(
                new LspTextDocumentIdentifier("file:///Program.cs"),
                new LspPosition(8, 26),
                null),
            TestContext.Current.CancellationToken);

        Assert.Contains("WriteLine", resolved.Detail, StringComparison.Ordinal);
        Assert.NotNull(resolved.TextEdit);
        Assert.NotNull(hover);
        Assert.Contains("Length", hover.Contents.Value, StringComparison.Ordinal);
        Assert.NotNull(signature);
        Assert.Contains(signature.Signatures, static item => item.Label.Contains("WriteLine", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompletionResolveKeepsTypedSpanPrimaryAndAddsUsingAtTheRoslynLocation()
    {
        const string source = "class Program\n{\n    static async System.Threading.Tasks.Task Main()\n    {\n        Console\n    }\n}";
        await using var manager = CreateManager();
        var contract = await manager.OpenAsync(
            CreateOpenRequest("completion-import", source),
            TestContext.Current.CancellationToken);
        var session = manager.GetRequired(contract.SessionId);
        await session.DidOpenAsync(
            new LspDidOpenTextDocumentParams(
                new LspTextDocumentItem("file:///Program.cs", "csharp", 1, source)),
            TestContext.Current.CancellationToken);

        var completions = await session.GetCompletionsAsync(
            new LspCompletionParams(
                new LspTextDocumentIdentifier("file:///Program.cs"),
                new LspPosition(4, 15),
                new LspCompletionContext(1, null)),
            TestContext.Current.CancellationToken);
        var console = Assert.Single(completions.Items, static item => item.Label == "Console");
        var resolved = await session.ResolveCompletionAsync(console, TestContext.Current.CancellationToken);

        Assert.NotNull(console.TextEdit);
        Assert.NotNull(resolved.TextEdit);
        Assert.Equal(new LspPosition(4, 8), resolved.TextEdit.Range.Start);
        Assert.Equal(new LspPosition(4, 15), resolved.TextEdit.Range.End);
        Assert.Equal(console.TextEdit.Range, resolved.TextEdit.Range);
        Assert.Equal("Console", resolved.TextEdit.NewText);
        Assert.Null(resolved.InsertTextFormat);
        var import = Assert.Single(resolved.AdditionalTextEdits!);
        Assert.Equal(new LspPosition(0, 0), import.Range.Start);
        Assert.Equal(import.Range.Start, import.Range.End);
        Assert.Contains("using System;", import.NewText, StringComparison.Ordinal);

        var updated = ApplyCompletionEdits(source, resolved);
        Assert.StartsWith("using System;", updated, StringComparison.Ordinal);
        Assert.Contains("        Console\n", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("Cousing", updated, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Con")]
    [InlineData("Console")]
    public async Task CompletionPreservesAllRoslynPrefixMatches(string prefix)
    {
        var source = $$"""
            class Program
            {
                void Run()
                {
                    {{prefix}}
                }
            }
            """;
        await using var manager = CreateManager();
        var contract = await manager.OpenAsync(
            CreateOpenRequest($"completion-prefix-{prefix}", source),
            TestContext.Current.CancellationToken);
        var session = manager.GetRequired(contract.SessionId);
        await session.DidOpenAsync(
            new LspDidOpenTextDocumentParams(
                new LspTextDocumentItem("file:///Program.cs", "csharp", 1, source)),
            TestContext.Current.CancellationToken);

        var completions = await session.GetCompletionsAsync(
            new LspCompletionParams(
                new LspTextDocumentIdentifier("file:///Program.cs"),
                PositionAfter(source, prefix),
                new LspCompletionContext(1, null)),
            TestContext.Current.CancellationToken);

        Assert.Contains(completions.Items, static item => item.Label == "Console");
        if (prefix == "Con")
            Assert.Contains(completions.Items, static item => item.Label == "const");
        Assert.False(completions.IsIncomplete);
    }

    [Theory]
    [InlineData(
        "cw",
        "class Program\n{\n    void Run()\n    {\n        cw\n    }\n}",
        4,
        10,
        "Console.WriteLine")]
    [InlineData(
        "forr",
        "class Program\n{\n    void Run()\n    {\n        forr\n    }\n}",
        4,
        12,
        "for (int")]
    [InlineData("svm", "class Program\n{\n    svm\n}", 2, 7, "static void Main")]
    [InlineData("svm", "class Program\n{\nsvm\n}", 2, 3, "static void Main")]
    [InlineData("svm", "class Program {\n    svm\n\n}", 1, 7, "static void Main")]
    [InlineData("svm", "class Program {\n\tsvm\n\n}", 1, 4, "static void Main")]
    [InlineData(
        "svm",
        "class Program {\n    void Test() {\n\n    }\n\n    svm\n}",
        5,
        7,
        "static void Main")]
    [InlineData(
        "svm",
        "class Program {\r\n    void Test() {\r\n        \r\n    }\r\n    svm\r\n    \r\n}",
        4,
        7,
        "static void Main")]
    [InlineData("prop", "class Program\n{\n    prop\n}", 2, 8, "public ${1:int} ${2:MyProperty}")]
    [InlineData("props", "class Program\n{\n    props\n}", 2, 9, "public ${1:int} ${2:MyProperty}")]
    [InlineData("PROPS", "class Program\n{\n    PROPS\n}", 2, 9, "public ${1:int} ${2:MyProperty}")]
    [InlineData("sim", "class Program\n{\n    sim\n}", 2, 7, "static int Main")]
    public async Task SemanticSnippetShortcutsUseRoslynContextAndStableEditRanges(
        string shortcut,
        string source,
        int line,
        int character,
        string expectedText)
    {
        await using var manager = CreateManager();
        var contract = await manager.OpenAsync(
            CreateOpenRequest($"snippet-{shortcut}", source),
            TestContext.Current.CancellationToken);
        var session = manager.GetRequired(contract.SessionId);
        await session.DidOpenAsync(
            new LspDidOpenTextDocumentParams(
                new LspTextDocumentItem("file:///Program.cs", "csharp", 1, source)),
            TestContext.Current.CancellationToken);

        var completions = await session.GetCompletionsAsync(
            new LspCompletionParams(
                new LspTextDocumentIdentifier("file:///Program.cs"),
                new LspPosition(line, character),
                new LspCompletionContext(1, null)),
            TestContext.Current.CancellationToken);
        var snippet = Assert.Single(completions.Items, item => item.Label == shortcut);
        var resolved = await session.ResolveCompletionAsync(snippet, TestContext.Current.CancellationToken);

        Assert.False(completions.IsIncomplete);
        Assert.Equal(15, snippet.Kind);
        Assert.NotNull(snippet.TextEdit);
        Assert.Equal(new LspPosition(line, character - shortcut.Length), snippet.TextEdit.Range.Start);
        Assert.Equal(new LspPosition(line, character), snippet.TextEdit.Range.End);
        Assert.Equal(2, snippet.InsertTextFormat);
        Assert.Contains(expectedText, snippet.TextEdit.NewText, StringComparison.Ordinal);
        Assert.NotNull(resolved.TextEdit);
        Assert.Equal(snippet.TextEdit, resolved.TextEdit);
        Assert.Equal(snippet.InsertTextFormat, resolved.InsertTextFormat);
        Assert.Equal(snippet.AdditionalTextEdits, resolved.AdditionalTextEdits);
        Assert.Contains(expectedText, resolved.TextEdit.NewText, StringComparison.Ordinal);
        Assert.Equal(2, resolved.InsertTextFormat);
        Assert.Contains("${0}", resolved.TextEdit.NewText, StringComparison.Ordinal);
        Assert.DoesNotContain("\\{", resolved.TextEdit.NewText, StringComparison.Ordinal);

        if (shortcut == "svm")
        {
            var normalizedSnippet = resolved.TextEdit.NewText.Replace("\r\n", "\n", StringComparison.Ordinal);
            var expectedSnippet = source.Contains("\nsvm", StringComparison.Ordinal)
                ? "    static void Main(string[] args)\n    {\n        ${0}\n    \\}"
                : "static void Main(string[] args)\n{\n    ${0}\n\\}";
            Assert.Contains(
                expectedSnippet,
                normalizedSnippet,
                StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("\t ")]
    [InlineData("      ")]
    public async Task SvmPreservesNonStandardMemberIndentation(string indentation)
    {
        var source = $"class Program {{\r\n{indentation}svm\r\n{indentation}int Keep = 42;\r\n}}";
        await using var manager = CreateManager();
        var contract = await manager.OpenAsync(
            CreateOpenRequest("snippet-svm-indentation", source),
            TestContext.Current.CancellationToken);
        var session = manager.GetRequired(contract.SessionId);
        await session.DidOpenAsync(
            new LspDidOpenTextDocumentParams(
                new LspTextDocumentItem("file:///Program.cs", "csharp", 1, source)),
            TestContext.Current.CancellationToken);

        var completions = await session.GetCompletionsAsync(
            new LspCompletionParams(
                new LspTextDocumentIdentifier("file:///Program.cs"),
                new LspPosition(1, indentation.Length + 3),
                new LspCompletionContext(1, null)),
            TestContext.Current.CancellationToken);
        var svm = Assert.Single(completions.Items);
        var resolved = await session.ResolveCompletionAsync(svm, TestContext.Current.CancellationToken);

        Assert.NotNull(resolved.TextEdit);
        Assert.Equal(new LspPosition(1, indentation.Length), resolved.TextEdit.Range.Start);
        Assert.Equal(new LspPosition(1, indentation.Length + 3), resolved.TextEdit.Range.End);
        var lines = resolved.TextEdit.NewText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');
        Assert.Equal("{", lines[1]);
        Assert.Equal("    ${0}", lines[2]);
        Assert.Equal("\\}", lines[3]);
    }

    [Theory]
    [InlineData("c")]
    [InlineData("cl")]
    [InlineData("class")]
    public async Task ClassSnippetRemainsAvailableWhileTypingItsFullShortcut(string prefix)
    {
        await using var manager = CreateManager();
        var contract = await manager.OpenAsync(
            CreateOpenRequest($"snippet-class-{prefix}", prefix),
            TestContext.Current.CancellationToken);
        var session = manager.GetRequired(contract.SessionId);
        await session.DidOpenAsync(
            new LspDidOpenTextDocumentParams(
                new LspTextDocumentItem("file:///Program.cs", "csharp", 1, prefix)),
            TestContext.Current.CancellationToken);

        var completions = await session.GetCompletionsAsync(
            new LspCompletionParams(
                new LspTextDocumentIdentifier("file:///Program.cs"),
                new LspPosition(0, prefix.Length),
                new LspCompletionContext(1, null)),
            TestContext.Current.CancellationToken);
        var snippet = Assert.Single(
            completions.Items,
            static item => item.Label == "class" && item.Kind == 15);
        Assert.Single(
            completions.Items,
            static item => item.Label == "class" && item.Kind == 14);
        var resolved = await session.ResolveCompletionAsync(snippet, TestContext.Current.CancellationToken);

        Assert.False(completions.IsIncomplete);
        Assert.NotNull(snippet.TextEdit);
        Assert.NotNull(resolved.TextEdit);
        Assert.Equal(new LspPosition(0, 0), resolved.TextEdit.Range.Start);
        Assert.Equal(new LspPosition(0, prefix.Length), resolved.TextEdit.Range.End);
        var nameTabStop = resolved.TextEdit.NewText.IndexOf("${1:MyClass}", StringComparison.Ordinal);
        var finalTabStop = resolved.TextEdit.NewText.IndexOf("${0}", StringComparison.Ordinal);
        Assert.Contains("class ${1:MyClass}", snippet.TextEdit.NewText, StringComparison.Ordinal);
        Assert.Contains("class ${1:MyClass}", resolved.TextEdit.NewText, StringComparison.Ordinal);
        Assert.True(nameTabStop >= 0);
        Assert.True(finalTabStop > nameTabStop);
        Assert.Equal(snippet.TextEdit, resolved.TextEdit);
        Assert.Equal(2, resolved.InsertTextFormat);
    }

    [Theory]
    [InlineData("while", "while (${1:true})")]
    [InlineData("if", "if (${1:true})")]
    [InlineData("do", "while (${1:true});")]
    [InlineData("lock", "lock (${1:this})")]
    [InlineData("using", "using (${1:resource})")]
    [InlineData("foreach", "foreach (var ${1:item} in ${2:collection})")]
    [InlineData("for", "for (int ${1:i} = 0; ${1:i} < ${2:length}; ${1:i}++)")]
    [InlineData("forr", "for (int ${1:i} = ${2:length} - 1; ${1:i} >= 0; ${1:i}--)")]
    public async Task StatementSnippetsExposeRoslynPlaceholderOrder(
        string shortcut,
        string expectedHeader)
    {
        var source = $"class Program\n{{\n    void Run()\n    {{\n        {shortcut}\n    }}\n}}";
        await using var manager = CreateManager();
        var contract = await manager.OpenAsync(
            CreateOpenRequest($"snippet-placeholders-{shortcut}", source),
            TestContext.Current.CancellationToken);
        var session = manager.GetRequired(contract.SessionId);
        await session.DidOpenAsync(
            new LspDidOpenTextDocumentParams(
                new LspTextDocumentItem("file:///Program.cs", "csharp", 1, source)),
            TestContext.Current.CancellationToken);

        var completions = await session.GetCompletionsAsync(
            new LspCompletionParams(
                new LspTextDocumentIdentifier("file:///Program.cs"),
                new LspPosition(4, 8 + shortcut.Length),
                new LspCompletionContext(1, null)),
            TestContext.Current.CancellationToken);
        var snippet = Assert.Single(
            completions.Items,
            item => item.Label == shortcut && item.Kind == 15);
        var resolved = await session.ResolveCompletionAsync(snippet, TestContext.Current.CancellationToken);

        Assert.NotNull(resolved.TextEdit);
        Assert.Equal(2, resolved.InsertTextFormat);
        Assert.Contains(expectedHeader, resolved.TextEdit.NewText, StringComparison.Ordinal);
        Assert.Contains("${0}", resolved.TextEdit.NewText, StringComparison.Ordinal);
        Assert.Equal(snippet.TextEdit, resolved.TextEdit);
        if (shortcut == "do")
        {
            Assert.True(
                resolved.TextEdit.NewText.IndexOf("${0}", StringComparison.Ordinal) <
                resolved.TextEdit.NewText.IndexOf("${1:true}", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("enum", "enum ${1:MyEnum}")]
    [InlineData("interface", "interface ${1:IMyInterface}")]
    [InlineData("struct", "struct ${1:MyStruct}")]
    public async Task TypeSnippetsSelectTheirGeneratedIdentifier(
        string shortcut,
        string expectedDeclaration)
    {
        await using var manager = CreateManager();
        var contract = await manager.OpenAsync(
            CreateOpenRequest($"snippet-type-{shortcut}", shortcut),
            TestContext.Current.CancellationToken);
        var session = manager.GetRequired(contract.SessionId);
        await session.DidOpenAsync(
            new LspDidOpenTextDocumentParams(
                new LspTextDocumentItem("file:///Program.cs", "csharp", 1, shortcut)),
            TestContext.Current.CancellationToken);

        var completions = await session.GetCompletionsAsync(
            new LspCompletionParams(
                new LspTextDocumentIdentifier("file:///Program.cs"),
                new LspPosition(0, shortcut.Length),
                new LspCompletionContext(1, null)),
            TestContext.Current.CancellationToken);
        var snippet = Assert.Single(
            completions.Items,
            item => item.Label == shortcut && item.Kind == 15);

        Assert.NotNull(snippet.TextEdit);
        Assert.Equal(2, snippet.InsertTextFormat);
        Assert.Contains(expectedDeclaration, snippet.TextEdit.NewText, StringComparison.Ordinal);
        Assert.Contains("${0}", snippet.TextEdit.NewText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("prop")]
    [InlineData("propg")]
    [InlineData("propi")]
    [InlineData("propr")]
    [InlineData("props")]
    public async Task PropertySnippetsSelectTypeThenIdentifier(string shortcut)
    {
        var source = $"class Program\n{{\n    {shortcut}\n}}";
        await using var manager = CreateManager();
        var contract = await manager.OpenAsync(
            CreateOpenRequest($"snippet-property-{shortcut}", source),
            TestContext.Current.CancellationToken);
        var session = manager.GetRequired(contract.SessionId);
        await session.DidOpenAsync(
            new LspDidOpenTextDocumentParams(
                new LspTextDocumentItem("file:///Program.cs", "csharp", 1, source)),
            TestContext.Current.CancellationToken);

        var completions = await session.GetCompletionsAsync(
            new LspCompletionParams(
                new LspTextDocumentIdentifier("file:///Program.cs"),
                new LspPosition(2, 4 + shortcut.Length),
                new LspCompletionContext(1, null)),
            TestContext.Current.CancellationToken);
        var snippet = Assert.Single(
            completions.Items,
            item => item.Label == shortcut && item.Kind == 15);

        Assert.NotNull(snippet.TextEdit);
        Assert.Equal(2, snippet.InsertTextFormat);
        var typeTabStop = snippet.TextEdit.NewText.IndexOf("${1:int}", StringComparison.Ordinal);
        var nameTabStop = snippet.TextEdit.NewText.IndexOf("${2:MyProperty}", StringComparison.Ordinal);
        Assert.True(typeTabStop >= 0);
        Assert.True(nameTabStop > typeTabStop);
        Assert.Contains("${0}", snippet.TextEdit.NewText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("c")]
    [InlineData("cl")]
    [InlineData("class")]
    public async Task ClassKeywordAndSnippetStayTogetherInTypeMemberContext(string prefix)
    {
        var source = $"class Program\n{{\n    {prefix}\n}}";
        await using var manager = CreateManager();
        var contract = await manager.OpenAsync(
            CreateOpenRequest($"member-class-{prefix}", source),
            TestContext.Current.CancellationToken);
        var session = manager.GetRequired(contract.SessionId);
        await session.DidOpenAsync(
            new LspDidOpenTextDocumentParams(
                new LspTextDocumentItem("file:///Program.cs", "csharp", 1, source)),
            TestContext.Current.CancellationToken);

        var completions = await session.GetCompletionsAsync(
            new LspCompletionParams(
                new LspTextDocumentIdentifier("file:///Program.cs"),
                new LspPosition(2, 4 + prefix.Length),
                new LspCompletionContext(1, null)),
            TestContext.Current.CancellationToken);

        Assert.Single(
            completions.Items,
            static item => item.Label == "class" && item.Kind == 14);
        Assert.Single(
            completions.Items,
            static item => item.Label == "class" && item.Kind == 15);
        Assert.False(completions.IsIncomplete);
    }

    [Fact]
    public async Task SvmDoesNotFallThroughToAnUnrelatedFuzzyImportOutsideTypeContext()
    {
        const string source = "svm";
        await using var manager = CreateManager();
        var contract = await manager.OpenAsync(
            CreateOpenRequest("snippet-svm-top-level", source),
            TestContext.Current.CancellationToken);
        var session = manager.GetRequired(contract.SessionId);
        await session.DidOpenAsync(
            new LspDidOpenTextDocumentParams(
                new LspTextDocumentItem("file:///Program.cs", "csharp", 1, source)),
            TestContext.Current.CancellationToken);

        var completions = await session.GetCompletionsAsync(
            new LspCompletionParams(
                new LspTextDocumentIdentifier("file:///Program.cs"),
                new LspPosition(0, 3),
                new LspCompletionContext(1, null)),
            TestContext.Current.CancellationToken);

        Assert.Empty(completions.Items);
    }

    [Fact]
    public async Task SvmIsOfferedFromItsSvPrefixInsideAType()
    {
        const string source = "class Program {\n\tsv\n}";
        await using var manager = CreateManager();
        var contract = await manager.OpenAsync(
            CreateOpenRequest("snippet-svm-prefix", source),
            TestContext.Current.CancellationToken);
        var session = manager.GetRequired(contract.SessionId);
        await session.DidOpenAsync(
            new LspDidOpenTextDocumentParams(
                new LspTextDocumentItem("file:///Program.cs", "csharp", 1, source)),
            TestContext.Current.CancellationToken);

        var completions = await session.GetCompletionsAsync(
            new LspCompletionParams(
                new LspTextDocumentIdentifier("file:///Program.cs"),
                new LspPosition(1, 3),
                new LspCompletionContext(1, null)),
            TestContext.Current.CancellationToken);

        var svm = Assert.Single(completions.Items, static item => item.Label == "svm");
        Assert.False(completions.IsIncomplete);
        Assert.Equal("svm", svm.Label);
        Assert.Equal("svm", svm.FilterText);
        Assert.Equal(2, svm.InsertTextFormat);
        Assert.Contains("static void Main", svm.TextEdit?.NewText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CSharpMemberCompletionRetriggersUntilSemanticSnippetPrefixesAreKnown()
    {
        const string source = "class Program {\n    \n}";
        await using var manager = CreateManager();
        var contract = await manager.OpenAsync(
            CreateOpenRequest("completion-dynamic-snippet-prefix", source),
            TestContext.Current.CancellationToken);
        var session = manager.GetRequired(contract.SessionId);
        await session.DidOpenAsync(
            new LspDidOpenTextDocumentParams(
                new LspTextDocumentItem("file:///Program.cs", "csharp", 1, source)),
            TestContext.Current.CancellationToken);

        var completions = await session.GetCompletionsAsync(
            new LspCompletionParams(
                new LspTextDocumentIdentifier("file:///Program.cs"),
                new LspPosition(1, 4),
                new LspCompletionContext(1, null)),
            TestContext.Current.CancellationToken);

        Assert.True(completions.IsIncomplete);
    }

    [Fact]
    public async Task AutoImportCompletionIsCompleteBeforeFirstCommit()
    {
        const string source = "class Program {\n    void Test() {\n        Ta\n    }\n}";
        await using var manager = CreateManager();
        var contract = await manager.OpenAsync(
            CreateOpenRequest("completion-auto-import", source),
            TestContext.Current.CancellationToken);
        var session = manager.GetRequired(contract.SessionId);
        await session.DidOpenAsync(
            new LspDidOpenTextDocumentParams(
                new LspTextDocumentItem("file:///Program.cs", "csharp", 1, source)),
            TestContext.Current.CancellationToken);

        var completions = await session.GetCompletionsAsync(
            new LspCompletionParams(
                new LspTextDocumentIdentifier("file:///Program.cs"),
                new LspPosition(2, 10),
                new LspCompletionContext(1, null)),
            TestContext.Current.CancellationToken);

        var task = Assert.Single(completions.Items, item => item.Label == "Task");
        var genericTask = Assert.Single(completions.Items, item => item.Label == "Task<>");
        foreach (var item in new[] { task, genericTask })
        {
            Assert.NotNull(item.TextEdit);
            Assert.Contains(
                item.AdditionalTextEdits ?? [],
                edit => edit.NewText.Contains("using System.Threading.Tasks;", StringComparison.Ordinal));
        }

        var updated = ApplyCompletionEdits(source, task);
        Assert.StartsWith("using System.Threading.Tasks;", updated, StringComparison.Ordinal);
        Assert.Contains("        Task\n", updated, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("void", "Task")]
    [InlineData("int", "Task<int>")]
    public async Task AwaitPostfixMakesContainingMethodAsyncAndTaskReturning(
        string originalReturnType,
        string expectedReturnType)
    {
        var source = $$"""
            class Program
            {
                {{originalReturnType}} Run()
                {
                    var task = System.Threading.Tasks.Task.CompletedTask;
                    task.await
                    {{(originalReturnType == "int" ? "return 42;" : string.Empty)}}
                }
            }
            """;
        await using var manager = CreateManager();
        var contract = await manager.OpenAsync(
            CreateOpenRequest($"postfix-await-{originalReturnType}", source),
            TestContext.Current.CancellationToken);
        var session = manager.GetRequired(contract.SessionId);
        await session.DidOpenAsync(
            new LspDidOpenTextDocumentParams(
                new LspTextDocumentItem("file:///Program.cs", "csharp", 1, source)),
            TestContext.Current.CancellationToken);

        var completions = await session.GetCompletionsAsync(
            new LspCompletionParams(
                new LspTextDocumentIdentifier("file:///Program.cs"),
                PositionAfter(source, "task.await"),
                new LspCompletionContext(1, null)),
            TestContext.Current.CancellationToken);
        var item = completions.Items.SingleOrDefault(static item => item.Label == "await");
        Assert.True(
            item is not null,
            $"Available completions: {string.Join(", ", completions.Items.Select(static candidate => candidate.Label))}");
        var resolved = await session.ResolveCompletionAsync(item, TestContext.Current.CancellationToken);
        var updated = ApplyCompletionEdits(source, item);
        var expectedEnd = PositionAfter(source, "task.await");
        var expectedStart = expectedEnd with
        {
            Character = expectedEnd.Character - "task.await".Length
        };

        Assert.Equal(14, item.Kind);
        Assert.Null(item.InsertTextFormat);
        Assert.Equal(new LspRange(expectedStart, expectedEnd), item.TextEdit?.Range);
        Assert.Equal("await task", item.TextEdit?.NewText);
        Assert.DoesNotContain(
            item.AdditionalTextEdits ?? [],
            static edit => edit.NewText.Contains("await", StringComparison.Ordinal));
        Assert.Equal(item.TextEdit, resolved.TextEdit);
        Assert.Equal(item.AdditionalTextEdits, resolved.AdditionalTextEdits);
        Assert.StartsWith("using System.Threading.Tasks;", updated, StringComparison.Ordinal);
        Assert.Contains($"async {expectedReturnType} Run()", updated, StringComparison.Ordinal);
        Assert.Contains("await task", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("task.await", updated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForeachPostfixExpandsEnumerableExpressionAsSnippet()
    {
        const string source = """
            class Program
            {
                void Run()
                {
                    int[] arr = [];
                    arr.foreach
                }
            }
            """;
        await using var manager = CreateManager();
        var contract = await manager.OpenAsync(
            CreateOpenRequest("postfix-foreach", source),
            TestContext.Current.CancellationToken);
        var session = manager.GetRequired(contract.SessionId);
        await session.DidOpenAsync(
            new LspDidOpenTextDocumentParams(
                new LspTextDocumentItem("file:///Program.cs", "csharp", 1, source)),
            TestContext.Current.CancellationToken);

        var completions = await session.GetCompletionsAsync(
            new LspCompletionParams(
                new LspTextDocumentIdentifier("file:///Program.cs"),
                PositionAfter(source, "arr.foreach"),
                new LspCompletionContext(1, null)),
            TestContext.Current.CancellationToken);
        var item = completions.Items.SingleOrDefault(static item => item.Label == "foreach");
        Assert.True(
            item is not null,
            $"Available completions: {string.Join(", ", completions.Items.Select(static candidate => candidate.Label))}");
        var resolved = await session.ResolveCompletionAsync(item, TestContext.Current.CancellationToken);
        var updated = ApplyCompletionEdits(source, item);

        Assert.Equal(15, item.Kind);
        Assert.Equal(item.TextEdit, resolved.TextEdit);
        Assert.Equal(item.AdditionalTextEdits, resolved.AdditionalTextEdits);
        Assert.Contains("foreach (var ${1:item} in arr)", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("${2:", updated, StringComparison.Ordinal);
        Assert.Contains("${0}", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("arr.foreach", updated, StringComparison.Ordinal);
        Assert.Equal(2, resolved.InsertTextFormat);
    }

    [Theory]
    [InlineData("arr. foreach")]
    [InlineData("arr.\n        foreach")]
    public async Task ForeachPostfixKeepsItsExpressionOutsideSnippetTabStops(string invocation)
    {
        var source = $$"""
            class Program
            {
                void Run()
                {
                    int[] arr = [];
                    {{invocation}}
                }
            }
            """;
        await using var manager = CreateManager();
        var contract = await manager.OpenAsync(
            CreateOpenRequest($"postfix-foreach-trivia-{invocation.Length}", source),
            TestContext.Current.CancellationToken);
        var session = manager.GetRequired(contract.SessionId);
        await session.DidOpenAsync(
            new LspDidOpenTextDocumentParams(
                new LspTextDocumentItem("file:///Program.cs", "csharp", 1, source)),
            TestContext.Current.CancellationToken);

        var completions = await session.GetCompletionsAsync(
            new LspCompletionParams(
                new LspTextDocumentIdentifier("file:///Program.cs"),
                PositionAfter(source, invocation),
                new LspCompletionContext(1, null)),
            TestContext.Current.CancellationToken);
        var item = Assert.Single(completions.Items, static candidate => candidate.Label == "foreach");
        var updated = ApplyCompletionEdits(source, item);

        Assert.Contains("foreach (var ${1:item} in arr)", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("${2:", updated, StringComparison.Ordinal);
        Assert.Contains("${0}", updated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForeachPostfixSelectsTheGeneratedOuterLoopWhenItsExpressionContainsAnotherLoop()
    {
        const string invocation = """
            ((System.Func<int[]>)(() =>
            {
                foreach (var inner in new[] { 1 })
                {
                }
                return [1];
            }))().
            foreach
            """;
        var source = $$"""
            class Program
            {
                void Run()
                {
                    {{invocation}}
                }
            }
            """;
        await using var manager = CreateManager();
        var contract = await manager.OpenAsync(
            CreateOpenRequest("postfix-foreach-nested-loop", source),
            TestContext.Current.CancellationToken);
        var session = manager.GetRequired(contract.SessionId);
        await session.DidOpenAsync(
            new LspDidOpenTextDocumentParams(
                new LspTextDocumentItem("file:///Program.cs", "csharp", 1, source)),
            TestContext.Current.CancellationToken);

        var completions = await session.GetCompletionsAsync(
            new LspCompletionParams(
                new LspTextDocumentIdentifier("file:///Program.cs"),
                PositionAfter(source, invocation),
                new LspCompletionContext(1, null)),
            TestContext.Current.CancellationToken);
        var item = Assert.Single(completions.Items, static candidate => candidate.Label == "foreach");
        var updated = ApplyCompletionEdits(source, item);

        Assert.Contains("${1:item}", updated, StringComparison.Ordinal);
        Assert.Contains("foreach (var inner", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("${1:inner}", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("${2:", updated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoPostfixKeepsItsConditionOutsideSnippetTabStops()
    {
        const string source = """
            class Program
            {
                void Run()
                {
                    bool flag = true;
                    flag.do
                }
            }
            """;
        await using var manager = CreateManager();
        var contract = await manager.OpenAsync(
            CreateOpenRequest("postfix-do", source),
            TestContext.Current.CancellationToken);
        var session = manager.GetRequired(contract.SessionId);
        await session.DidOpenAsync(
            new LspDidOpenTextDocumentParams(
                new LspTextDocumentItem("file:///Program.cs", "csharp", 1, source)),
            TestContext.Current.CancellationToken);

        var completions = await session.GetCompletionsAsync(
            new LspCompletionParams(
                new LspTextDocumentIdentifier("file:///Program.cs"),
                PositionAfter(source, "flag.do"),
                new LspCompletionContext(1, null)),
            TestContext.Current.CancellationToken);
        var item = Assert.Single(completions.Items, static candidate => candidate.Label == "do");
        var updated = ApplyCompletionEdits(source, item);

        Assert.Contains("while (flag);", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("${1:flag}", updated, StringComparison.Ordinal);
        Assert.Contains("${0}", updated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DotInEarlierCommentDoesNotTurnDoIntoAPostfixSnippet()
    {
        const string source = """
            class Program
            {
                void Run()
                {
                    // .
                    do
                }
            }
            """;
        await using var manager = CreateManager();
        var contract = await manager.OpenAsync(
            CreateOpenRequest("snippet-do-after-dot-comment", source),
            TestContext.Current.CancellationToken);
        var session = manager.GetRequired(contract.SessionId);
        await session.DidOpenAsync(
            new LspDidOpenTextDocumentParams(
                new LspTextDocumentItem("file:///Program.cs", "csharp", 1, source)),
            TestContext.Current.CancellationToken);

        var completions = await session.GetCompletionsAsync(
            new LspCompletionParams(
                new LspTextDocumentIdentifier("file:///Program.cs"),
                PositionAfter(source, "\n        do"),
                new LspCompletionContext(1, null)),
            TestContext.Current.CancellationToken);
        var item = Assert.Single(
            completions.Items,
            static candidate => candidate.Label == "do" && candidate.Kind == 15);

        Assert.Contains("while (${1:true});", item.TextEdit?.NewText, StringComparison.Ordinal);
        Assert.Contains("${0}", item.TextEdit?.NewText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletionFiltersTypedTextBeforeApplyingItemLimit()
    {
        const string source = "using System;\n\nConsole.WriteLine(\"Hello\");\nwhi";
        await using var manager = CreateManager(LspLimits.Default with { MaxCompletionItems = 1 });
        var contract = await manager.OpenAsync(
            CreateOpenRequest("filtered-completion", source),
            TestContext.Current.CancellationToken);
        var session = manager.GetRequired(contract.SessionId);
        await session.DidOpenAsync(
            new LspDidOpenTextDocumentParams(
                new LspTextDocumentItem("file:///Program.cs", "csharp", 1, source)),
            TestContext.Current.CancellationToken);

        var completions = await session.GetCompletionsAsync(
            new LspCompletionParams(
                new LspTextDocumentIdentifier("file:///Program.cs"),
                new LspPosition(3, 3),
                new LspCompletionContext(1, null)),
            TestContext.Current.CancellationToken);

        Assert.Equal("while", Assert.Single(completions.Items).Label);
        Assert.True(completions.IsIncomplete);
    }

    [Fact]
    public async Task SemanticTokensSymbolsAndCodeActionsAreBoundedAndStructured()
    {
        const string source = "using System.Text;\nusing System;\nclass Demo{private int _value; public int Add(int left,int right)=>_value+left+right;}";
        await using var manager = CreateManager();
        var contract = await manager.OpenAsync(CreateOpenRequest("structure", source), TestContext.Current.CancellationToken);
        var session = manager.GetRequired(contract.SessionId);
        await session.DidOpenAsync(
            new LspDidOpenTextDocumentParams(new LspTextDocumentItem("file:///Program.cs", "csharp", 1, source)),
            TestContext.Current.CancellationToken);

        var tokens = await session.GetSemanticTokensAsync(
            new LspSemanticTokensParams(new LspTextDocumentIdentifier("file:///Program.cs")),
            TestContext.Current.CancellationToken);
        var symbols = await session.GetDocumentSymbolsAsync(
            new LspDocumentSymbolParams(new LspTextDocumentIdentifier("file:///Program.cs")),
            TestContext.Current.CancellationToken);
        var actions = await session.GetCodeActionsAsync(
            new LspCodeActionParams(
                new LspTextDocumentIdentifier("file:///Program.cs"),
                new LspRange(new LspPosition(0, 0), new LspPosition(2, 65)),
                new LspCodeActionContext([], null)),
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(tokens.Data);
        Assert.Equal(0, tokens.Data.Count % 5);
        var tokenTypes = tokens.Data.Where(static (_, index) => index % 5 == 3).ToArray();
        Assert.Contains(2, tokenTypes);
        Assert.Contains(7, tokenTypes);
        Assert.Contains(13, tokenTypes);
        Assert.Contains(23, tokenTypes);
        var demo = Assert.Single(symbols, static symbol => symbol.Name == "Demo");
        Assert.Contains(demo.Children, static symbol => symbol.Name == "Add");
        Assert.Contains(actions, static action => action.Kind == "source.organizeImports");
        Assert.Contains(actions, static action => action.Kind == "source.formatDocument");
    }

    [Fact]
    public async Task SemanticTokensDistinguishStringEscapesFromStringContent()
    {
        const string source = "class Demo { string Regular = \"line\\n\"; string Verbatim = @\"line\\n\"; }";
        await using var manager = CreateManager();
        var contract = await manager.OpenAsync(
            CreateOpenRequest("string-escapes", source),
            TestContext.Current.CancellationToken);
        var session = manager.GetRequired(contract.SessionId);
        await session.DidOpenAsync(
            new LspDidOpenTextDocumentParams(
                new LspTextDocumentItem("file:///Program.cs", "csharp", 1, source)),
            TestContext.Current.CancellationToken);

        var tokens = await session.GetSemanticTokensAsync(
            new LspSemanticTokensParams(new LspTextDocumentIdentifier("file:///Program.cs")),
            TestContext.Current.CancellationToken);
        var decoded = DecodeSingleLineTokens(source, tokens.Data);

        Assert.Single(decoded, static token => token.Text == "\\n" && token.Type == 25);
        Assert.Contains(decoded, static token => token.Text.Contains("line", StringComparison.Ordinal) && token.Type == 18);
    }

    [Fact]
    public async Task FeatureRequestsHonorCancellation()
    {
        await using var manager = CreateManager();
        var contract = await manager.OpenAsync(CreateOpenRequest("cancel", "System.Console."), TestContext.Current.CancellationToken);
        var session = manager.GetRequired(contract.SessionId);
        await session.DidOpenAsync(
            new LspDidOpenTextDocumentParams(new LspTextDocumentItem("file:///Program.cs", "csharp", 1, "System.Console.")),
            TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.GetCompletionsAsync(
            new LspCompletionParams(
                new LspTextDocumentIdentifier("file:///Program.cs"),
                new LspPosition(0, 15),
                null),
            cancellation.Token));
    }

    [Fact]
    public async Task PrematureWebSocketCloseEndsConnectionWithoutFailure()
    {
        await using var manager = CreateManager();
        var contract = await manager.OpenAsync(
            CreateOpenRequest("premature-websocket-close", "int value = 1;"),
            TestContext.Current.CancellationToken);
        var socket = new PrematureCloseWebSocket();
        await using var connection = new LspJsonRpcWebSocketConnection(
            socket,
            manager.GetRequired(contract.SessionId),
            LspLimits.Default,
            CancellationToken.None);

        await connection.RunAsync();

        Assert.Equal(WebSocketState.Closed, socket.State);
        using var replacementLease = manager.GetRequired(contract.SessionId).AttachConnection();
    }

    [Fact]
    public void CancellationCleanupToleratesAlreadyDisposedSource()
    {
        var cancellation = new CancellationTokenSource();
        cancellation.Dispose();

        LspJsonRpcWebSocketConnection.CancelIgnoringDisposal(cancellation);
    }

    private static RoslynLanguageSessionManager CreateManager(LspLimits? lspLimits = null) =>
        new(
            new ReferenceSetProvider(
                [new ReferenceSetDefinition("net10-ref", CSharpBuildServiceTests.GetNet10ReferencePathForHost(), "net10.0", "10.0.9")]),
            new RoslynWorkerIdentity("development", "roslyn-stable", "5.6.0", null, "development-worker-image"),
            CompilationLimits.Default,
            lspLimits ?? LspLimits.Default);

    private static string ApplyCompletionEdits(string source, LspCompletionItem completion)
    {
        var text = SourceText.From(source);
        var edits = new[] { completion.TextEdit! }.Concat(completion.AdditionalTextEdits ?? []);
        var changes = edits.Select(edit => new TextChange(
            TextSpan.FromBounds(Position(text, edit.Range.Start), Position(text, edit.Range.End)),
            edit.NewText));
        return text.WithChanges(changes).ToString();
    }

    private static int Position(SourceText text, LspPosition position) =>
        text.Lines[position.Line].Start + position.Character;

    private static LspPosition PositionAfter(string source, string marker)
    {
        var offset = source.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var text = SourceText.From(source);
        var line = text.Lines.GetLineFromPosition(offset);
        return new LspPosition(line.LineNumber, offset - line.Start);
    }

    private static List<(string Text, int Type)> DecodeSingleLineTokens(
        string source,
        IReadOnlyList<int> data)
    {
        var decoded = new List<(string Text, int Type)>();
        var line = 0;
        var character = 0;
        for (var index = 0; index < data.Count; index += 5)
        {
            line += data[index];
            character = data[index] == 0 ? character + data[index + 1] : data[index + 1];
            Assert.Equal(0, line);
            decoded.Add((source.Substring(character, data[index + 2]), data[index + 3]));
        }
        return decoded;
    }

    private sealed class PrematureCloseWebSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override void Dispose() => _state = WebSocketState.Closed;

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken) =>
            Task.FromException<WebSocketReceiveResult>(
                new WebSocketException(WebSocketError.ConnectionClosedPrematurely));

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    internal static OpenLanguageSessionRequest CreateOpenRequest(
        string requestId,
        string source,
        long revision = 1,
        long selectionRevision = 1,
        BuildOutputKind outputKind = BuildOutputKind.Console)
    {
        var options = new BuildOptions(
            BuildConfiguration.Debug,
            Optimize: false,
            outputKind,
            AllowUnsafe: false,
            EmitPortablePdb: true,
            NullableContextMode.Enable,
            LanguageVersion: "14.0");
        return new OpenLanguageSessionRequest(
            requestId,
            $"pipeline-{requestId}",
            "csharp",
            "roslyn-stable",
            "net10-ref",
            new WorkspaceSnapshot(
                ContractSchemaVersions.WorkspaceSnapshot,
                revision,
                selectionRevision,
                "csharp",
                [new WorkspaceFile("Program.cs", 1, source)],
                "Program.cs",
                ["Program.cs"],
                "net10-ref",
                options));
    }

    internal static OpenLanguageSessionRequest CreateVisualBasicOpenRequest(
        string requestId,
        string source,
        long revision = 1,
        long selectionRevision = 1,
        BuildOutputKind outputKind = BuildOutputKind.Console)
    {
        var options = new BuildOptions(
            BuildConfiguration.Debug,
            Optimize: false,
            outputKind,
            AllowUnsafe: false,
            EmitPortablePdb: true,
            NullableContextMode.Disable,
            LanguageVersion: "latest");
        return new OpenLanguageSessionRequest(
            requestId,
            $"pipeline-{requestId}",
            "visual-basic",
            "roslyn-stable",
            "net10-ref",
            new WorkspaceSnapshot(
                ContractSchemaVersions.WorkspaceSnapshot,
                revision,
                selectionRevision,
                "visual-basic",
                [new WorkspaceFile("Program.vb", 1, source)],
                "Program.vb",
                ["Program.vb"],
                "net10-ref",
                options));
    }
}
