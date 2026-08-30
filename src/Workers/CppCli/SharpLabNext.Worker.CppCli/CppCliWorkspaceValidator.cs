using System.Text;
using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Sdk;

namespace SharpLabNext.Worker.CppCli;

internal sealed record ValidatedCppCliWorkspace(WorkspaceSnapshot Snapshot, ValidatedCppCliWorkspaceFile SourceFile, BuildOptions Options);

internal sealed record ValidatedCppCliWorkspaceFile(string Path, long Version, string Text);

internal static class CppCliWorkspaceValidator
{
    public static ValidatedCppCliWorkspace Validate(BuildRequest request, LanguageWorkerCapabilityManifest manifest, string compilerVersion)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.PipelineResolutionId))
            throw Invalid("invalid-request", "Pipeline resolution ID is required.");
        if (request.Target is not (BuildTarget.Artifact or BuildTarget.CompileCheck))
            throw Invalid("unsupported-target", "C++/CLI supports Artifact and Compile Check builds.");
        if (!StringComparer.Ordinal.Equals(request.ToolchainId, CppCliToolchain.ToolchainId) || !StringComparer.Ordinal.Equals(request.Workspace.LanguageId, CppCliToolchain.LanguageId))
        {
            throw Invalid("wrong-toolchain", "The request does not target the C++/CLI worker.");
        }
        if (!StringComparer.Ordinal.Equals(request.ReferenceSetId, CppCliToolchain.ReferenceSetId) || !StringComparer.Ordinal.Equals(request.Workspace.ReferenceSetId, CppCliToolchain.ReferenceSetId))
        {
            throw Invalid("unsupported-reference-set", "C++/CLI requires the .NET Framework 4.8 reference set.");
        }

        var workspace = request.Workspace;
        if (workspace.SchemaVersion != ContractSchemaVersions.WorkspaceSnapshot || workspace.Revision < 0 || workspace.SelectionRevision < 0)
        {
            throw Invalid("invalid-workspace", "The C++/CLI workspace metadata is invalid.");
        }
        if (workspace.Files.Count != 1 || workspace.Files.Count > manifest.Limits.MaximumFiles)
            throw Invalid("invalid-workspace", "A C++/CLI workspace must contain exactly one source file.");

        var source = workspace.Files[0];
        if (source.Version < 0)
            throw Invalid("invalid-workspace", "C++/CLI file versions cannot be negative.");
        var path = NormalizeRelativePath(source.Path);
        if (!path.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase))
            throw Invalid("invalid-workspace", "The C++/CLI source file must use the .cpp extension.");
        if (Encoding.UTF8.GetByteCount(source.Text) > manifest.Limits.MaximumSourceUtf8Bytes)
            throw Invalid("workspace-too-large", "The C++/CLI source exceeds the configured limit.", StatusCodes.Status413PayloadTooLarge);
        ValidateCompilerFileAccess(source.Text);
        if (workspace.SourceOrder.Count != 1 || !StringComparer.Ordinal.Equals(NormalizeRelativePath(workspace.SourceOrder[0]), path) || !StringComparer.Ordinal.Equals(NormalizeRelativePath(workspace.ActiveFile), path))
        {
            throw Invalid("invalid-workspace", "SourceOrder and ActiveFile must identify the C++/CLI source file.");
        }

        ValidateOptions(request.EffectiveOptions, compilerVersion);
        return new ValidatedCppCliWorkspace(workspace, new ValidatedCppCliWorkspaceFile(path, source.Version, source.Text), request.EffectiveOptions);
    }

    internal static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.Contains('\0'))
            throw Invalid("invalid-workspace", "A C++/CLI workspace path is invalid.");
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || Path.IsPathRooted(normalized) || normalized.Contains(':'))
            throw Invalid("invalid-workspace", "C++/CLI workspace paths must be relative.");
        var segments = normalized.Split('/');
        if (segments.Any(static segment => segment.Length == 0 || segment is "." or ".."))
            throw Invalid("invalid-workspace", "C++/CLI workspace paths cannot contain traversal segments.");
        return string.Join('/', segments);
    }

    internal static void ValidateCompilerFileAccess(string source)
    {
        using var reader = new StringReader(source);
        while (reader.ReadLine() is { } line)
        {
            var directive = line.AsSpan().TrimStart();
            if (directive.StartsWith("%:", StringComparison.Ordinal) || directive.StartsWith("??=", StringComparison.Ordinal))
            {
                throw UnsafeDirective("Alternative preprocessor directive tokens are not supported.");
            }
            if (directive.IsEmpty || directive[0] != '#')
                continue;

            directive = directive[1..].TrimStart();
            var nameLength = 0;
            while (nameLength < directive.Length && (char.IsAsciiLetter(directive[nameLength]) || directive[nameLength] == '_'))
            {
                nameLength++;
            }
            if (nameLength == 0)
                throw UnsafeDirective("Obfuscated preprocessor directives are not supported.");

            var name = directive[..nameLength];
            var argument = directive[nameLength..].Trim();
            if (name.Equals("include", StringComparison.OrdinalIgnoreCase) || name.Equals("include_next", StringComparison.OrdinalIgnoreCase))
            {
                ValidateLiteralFileOperand(argument, "include", allowDirectories: true);
                continue;
            }
            if (name.Equals("using", StringComparison.OrdinalIgnoreCase))
            {
                ValidateLiteralFileOperand(argument, "using", allowDirectories: false);
                continue;
            }
            if (name.Equals("import", StringComparison.OrdinalIgnoreCase) || name.Equals("embed", StringComparison.OrdinalIgnoreCase))
            {
                throw UnsafeDirective($"#{name.ToString()} is not available in the isolated C++/CLI compiler.");
            }
            if (name.Equals("pragma", StringComparison.OrdinalIgnoreCase))
            {
                var pragma = FirstIdentifier(argument);
                if (pragma.Equals("comment", StringComparison.OrdinalIgnoreCase) || pragma.Equals("include_alias", StringComparison.OrdinalIgnoreCase))
                {
                    throw UnsafeDirective($"#pragma {pragma.ToString()} is not available in the isolated C++/CLI compiler.");
                }
            }
        }
    }

    private static void ValidateLiteralFileOperand(ReadOnlySpan<char> argument, string directive, bool allowDirectories)
    {
        if (argument.Length < 3 || argument[0] is not ('<' or '"'))
            throw UnsafeDirective($"#{directive} requires a literal, relative file name.");
        var closing = argument[0] == '<' ? '>' : '"';
        var closingIndex = argument[1..].IndexOf(closing);
        if (closingIndex < 1)
            throw UnsafeDirective($"#{directive} requires a bounded literal file name.");
        closingIndex++;
        if (!argument[(closingIndex + 1)..].TrimStart().IsEmpty &&
            !argument[(closingIndex + 1)..].TrimStart().StartsWith("//", StringComparison.Ordinal))
        {
            throw UnsafeDirective($"#{directive} contains an unsupported trailing token.");
        }

        var path = argument[1..closingIndex];
        if (path.IsEmpty || path.Length > 200 || path.Contains('\0') || path.Contains('\\') || path.Contains(':') || path[0] == '/' || path.ToString().Split('/').Any(static segment => segment is "" or "." or "..") || (!allowDirectories && path.Contains('/')))
        {
            throw UnsafeDirective($"#{directive} can only access a safe compiler-provided file name.");
        }
        foreach (var character in path)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-' or '+' or '/'))
            {
                throw UnsafeDirective($"#{directive} contains an unsupported file name.");
            }
        }
    }

    private static ReadOnlySpan<char> FirstIdentifier(ReadOnlySpan<char> value)
    {
        value = value.TrimStart();
        var length = 0;
        while (length < value.Length && (char.IsAsciiLetter(value[length]) || value[length] == '_'))
        {
            length++;
        }
        return value[..length];
    }

    private static LanguageWorkerRequestException UnsafeDirective(string message) =>
        Invalid("unsafe-source-directive", message);

    private static void ValidateOptions(BuildOptions options, string compilerVersion)
    {
        if (options.OutputKind != BuildOutputKind.Console)
            throw Invalid("unsupported-option", "C++/CLI currently supports console executables only.");
        if (options.AllowUnsafe)
            throw Invalid("unsupported-option", "The C# allowUnsafe option does not apply to C++/CLI.");
        if (options.NullableContext is not (NullableContextMode.ProjectDefault or NullableContextMode.Disable))
            throw Invalid("unsupported-option", "C# nullable context options do not apply to C++/CLI.");
        if (options.LanguageVersion is not null && options.LanguageVersion is not ("default" or "latest") && !StringComparer.Ordinal.Equals(options.LanguageVersion, compilerVersion))
        {
            throw Invalid("unsupported-option", $"C++/CLI compiler version '{options.LanguageVersion}' is not supported.");
        }
        if (options.PreprocessorSymbols is { Count: > 0 })
            throw Invalid("unsupported-option", "C++/CLI preprocessor symbols are not exposed yet.");
        if (options.CheckOverflow)
            throw Invalid("unsupported-option", "The C# overflow option does not apply to C++/CLI.");
    }

    private static LanguageWorkerRequestException Invalid(string code, string message, int statusCode = StatusCodes.Status400BadRequest) => new(code, message, statusCode);
}
