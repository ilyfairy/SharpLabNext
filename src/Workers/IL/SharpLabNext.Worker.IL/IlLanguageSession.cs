using System.Collections.Immutable;
using System.Text;
using EleCho.ILSense;
using EleCho.ILSense.Contracts;
using SharpLabNext.Contracts;
using IlSenseBuildOptions = EleCho.ILSense.Contracts.BuildOptions;
using IlSenseDocumentId = EleCho.ILSense.Contracts.DocumentId;
using IlSenseDocumentSnapshot = EleCho.ILSense.Contracts.DocumentSnapshot;
using IlSenseWorkspaceSnapshot = EleCho.ILSense.Contracts.WorkspaceSnapshot;

namespace SharpLabNext.Worker.IL;

public sealed class IlLanguageSession : IDisposable
{
    private readonly IlLanguageService _languageService;
    private readonly IILLanguageEngine _languageEngine;
    private readonly IlCompilationLimits _compilationLimits;
    private readonly string _referenceSetId;
    private readonly IlSenseBuildOptions _buildOptions;
    private readonly Dictionary<string, SessionDocument> _documents;
    private readonly HashSet<string> _openDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly string[] _sourceOrder;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private long _workspaceRevision;
    private readonly long _selectionRevision;
    private int _connectionAttached;
    private int _disposed;

    internal IlLanguageSession(
        string sessionId,
        ValidatedIlWorkspace workspace,
        IlLanguageService languageService,
        IILMetadataCatalog metadataCatalog,
        IlCompilationLimits compilationLimits,
        IlLspLimits lspLimits,
        string referenceSetId,
        DateTimeOffset expiresAtUtc)
    {
        SessionId = sessionId;
        ExpiresAtUtc = expiresAtUtc;
        var remainingLifetime = expiresAtUtc - DateTimeOffset.UtcNow;
        _lifetimeCancellation.CancelAfter(remainingLifetime > TimeSpan.Zero ? remainingLifetime : TimeSpan.Zero);
        _languageService = languageService;
        _languageEngine = languageService.CreateEngine(metadataCatalog, compilationLimits, lspLimits);
        _compilationLimits = compilationLimits;
        _referenceSetId = referenceSetId;
        _workspaceRevision = workspace.Snapshot.Revision;
        _selectionRevision = workspace.Snapshot.SelectionRevision;
        _sourceOrder = workspace.OrderedFiles.Select(static file => file.Path).ToArray();
        _documents = workspace.OrderedFiles.ToDictionary(
            static file => file.Path,
            static file => new SessionDocument(file.Path, file.Version, file.Text),
            StringComparer.OrdinalIgnoreCase);
        _buildOptions = new IlSenseBuildOptions(
            workspace.Options.OutputKind switch
            {
                BuildOutputKind.Console => AssemblyOutputKind.ConsoleApplication,
                BuildOutputKind.Library => AssemblyOutputKind.Library,
                _ => throw new IlLspInvalidParamsException("ILSense requires a concrete console or library output kind.")
            },
            deterministic: true,
            optimize: workspace.Options.Optimize,
            includeDebugSymbols: workspace.Options.EmitPortablePdb);
    }

