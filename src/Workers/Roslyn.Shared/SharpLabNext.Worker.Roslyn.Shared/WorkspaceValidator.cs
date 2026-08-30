using System.Text;
using SharpLabNext.Contracts;
using CSharpSyntaxFacts = Microsoft.CodeAnalysis.CSharp.SyntaxFacts;
using VisualBasicSyntaxFacts = Microsoft.CodeAnalysis.VisualBasic.SyntaxFacts;

namespace SharpLabNext.Worker.Roslyn;

internal sealed record ValidatedWorkspace(WorkspaceSnapshot Snapshot, IReadOnlyList<ValidatedWorkspaceFile> OrderedFiles, string ActiveFile, BuildOptions Options);

internal sealed record ValidatedWorkspaceFile(string Path, long Version, string Text);

internal static class WorkspaceValidator
{
    public static ValidatedWorkspace Validate(BuildRequest request, CompilationLimits limits, RoslynWorkerIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(identity);

        if (string.IsNullOrWhiteSpace(request.RequestId))
            throw new BuildRequestValidationException("RequestId is required.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new BuildRequestValidationException("IdempotencyKey is required.");
        if (string.IsNullOrWhiteSpace(request.PipelineResolutionId))
            throw new BuildRequestValidationException("PipelineResolutionId is required.");
        if (!StringComparer.Ordinal.Equals(request.ToolchainId, identity.ToolchainId))
            throw new BuildRequestValidationException($"This worker only accepts the '{identity.ToolchainId}' toolchain.");
        if (request.Target is not (BuildTarget.Artifact or BuildTarget.CompileCheck or BuildTarget.Ast))
            throw new BuildRequestValidationException($"Build target '{request.Target}' is not supported by this worker.");

        return ValidateWorkspace(request.Workspace, request.ReferenceSetId, request.EffectiveOptions, limits, supportedLanguageIds: identity.SupportedLanguageIds);
    }

    public static ValidatedWorkspace Validate(ExplainRequest request, CompilationLimits limits)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(limits);

        ValidateRequestIdentity(request.RequestId, request.IdempotencyKey, request.PipelineResolutionId);
        var workspace = request.Workspace ?? throw new BuildRequestValidationException("Workspace is required.");
        return ValidateWorkspace(workspace, workspace.ReferenceSetId, workspace.BuildOptions, limits, requiredLanguageId: "csharp");
    }

    private static ValidatedWorkspace ValidateWorkspace(WorkspaceSnapshot workspace, string referenceSetId, BuildOptions options, CompilationLimits limits, string? requiredLanguageId = null, IReadOnlyList<string>? supportedLanguageIds = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.SchemaVersion != ContractSchemaVersions.WorkspaceSnapshot)
        {
            throw new BuildRequestValidationException($"Workspace schema version {workspace.SchemaVersion} is not supported.");
        }

        if (workspace.Revision < 0 || workspace.SelectionRevision < 0)
            throw new BuildRequestValidationException("Workspace and selection revisions cannot be negative.");
        if (supportedLanguageIds is not null && !supportedLanguageIds.Contains(workspace.LanguageId, StringComparer.Ordinal))
        {
            throw new BuildRequestValidationException($"This Roslyn worker does not support languageId '{workspace.LanguageId}'.");
        }

        var extension = workspace.LanguageId switch
        {
            "csharp" => ".cs",
            "visual-basic" => ".vb",
            _ => throw new BuildRequestValidationException("The Roslyn worker only accepts C# or Visual Basic workspaces.")
        };
        if (requiredLanguageId is not null && !StringComparer.Ordinal.Equals(workspace.LanguageId, requiredLanguageId))
        {
            throw new BuildRequestValidationException($"This operation only accepts languageId '{requiredLanguageId}'.");
        }
        if (!StringComparer.Ordinal.Equals(referenceSetId, workspace.ReferenceSetId))
            throw new BuildRequestValidationException("Request and workspace reference set IDs must match.");
        if (workspace.Files.Count == 0)
            throw new BuildRequestValidationException("Workspace must contain at least one source file.");
        if (workspace.Files.Count > limits.MaxFiles)
            throw new BuildRequestValidationException($"Workspace exceeds the {limits.MaxFiles} file limit.");

