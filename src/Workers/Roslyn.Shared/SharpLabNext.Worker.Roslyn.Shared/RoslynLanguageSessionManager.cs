using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.Roslyn;

public sealed class RoslynLanguageSessionManager(
    ReferenceSetProvider referenceSets,
    RoslynWorkerIdentity identity,
    CompilationLimits compilationLimits,
    LspLimits lspLimits) : IAsyncDisposable
{
    private static readonly Lazy<MefHostServices> HostServices = new(CreateHostServices);

    private readonly ConcurrentDictionary<string, RoslynLanguageSession> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    public async Task<LanguageSession> OpenAsync(
        OpenLanguageSessionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!identity.SupportsLanguage(request.LanguageId))
            throw new LspInvalidParamsException($"This Roslyn worker does not support languageId '{request.LanguageId}'.");
        if (identity.SupportsLanguage("csharp"))
        {
            RoslynCompilerIdentity.Ensure(
                identity,
                "C# compiler",
                CSharpBuildService.GetLoadedCompilerVersion(),
                CSharpBuildService.GetLoadedCompilerCommit());
        }
        if (identity.SupportsLanguage("visual-basic"))
        {
            RoslynCompilerIdentity.Ensure(
                identity,
                "Visual Basic compiler",
                VisualBasicBuildService.GetLoadedCompilerVersion(),
                VisualBasicBuildService.GetLoadedCompilerCommit());
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!StringComparer.Ordinal.Equals(request.LspVersion, ContractSchemaVersions.Lsp))
            throw new LspInvalidParamsException($"LSP version '{request.LspVersion}' is not supported.");

        var validated = WorkspaceValidator.Validate(
            new BuildRequest(
                request.RequestId,
                request.RequestId,
                request.PipelineResolutionId,
                request.ToolchainId,
                request.ReferenceSetId,
                request.Workspace,
                DateTimeOffset.UtcNow.AddMilliseconds(compilationLimits.MaxBuildMilliseconds),
                request.Workspace.BuildOptions,
                BuildTarget.CompileCheck),
            compilationLimits,
            identity);
        if (!StringComparer.Ordinal.Equals(request.LanguageId, validated.Snapshot.LanguageId))
            throw new LspInvalidParamsException("The language session request and workspace language IDs must match.");
        var loadedReferences = await referenceSets
            .GetAsync(request.ReferenceSetId, cancellationToken)
            .ConfigureAwait(false);

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RemoveExpiredSessions();
            if (_sessions.Count >= lspLimits.MaxSessions)
                throw new LspLimitExceededException($"The worker has reached its {lspLimits.MaxSessions} language session limit.");

            var sessionId = $"lsp_{Guid.NewGuid():N}";
            var expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(lspLimits.SessionTtlMinutes);
            var workspace = CreateWorkspace(sessionId, validated, loadedReferences, cancellationToken);
            var session = new RoslynLanguageSession(
                sessionId,
                workspace.Workspace,
                workspace.ProjectId,
                workspace.Documents,
                validated,
                compilationLimits,
                lspLimits,
                expiresAtUtc);
            if (!_sessions.TryAdd(sessionId, session))
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException("A unique language session ID could not be allocated.");
            }

            return new LanguageSession(
                sessionId,
                validated.Snapshot.LanguageId,
                identity.ToolchainId,
                $"{identity.ToolchainId}/{identity.CompilerVersion}",
                ContractSchemaVersions.Lsp,
                validated.Snapshot.Revision,
                validated.Snapshot.SelectionRevision,
                expiresAtUtc);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public RoslynLanguageSession GetRequired(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!_sessions.TryGetValue(sessionId, out var session) || session.IsExpired)
            throw new LspSessionUnavailableException("The language session does not exist or has expired.");
        return session;
    }

    public bool TryGet(string sessionId, out RoslynLanguageSession? session)
    {
        if (_sessions.TryGetValue(sessionId, out session) && !session.IsExpired)
            return true;

        session = null;
        return false;
    }

    public async Task<bool> CloseAsync(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
            return false;

        await session.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var sessionId in _sessions.Keys)
            await CloseAsync(sessionId).ConfigureAwait(false);
        _lifecycleLock.Dispose();
    }

    private static SessionWorkspace CreateWorkspace(
        string sessionId,
        ValidatedWorkspace validated,
        LoadedReferenceSet references,
        CancellationToken cancellationToken)
    {
        var workspace = new AdhocWorkspace(HostServices.Value, "SharpLabNext.Lsp");
        var projectId = ProjectId.CreateNewId($"{sessionId}-project");
        var language = validated.Snapshot.LanguageId switch
        {
            "csharp" => LanguageNames.CSharp,
            "visual-basic" => LanguageNames.VisualBasic,
            _ => throw new BuildRequestValidationException("The Roslyn worker only accepts C# or Visual Basic language sessions.")
        };
        var parseOptions = language == LanguageNames.CSharp
            ? (ParseOptions)CSharpBuildService.CreateParseOptions(validated.Options)
            : VisualBasicBuildService.CreateParseOptions(validated.Options);
        var compilationOptions = language == LanguageNames.CSharp
            ? (CompilationOptions)CreateCSharpCompilationOptions(
                validated,
                (CSharpParseOptions)parseOptions,
                cancellationToken)
            : VisualBasicBuildService.CreateCompilationOptions(validated.Options);
        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            $"SharpLabNext {sessionId}",
            $"SharpLabNext.LanguageSession.{sessionId}",
            language,
            compilationOptions: compilationOptions,
            parseOptions: parseOptions,
            metadataReferences: references.References);
        var solution = workspace.CurrentSolution.AddProject(projectInfo);
        var documents = new Dictionary<string, DocumentId>(StringComparer.Ordinal);
        foreach (var file in validated.OrderedFiles)
        {
            var documentId = DocumentId.CreateNewId(projectId, file.Path);
            var text = SourceText.From(file.Text, Encoding.UTF8, SourceHashAlgorithm.Sha256);
            solution = solution.AddDocument(
                documentId,
                Path.GetFileName(file.Path),
                text,
                folders: GetFolders(file.Path),
                filePath: file.Path);
            documents.Add(file.Path, documentId);
        }

        if (!workspace.TryApplyChanges(solution))
        {
            workspace.Dispose();
            throw new InvalidOperationException("The Roslyn LSP workspace could not be initialized.");
        }

        return new SessionWorkspace(workspace, projectId, documents);
    }

    private static CSharpCompilationOptions CreateCSharpCompilationOptions(
        ValidatedWorkspace validated,
        CSharpParseOptions parseOptions,
        CancellationToken cancellationToken)
    {
        var syntaxTrees = validated.OrderedFiles
            .Select(file => (SyntaxTree)CSharpSyntaxTree.ParseText(
                SourceText.From(file.Text, Encoding.UTF8, SourceHashAlgorithm.Sha256),
                parseOptions,
                file.Path,
                cancellationToken))
            .ToArray();
        var outputKind = CSharpBuildService.ResolveOutputKind(
            validated.Options.OutputKind,
            syntaxTrees,
            cancellationToken);
        return CSharpBuildService.CreateCompilationOptions(validated.Options, outputKind);
    }

    private static MefHostServices CreateHostServices()
    {
        var explicitAssemblies = new[]
        {
            typeof(Workspace).Assembly,
            typeof(CompletionService).Assembly,
            typeof(Formatter).Assembly,
            typeof(Microsoft.CodeAnalysis.CSharp.CSharpCompilation).Assembly,
            Assembly.Load(new AssemblyName("Microsoft.CodeAnalysis.CSharp.Workspaces")),
            Assembly.Load(new AssemblyName("Microsoft.CodeAnalysis.CSharp.Features")),
            typeof(Microsoft.CodeAnalysis.VisualBasic.VisualBasicCompilation).Assembly,
            Assembly.Load(new AssemblyName("Microsoft.CodeAnalysis.VisualBasic.Workspaces")),
            Assembly.Load(new AssemblyName("Microsoft.CodeAnalysis.VisualBasic.Features"))
        };
        var assemblies = MefHostServices.DefaultAssemblies
            .Concat(explicitAssemblies)
            .Distinct()
            .ToArray();
        return MefHostServices.Create(assemblies);
    }

    private void RemoveExpiredSessions()
    {
        foreach (var pair in _sessions)
        {
            if (pair.Value.IsExpired && _sessions.TryRemove(pair.Key, out var expired))
                expired.Dispose();
        }
    }

    private static string[] GetFolders(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator <= 0
            ? []
            : path[..separator].Split('/');
    }

    private sealed record SessionWorkspace(
        AdhocWorkspace Workspace,
        ProjectId ProjectId,
        IReadOnlyDictionary<string, DocumentId> Documents);
}
