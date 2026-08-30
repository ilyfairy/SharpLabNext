using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.IL.Tests;

public sealed class IlLanguageSessionTests
{
    [Fact]
    public void ReferenceCatalogUsesTheAttestedRuntimeApiCopyFromItsRoot()
    {
        var root = IlTestSettings.CreateRoot();
        try
        {
            var referenceRoot = Path.Combine(root, "reference-sets", "net10-ref");

            var runtimeApiPath = IlReferenceSetProvider.RuntimeApiPath(referenceRoot);

            Assert.Equal(Path.Combine(referenceRoot, "SharpLab.Runtime.dll"), runtimeApiPath, ignoreCase: true);
        }
        finally
        {
            IlTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task SessionTracksVersionsAndClosesDeterministically()
    {
        var root = IlTestSettings.CreateRoot();
        try
        {
            var settings = IlTestSettings.Create(root);
            using var manager = new IlLanguageSessionManager(new IlReferenceSetProvider(settings.ReferenceSets), new IlLanguageService(), settings);
            var source = IlTestSettings.ValidMultiFileWorkspace()[0].Text;
            var request = new OpenLanguageSessionRequest("request-lsp", "pipeline-lsp", "il", "mobius-ilasm-stable", "net10-ref", IlTestSettings.CreateRequest(BuildTarget.CompileCheck, [new WorkspaceFile("Program.il", 1, source)], ["Program.il"]).Workspace);
            var descriptor = await manager.OpenAsync(request, TestContext.Current.CancellationToken);
            Assert.EndsWith("+EleCho.ILSense/0.1.0", descriptor.CompilerBuildIdentity, StringComparison.Ordinal);
            Assert.True(manager.TryGet(descriptor.SessionId, out var session));
            Assert.NotNull(session);
            const string uri = "sharplabnext:///Program.il";
            var sourceLines = source.Split('\n');
            var instructionLine = Array.FindIndex(sourceLines, static line => line.Contains("call void Helper::Ping()", StringComparison.Ordinal));
            var instructionCharacter = sourceLines[instructionLine].IndexOf("call", StringComparison.Ordinal);
            var completion = await session.GetCompletionsAsync(new IlLspCompletionParams(new IlLspTextDocumentIdentifier(uri), new IlLspPosition(instructionLine, instructionCharacter), null), TestContext.Current.CancellationToken);
            Assert.Contains(completion.Items, static item => item.Label == "add");

            const string invalid = ".assembly Demo {}\n.method public static void Main() cil managed\n{\n  nope\n}\n";
            await session.DidChangeAsync(new IlLspDidChangeParams(new IlLspVersionedTextDocumentIdentifier(uri, 2), [new IlLspTextChange(null, null, invalid)]), TestContext.Current.CancellationToken);
            var diagnostics = await session.GetDiagnosticsAsync(uri, TestContext.Current.CancellationToken);
            Assert.Equal(2, diagnostics.Version);
            Assert.Contains(diagnostics.Diagnostics, static item => item.Code == "ILPAR202" && item.Source == "Parse");

            using var connection = session.AttachConnection();
            Assert.Throws<IlLspSessionUnavailableException>(() => session.AttachConnection());
            Assert.True(manager.Close(descriptor.SessionId));
            Assert.False(manager.TryGet(descriptor.SessionId, out _));
        }
        finally
        {
            IlTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CompletionIncludesOtherWorkspaceFilesAndSelectedReferencePackSymbols()
    {
        var root = IlTestSettings.CreateRoot();
        try
        {
            var settings = IlTestSettings.Create(root);
            using var manager = new IlLanguageSessionManager(new IlReferenceSetProvider(settings.ReferenceSets), new IlLanguageService(), settings);
            var files = IlTestSettings.ValidMultiFileWorkspace();
            var request = new OpenLanguageSessionRequest("request-lsp-multi", "pipeline-lsp-multi", "il", "mobius-ilasm-stable", "net11-preview-ref", IlTestSettings.CreateRequest(BuildTarget.CompileCheck, files, ["Program.il", "Helper.il"], referenceSetId: "net11-preview-ref").Workspace);
            var descriptor = await manager.OpenAsync(request, TestContext.Current.CancellationToken);
            Assert.True(manager.TryGet(descriptor.SessionId, out var session));
            Assert.NotNull(session);
            const string uri = "sharplabnext:///Program.il";
            var workspace = await session.GetCompletionsAsync(new IlLspCompletionParams(new IlLspTextDocumentIdentifier(uri), new IlLspPosition(0, 0), null), TestContext.Current.CancellationToken);
            Assert.Contains(workspace.Items, static item => item.Label == "Helper");

            const string assemblyPrefix = """
                .assembly Demo {}
                .class public auto ansi Program extends [System.Runtime]System.Object
                {
                  .method public static void Main() cil managed
                  {
                    .maxstack 1
                    call void [System.R
                  }
                }
                """;
            var assemblyPrefixLines = assemblyPrefix.Split('\n');
            await session.DidChangeAsync(new IlLspDidChangeParams(new IlLspVersionedTextDocumentIdentifier(uri, 2), [new IlLspTextChange(null, null, assemblyPrefix)]), TestContext.Current.CancellationToken);
            var references = await session.GetCompletionsAsync(new IlLspCompletionParams(new IlLspTextDocumentIdentifier(uri), new IlLspPosition(6, assemblyPrefixLines[6].Length), null), TestContext.Current.CancellationToken);
            Assert.Contains(references.Items, static item => item.Label == "System.Runtime" && item.TextEdit.NewText == "System.Runtime]");

            const string runtimeApiPrefix = """
                .assembly Demo {}
                .class public auto ansi Program extends [System.Runtime]System.Object
                {
                  .method public static void Main() cil managed
                  {
                    .maxstack 1
                    call void [SharpLab.
                  }
                }
                """;
            var runtimeApiPrefixLines = runtimeApiPrefix.Split('\n');
            await session.DidChangeAsync(new IlLspDidChangeParams(new IlLspVersionedTextDocumentIdentifier(uri, 3), [new IlLspTextChange(null, null, runtimeApiPrefix)]), TestContext.Current.CancellationToken);
            var runtimeApi = await session.GetCompletionsAsync(new IlLspCompletionParams(new IlLspTextDocumentIdentifier(uri), new IlLspPosition(6, runtimeApiPrefixLines[6].Length), null), TestContext.Current.CancellationToken);
            Assert.Contains(runtimeApi.Items, static item => item.Label == "SharpLab.Runtime" && item.TextEdit.NewText == "SharpLab.Runtime]");
        }
        finally
        {
            IlTestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task SessionCountAndDocumentSizeLimitsAreEnforced()
    {
        var root = IlTestSettings.CreateRoot();
        try
        {
            var original = IlTestSettings.Create(root);
            var settings = original with { CompilationLimits = original.CompilationLimits with { MaxFileUtf8Bytes = 64, MaxTotalSourceUtf8Bytes = 128 }, LspLimits = original.LspLimits with { MaxSessions = 1 } };
            using var manager = new IlLanguageSessionManager(new IlReferenceSetProvider(settings.ReferenceSets), new IlLanguageService(), settings);
            const string source = ".assembly A {}";
            var workspace = IlTestSettings.CreateRequest(BuildTarget.CompileCheck, [new WorkspaceFile("Program.il", 1, source)], ["Program.il"]).Workspace;
            var request = new OpenLanguageSessionRequest("request-limits", "pipeline-limits", "il", "mobius-ilasm-stable", "net10-ref", workspace);
            var descriptor = await manager.OpenAsync(request, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<IlLspLimitExceededException>(() =>
                manager.OpenAsync(request with { RequestId = "request-second" }, TestContext.Current.CancellationToken));
            Assert.True(manager.TryGet(descriptor.SessionId, out var session));
            Assert.NotNull(session);
            await Assert.ThrowsAsync<IlLspLimitExceededException>(() => session.DidChangeAsync(new IlLspDidChangeParams(new IlLspVersionedTextDocumentIdentifier("sharplabnext:///Program.il", 2), [new IlLspTextChange(null, null, new string('x', 65))]), TestContext.Current.CancellationToken));
        }
        finally
        {
            IlTestSettings.DeleteRoot(root);
        }
    }
}
