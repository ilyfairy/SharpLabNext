using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.Roslyn;

public sealed class RoslynLanguageSession : IDisposable, IAsyncDisposable
{
    private readonly AdhocWorkspace _workspace;
    private readonly ProjectId _projectId;
    private readonly Dictionary<string, DocumentId> _documentsByPath;
    private readonly Dictionary<string, long> _versionsByPath;
    private readonly Dictionary<string, int> _utf8BytesByPath;
    private readonly CompilationLimits _compilationLimits;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RoslynLspFeatureService _features;
    private readonly bool _automaticallySelectCSharpOutputKind;
    private long _workspaceRevision;
    private readonly long _selectionRevision;
    private int _connectionAttached;
    private bool _disposed;

    internal RoslynLanguageSession(string sessionId, AdhocWorkspace workspace, ProjectId projectId, IReadOnlyDictionary<string, DocumentId> documents, ValidatedWorkspace validated, CompilationLimits compilationLimits, LspLimits lspLimits, DateTimeOffset expiresAtUtc)
    {
        SessionId = sessionId;
        LanguageId = validated.Snapshot.LanguageId;
        _workspace = workspace;
        _projectId = projectId;
        _documentsByPath = new Dictionary<string, DocumentId>(documents, StringComparer.Ordinal);
        _versionsByPath = validated.OrderedFiles.ToDictionary(static file => file.Path, static file => file.Version, StringComparer.Ordinal);
        _utf8BytesByPath = validated.OrderedFiles.ToDictionary(static file => file.Path, static file => Encoding.UTF8.GetByteCount(file.Text), StringComparer.Ordinal);
        _workspaceRevision = validated.Snapshot.Revision;
        _selectionRevision = validated.Snapshot.SelectionRevision;
        _compilationLimits = compilationLimits;
        _automaticallySelectCSharpOutputKind =
            LanguageId == "csharp" && validated.Options.OutputKind == BuildOutputKind.Auto;
        ExpiresAtUtc = expiresAtUtc;
        _features = new RoslynLspFeatureService(this, lspLimits);
    }

    public string SessionId { get; }

    public string LanguageId { get; }

    internal string MarkdownLanguageId => LanguageId == "visual-basic" ? "vb" : "csharp";

    public DateTimeOffset ExpiresAtUtc { get; }

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAtUtc || _disposed;

    public async Task<LspDocumentState> DidOpenAsync(LspDidOpenTextDocumentParams parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!StringComparer.Ordinal.Equals(parameters.TextDocument.LanguageId, LanguageId)) throw new LspInvalidParamsException($"This Roslyn session only accepts languageId '{LanguageId}'.");