    public string SessionId { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
    public bool IsExpired => Volatile.Read(ref _disposed) != 0 || DateTimeOffset.UtcNow >= ExpiresAtUtc;
    internal CancellationToken LifetimeToken => _lifetimeCancellation.Token;

    public IDisposable AttachConnection()
    {
        ThrowIfUnavailable();
        if (Interlocked.CompareExchange(ref _connectionAttached, 1, 0) != 0)
            throw new IlLspSessionUnavailableException("This IL language session already has an attached LSP connection.");
        return new ConnectionLease(this);
    }

    public async Task<IlLspDocumentState> DidOpenAsync(IlLspDidOpenParams parameters, CancellationToken cancellationToken)
    {
        if (!StringComparer.Ordinal.Equals(parameters.TextDocument.LanguageId, "il"))
            throw new IlLspInvalidParamsException("This session only accepts languageId 'il'.");
        var (uri, path) = ValidateUri(parameters.TextDocument.Uri);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            var document = GetDocument(path);
            if (parameters.TextDocument.Version < document.Version || parameters.TextDocument.Version > int.MaxValue)
                throw new IlLspContentModifiedException("didOpen version must not be older and must fit a 32-bit integer.");
            ReplaceText(document, parameters.TextDocument.Version, parameters.TextDocument.Text);
            _openDocuments.Add(document.Path);
            return State(uri, document);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IlLspDocumentState> DidChangeAsync(IlLspDidChangeParams parameters, CancellationToken cancellationToken)
    {
        if (parameters.ContentChanges.Count is 0 or > 100)
            throw new IlLspInvalidParamsException("didChange must contain between 1 and 100 changes.");
        var (uri, path) = ValidateUri(parameters.TextDocument.Uri);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            var document = GetDocument(path);
            if (parameters.TextDocument.Version <= document.Version || parameters.TextDocument.Version > int.MaxValue)
                throw new IlLspContentModifiedException("didChange version must increase and fit a 32-bit integer.");
            var text = document.Text;
            foreach (var change in parameters.ContentChanges)
            {
                if (change.Range is null)
                {
                    text = change.Text;
                    continue;
                }
                var start = ToOffset(text, change.Range.Start);
                var end = ToOffset(text, change.Range.End);
                if (end < start || change.RangeLength is not null && change.RangeLength != end - start)
                    throw new IlLspContentModifiedException("didChange range is inconsistent with the current document.");
                text = string.Concat(text.AsSpan(0, start), change.Text, text.AsSpan(end));
            }
            ReplaceText(document, parameters.TextDocument.Version, text);
            return State(uri, document);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IlLspDocumentState> DidCloseAsync(IlLspDidCloseParams parameters, CancellationToken cancellationToken)
    {
        var (uri, path) = ValidateUri(parameters.TextDocument.Uri);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            var document = GetDocument(path);
            _openDocuments.Remove(document.Path);
            return State(uri, document);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<IlLspDiagnosticsReport> GetDiagnosticsAsync(string uri, CancellationToken cancellationToken) =>
        WithDocumentAsync(
            uri,
            (document, snapshot, documentId, token) => _languageService.GetDiagnosticsAsync(
                _languageEngine,
                snapshot,
                documentId,
                uri,
                _selectionRevision,
                token),
            cancellationToken);

    public async Task<IReadOnlyList<IlLspDiagnosticsReport>> GetWorkspaceDiagnosticsAsync(
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            var reports = new List<IlLspDiagnosticsReport>(_openDocuments.Count);
            foreach (var path in _sourceOrder)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (!_openDocuments.Contains(path))
                    continue;
                var document = _documents[path];
                reports.Add(await _languageService.GetDiagnosticsAsync(
                    _languageEngine,
                    CreateSnapshot(document),
                    new IlSenseDocumentId(document.Path),
                    DocumentUri(document.Path),
                    _selectionRevision,
                    linked.Token).ConfigureAwait(false));
            }
            return reports;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<IlLspCompletionList> GetCompletionsAsync(
        IlLspCompletionParams parameters,
        CancellationToken cancellationToken) =>
        WithDocumentAsync(
            parameters.TextDocument.Uri,
            (_, snapshot, documentId, token) => _languageService.CompleteAsync(
                _languageEngine,
                snapshot,
                documentId,
                parameters,
                token),
            cancellationToken);

    public Task<IlLspHover?> GetHoverAsync(
        IlLspTextDocumentPositionParams parameters,
        CancellationToken cancellationToken) =>
        WithDocumentAsync(
            parameters.TextDocument.Uri,
            (_, snapshot, documentId, token) => _languageService.GetHoverAsync(
                _languageEngine,
                snapshot,
                documentId,
                parameters.Position,
                token),
            cancellationToken);

    public Task<IlLspLocation?> GetDefinitionAsync(
        IlLspTextDocumentPositionParams parameters,
        CancellationToken cancellationToken) =>
        WithDocumentAsync(
            parameters.TextDocument.Uri,
            (_, snapshot, documentId, token) => _languageService.GetDefinitionAsync(
                _languageEngine,
                snapshot,
                documentId,
                parameters.Position,
                token),
            cancellationToken);

    public Task<IReadOnlyList<IlLspWorkspaceSymbol>> GetWorkspaceSymbolsAsync(
        IlLspWorkspaceSymbolParams parameters,
        int maximumResults,
        CancellationToken cancellationToken) =>
        WithWorkspaceAsync(
            (snapshot, token) => _languageService.GetWorkspaceSymbolsAsync(
                _languageEngine,
                snapshot,
                parameters,
                maximumResults,
                token),
            cancellationToken);

    public Task<IReadOnlyList<IlLspCodeAction>> GetCodeActionsAsync(
        IlLspCodeActionParams parameters,
        int maximumResults,
        CancellationToken cancellationToken) =>
        WithDocumentAsync(
            parameters.TextDocument.Uri,
            (_, snapshot, documentId, token) => _languageService.GetCodeActionsAsync(
                _languageEngine,
                snapshot,
                documentId,
                parameters,
                maximumResults,
                token),
            cancellationToken);

    public Task<IlLspSignatureHelp> GetSignatureHelpAsync(
        IlLspTextDocumentPositionParams parameters,
        CancellationToken cancellationToken) =>
        WithDocumentAsync(
            parameters.TextDocument.Uri,
            (_, snapshot, documentId, token) => _languageService.GetSignatureHelpAsync(
                _languageEngine,
                snapshot,
                documentId,
                parameters.Position,
                token),
            cancellationToken);

    public Task<IlLspSemanticTokens> GetSemanticTokensAsync(
        IlLspSemanticTokensParams parameters,
        CancellationToken cancellationToken) =>
        WithDocumentAsync(
            parameters.TextDocument.Uri,
            (_, snapshot, documentId, token) => _languageService.GetSemanticTokensAsync(
                _languageEngine,
                snapshot,
                documentId,
                token),
            cancellationToken);

    public Task<IReadOnlyList<IlLspDocumentSymbol>> GetDocumentSymbolsAsync(
        IlLspDocumentSymbolParams parameters,
        CancellationToken cancellationToken) =>
        WithDocumentAsync(
            parameters.TextDocument.Uri,
            (_, snapshot, documentId, token) => _languageService.GetDocumentSymbolsAsync(
                _languageEngine,
                snapshot,
                documentId,
                token),
            cancellationToken);

    public Task<IReadOnlyList<IlLspFoldingRange>> GetFoldingRangesAsync(
        IlLspFoldingRangeParams parameters,
        CancellationToken cancellationToken) =>
        WithDocumentAsync(
            parameters.TextDocument.Uri,
            (_, snapshot, documentId, token) => _languageService.GetFoldingRangesAsync(
                _languageEngine,
                snapshot,
                documentId,
                token),
            cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _lifetimeCancellation.Cancel();
    }

    private async Task<T> WithDocumentAsync<T>(
        string uri,
        Func<SessionDocument, IlSenseWorkspaceSnapshot, IlSenseDocumentId, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var (_, path) = ValidateUri(uri);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            var document = GetDocument(path);
            var snapshot = CreateSnapshot(document);
            return await action(
                document,
                snapshot,
                new IlSenseDocumentId(document.Path),
                linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> WithWorkspaceAsync<T>(
        Func<IlSenseWorkspaceSnapshot, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            var snapshot = CreateSnapshot(_documents[_sourceOrder[0]]);
            return await action(snapshot, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private IlSenseWorkspaceSnapshot CreateSnapshot(SessionDocument activeDocument)
    {
        var documents = _sourceOrder
            .Select(path =>
            {
                var document = _documents[path];
                return IlSenseDocumentSnapshot.Create(document.Path, document.Version, document.Text);
            })
            .ToImmutableArray();
        var sourceOrder = documents.Select(static document => document.Id).ToImmutableArray();
        return new IlSenseWorkspaceSnapshot(
            CoreSchemaVersion.Current,
            _workspaceRevision,
            _selectionRevision,
            "il",
            _referenceSetId,
            new IlSenseDocumentId(activeDocument.Path),
            sourceOrder,
            documents,
            _buildOptions);
    }

    private void ReplaceText(SessionDocument document, long version, string text)
    {
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes > _compilationLimits.MaxFileUtf8Bytes)
            throw new IlLspLimitExceededException("The IL document exceeds the configured source-size limit.");
        var total = _documents.Values.Where(item => !ReferenceEquals(item, document)).Sum(static item => Encoding.UTF8.GetByteCount(item.Text));
        if (total + bytes > _compilationLimits.MaxTotalSourceUtf8Bytes)
            throw new IlLspLimitExceededException("The IL workspace exceeds the configured source-size limit.");
        document.Version = version;
        document.Text = text;
        _workspaceRevision++;
    }

    private SessionDocument GetDocument(string path) =>
        _documents.TryGetValue(path, out var document)
            ? document
            : throw new IlLspInvalidParamsException($"Document '{path}' is not part of this language session.");

    private IlLspDocumentState State(string uri, SessionDocument document) =>
        new(uri, document.Path, document.Version, _workspaceRevision, _selectionRevision);

    private static (string Uri, string Path) ValidateUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !StringComparer.OrdinalIgnoreCase.Equals(uri.Scheme, "sharplabnext") ||
            !string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new IlLspInvalidParamsException("Document URI must use the sharplabnext:/// relative-workspace-path form.");
        }
        var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        try
        {
            path = IlWorkspaceValidator.NormalizeRelativePath(path);
        }
        catch (IlBuildRequestValidationException exception)
        {
            throw new IlLspInvalidParamsException(exception.Message);
        }
        return (value, path);
    }

    private static string DocumentUri(string path) =>
        $"sharplabnext:///{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}";

    private static int ToOffset(string text, IlLspPosition position)
    {
        if (position.Line < 0 || position.Character < 0)
            throw new IlLspContentModifiedException("LSP range cannot be negative.");
        var currentLine = 0;
        var lineStart = 0;
        while (currentLine < position.Line)
        {
            var newline = text.IndexOf('\n', lineStart);
            if (newline < 0)
                throw new IlLspContentModifiedException("LSP range line is outside the document.");
            lineStart = newline + 1;
            currentLine++;
        }
        var lineEnd = text.IndexOf('\n', lineStart);
        if (lineEnd < 0)
            lineEnd = text.Length;
        if (lineEnd > lineStart && text[lineEnd - 1] == '\r')
            lineEnd--;
        if (position.Character > lineEnd - lineStart)
            throw new IlLspContentModifiedException("LSP range character is outside the document line.");
        return lineStart + position.Character;
    }

    private void ThrowIfUnavailable()
    {
        if (IsExpired)
            throw new IlLspSessionUnavailableException("The IL language session is closed or expired.");
    }

    private void ReleaseConnection() => Interlocked.Exchange(ref _connectionAttached, 0);

    private sealed class SessionDocument(string path, long version, string text)
    {
        public string Path { get; } = path;
        public long Version { get; set; } = version;
        public string Text { get; set; } = text;
    }

    private sealed class ConnectionLease(IlLanguageSession owner) : IDisposable
    {
        private IlLanguageSession? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseConnection();
    }
}