        var files = new Dictionary<string, ValidatedWorkspaceFile>(StringComparer.Ordinal);
        var totalBytes = 0;
        foreach (var file in workspace.Files)
        {
            if (file.Version < 0)
                throw new BuildRequestValidationException("Workspace file versions cannot be negative.");

            var path = NormalizeRelativePath(file.Path);
            if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                throw new BuildRequestValidationException($"Workspace file '{path}' must use the {extension} extension.");
            if (!files.TryAdd(path, new ValidatedWorkspaceFile(path, file.Version, file.Text)))
                throw new BuildRequestValidationException($"Workspace contains duplicate path '{path}'.");

            var fileBytes = Encoding.UTF8.GetByteCount(file.Text);
            if (fileBytes > limits.MaxFileUtf8Bytes)
            {
                throw new BuildRequestValidationException($"Workspace file '{path}' exceeds the {limits.MaxFileUtf8Bytes} byte source limit.");
            }

            totalBytes = checked(totalBytes + fileBytes);
            if (totalBytes > limits.MaxTotalSourceUtf8Bytes)
            {
                throw new BuildRequestValidationException($"Workspace exceeds the {limits.MaxTotalSourceUtf8Bytes} byte total source limit.");
            }
        }

        if (workspace.SourceOrder.Count != files.Count)
            throw new BuildRequestValidationException("SourceOrder must contain every workspace file exactly once.");

        var seenOrder = new HashSet<string>(StringComparer.Ordinal);
        var orderedFiles = new List<ValidatedWorkspaceFile>(files.Count);
        foreach (var sourcePath in workspace.SourceOrder)
        {
            var path = NormalizeRelativePath(sourcePath);
            if (!seenOrder.Add(path))
                throw new BuildRequestValidationException($"SourceOrder contains duplicate path '{path}'.");
            if (!files.TryGetValue(path, out var file))
                throw new BuildRequestValidationException($"SourceOrder contains unknown path '{path}'.");
            orderedFiles.Add(file);
        }

        var activeFile = NormalizeRelativePath(workspace.ActiveFile);
        if (!files.ContainsKey(activeFile))
            throw new BuildRequestValidationException("ActiveFile must identify a workspace file.");

        ValidateBuildOptions(options, workspace.LanguageId);
        return new ValidatedWorkspace(workspace, orderedFiles, activeFile, options);
    }

    private static void ValidateRequestIdentity(string requestId, string idempotencyKey, string pipelineResolutionId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            throw new BuildRequestValidationException("RequestId is required.");
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new BuildRequestValidationException("IdempotencyKey is required.");
        if (string.IsNullOrWhiteSpace(pipelineResolutionId))
            throw new BuildRequestValidationException("PipelineResolutionId is required.");
    }

    private static void ValidateBuildOptions(BuildOptions options, string languageId)
    {
        if (languageId == "visual-basic" && options.AllowUnsafe)
            throw new BuildRequestValidationException("Visual Basic does not support the allowUnsafe build option.");
        if (options.PreprocessorSymbols is { Count: > 64 })
            throw new BuildRequestValidationException("At most 64 preprocessor symbols are allowed.");

        if (options.PreprocessorSymbols is null)
            return;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in options.PreprocessorSymbols)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                throw new BuildRequestValidationException($"Preprocessor symbol '{symbol}' is not a valid {languageId} identifier.");
            var isValid = languageId == "csharp"
                ? CSharpSyntaxFacts.IsValidIdentifier(symbol) : VisualBasicSyntaxFacts.IsValidIdentifier(symbol);
            if (!isValid)
                throw new BuildRequestValidationException($"Preprocessor symbol '{symbol}' is not a valid {languageId} identifier.");
            if (!seen.Add(symbol))
                throw new BuildRequestValidationException($"Preprocessor symbol '{symbol}' is duplicated.");
        }
    }

    internal static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new BuildRequestValidationException("Workspace paths cannot be empty.");
        if (path.Length > 240 || path.Contains('\0'))
            throw new BuildRequestValidationException("Workspace path is invalid or too long.");

        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || Path.IsPathRooted(normalized) || normalized.Contains(':'))
            throw new BuildRequestValidationException("Workspace paths must be relative.");

        var segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Any(static segment => segment.Length == 0 || segment is "." or ".."))
            throw new BuildRequestValidationException("Workspace paths must be normalized and cannot contain traversal segments.");

        return string.Join('/', segments);
    }
}
