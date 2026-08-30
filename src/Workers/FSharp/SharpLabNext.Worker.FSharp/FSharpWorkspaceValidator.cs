using System.Text;
using System.Text.RegularExpressions;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.FSharp;

internal sealed record ValidatedFSharpWorkspace(WorkspaceSnapshot Snapshot, IReadOnlyList<ValidatedFSharpWorkspaceFile> OrderedFiles, string ActiveFile, BuildOptions Options);

internal sealed record ValidatedFSharpWorkspaceFile(string Path, long Version, string Text);

internal static partial class FSharpWorkspaceValidator
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_']*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    public static ValidatedFSharpWorkspace Validate(BuildRequest request, FSharpCompilationLimits limits)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.IdempotencyKey) || string.IsNullOrWhiteSpace(request.PipelineResolutionId))
        {
            throw new FSharpBuildRequestValidationException("Request, idempotency and pipeline IDs are required.");
        }
        if (request.ToolchainId != "fsharp-stable")
            throw new FSharpBuildRequestValidationException("This worker only accepts the 'fsharp-stable' toolchain.");
        if (request.Target is not (BuildTarget.Artifact or BuildTarget.CompileCheck or BuildTarget.Ast))
            throw new FSharpBuildRequestValidationException($"Build target '{request.Target}' is not supported.");

        var workspace = request.Workspace ?? throw new FSharpBuildRequestValidationException("Workspace is required.");
        if (workspace.SchemaVersion != ContractSchemaVersions.WorkspaceSnapshot)
            throw new FSharpBuildRequestValidationException("The workspace schema version is not supported.");
        if (workspace.LanguageId != "fsharp")
            throw new FSharpBuildRequestValidationException("The F# worker only accepts F# workspaces.");
        if (workspace.Revision < 0 || workspace.SelectionRevision < 0)
            throw new FSharpBuildRequestValidationException("Workspace revisions cannot be negative.");
        if (request.ReferenceSetId != workspace.ReferenceSetId)
            throw new FSharpBuildRequestValidationException("Request and workspace reference set IDs must match.");
        if (workspace.Files.Count == 0 || workspace.Files.Count > limits.MaxFiles)
            throw new FSharpBuildRequestValidationException($"Workspace must contain between 1 and {limits.MaxFiles} files.");

        var files = new Dictionary<string, ValidatedFSharpWorkspaceFile>(StringComparer.Ordinal);
        var totalBytes = 0;
        foreach (var file in workspace.Files)
        {
            if (file.Version < 0 || file.Version > int.MaxValue)
                throw new FSharpBuildRequestValidationException("Workspace file versions must fit a non-negative 32-bit integer.");
            var path = NormalizeRelativePath(file.Path);
            if (!path.EndsWith(".fs", StringComparison.OrdinalIgnoreCase))
                throw new FSharpBuildRequestValidationException($"Workspace file '{path}' must use the .fs extension.");
            if (!files.TryAdd(path, new ValidatedFSharpWorkspaceFile(path, file.Version, file.Text)))
                throw new FSharpBuildRequestValidationException($"Workspace contains duplicate path '{path}'.");
            var bytes = Encoding.UTF8.GetByteCount(file.Text);
            if (bytes > limits.MaxFileUtf8Bytes)
                throw new FSharpBuildRequestValidationException($"Workspace file '{path}' exceeds the source size limit.");
            totalBytes = checked(totalBytes + bytes);
            if (totalBytes > limits.MaxTotalSourceUtf8Bytes)
                throw new FSharpBuildRequestValidationException("Workspace exceeds the total source size limit.");
        }

        if (workspace.SourceOrder.Count != files.Count)
            throw new FSharpBuildRequestValidationException("SourceOrder must contain every F# file exactly once.");
        var ordered = new List<ValidatedFSharpWorkspaceFile>(files.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in workspace.SourceOrder)
        {
            var path = NormalizeRelativePath(item);
            if (!seen.Add(path) || !files.TryGetValue(path, out var file))
                throw new FSharpBuildRequestValidationException("SourceOrder contains duplicate or unknown files.");
            ordered.Add(file);
        }
        var activeFile = NormalizeRelativePath(workspace.ActiveFile);
        if (!files.ContainsKey(activeFile))
            throw new FSharpBuildRequestValidationException("ActiveFile must identify a workspace file.");

        ValidateOptions(request.EffectiveOptions);
        return new ValidatedFSharpWorkspace(workspace, ordered, activeFile, request.EffectiveOptions);
    }

    internal static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.Contains('\0'))
            throw new FSharpBuildRequestValidationException("Workspace path is invalid.");
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || Path.IsPathRooted(normalized) || normalized.Contains(':'))
            throw new FSharpBuildRequestValidationException("Workspace paths must be relative.");
        var segments = normalized.Split('/');
        if (segments.Any(static segment => segment.Length == 0 || segment is "." or ".."))
            throw new FSharpBuildRequestValidationException("Workspace paths cannot contain traversal segments.");
        return string.Join('/', segments);
    }

    private static void ValidateOptions(BuildOptions options)
    {
        if (options.OutputKind is not (BuildOutputKind.Console or BuildOutputKind.Library or BuildOutputKind.WindowsApplication))
        {
            throw new FSharpBuildRequestValidationException("F# supports console, library and Windows application outputs only.");
        }
        if (options.AllowUnsafe)
            throw new FSharpBuildRequestValidationException("F# does not expose the allowUnsafe option.");
        if (options.NullableContext is not (NullableContextMode.ProjectDefault or NullableContextMode.Disable))
            throw new FSharpBuildRequestValidationException("C# nullable context options are not applicable to F#.");
        if (options.LanguageVersion is not null && options.LanguageVersion is not ("default" or "latest" or "preview" or "8.0" or "9.0"))
        {
            throw new FSharpBuildRequestValidationException($"F# language version '{options.LanguageVersion}' is not allowed.");
        }
        if (options.PreprocessorSymbols is { Count: > 64 })
            throw new FSharpBuildRequestValidationException("At most 64 preprocessor symbols are allowed.");
        if (options.PreprocessorSymbols is null)
            return;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in options.PreprocessorSymbols)
        {
            if (!IdentifierPattern().IsMatch(symbol) || !seen.Add(symbol))
                throw new FSharpBuildRequestValidationException($"Preprocessor symbol '{symbol}' is invalid or duplicated.");
        }
    }
}