        var uri = ValidateUri(parameters.TextDocument.Uri);
        var path = PathFromUri(uri);
        var extension = LanguageId == "visual-basic" ? ".vb" : ".cs";
        if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) throw new LspInvalidParamsException($"Document '{path}' must use the {extension} extension for this language session.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            var text = SourceText.From(parameters.TextDocument.Text, Encoding.UTF8, SourceHashAlgorithm.Sha256);
            ValidateDocumentSize(path, text);
            var updatedBytes = Encoding.UTF8.GetByteCount(parameters.TextDocument.Text);
            var previousBytes = _utf8BytesByPath.GetValueOrDefault(path);
            var totalBytes = checked(_utf8BytesByPath.Values.Sum() - previousBytes + updatedBytes);
            if (totalBytes > _compilationLimits.MaxTotalSourceUtf8Bytes) throw new LspLimitExceededException($"The session exceeds the {_compilationLimits.MaxTotalSourceUtf8Bytes} byte total source limit.");

            if (_documentsByPath.TryGetValue(path, out var existingDocumentId))
            {
                var currentVersion = _versionsByPath[path];
                if (parameters.TextDocument.Version < currentVersion) throw new LspContentModifiedException("didOpen document version is older than the current session version.");

                var currentDocument = GetRequiredDocument(existingDocumentId);
                var currentText = await currentDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
                if (!currentText.ContentEquals(text))
                {
                    await ApplyDocumentTextAsync(existingDocumentId, text, cancellationToken).ConfigureAwait(false);
                    _workspaceRevision++;
                    _features.ClearCompletionCache();
                }

                _versionsByPath[path] = parameters.TextDocument.Version;
                _utf8BytesByPath[path] = updatedBytes;
            }
            else
            {
                if (_documentsByPath.Count >= _compilationLimits.MaxFiles) throw new LspLimitExceededException($"The session exceeds the {_compilationLimits.MaxFiles} file limit.");

                var documentId = DocumentId.CreateNewId(_projectId, path);
                var solution = _workspace.CurrentSolution.AddDocument(documentId, Path.GetFileName(path), text, folders: GetFolders(path), filePath: path);
                solution = await WithAutomaticCSharpOutputKindAsync(solution, cancellationToken).ConfigureAwait(false);
                ApplySolution(solution);
                _documentsByPath.Add(path, documentId);
                _versionsByPath.Add(path, parameters.TextDocument.Version);
                _utf8BytesByPath.Add(path, updatedBytes);
                _workspaceRevision++;
            }

            return CreateState(uri, path);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LspDocumentState> DidChangeAsync(LspDidChangeTextDocumentParams parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.ContentChanges.Count == 0) throw new LspInvalidParamsException("didChange must contain at least one content change.");
        if (parameters.ContentChanges.Count > 100) throw new LspLimitExceededException("didChange contains too many incremental edits.");

        var uri = ValidateUri(parameters.TextDocument.Uri);
        var path = PathFromUri(uri);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            var documentId = GetDocumentId(path);
            var currentVersion = _versionsByPath[path];
            if (parameters.TextDocument.Version <= currentVersion) throw new LspContentModifiedException("didChange document version must be greater than the current version.");

            var document = GetRequiredDocument(documentId);
            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            foreach (var change in parameters.ContentChanges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (change.Range is null)
                {
                    text = SourceText.From(change.Text, Encoding.UTF8, SourceHashAlgorithm.Sha256);
                    continue;
                }

                var span = ToTextSpan(text, change.Range);
                if (change.RangeLength is not null && change.RangeLength != span.Length) throw new LspContentModifiedException("didChange rangeLength does not match the UTF-16 source span.");
                text = text.WithChanges(new TextChange(span, change.Text));
            }

            ValidateDocumentSize(path, text);
            var previousBytes = _utf8BytesByPath[path];
            var updatedBytes = Encoding.UTF8.GetByteCount(text.ToString());
            var totalBytes = checked(_utf8BytesByPath.Values.Sum() - previousBytes + updatedBytes);
            if (totalBytes > _compilationLimits.MaxTotalSourceUtf8Bytes) throw new LspLimitExceededException($"The session exceeds the {_compilationLimits.MaxTotalSourceUtf8Bytes} byte total source limit.");

            await ApplyDocumentTextAsync(documentId, text, cancellationToken).ConfigureAwait(false);
            _versionsByPath[path] = parameters.TextDocument.Version;
            _utf8BytesByPath[path] = updatedBytes;
            _workspaceRevision++;
            _features.ClearCompletionCache();
            return CreateState(uri, path);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LspDocumentState> DidCloseAsync(LspDidCloseTextDocumentParams parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var uri = ValidateUri(parameters.TextDocument.Uri);
        var path = PathFromUri(uri);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            GetDocumentId(path);
            return CreateState(uri, path);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<LspDiagnosticsReport?> GetDiagnosticsAsync(string uri, long expectedVersion, CancellationToken cancellationToken) => _features.GetDiagnosticsAsync(uri, expectedVersion, cancellationToken);

    public Task<LspCompletionList> GetCompletionsAsync(LspCompletionParams parameters, CancellationToken cancellationToken) => _features.GetCompletionsAsync(parameters, cancellationToken);

    public Task<LspCompletionItem> ResolveCompletionAsync(LspCompletionItem item, CancellationToken cancellationToken) => _features.ResolveCompletionAsync(item, cancellationToken);

    public Task<LspHover?> GetHoverAsync(LspTextDocumentPositionParams parameters, CancellationToken cancellationToken) => _features.GetHoverAsync(parameters, cancellationToken);

    public Task<LspSignatureHelp?> GetSignatureHelpAsync(LspSignatureHelpParams parameters, CancellationToken cancellationToken) => _features.GetSignatureHelpAsync(parameters, cancellationToken);

    public Task<LspSemanticTokens> GetSemanticTokensAsync(LspSemanticTokensParams parameters, CancellationToken cancellationToken) => _features.GetSemanticTokensAsync(parameters, cancellationToken);

    public Task<IReadOnlyList<LspDocumentSymbol>> GetDocumentSymbolsAsync(LspDocumentSymbolParams parameters, CancellationToken cancellationToken) => _features.GetDocumentSymbolsAsync(parameters, cancellationToken);

    public Task<IReadOnlyList<LspCodeAction>> GetCodeActionsAsync(LspCodeActionParams parameters, CancellationToken cancellationToken) => _features.GetCodeActionsAsync(parameters, cancellationToken);

    public IDisposable AttachConnection()
    {
        ThrowIfUnavailable();
        if (Interlocked.CompareExchange(ref _connectionAttached, 1, 0) != 0) throw new LspSessionUnavailableException("The language session already has an active LSP connection.");
        return new ConnectionLease(this);
    }

    internal async Task<LspDocumentSnapshot> GetDocumentSnapshotAsync(string uri, CancellationToken cancellationToken)
    {
        uri = ValidateUri(uri);
        var path = PathFromUri(uri);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            var documentId = GetDocumentId(path);
            var document = GetRequiredDocument(documentId);
            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            return new LspDocumentSnapshot(uri, path, document, text, _versionsByPath[path], _workspaceRevision, _selectionRevision);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<bool> IsCurrentAsync(string path, long version, long workspaceRevision, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return !_disposed &&
                _versionsByPath.TryGetValue(path, out var currentVersion) &&
                currentVersion == version &&
                _workspaceRevision == workspaceRevision;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _features.Dispose();
        _workspace.Dispose();
        _gate.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task ApplyDocumentTextAsync(DocumentId documentId, SourceText text, CancellationToken cancellationToken)
    {
        var solution = _workspace.CurrentSolution.WithDocumentText(documentId, text, PreservationMode.PreserveIdentity);
        solution = await WithAutomaticCSharpOutputKindAsync(solution, cancellationToken).ConfigureAwait(false);
        ApplySolution(solution);
    }

    private void ApplySolution(Solution solution)
    {
        if (!_workspace.TryApplyChanges(solution)) throw new InvalidOperationException("Roslyn rejected a language session workspace update.");
    }

    private async Task<Solution> WithAutomaticCSharpOutputKindAsync(Solution solution, CancellationToken cancellationToken)
    {
        if (!_automaticallySelectCSharpOutputKind) return solution;

        var project = solution.GetProject(_projectId) ?? throw new InvalidOperationException("The Roslyn language session project no longer exists.");
        var syntaxTrees = new List<SyntaxTree>(project.DocumentIds.Count);
        foreach (var document in project.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Roslyn did not produce a C# syntax tree for the language session document.");
            syntaxTrees.Add(syntaxTree);
        }

        var resolvedOutputKind = CSharpBuildService.ResolveOutputKind(BuildOutputKind.Auto, syntaxTrees, cancellationToken);
        var roslynOutputKind = CSharpBuildService.ToRoslynOutputKind(resolvedOutputKind);
        if (project.CompilationOptions?.OutputKind == roslynOutputKind) return solution;

        var compilationOptions = project.CompilationOptions as CSharpCompilationOptions ?? throw new InvalidOperationException("The C# language session does not have C# compilation options.");
        return project.Solution.WithProjectCompilationOptions(_projectId, compilationOptions.WithOutputKind(roslynOutputKind));
    }

    private DocumentId GetDocumentId(string path) => _documentsByPath.TryGetValue(path, out var documentId) ? documentId : throw new LspInvalidParamsException($"Document '{path}' is not part of this language session.");

    private Document GetRequiredDocument(DocumentId documentId) => _workspace.CurrentSolution.GetDocument(documentId) ?? throw new InvalidOperationException("The Roslyn language session document no longer exists.");

    private LspDocumentState CreateState(string uri, string path) => new(uri, path, _versionsByPath[path], _workspaceRevision, _selectionRevision);

    private void ValidateDocumentSize(string path, SourceText text)
    {
        var bytes = Encoding.UTF8.GetByteCount(text.ToString());
        if (bytes > _compilationLimits.MaxFileUtf8Bytes) throw new LspLimitExceededException($"Document '{path}' exceeds the {_compilationLimits.MaxFileUtf8Bytes} byte source limit.");
    }

    private void ThrowIfUnavailable()
    {
        if (_disposed || IsExpired) throw new LspSessionUnavailableException("The language session has expired or was closed.");
    }

    private static string ValidateUri(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("file" or "sharplabnext" or "inmemory"))
        {
            throw new LspInvalidParamsException("Document URI must use file, sharplabnext, or inmemory scheme.");
        }

        return parsed.AbsoluteUri;
    }

    private static string PathFromUri(string uri)
    {
        var parsed = new Uri(uri, UriKind.Absolute);
        var path = Uri.UnescapeDataString(parsed.AbsolutePath).TrimStart('/');
        return WorkspaceValidator.NormalizeRelativePath(path);
    }

    internal static int ToPosition(SourceText text, LspPosition position)
    {
        if (position.Line < 0 || position.Line >= text.Lines.Count) throw new LspInvalidParamsException("LSP line is outside the document.");
        var line = text.Lines[position.Line];
        if (position.Character < 0 || position.Character > line.Span.Length) throw new LspInvalidParamsException("LSP UTF-16 character is outside the line.");
        return line.Start + position.Character;
    }

    internal static LspRange ToRange(SourceText text, TextSpan span)
    {
        var lineSpan = text.Lines.GetLinePositionSpan(span);
        return new LspRange(new LspPosition(lineSpan.Start.Line, lineSpan.Start.Character), new LspPosition(lineSpan.End.Line, lineSpan.End.Character));
    }

    private static TextSpan ToTextSpan(SourceText text, LspRange range) => TextSpan.FromBounds(ToPosition(text, range.Start), ToPosition(text, range.End));

    private static string[] GetFolders(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator <= 0 ? [] : path[..separator].Split('/');
    }

    private sealed class ConnectionLease(RoslynLanguageSession session) : IDisposable
    {
        private RoslynLanguageSession? _session = session;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _session, null);
            if (owner is not null) Interlocked.Exchange(ref owner._connectionAttached, 0);
        }
    }
}

internal sealed record LspDocumentSnapshot(string Uri, string Path, Document Document, SourceText Text, long Version, long WorkspaceRevision, long SelectionRevision);
