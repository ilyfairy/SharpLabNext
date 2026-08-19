using System.Text;
using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Sdk;

namespace SharpLabNext.Worker.GSharp;

internal sealed record ValidatedGSharpWorkspace(
    WorkspaceSnapshot Snapshot,
    IReadOnlyList<ValidatedGSharpWorkspaceFile> OrderedFiles,
    BuildOptions Options);

internal sealed record ValidatedGSharpWorkspaceFile(string Path, long Version, string Text);

internal static class GSharpWorkspaceValidator
{
    public static ValidatedGSharpWorkspace Validate(
        BuildRequest request,
        LanguageWorkerCapabilityManifest manifest,
        GSharpToolchainProfile toolchain)
    {
        ArgumentNullException.ThrowIfNull(request);
        var workspace = request.Workspace;
        if (request.Target is not (BuildTarget.Artifact or BuildTarget.CompileCheck))
            throw Invalid("unsupported-target", "G# supports Artifact and Compile Check builds.");
        if (!StringComparer.Ordinal.Equals(request.ToolchainId, toolchain.ToolchainId) ||
            !manifest.ToolchainIds.Contains(request.ToolchainId, StringComparer.Ordinal) ||
            !StringComparer.Ordinal.Equals(workspace.LanguageId, GSharpToolchain.LanguageId))
        {
            throw Invalid("wrong-toolchain", "The request does not target the G# worker.");
        }
        if (workspace.Files.Count is 0 || workspace.Files.Count > manifest.Limits.MaximumFiles)
            throw Invalid("invalid-workspace", $"A G# workspace must contain between 1 and {manifest.Limits.MaximumFiles} files.");

        var files = new Dictionary<string, ValidatedGSharpWorkspaceFile>(StringComparer.Ordinal);
        var totalBytes = 0;
        foreach (var file in workspace.Files)
        {
            if (file.Version < 0)
                throw Invalid("invalid-workspace", "G# file versions cannot be negative.");
            var path = NormalizeRelativePath(file.Path);
            if (!path.EndsWith(".gs", StringComparison.OrdinalIgnoreCase))
                throw Invalid("invalid-workspace", $"G# file '{path}' must use the .gs extension.");
            if (!files.TryAdd(path, new ValidatedGSharpWorkspaceFile(path, file.Version, file.Text)))
                throw Invalid("invalid-workspace", $"The G# workspace contains duplicate path '{path}'.");
            totalBytes = checked(totalBytes + Encoding.UTF8.GetByteCount(file.Text));
            if (totalBytes > manifest.Limits.MaximumSourceUtf8Bytes)
                throw Invalid("workspace-too-large", "The G# workspace exceeds the configured source limit.", StatusCodes.Status413PayloadTooLarge);
        }

        if (workspace.SourceOrder.Count != files.Count)
            throw Invalid("invalid-workspace", "SourceOrder must contain every G# file exactly once.");
        var ordered = new List<ValidatedGSharpWorkspaceFile>(files.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in workspace.SourceOrder)
        {
            var path = NormalizeRelativePath(item);
            if (!seen.Add(path) || !files.TryGetValue(path, out var file))
                throw Invalid("invalid-workspace", "SourceOrder contains a duplicate or unknown G# file.");
            ordered.Add(file);
        }
        if (!files.ContainsKey(NormalizeRelativePath(workspace.ActiveFile)))
            throw Invalid("invalid-workspace", "ActiveFile must identify a G# workspace file.");

        ValidateOptions(request.EffectiveOptions, toolchain.CompilerVersion);
        return new ValidatedGSharpWorkspace(workspace, ordered, request.EffectiveOptions);
    }

    public static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.Contains('\0'))
            throw Invalid("invalid-workspace", "A G# workspace path is invalid.");
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || Path.IsPathRooted(normalized) || normalized.Contains(':'))
            throw Invalid("invalid-workspace", "G# workspace paths must be relative.");
        var segments = normalized.Split('/');
        if (segments.Any(static segment => segment.Length == 0 || segment is "." or ".."))
            throw Invalid("invalid-workspace", "G# workspace paths cannot contain traversal segments.");
        return string.Join('/', segments);
    }

    private static void ValidateOptions(BuildOptions options, string compilerVersion)
    {
        ValidateOutputKind(options.OutputKind);
        if (options.AllowUnsafe)
            throw Invalid("unsupported-option", "G# does not expose the C# allowUnsafe option.");
        if (options.NullableContext is not (NullableContextMode.ProjectDefault or NullableContextMode.Disable))
            throw Invalid("unsupported-option", "C# nullable context options do not apply to G#.");
        var featureVersion = GSharpCompilerIdentity.GetFeatureVersion(compilerVersion);
        if (options.LanguageVersion is not null &&
            options.LanguageVersion is not "default" &&
            !string.Equals(options.LanguageVersion, featureVersion, StringComparison.Ordinal) &&
            !string.Equals(options.LanguageVersion, compilerVersion, StringComparison.Ordinal))
            throw Invalid("unsupported-option", $"G# language version '{options.LanguageVersion}' is not supported.");
        if (options.PreprocessorSymbols is { Count: > 0 })
            throw Invalid("unsupported-option", "G# does not expose C# preprocessor symbols.");
        if (options.CheckOverflow)
            throw Invalid("unsupported-option", "G# does not expose a project-level overflow option.");
    }

    internal static void ValidateOutputKind(BuildOutputKind outputKind)
    {
        if (outputKind is not (BuildOutputKind.Auto or BuildOutputKind.Console or BuildOutputKind.Library))
            throw Invalid("unsupported-option", "G# supports automatic, console, and library outputs only.");
    }

    private static LanguageWorkerRequestException Invalid(
        string code,
        string message,
        int statusCode = StatusCodes.Status400BadRequest) => new(code, message, statusCode);
}
