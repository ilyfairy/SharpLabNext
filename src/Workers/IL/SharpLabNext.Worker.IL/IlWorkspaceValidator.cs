using System.Text;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.IL;

internal sealed record ValidatedIlWorkspace(WorkspaceSnapshot Snapshot, IReadOnlyList<ValidatedIlWorkspaceFile> OrderedFiles, string ActiveFile, BuildOptions Options);

internal sealed record ValidatedIlWorkspaceFile(string Path, long Version, string Text);

internal static class IlWorkspaceValidator
{
    public static ValidatedIlWorkspace Validate(BuildRequest request, IlCompilationLimits limits)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(limits);
        if (string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.IdempotencyKey) || string.IsNullOrWhiteSpace(request.PipelineResolutionId))
        {
            throw new IlBuildRequestValidationException("Request, idempotency and pipeline IDs are required.");
        }
        if (!StringComparer.Ordinal.Equals(request.ToolchainId, "mobius-ilasm-stable"))
            throw new IlBuildRequestValidationException("This worker only accepts the 'mobius-ilasm-stable' toolchain.");
        if (request.Target is not (BuildTarget.Artifact or BuildTarget.CompileCheck))
            throw new IlBuildRequestValidationException($"Build target '{request.Target}' is not supported by the IL worker.");

        var workspace = request.Workspace ?? throw new IlBuildRequestValidationException("Workspace is required.");
        if (workspace.SchemaVersion != ContractSchemaVersions.WorkspaceSnapshot)
            throw new IlBuildRequestValidationException("The workspace schema version is not supported.");
        if (!StringComparer.Ordinal.Equals(workspace.LanguageId, "il"))
            throw new IlBuildRequestValidationException("The IL worker only accepts IL workspaces.");
        if (workspace.Revision < 0 || workspace.SelectionRevision < 0)
            throw new IlBuildRequestValidationException("Workspace revisions cannot be negative.");
        if (!StringComparer.Ordinal.Equals(request.ReferenceSetId, workspace.ReferenceSetId))
            throw new IlBuildRequestValidationException("Request and workspace reference set IDs must match.");
        if (workspace.Files.Count is 0 || workspace.Files.Count > limits.MaxFiles)
            throw new IlBuildRequestValidationException($"Workspace must contain between 1 and {limits.MaxFiles} files.");

        var files = new Dictionary<string, ValidatedIlWorkspaceFile>(StringComparer.OrdinalIgnoreCase);
        var totalBytes = 0;
        foreach (var file in workspace.Files)
        {
            if (file.Version < 0 || file.Version > int.MaxValue)
                throw new IlBuildRequestValidationException("Workspace file versions must fit a non-negative 32-bit integer.");
            var path = NormalizeRelativePath(file.Path);
            if (!path.EndsWith(".il", StringComparison.OrdinalIgnoreCase))
                throw new IlBuildRequestValidationException($"Workspace file '{path}' must use the .il extension.");
            if (!files.TryAdd(path, new ValidatedIlWorkspaceFile(path, file.Version, file.Text)))
                throw new IlBuildRequestValidationException($"Workspace contains duplicate path '{path}'.");
            var bytes = Encoding.UTF8.GetByteCount(file.Text);
            if (bytes > limits.MaxFileUtf8Bytes)
                throw new IlBuildRequestValidationException($"Workspace file '{path}' exceeds the source size limit.");
            totalBytes = checked(totalBytes + bytes);
            if (totalBytes > limits.MaxTotalSourceUtf8Bytes)
                throw new IlBuildRequestValidationException("Workspace exceeds the total source size limit.");
        }

        if (workspace.SourceOrder.Count != files.Count)
            throw new IlBuildRequestValidationException("SourceOrder must contain every IL file exactly once.");
        var ordered = new List<ValidatedIlWorkspaceFile>(files.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in workspace.SourceOrder)
        {
            var path = NormalizeRelativePath(item);
            if (!seen.Add(path) || !files.TryGetValue(path, out var file))
                throw new IlBuildRequestValidationException("SourceOrder contains duplicate or unknown files.");
            ordered.Add(file);
        }
        var requestedActiveFile = NormalizeRelativePath(workspace.ActiveFile);
        if (!files.TryGetValue(requestedActiveFile, out var activeFile))
            throw new IlBuildRequestValidationException("ActiveFile must identify a workspace file.");
        ValidateOptions(request.EffectiveOptions);
        return new ValidatedIlWorkspace(workspace, ordered, activeFile.Path, request.EffectiveOptions);
    }

    internal static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.Contains('\0'))
            throw new IlBuildRequestValidationException("Workspace path is invalid.");
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || Path.IsPathRooted(normalized) || normalized.Contains(':'))
            throw new IlBuildRequestValidationException("Workspace paths must be relative.");
        var segments = normalized.Split('/');
        if (segments.Any(static segment => segment.Length == 0 || segment is "." or ".."))
            throw new IlBuildRequestValidationException("Workspace paths cannot contain traversal segments.");
        return string.Join('/', segments);
    }

    private static void ValidateOptions(BuildOptions options)
    {
        if (options.OutputKind is not (BuildOutputKind.Console or BuildOutputKind.Library))
            throw new IlBuildRequestValidationException("IL supports console and library outputs only.");
        if (options.AllowUnsafe)
            throw new IlBuildRequestValidationException("IL does not expose the allowUnsafe option.");
        if (options.NullableContext is not (NullableContextMode.ProjectDefault or NullableContextMode.Disable))
            throw new IlBuildRequestValidationException("C# nullable context options are not applicable to IL.");
        if (options.LanguageVersion is not null && options.LanguageVersion is not ("default" or "ecma-335"))
            throw new IlBuildRequestValidationException($"IL language version '{options.LanguageVersion}' is not allowed.");
        if (options.PreprocessorSymbols is { Count: > 0 })
            throw new IlBuildRequestValidationException("IL does not support preprocessor symbols.");
        if (options.CheckOverflow)
            throw new IlBuildRequestValidationException("IL does not expose a project-level overflow-check option.");
    }
}
