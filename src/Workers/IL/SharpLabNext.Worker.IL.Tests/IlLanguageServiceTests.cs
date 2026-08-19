using System.Collections.Immutable;
using EleCho.ILSense;
using EleCho.ILSense.Contracts;

namespace SharpLabNext.Worker.IL.Tests;

public sealed class IlLanguageServiceTests : IClassFixture<IlLanguageServiceFixture>
{
    private const string DocumentUri = "sharplabnext:///Program.il";
    private readonly IlLanguageServiceFixture _fixture;
    private readonly IlLanguageService _service = new();

    public IlLanguageServiceTests(IlLanguageServiceFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task BuiltinCompletionKeepsOpcodesPlainAndDirectiveSnippetsStructured()
    {
        var engine = await CreateEngineAsync();

        var directive = await CompleteAsync(engine, ".cla|");
        var classItem = Assert.Single(directive.Result.Items, static item => item.Label == ".class");
        Assert.Equal(14, classItem.Kind);
        Assert.Equal(".cla", TextAt(directive.Source, classItem.TextEdit.Range));
        Assert.StartsWith(".class", classItem.TextEdit.NewText, StringComparison.Ordinal);
        Assert.Equal(7, classItem.Data.DocumentVersion);
        Assert.Equal(41, classItem.Data.WorkspaceRevision);

        var methodDirective = await CompleteAsync(engine, """
            .class public C
            {
              .met|
            }
            """);
        var methodItem = Assert.Single(methodDirective.Result.Items, static item => item.Label == ".method");
        Assert.Equal(2, methodItem.InsertTextFormat);
        Assert.Equal(
            ".method public static void ${1:Method}() cil managed\n{\n\t$0\n}",
            methodItem.TextEdit.NewText);

        var opcode = await CompleteAsync(engine, MethodBody("cal|"));
        var call = Assert.Single(opcode.Result.Items, static item => item.Label == "call");
        Assert.Equal(14, call.Kind);
        Assert.Equal("cal", TextAt(opcode.Source, call.TextEdit.Range));
        Assert.Equal("call", call.InsertText);
        Assert.Equal(1, call.InsertTextFormat);
        Assert.Equal("call", call.TextEdit.NewText);
        Assert.Equal("call", call.Data.OpcodeFamily);

        var stringLoad = await CompleteAsync(engine, MethodBody("ldst|"));
        var ldstr = Assert.Single(stringLoad.Result.Items, static item => item.Label == "ldstr");
        Assert.Equal(1, ldstr.InsertTextFormat);
        Assert.Equal("ldstr", ldstr.TextEdit.NewText);

        Assert.Contains(".", IlLanguageService.CompletionTriggerCharacters);
        Assert.DoesNotContain(" ", IlLanguageService.CompletionTriggerCharacters);
        Assert.Contains("(", IlLanguageService.SignatureHelpTriggerCharacters);
    }

    [Fact]
    public async Task UpdatedCompletionSupportsNestedTypesGenericParametersAndAssemblyHashAlgorithm()
    {
        var engine = await CreateEngineAsync();

        var nestedType = await CompleteAsync(engine, """
            .class public Outer
            {
              .cl|
            }
            """);
        var nestedClass = Assert.Single(nestedType.Result.Items, static item => item.Label == ".class");
        Assert.Equal(2, nestedClass.InsertTextFormat);
        Assert.Equal(
            ".class nested public ${1:Name}\n{\n\t$0\n}",
            nestedClass.TextEdit.NewText);

        var typeParameter = await CompleteAsync(engine, """
            .class public Container`1<T>
            {
              .method public static void M<M>() cil managed
              {
                box !|
                pop
                ret
              }
            }
            """);
        Assert.Contains(typeParameter.Result.Items, static item =>
            item.Kind == 25 && item.Label == "T" && item.TextEdit.NewText == "T");
        Assert.DoesNotContain(typeParameter.Result.Items, static item => item.Label == "M");

        var methodParameter = await CompleteAsync(engine, """
            .class public Container`1<T>
            {
              .method public static void M<M>() cil managed
              {
                box !!|
                pop
                ret
              }
            }
            """);
        Assert.Contains(methodParameter.Result.Items, static item =>
            item.Kind == 25 && item.Label == "M" && item.TextEdit.NewText == "M");
        Assert.DoesNotContain(methodParameter.Result.Items, static item => item.Label == "T");

        var assemblyHash = await CompleteAsync(engine, """
            .assembly Example
            {
              .hash alg|
            }
            """);
        var algorithm = Assert.Single(assemblyHash.Result.Items, static item => item.Label == "algorithm");
        Assert.Equal(14, algorithm.Kind);
        Assert.Equal("algorithm", algorithm.TextEdit.NewText);
    }

    [Fact]
    public async Task EmptyMethodBodyOffersInstructionAndDirectiveCompletions()
    {
        var engine = await CreateEngineAsync();

        var completion = await CompleteAsync(engine, MethodBody("|"));

        Assert.Contains(completion.Result.Items, static item => item.Label == "nop");
        Assert.Contains(completion.Result.Items, static item => item.Label == ".maxstack");
        Assert.All(
            completion.Result.Items,
            static item => Assert.NotEqual("No suggestions.", item.Label));
    }

    [Fact]
    public async Task BranchArgumentAndLocalCompletionStayInTheCurrentMethod()
    {
        var engine = await CreateEngineAsync();
        var branch = await CompleteAsync(engine, """
            .method public static void Other() cil managed
            {
            foreign:
              ret
            }
            .method public instance void M(int32 value) cil managed
            {
              .locals init ([2] int32 localValue)
            target:
              br.s ta|
            another:
              ret
            }
            """);
        var target = Assert.Single(branch.Result.Items, static item => item.Kind == 18 && item.Label == "target");
        Assert.Equal("ta", TextAt(branch.Source, target.TextEdit.Range));
        Assert.Equal("target", target.TextEdit.NewText);
        Assert.DoesNotContain(branch.Result.Items, static item => item.Label == "foreign");

        var argumentName = await CompleteAsync(engine, """
            .method public instance void M(int32 value) cil managed
            {
              ldarg val|
              ret
            }
            """);
        var value = Assert.Single(argumentName.Result.Items, static item => item.Kind == 6 && item.Label == "value");
        Assert.Equal("val", TextAt(argumentName.Source, value.TextEdit.Range));

        var arguments = await CompleteAsync(engine, """
            .method public instance void M(int32 value) cil managed
            {
              ldarg |
              ret
            }
            """);
        Assert.Contains(arguments.Result.Items, static item => item.Kind == 6 && item.Label == "this");
        Assert.Contains(arguments.Result.Items, static item => item.Kind == 6 && item.Label == "value");
        Assert.Contains(arguments.Result.Items, static item => item.Kind == 6 && item.Label == "1");

        var locals = await CompleteAsync(engine, """
            .method public static void M() cil managed
            {
              .locals init ([2] int32 localValue)
              ldloc |
              ret
            }
            """);
        Assert.Contains(locals.Result.Items, static item => item.Kind == 6 && item.Label == "localValue");
        Assert.Contains(locals.Result.Items, static item => item.Kind == 6 && item.Label == "2");
    }

    [Fact]
    public async Task WorkspaceMembersPreserveCallShapeFieldsNestedTypesAndSameLabelCandidates()
    {
        var engine = await CreateEngineAsync();

        var constructor = await CompleteAsync(engine, MemberWorkspace("newobj instance void C::.c|"));
        Assert.Contains(constructor.Result.Items, static item =>
            item.Kind == 4 && item.Label.Contains(".ctor(int32)", StringComparison.Ordinal));

        var staticMethod = await CompleteAsync(engine, MemberWorkspace("call void C::Sta|"));
        Assert.Contains(staticMethod.Result.Items, static item =>
            item.Kind == 2 && item.Label == "Static(string)" && item.TextEdit.NewText == "void C::Static(string)");

        var instanceMethod = await CompleteAsync(engine, MemberWorkspace("callvirt instance void C::Ins|"));
        Assert.Contains(instanceMethod.Result.Items, static item =>
            item.Kind == 2 && item.Label == "Instance(int32)" &&
            item.TextEdit.NewText == "instance void C::Instance(int32)");

        var instanceField = await CompleteAsync(engine, MemberWorkspace("ldfld int32 C::Val|"));
        Assert.Contains(instanceField.Result.Items, static item =>
            item.Kind == 5 && item.Label == "Value" && item.TextEdit.NewText == "int32 C::Value");

        var staticField = await CompleteAsync(engine, MemberWorkspace("ldsfld int32 C::Sha|"));
        Assert.Contains(staticField.Result.Items, static item =>
            item.Kind == 5 && item.Label == "Shared" && item.TextEdit.NewText == "int32 C::Shared");

        var nested = await CompleteAsync(engine, MemberWorkspace("C::In|"));
        Assert.Contains(nested.Result.Items, static item => item.Kind == 7 && item.Label == "Inner");

        var sameLabel = await CompleteAsync(engine, MemberWorkspace("call C::Conv|"));
        var overloads = sameLabel.Result.Items
            .Where(static item => item.Kind == 2 && item.Label == "Convert(int32)")
            .ToArray();
        Assert.Equal(2, overloads.Length);
        Assert.Equal(2, overloads.Select(static item => item.Data.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(overloads, static item => item.TextEdit.NewText == "void C::Convert(int32)");
        Assert.Contains(overloads, static item => item.TextEdit.NewText == "int32 C::Convert(int32)");
    }

    [Fact]
    public async Task ReferenceMetadataCompletesAssembliesGenericTypesMethodsFieldsAndOverloads()
    {
        var engine = await CreateEngineAsync();

        var assembly = await CompleteAsync(engine, MethodBody("call void [System.Con|"));
        var systemConsole = Assert.Single(assembly.Result.Items, static item => item.Label == "System.Console");
        Assert.Equal("ImportedAssembly", systemConsole.Data.Origin);
        Assert.Equal("System.Con", TextAt(assembly.Source, systemConsole.TextEdit.Range));
        Assert.Equal("System.Console]", systemConsole.TextEdit.NewText);

        var type = await CompleteAsync(engine, MethodBody("box [System.Console]System.Con|"));
        var consoleType = Assert.Single(type.Result.Items, static item =>
            item.Kind == 7 && item.Label == "System.Console");
        Assert.Equal("ImportedAssembly", consoleType.Data.Origin);
        Assert.Equal("System.Console", consoleType.Data.Properties["metadataName"]);

        var generic = await CompleteAsync(
            engine,
            MethodBody("box [System.Collections]System.Collections.Generic.Lis|"));
        Assert.Contains(generic.Result.Items, static item =>
            item.Kind == 7 &&
            item.Data.Properties.TryGetValue("metadataName", out var metadataName) &&
            metadataName == "System.Collections.Generic.List`1");

        var methods = await CompleteAsync(
            engine,
            MethodBody("call void [System.Console]System.Console::WriteL|"));
        var writeLineOverloads = methods.Result.Items
            .Where(static item => item.Kind == 2 && item.Label.StartsWith("WriteLine(", StringComparison.Ordinal))
            .ToArray();
        Assert.True(writeLineOverloads.Length > 3);
        Assert.Equal(
            writeLineOverloads.Length,
            writeLineOverloads.Select(static item => item.Data.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(writeLineOverloads, static item =>
        {
            Assert.Equal("ImportedAssembly", item.Data.Origin);
            Assert.Equal("System.Console", item.Data.Properties["assembly"]);
            Assert.StartsWith("void [System.Console]System.Console::WriteLine(", item.TextEdit.NewText, StringComparison.Ordinal);
        });

        var field = await CompleteAsync(
            engine,
            MethodBody("ldsfld int32 [SharpLab.Runtime]SharpLab.Runtime.Internal.Flow::Unk|"));
        Assert.Contains(field.Result.Items, static item =>
            item.Kind == 5 && item.Label == "UnknownLineNumber" && item.Data.Origin == "ImportedAssembly");
    }

    [Fact]
    public async Task ReferenceMethodParameterCompletionOnlyOffersTypesAndPrimitives()
    {
        var engine = await CreateEngineAsync();
        var completion = await CompleteAsync(
            engine,
            MethodBody("call void [System.Console]System.Console::WriteLine(|)"));

        Assert.Contains(completion.Result.Items, static item => item.Kind == 14 && item.Label == "string");
        Assert.All(completion.Result.Items, static item =>
            Assert.True(item.Kind is 7 or 8 or 14, $"Unexpected completion kind {item.Kind}: {item.Label}"));
        Assert.DoesNotContain(completion.Result.Items, static item => item.Kind is 2 or 5);

        var secondParameter = await CompleteAsync(
            engine,
            MethodBody("call void [System.Console]System.Console::WriteLine(int32, |)"));
        Assert.Contains(secondParameter.Result.Items, static item => item.Kind == 14 && item.Label == "string");
        Assert.All(secondParameter.Result.Items, static item =>
            Assert.True(item.Kind is 7 or 8 or 14, $"Unexpected completion kind {item.Kind}: {item.Label}"));
        Assert.DoesNotContain(secondParameter.Result.Items, static item => item.Kind is 2 or 5);

        var assembly = await CompleteAsync(
            engine,
            MethodBody("call void [System.Console]System.Console::WriteLine([System.Con|)"));
        var systemConsole = Assert.Single(assembly.Result.Items, static item =>
            item.Kind == 9 && item.Label == "System.Console");
        Assert.Equal("ImportedAssembly", systemConsole.Data.Origin);
        Assert.Equal("System.Con", TextAt(assembly.Source, systemConsole.TextEdit.Range));
        Assert.Equal("System.Console]", systemConsole.TextEdit.NewText);
    }

    [Fact]
    public async Task ReferenceMemberCompletionRequiresADoubleColon()
    {
        var engine = await CreateEngineAsync();

        var incomplete = await CompleteAsync(
            engine,
            MethodBody("call void [System.Console]System.Console:|"));
        Assert.Empty(incomplete.Result.Items);

        var complete = await CompleteAsync(
            engine,
            MethodBody("call void [System.Console]System.Console::|"));
        Assert.Contains(complete.Result.Items, static item =>
            item.Kind == 2 && item.Label.StartsWith("WriteLine(", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompletionUsesDeclarationsFromEveryWorkspaceDocument()
    {
        var engine = await CreateEngineAsync();
        var completion = await CompleteAsync(
            engine,
            """
            .class public Program
            {
              .method public static void Main() cil managed
              {
                call void Helper::Pi|
                ret
              }
            }
            """,
            [
                new TestSource("Helper.il", """
                    .class public abstract sealed Helper
                    {
                      .method public static void Ping() cil managed { ret }
                    }
                    """)
            ]);

        var ping = Assert.Single(completion.Result.Items, static item => item.Kind == 2 && item.Label == "Ping()");
        Assert.Equal("void Helper::Pi", TextAt(completion.Source, ping.TextEdit.Range));
        Assert.Equal("void Helper::Ping()", ping.TextEdit.NewText);
        Assert.Equal("Workspace", ping.Data.Origin);
    }

    [Fact]
    public async Task CompletionRecoversAtAnIncompleteEndOfFile()
    {
        var engine = await CreateEngineAsync();
        var completion = await CompleteAsync(engine, """
            .class public C
            {
              .method public static void WriteLine(string value) cil managed { ret }
              .method public static void WriteLine(int32 value) cil managed { ret }
              .method public static void Use() cil managed
              {
                call void C::Wri|
            """);

        var recovered = completion.Result.Items
            .Where(static item => item.Kind == 2 && item.Label.StartsWith("WriteLine(", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, recovered.Length);
        Assert.All(recovered, item => Assert.Equal("void C::Wri", TextAt(completion.Source, item.TextEdit.Range)));
    }

    [Fact]
    public async Task DiagnosticsHoverSignatureTokensSymbolsAndFoldingMapToLspDtos()
    {
        var engine = await CreateEngineAsync();
        var marked = CreateMarkedWorkspace("""
            .class public C
            {
              .method public static void M(int32 value) cil managed
              {
              start:
                ldarg.0
                call void C::Target(|)
                invalid.opcode
                ret
              }
              .method public static void Target(int32 value) cil managed
              {
                ret
              }
            }
            """);
        var cancellationToken = TestContext.Current.CancellationToken;

        var diagnostics = await _service.GetDiagnosticsAsync(
            engine,
            marked.Snapshot,
            marked.Document,
            DocumentUri,
            selectionRevision: 43,
            cancellationToken);
        var invalidOpcode = Assert.Single(diagnostics.Diagnostics, static item => item.Code == "ILPAR202");
        Assert.Equal("Parse", invalidOpcode.Source);
        Assert.Equal(7, invalidOpcode.Data.DocumentVersion);
        Assert.Equal(41, invalidOpcode.Data.WorkspaceRevision);
        Assert.Equal(43, invalidOpcode.Data.SelectionRevision);
        Assert.Equal("Parse", invalidOpcode.Data.DiagnosticKind);

        var hoverPosition = PositionAt(marked.Source, marked.Source.IndexOf("ldarg.0", StringComparison.Ordinal) + 2);
        var hover = await _service.GetHoverAsync(
            engine,
            marked.Snapshot,
            marked.Document,
            hoverPosition,
            cancellationToken);
        Assert.NotNull(hover);
        Assert.Contains("ldarg.0", hover.Contents.Value, StringComparison.Ordinal);
        Assert.Equal("ldarg.0", TextAt(marked.Source, hover.Range));

        var signature = await _service.GetSignatureHelpAsync(
            engine,
            marked.Snapshot,
            marked.Document,
            marked.Position,
            cancellationToken);
        Assert.Contains(signature.Signatures, static item =>
            item.Label.Contains("C::Target(int32)", StringComparison.Ordinal));
        Assert.Equal(0, signature.ActiveParameter);

        var semanticTokens = await _service.GetSemanticTokensAsync(
            engine,
            marked.Snapshot,
            marked.Document,
            cancellationToken);
        Assert.NotEmpty(semanticTokens.Data);
        Assert.Equal(0, semanticTokens.Data.Count % 5);
        var tokenTypes = semanticTokens.Data.Where(static (_, index) => index % 5 == 3).ToArray();
        Assert.Contains(5, tokenTypes);
        Assert.Contains(10, tokenTypes);

        var symbols = await _service.GetDocumentSymbolsAsync(
            engine,
            marked.Snapshot,
            marked.Document,
            cancellationToken);
        var type = Assert.Single(symbols, static item => item.Kind == 5 && item.Name == "C");
        var method = Assert.Single(type.Children, static item => item.Kind == 6 && item.Name == "M");
        Assert.Contains(method.Children, static item => item.Kind == 13 && item.Name == "start");

        var folding = await _service.GetFoldingRangesAsync(
            engine,
            marked.Snapshot,
            marked.Document,
            cancellationToken);
        Assert.True(folding.Count >= 3);
        Assert.All(folding, static range => Assert.True(range.StartLine < range.EndLine));
    }

    [Fact]
    public async Task UpdatedSemanticTokensAndTooltipsMapToLspDtos()
    {
        var engine = await CreateEngineAsync();
        var generic = CreateMarkedWorkspace("""
            .class public Container`1<T>
            {
              .field public !|T Value
              .method public static !!M Echo<M>(!T value) cil managed
              {
                ldarg.0
                ret
              }
            }
            """);
        var cancellationToken = TestContext.Current.CancellationToken;

        var semanticTokens = await _service.GetSemanticTokensAsync(
            engine,
            generic.Snapshot,
            generic.Document,
            cancellationToken);
        var decodedTokens = DecodeSemanticTokens(generic.Source, semanticTokens);
        var genericParameters = decodedTokens.Where(static token => token.Type == 14).ToArray();
        Assert.NotEmpty(genericParameters);
        Assert.Equal("typeParameter", IlLanguageService.SemanticTokenTypes[14]);
        Assert.Contains(genericParameters, static token => token.Text == "T" && (token.Modifiers & 1) != 0);
        Assert.Contains(genericParameters, static token => token.Text == "M" && (token.Modifiers & 1) != 0);
        Assert.Contains(genericParameters, static token => token.Text == "T" && token.Modifiers == 0);
        Assert.All(genericParameters, static token => Assert.DoesNotContain('!', token.Text));

        var genericHover = await _service.GetHoverAsync(
            engine,
            generic.Snapshot,
            generic.Document,
            generic.Position,
            cancellationToken);
        Assert.NotNull(genericHover);
        Assert.Equal("markdown", genericHover.Contents.Kind);
        Assert.Equal("T", TextAt(generic.Source, genericHover.Range));
        Assert.Contains("Type generic parameter `T`", genericHover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("index `0`", genericHover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("Container`1", genericHover.Contents.Value, StringComparison.Ordinal);

        var attribute = CreateMarkedWorkspace("""
            .custom instance void MyAttribute::.ctor(string) = (
              01 00 03 71 7|7 71 00 00
            )
            """);
        var attributeHover = await _service.GetHoverAsync(
            engine,
            attribute.Snapshot,
            attribute.Document,
            attribute.Position,
            cancellationToken);
        Assert.NotNull(attributeHover);
        Assert.Equal("77", TextAt(attribute.Source, attributeHover.Range));
        Assert.Contains("Fixed arguments:", attributeHover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("string = \"qwq\"", attributeHover.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("Named arguments: none", attributeHover.Contents.Value, StringComparison.Ordinal);

        var assemblyHash = CreateMarkedWorkspace("""
            .assembly Example
            {
              .hash alg|orithm 0x00008004
            }
            """);
        var hashTokens = DecodeSemanticTokens(
            assemblyHash.Source,
            await _service.GetSemanticTokensAsync(
                engine,
                assemblyHash.Snapshot,
                assemblyHash.Document,
                cancellationToken));
        Assert.Contains(hashTokens, static token => token.Type == 5 && token.Text == "algorithm");
        Assert.Contains(hashTokens, static token => token.Type == 6 && token.Text == "0x00008004");
    }

    [Fact]
    public async Task NavigationWorkspaceSymbolsAndCodeActionsMapToVersionedLspDtos()
    {
        var engine = await CreateEngineAsync();
        var navigation = CreateMarkedWorkspace(
            """
            .class public C
            {
              .method public static void Target() cil managed { ret }
              .method public static void Use() cil managed
              {
                call void C::Tar|get()
                ret
              }
            }
            """,
            [new TestSource("Helper.il", ".class public Helper { }")]);
        var cancellationToken = TestContext.Current.CancellationToken;

        var definition = await _service.GetDefinitionAsync(
            engine,
            navigation.Snapshot,
            navigation.Document,
            navigation.Position,
            cancellationToken);
        Assert.NotNull(definition);
        Assert.Equal(DocumentUri, definition.Uri);
        Assert.Equal("Target", TextAt(navigation.Source, definition.Range));

        var metadata = CreateMarkedWorkspace(MethodBody(
            "call void [System.Console]System.Console::Write|Line(string)"));
        var metadataResolution = engine.ResolveSymbolAt(
            metadata.Snapshot,
            metadata.Document,
            new SourcePosition(metadata.Position.Line, metadata.Position.Character));
        Assert.NotNull(metadataResolution);
        Assert.NotNull(metadataResolution.MetadataTarget);
        Assert.Null(metadataResolution.Location);
        Assert.Null(await _service.GetDefinitionAsync(
            engine,
            metadata.Snapshot,
            metadata.Document,
            metadata.Position,
            cancellationToken));

        var workspaceSymbols = await _service.GetWorkspaceSymbolsAsync(
            engine,
            navigation.Snapshot,
            new IlLspWorkspaceSymbolParams("Helper"),
            maximumResults: 20,
            cancellationToken);
        var helper = Assert.Single(workspaceSymbols, static symbol => symbol.Name == "Helper");
        Assert.Equal(5, helper.Kind);
        Assert.Equal("sharplabnext:///Helper.il", helper.Location.Uri);
        Assert.Equal(41, helper.Data.WorkspaceRevision);
        Assert.NotEmpty(helper.Data.Id);

        var actionWorkspace = CreateMarkedWorkspace("""
            .class public C
            {
              .method public static void M() cil managed
              {
                ldc.i4.1
                pop
                br.s miss|ing
                ret
              }
            }
            """);
        var diagnostics = await _service.GetDiagnosticsAsync(
            engine,
            actionWorkspace.Snapshot,
            actionWorkspace.Document,
            DocumentUri,
            selectionRevision: 42,
            cancellationToken);
        var actions = await _service.GetCodeActionsAsync(
            engine,
            actionWorkspace.Snapshot,
            actionWorkspace.Document,
            new IlLspCodeActionParams(
                new IlLspTextDocumentIdentifier(DocumentUri),
                new IlLspRange(new IlLspPosition(0, 0), PositionAt(actionWorkspace.Source, actionWorkspace.Source.Length)),
                new IlLspCodeActionContext(diagnostics.Diagnostics, Only: null)),
            maximumResults: 20,
            cancellationToken);
        var quickFix = Assert.Single(actions, static action => action.Data.Diagnostic == "ILBIND201");
        Assert.Equal("quickfix", quickFix.Kind);
        Assert.Contains(quickFix.Diagnostics!, static diagnostic => diagnostic.Code == "ILBIND201");
        Assert.Equal(7, quickFix.Data.DocumentVersion);
        Assert.Equal(41, quickFix.Data.WorkspaceRevision);
        Assert.Contains("missing:", Assert.Single(quickFix.Edit.Changes[DocumentUri]).NewText, StringComparison.Ordinal);
        var rewrite = Assert.Single(actions, static action => action.Kind == "refactor.rewrite");
        Assert.Equal("br", Assert.Single(rewrite.Edit.Changes[DocumentUri]).NewText);

        await Assert.ThrowsAsync<IlLspInvalidParamsException>(() => _service.GetCodeActionsAsync(
            engine,
            actionWorkspace.Snapshot,
            actionWorkspace.Document,
            new IlLspCodeActionParams(
                new IlLspTextDocumentIdentifier(DocumentUri),
                new IlLspRange(new IlLspPosition(3, 1), new IlLspPosition(2, 1)),
                new IlLspCodeActionContext([], Only: null)),
            maximumResults: 20,
            cancellationToken));
        await Assert.ThrowsAsync<IlLspLimitExceededException>(() => _service.GetWorkspaceSymbolsAsync(
            engine,
            navigation.Snapshot,
            new IlLspWorkspaceSymbolParams(string.Empty),
            maximumResults: 0,
            cancellationToken));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.GetWorkspaceSymbolsAsync(
            engine,
            navigation.Snapshot,
            new IlLspWorkspaceSymbolParams(string.Empty),
            maximumResults: 20,
            cancellation.Token));
    }

    [Fact]
    public async Task CompletionHonorsTheConfiguredItemLimitAndCallerCancellation()
    {
        var limitedEngine = await CreateEngineAsync(maximumItems: 3);
        var limited = await CompleteAsync(limitedEngine, MethodBody("l|"));
        Assert.True(limited.Result.IsIncomplete);
        Assert.InRange(limited.Result.Items.Count, 1, 3);

        var engine = await CreateEngineAsync();
        var marked = CreateMarkedWorkspace(MethodBody("l|"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.CompleteAsync(
            engine,
            marked.Snapshot,
            marked.Document,
            new IlLspCompletionParams(
                new IlLspTextDocumentIdentifier(DocumentUri),
                marked.Position,
                new IlLspCompletionContext(1, null)),
            cancellation.Token));
    }

    private async Task<IILLanguageEngine> CreateEngineAsync(int maximumItems = 300)
    {
        var catalog = await _fixture.GetCatalogAsync();
        return _service.CreateEngine(
            catalog,
            IlCompilationLimits.Default,
            IlLspLimits.Default with { MaxCompletionItems = maximumItems });
    }

    private async Task<CompletionFixture> CompleteAsync(
        IILLanguageEngine engine,
        string markedSource,
        IReadOnlyList<TestSource>? additionalSources = null)
    {
        var marked = CreateMarkedWorkspace(markedSource, additionalSources);
        var result = await _service.CompleteAsync(
            engine,
            marked.Snapshot,
            marked.Document,
            new IlLspCompletionParams(
                new IlLspTextDocumentIdentifier(DocumentUri),
                marked.Position,
                new IlLspCompletionContext(1, null)),
            TestContext.Current.CancellationToken);
        return new CompletionFixture(marked.Source, result);
    }

    private static MarkedWorkspace CreateMarkedWorkspace(
        string markedSource,
        IReadOnlyList<TestSource>? additionalSources = null)
    {
        var marker = markedSource.IndexOf('|');
        Assert.True(marker >= 0, "Completion source must contain a cursor marker.");
        Assert.Equal(marker, markedSource.LastIndexOf('|'));
        var source = markedSource.Remove(marker, 1);
        var active = DocumentSnapshot.Create("Program.il", 7, source);
        var documents = new List<DocumentSnapshot> { active };
        if (additionalSources is not null)
        {
            documents.AddRange(additionalSources.Select(static item =>
                DocumentSnapshot.Create(item.Path, 3, item.Text)));
        }
        var immutableDocuments = documents.ToImmutableArray();
        var sourceOrder = immutableDocuments.Select(static document => document.Id).ToImmutableArray();
        var snapshot = new WorkspaceSnapshot(
            CoreSchemaVersion.Current,
            revision: 41,
            selectionRevision: 42,
            languageId: "il",
            referenceSetId: "net10-ref",
            activeFile: active.Id,
            sourceOrder,
            files: immutableDocuments,
            BuildOptions.Default);
        return new MarkedWorkspace(source, snapshot, active.Id, PositionAt(source, marker));
    }

    private static IlLspPosition PositionAt(string text, int offset)
    {
        var before = text[..offset];
        var line = before.Count(static character => character == '\n');
        var lastNewLine = before.LastIndexOf('\n');
        return new IlLspPosition(line, lastNewLine < 0 ? before.Length : before.Length - lastNewLine - 1);
    }

    private static string TextAt(string text, IlLspRange range) =>
        text[OffsetAt(text, range.Start)..OffsetAt(text, range.End)];

    private static List<DecodedSemanticToken> DecodeSemanticTokens(
        string source,
        IlLspSemanticTokens semanticTokens)
    {
        var result = new List<DecodedSemanticToken>();
        var line = 0;
        var character = 0;
        for (var index = 0; index < semanticTokens.Data.Count; index += 5)
        {
            var deltaLine = semanticTokens.Data[index];
            line += deltaLine;
            character = deltaLine == 0
                ? character + semanticTokens.Data[index + 1]
                : semanticTokens.Data[index + 1];
            var length = semanticTokens.Data[index + 2];
            var range = new IlLspRange(
                new IlLspPosition(line, character),
                new IlLspPosition(line, character + length));
            result.Add(new DecodedSemanticToken(
                TextAt(source, range),
                semanticTokens.Data[index + 3],
                semanticTokens.Data[index + 4]));
        }
        return result;
    }

    private static int OffsetAt(string text, IlLspPosition position)
    {
        var offset = 0;
        for (var line = 0; line < position.Line; line++)
            offset = text.IndexOf('\n', offset) + 1;
        return offset + position.Character;
    }

    private static string MethodBody(string markedInstruction) => $$"""
        .method public static void M() cil managed
        {
          {{markedInstruction}}
          ret
        }
        """;

    private static string MemberWorkspace(string markedInstruction) => $$"""
        .class public C
        {
          .field public int32 Value
          .field public static int32 Shared
          .method public specialname rtspecialname instance void .ctor(int32 value) cil managed { ret }
          .method public instance void Instance(int32 value) cil managed { ret }
          .method public static void Static(string value) cil managed { ret }
          .method public static void Convert(int32 value) cil managed { ret }
          .method public static int32 Convert(int32 value) cil managed { ldc.i4.0 ret }
          .class nested public Inner { }
          .method public static void Use() cil managed
          {
            {{markedInstruction}}
            ret
          }
        }
        """;

    private sealed record TestSource(string Path, string Text);
    private sealed record CompletionFixture(string Source, IlLspCompletionList Result);
    private sealed record DecodedSemanticToken(string Text, int Type, int Modifiers);
    private sealed record MarkedWorkspace(
        string Source,
        WorkspaceSnapshot Snapshot,
        DocumentId Document,
        IlLspPosition Position);
}

public sealed class IlLanguageServiceFixture : IDisposable
{
    private readonly string _root = IlTestSettings.CreateRoot();
    private readonly Task<IILMetadataCatalog> _catalog;

    public IlLanguageServiceFixture()
    {
        var settings = IlTestSettings.Create(_root);
        var provider = new IlReferenceSetProvider(settings.ReferenceSets);
        _catalog = provider.GetCatalogAsync("net10-ref", CancellationToken.None);
    }

    public Task<IILMetadataCatalog> GetCatalogAsync() => _catalog;

    public void Dispose() => IlTestSettings.DeleteRoot(_root);
}
