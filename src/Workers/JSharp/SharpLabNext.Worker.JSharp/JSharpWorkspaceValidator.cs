using System.Text;
using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Sdk;

namespace SharpLabNext.Worker.JSharp;

internal sealed record ValidatedJSharpWorkspace(WorkspaceSnapshot Snapshot, ValidatedJSharpWorkspaceFile SourceFile, BuildOptions Options);

internal sealed record ValidatedJSharpWorkspaceFile(string Path, long Version, string Text);

internal static class JSharpWorkspaceValidator
{
    public static ValidatedJSharpWorkspace Validate(BuildRequest request, LanguageWorkerCapabilityManifest manifest, string compilerVersion)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.PipelineResolutionId))
            throw Invalid("invalid-request", "Pipeline resolution ID is required.");
        if (request.Target is not (BuildTarget.Artifact or BuildTarget.CompileCheck))
            throw Invalid("unsupported-target", "J# supports Artifact and Compile Check builds.");
        if (!StringComparer.Ordinal.Equals(request.ToolchainId, JSharpToolchain.ToolchainId) || !StringComparer.Ordinal.Equals(request.Workspace.LanguageId, JSharpToolchain.LanguageId))
        {
            throw Invalid("wrong-toolchain", "The request does not target the J# worker.");
        }
        if (!StringComparer.Ordinal.Equals(request.ReferenceSetId, JSharpToolchain.ReferenceSetId) || !StringComparer.Ordinal.Equals(request.Workspace.ReferenceSetId, JSharpToolchain.ReferenceSetId))
        {
            throw Invalid("unsupported-reference-set", "J# requires the private CLR 2.0/J# reference set.");
        }

        var workspace = request.Workspace;
        if (workspace.SchemaVersion != ContractSchemaVersions.WorkspaceSnapshot || workspace.Revision < 0 || workspace.SelectionRevision < 0)
        {
            throw Invalid("invalid-workspace", "The J# workspace metadata is invalid.");
        }
        if (workspace.Files.Count != 1 || workspace.Files.Count > manifest.Limits.MaximumFiles)
            throw Invalid("invalid-workspace", "A J# workspace must contain exactly one source file.");

        var source = workspace.Files[0];
        if (source.Version < 0)
            throw Invalid("invalid-workspace", "J# file versions cannot be negative.");
        var path = NormalizeRelativePath(source.Path);
        if (!path.EndsWith(".jsl", StringComparison.OrdinalIgnoreCase))
            throw Invalid("invalid-workspace", "The J# source file must use the .jsl extension.");
        if (source.Text.Contains('\0'))
            throw Invalid("invalid-workspace", "The J# source contains a null character.");
        if (Encoding.UTF8.GetByteCount(source.Text) > manifest.Limits.MaximumSourceUtf8Bytes)
        {
            throw Invalid("workspace-too-large", "The J# source exceeds the configured limit.", StatusCodes.Status413PayloadTooLarge);
        }
        if (workspace.SourceOrder.Count != 1 || !StringComparer.Ordinal.Equals(NormalizeRelativePath(workspace.SourceOrder[0]), path) || !StringComparer.Ordinal.Equals(NormalizeRelativePath(workspace.ActiveFile), path))
        {
            throw Invalid("invalid-workspace", "SourceOrder and ActiveFile must identify the J# source file.");
        }

        ValidateOptions(request.EffectiveOptions, compilerVersion);
        return new ValidatedJSharpWorkspace(workspace, new ValidatedJSharpWorkspaceFile(path, source.Version, source.Text), request.EffectiveOptions);
    }

    internal static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.Contains('\0'))
            throw Invalid("invalid-workspace", "A J# workspace path is invalid.");
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || Path.IsPathRooted(normalized) || normalized.Contains(':'))
            throw Invalid("invalid-workspace", "J# workspace paths must be relative.");
        var segments = normalized.Split('/');
        if (segments.Any(static segment => segment.Length == 0 || segment is "." or ".."))
            throw Invalid("invalid-workspace", "J# workspace paths cannot contain traversal segments.");
        return string.Join('/', segments);
    }

    private static void ValidateOptions(BuildOptions options, string compilerVersion)
    {
        if (options.OutputKind != BuildOutputKind.Console)
            throw Invalid("unsupported-option", "J# currently supports console executables only.");
        if (options.AllowUnsafe)
            throw Invalid("unsupported-option", "The C# allowUnsafe option does not apply to J#.");
        if (options.EmitPortablePdb)
            throw Invalid("unsupported-option", "Portable PDB output is not available from the J# compiler.");
        if (options.NullableContext is not (NullableContextMode.ProjectDefault or NullableContextMode.Disable))
            throw Invalid("unsupported-option", "C# nullable context options do not apply to J#.");
        if (options.LanguageVersion is not null && options.LanguageVersion is not ("default" or "latest") && !StringComparer.Ordinal.Equals(options.LanguageVersion, compilerVersion))
        {
            throw Invalid("unsupported-option", $"J# compiler version '{options.LanguageVersion}' is not supported.");
        }
        if (options.PreprocessorSymbols is { Count: > 0 })
            throw Invalid("unsupported-option", "J# preprocessor symbols are not exposed.");
        if (options.CheckOverflow)
            throw Invalid("unsupported-option", "The C# overflow option does not apply to J#.");
    }

    private static LanguageWorkerRequestException Invalid(string code, string message, int statusCode = StatusCodes.Status400BadRequest) => new(code, message, statusCode);
}
