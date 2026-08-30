using System.Text;
using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Sdk;

namespace SharpLabNext.Worker.PeachPie;

internal sealed record ValidatedPeachPieWorkspace(WorkspaceSnapshot Snapshot, IReadOnlyList<ValidatedPeachPieWorkspaceFile> OrderedFiles, string ActiveFile, BuildOptions Options);

internal sealed record ValidatedPeachPieWorkspaceFile(string Path, long Version, string Text);

internal static class PeachPieWorkspaceValidator
{
    public static ValidatedPeachPieWorkspace Validate(BuildRequest request, LanguageWorkerCapabilityManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.PipelineResolutionId))
            throw new PeachPieBuildRequestValidationException("Pipeline resolution ID is required.");
        if (!StringComparer.Ordinal.Equals(request.ToolchainId, PeachPieToolchain.ToolchainId))
            throw new PeachPieBuildRequestValidationException("This worker only accepts the 'peachpie-stable' toolchain.");
        if (request.Target is not (BuildTarget.Artifact or BuildTarget.CompileCheck))
            throw new PeachPieBuildRequestValidationException($"Build target '{request.Target}' is not supported.");

        var workspace = request.Workspace ?? throw new PeachPieBuildRequestValidationException("Workspace is required.");
        if (workspace.SchemaVersion != ContractSchemaVersions.WorkspaceSnapshot)
            throw new PeachPieBuildRequestValidationException("The workspace schema version is not supported.");
        if (!StringComparer.Ordinal.Equals(workspace.LanguageId, PeachPieToolchain.LanguageId))
            throw new PeachPieBuildRequestValidationException("The PeachPie worker only accepts PHP workspaces.");
        if (workspace.Revision < 0 || workspace.SelectionRevision < 0)
            throw new PeachPieBuildRequestValidationException("Workspace revisions cannot be negative.");
        if (!StringComparer.Ordinal.Equals(request.ReferenceSetId, workspace.ReferenceSetId))
            throw new PeachPieBuildRequestValidationException("Request and workspace reference set IDs must match.");
        if (workspace.Files.Count == 0 || workspace.Files.Count > manifest.Limits.MaximumFiles)
            throw new PeachPieBuildRequestValidationException($"Workspace must contain between 1 and {manifest.Limits.MaximumFiles} files.");

        var files = new Dictionary<string, ValidatedPeachPieWorkspaceFile>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var file in workspace.Files)
        {
            if (file.Version < 0 || file.Version > int.MaxValue)
                throw new PeachPieBuildRequestValidationException("Workspace file versions must fit a non-negative 32-bit integer.");
            var path = NormalizeRelativePath(file.Path);
            if (StringComparer.OrdinalIgnoreCase.Equals(path, PeachPieCompiler.BootstrapFileName))
                throw new PeachPieBuildRequestValidationException("The internal PeachPie bootstrap path is reserved.");
            if (!path.EndsWith(".php", StringComparison.OrdinalIgnoreCase))
                throw new PeachPieBuildRequestValidationException($"Workspace file '{path}' must use the .php extension.");
            if (!files.TryAdd(path, new ValidatedPeachPieWorkspaceFile(path, file.Version, file.Text)))
                throw new PeachPieBuildRequestValidationException($"Workspace contains duplicate path '{path}'.");
            totalBytes += Encoding.UTF8.GetByteCount(file.Text);
            if (totalBytes > manifest.Limits.MaximumSourceUtf8Bytes)
                throw new PeachPieBuildRequestValidationException("Workspace exceeds the total source size limit.");
        }

        if (workspace.SourceOrder.Count != files.Count)
            throw new PeachPieBuildRequestValidationException("SourceOrder must contain every PHP file exactly once.");
        var ordered = new List<ValidatedPeachPieWorkspaceFile>(files.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in workspace.SourceOrder)
        {
            var path = NormalizeRelativePath(item);
            if (!seen.Add(path) || !files.TryGetValue(path, out var file))
                throw new PeachPieBuildRequestValidationException("SourceOrder contains duplicate or unknown files.");
            ordered.Add(file);
        }
        var activeFile = NormalizeRelativePath(workspace.ActiveFile);
        if (!files.ContainsKey(activeFile))
            throw new PeachPieBuildRequestValidationException("ActiveFile must identify a workspace file.");

        ValidateOptions(request.EffectiveOptions);
        return new ValidatedPeachPieWorkspace(workspace, ordered, activeFile, request.EffectiveOptions);
    }

    internal static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.Contains('\0'))
            throw new PeachPieBuildRequestValidationException("Workspace path is invalid.");
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || Path.IsPathRooted(normalized) || normalized.Contains(':'))
            throw new PeachPieBuildRequestValidationException("Workspace paths must be relative.");
        var segments = normalized.Split('/');
        if (segments.Any(static segment => segment.Length == 0 || segment is "." or ".."))
            throw new PeachPieBuildRequestValidationException("Workspace paths cannot contain traversal segments.");
        return string.Join('/', segments);
    }

    private static void ValidateOptions(BuildOptions options)
    {
        if (options.OutputKind != BuildOutputKind.Console)
            throw new PeachPieBuildRequestValidationException("PeachPie currently supports console output only.");
        if (options.AllowUnsafe)
            throw new PeachPieBuildRequestValidationException("PHP does not expose the allowUnsafe option.");
        if (options.NullableContext is not (NullableContextMode.ProjectDefault or NullableContextMode.Disable))
            throw new PeachPieBuildRequestValidationException("C# nullable context options are not applicable to PHP.");
        if (options.LanguageVersion is not null && options.LanguageVersion is not ("default" or "latest" or "8.5"))
        {
            throw new PeachPieBuildRequestValidationException($"PHP language version '{options.LanguageVersion}' is not supported by the pinned PeachPie compiler.");
        }
        if (options.PreprocessorSymbols is { Count: > 0 })
            throw new PeachPieBuildRequestValidationException("PHP does not expose preprocessor symbols.");
    }
}
