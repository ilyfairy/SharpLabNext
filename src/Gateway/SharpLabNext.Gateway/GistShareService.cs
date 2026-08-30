using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SharpLabNext.Contracts;

namespace SharpLabNext.Gateway;

public sealed record GistLoadOverrides(string? TargetKey, string? BranchId, BuildConfiguration? BuildMode);

public sealed partial class GistShareService(IGitHubGistClient github)
{
    public const string MetadataFileName = "\u200B\u200B.sharplab.json";
    private const int MetadataVersion = 1;
    private const int MaximumFiles = 32;
    private const int MaximumFileBytes = 512 * 1024;
    private const int MaximumWorkspaceBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = ContractJson.CreateCanonicalSerializerOptions();

    public async Task<GistDocument> GetAsync(string id, GistLoadOverrides overrides, GitHubOAuthSession? session, CancellationToken cancellationToken)
    {
        ValidateGistId(id);
        var gist = await github.GetAsync(id, session?.AccessToken, cancellationToken).ConfigureAwait(false);
        return Parse(gist, overrides, session?.Login);
    }

    public async Task<GistDocument> CreateAsync(CreateGistRequest request, GitHubOAuthSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var files = PrepareFiles(request.Workspace);
        var gist = await github.CreateAsync(new GitHubGistWriteRequest(ValidateDescription(request.Description), request.IsPublic, files), session.AccessToken, cancellationToken).ConfigureAwait(false);
        return Parse(gist, new GistLoadOverrides(null, null, null), session.Login);
    }

    public async Task<GistDocument> UpdateAsync(string id, UpdateGistRequest request, GitHubOAuthSession session, CancellationToken cancellationToken)
    {
        ValidateGistId(id);
        ArgumentNullException.ThrowIfNull(request);
        var current = await github.GetAsync(id, session.AccessToken, cancellationToken).ConfigureAwait(false);
        if (!StringComparer.OrdinalIgnoreCase.Equals(current.OwnerLogin, session.Login))
            throw new GistAuthorizationException("Only the Gist owner can update this Gist.");

        var files = PrepareFiles(request.Workspace).ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal);
        foreach (var oldFile in RecognizedWorkspaceFiles(current))
        {
            if (!files.ContainsKey(oldFile))
                files[oldFile] = null;
        }
        var gist = await github.UpdateAsync(id, new GitHubGistWriteRequest(ValidateDescription(request.Description), null, files), session.AccessToken, cancellationToken).ConfigureAwait(false);
        return Parse(gist, new GistLoadOverrides(null, null, null), session.Login);
    }

    private static GistDocument Parse(GitHubGist gist, GistLoadOverrides overrides, string? authenticatedLogin)
    {
        var metadataEntry = gist.Files.FirstOrDefault(static item => IsMetadataFile(item.Key));
        GistWorkspaceState workspace;
        string sourceFormat;
        var warnings = new List<string>();
        if (metadataEntry.Value is not null && TryParseSharpLabNextMetadata(metadataEntry.Value.Content, out var metadata))
        {
            workspace = FromMetadata(gist, metadata!);
            sourceFormat = "sharplabnext-v1";
        }
        else
        {
            workspace = ParseLegacy(gist, metadataEntry.Value?.Content, overrides, warnings);
            sourceFormat = metadataEntry.Value is null ? "github-gist" : "sharplab-v1";
            warnings.Add("Imported a legacy GitHub/SharpLab Gist; saving creates SharpLabNext metadata.");
        }

        if (sourceFormat == "sharplabnext-v1")
            workspace = ApplyOverrides(workspace, overrides, warnings);
        ValidateWorkspace(workspace, requireResolvedProfiles: sourceFormat == "sharplabnext-v1");
        return new GistDocument(gist.Id, gist.HtmlUrl, gist.OwnerLogin, gist.IsPublic, authenticatedLogin is not null && StringComparer.OrdinalIgnoreCase.Equals(gist.OwnerLogin, authenticatedLogin), gist.Description, sourceFormat, workspace, warnings, gist.UpdatedAtUtc);
    }

    private static GistWorkspaceState FromMetadata(GitHubGist gist, SharpLabNextGistMetadata metadata)
    {
        if (metadata.Version != MetadataVersion || metadata.Files is null || metadata.Files.Count == 0)
            throw new GistValidationException("The SharpLabNext Gist metadata version is not supported.");
        var files = metadata.Files.Select(mapping =>
        {
            if (string.IsNullOrWhiteSpace(mapping.Path) || string.IsNullOrWhiteSpace(mapping.GistFile) || !gist.Files.TryGetValue(mapping.GistFile, out var file) || file.Content is null)
            {
                throw new GistValidationException("The SharpLabNext Gist metadata references a missing source file.");
            }
            return new GistSourceFile(mapping.Path, file.Content);
        }).ToArray();
        return new GistWorkspaceState(
            ContractSchemaVersions.WorkspaceSnapshot,
            metadata.LanguageId ?? throw new GistValidationException("Gist metadata omitted languageId."),
            metadata.ToolchainId,
            metadata.ReferenceSetId,
            metadata.OutputId ?? "compile-check",
            metadata.RuntimeId,
            metadata.BuildMode,
            metadata.ReleaseId,
            metadata.ActiveFile ?? files[0].Path,
            metadata.SourceOrder ?? files.Select(static file => file.Path).ToArray(),
            files,
            metadata.LegacyBranchId);
    }

    private static GistWorkspaceState ParseLegacy(GitHubGist gist, string? metadataContent, GistLoadOverrides overrides, List<string> warnings)
    {
        var candidates = gist.Files.Where(static item => IsLegacySourceFile(item.Key) && item.Value.Content is not null).Select(static item => new GistSourceFile(item.Key, item.Value.Content!)).ToArray();
        if (candidates.Length == 0)
            throw new GistValidationException("The Gist does not contain a supported source file.");

        string? target = null;
        string? branch = null;
        var mode = BuildConfiguration.Debug;
        if (!string.IsNullOrWhiteSpace(metadataContent))
        {
            try
            {
                using var document = JsonDocument.Parse(metadataContent, new JsonDocumentOptions { MaxDepth = 16 });
                target = StringProperty(document.RootElement, "target");
                branch = StringProperty(document.RootElement, "branch");
                mode = StringComparer.OrdinalIgnoreCase.Equals(StringProperty(document.RootElement, "mode"), "Release")
                    ? BuildConfiguration.Release : BuildConfiguration.Debug;
            }
            catch (JsonException)
            {
                warnings.Add("The legacy .sharplab.json file was invalid and its options were ignored.");
            }
        }

        var languageId = LanguageForPath(candidates[0].Path);
        var outputId = OutputForLegacyTarget(target);
        var workspace = new GistWorkspaceState(
            ContractSchemaVersions.WorkspaceSnapshot,
            languageId,
            null,
            null,
            outputId,
            null,
            mode,
            null,
            candidates[0].Path,
            candidates.Select(static file => file.Path).ToArray(),
            candidates,
            branch);
        return ApplyOverrides(workspace, overrides, warnings);
    }

    private static GistWorkspaceState ApplyOverrides(GistWorkspaceState workspace, GistLoadOverrides overrides, List<string> warnings)
    {
        var outputId = workspace.OutputId;
        if (!string.IsNullOrWhiteSpace(overrides.TargetKey) && overrides.TargetKey != "_")
        {
            outputId = OutputForLegacyTarget(overrides.TargetKey);
            warnings.Add("Applied the target override from the legacy Gist URL.");
        }
        var branch = workspace.LegacyBranchId;
        if (!string.IsNullOrWhiteSpace(overrides.BranchId) && overrides.BranchId != "_")
        {
            branch = overrides.BranchId;
            warnings.Add("Applied the toolchain branch override from the legacy Gist URL.");
        }
        if (overrides.BuildMode is not null)
            warnings.Add("Applied the build mode override from the legacy Gist URL.");
        return workspace with { OutputId = outputId, LegacyBranchId = branch, BuildMode = overrides.BuildMode ?? workspace.BuildMode };
    }

    private static Dictionary<string, string?> PrepareFiles(GistWorkspaceState workspace)
    {
        ValidateWorkspace(workspace, requireResolvedProfiles: true);
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        var mappings = new List<SharpLabNextGistFile>(workspace.Files.Count);
        for (var index = 0; index < workspace.Files.Count; index++)
        {
            var file = workspace.Files[index];
            var gistFile = CreateGistFileName(file.Path, index, result.Keys);
            result[gistFile] = file.Text;
            mappings.Add(new SharpLabNextGistFile(file.Path, gistFile));
        }
        var metadata = new SharpLabNextGistMetadata(
            MetadataVersion,
            "SharpLabNext",
            workspace.LanguageId,
            workspace.ToolchainId,
            workspace.ReferenceSetId,
            workspace.OutputId,
            workspace.RuntimeId,
            workspace.BuildMode,
            workspace.ReleaseId,
            workspace.ActiveFile,
            workspace.SourceOrder,
            mappings,
            workspace.LegacyBranchId);
        result[MetadataFileName] = JsonSerializer.Serialize(metadata, JsonOptions);
        return result;
    }

    private static void ValidateWorkspace(GistWorkspaceState workspace, bool requireResolvedProfiles)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.SchemaVersion != ContractSchemaVersions.WorkspaceSnapshot)
            throw new GistValidationException("The Gist workspace schema version is not supported.");
        ValidateId(workspace.LanguageId, "languageId");
        ValidateId(workspace.OutputId, "outputId");
        if (requireResolvedProfiles)
        {
            ValidateId(workspace.ToolchainId, "toolchainId");
            ValidateId(workspace.ReferenceSetId, "referenceSetId");
        }
        if (workspace.RuntimeId is not null)
            ValidateId(workspace.RuntimeId, "runtimeId");
        if (workspace.ReleaseId is { Length: > 128 })
            throw new GistValidationException("releaseId exceeds the 128 character limit.");
        if (workspace.LegacyBranchId is { } branchId &&
            (branchId.Length > 128 || branchId.Any(static character => char.IsControl(character))))
        {
            throw new GistValidationException("legacyBranchId is invalid or too long.");
        }
        if (workspace.Files is null || workspace.Files.Count is < 1 or > MaximumFiles)
            throw new GistValidationException($"A Gist workspace must contain between 1 and {MaximumFiles} files.");

        var files = new Dictionary<string, GistSourceFile>(StringComparer.Ordinal);
        var totalBytes = 0;
        foreach (var file in workspace.Files)
        {
            if (file is null || file.Text is null)
                throw new GistValidationException("Gist source files require text content.");
            var path = NormalizePath(file.Path);
            if (!StringComparer.Ordinal.Equals(path, file.Path))
                throw new GistValidationException("Gist workspace paths must use normalized forward slashes.");
            if (!files.TryAdd(path, file))
                throw new GistValidationException($"The Gist workspace contains duplicate path '{path}'.");
            var bytes = Encoding.UTF8.GetByteCount(file.Text);
            if (bytes > MaximumFileBytes)
                throw new GistValidationException($"Source file '{path}' exceeds the {MaximumFileBytes} byte limit.");
            totalBytes = checked(totalBytes + bytes);
            if (totalBytes > MaximumWorkspaceBytes)
                throw new GistValidationException($"The Gist workspace exceeds the {MaximumWorkspaceBytes} byte limit.");
        }
        if (workspace.SourceOrder is null || workspace.SourceOrder.Count != files.Count)
            throw new GistValidationException("sourceOrder must contain every Gist source file exactly once.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in workspace.SourceOrder)
        {
            var path = NormalizePath(item);
            if (!StringComparer.Ordinal.Equals(path, item))
                throw new GistValidationException("sourceOrder paths must use normalized forward slashes.");
            if (!seen.Add(path) || !files.ContainsKey(path))
                throw new GistValidationException("sourceOrder contains a duplicate or unknown path.");
        }
        var activeFile = NormalizePath(workspace.ActiveFile);
        if (!StringComparer.Ordinal.Equals(activeFile, workspace.ActiveFile))
            throw new GistValidationException("activeFile must use normalized forward slashes.");
        if (!files.ContainsKey(activeFile))
            throw new GistValidationException("activeFile must identify a Gist source file.");
    }

    private static IEnumerable<string> RecognizedWorkspaceFiles(GitHubGist gist)
    {
        var metadataEntry = gist.Files.FirstOrDefault(static item => IsMetadataFile(item.Key));
        if (metadataEntry.Value is not null && TryParseSharpLabNextMetadata(metadataEntry.Value.Content, out var metadata))
        {
            foreach (var mapping in metadata!.Files ?? [])
                yield return mapping.GistFile;
        }
        else
        {
            foreach (var file in gist.Files.Keys.Where(IsLegacySourceFile))
                yield return file;
        }
        foreach (var file in gist.Files.Keys.Where(IsMetadataFile))
            yield return file;
    }

    private static bool TryParseSharpLabNextMetadata(string? content, out SharpLabNextGistMetadata? metadata)
    {
        metadata = null;
        if (string.IsNullOrWhiteSpace(content))
            return false;
        try
        {
            metadata = JsonSerializer.Deserialize<SharpLabNextGistMetadata>(content, JsonOptions);
            return metadata is not null && StringComparer.Ordinal.Equals(metadata.Product, "SharpLabNext");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string CreateGistFileName(string workspacePath, int index, IEnumerable<string> existing)
    {
        var existingSet = existing.ToHashSet(StringComparer.Ordinal);
        var name = workspacePath.Contains('/') || !SimpleGistFileName().IsMatch(workspacePath)
            ? $"{index + 1:D2}-{SanitizeFileName(workspacePath.Split('/')[^1])}" : workspacePath;
        if (IsMetadataFile(name) || existingSet.Contains(name))
            name = $"{index + 1:D2}-{SanitizeFileName(name)}";
        var suffix = 2;
        var candidate = name;
        while (existingSet.Contains(candidate) || IsMetadataFile(candidate))
            candidate = $"{Path.GetFileNameWithoutExtension(name)}-{suffix++}{Path.GetExtension(name)}";
        return candidate;
    }

    private static string SanitizeFileName(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, 120));
        foreach (var character in value)
        {
            if (builder.Length >= 120)
                break;
            builder.Append(char.IsLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_');
        }
        return builder.Length == 0 ? "source.txt" : builder.ToString();
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.Contains('\0'))
            throw new GistValidationException("Gist workspace paths are invalid or too long.");
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || Path.IsPathRooted(normalized) || normalized.Contains(':'))
            throw new GistValidationException("Gist workspace paths must be relative.");
        if (normalized.Split('/').Any(static segment => segment.Length == 0 || segment is "." or ".."))
            throw new GistValidationException("Gist workspace paths must be normalized and cannot traverse directories.");
        return normalized;
    }

    private static string ValidateDescription(string? description)
    {
        if (description is null)
            return string.Empty;
        if (description.Length > 256)
            throw new GistValidationException("The Gist description exceeds the 256 character limit.");
        return description;
    }

    private static void ValidateId(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || !ProfileId().IsMatch(value))
            throw new GistValidationException($"{name} is not a valid profile ID.");
    }

    private static void ValidateGistId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !GistId().IsMatch(id))
            throw new GistValidationException("The GitHub Gist ID is invalid.");
    }

    private static string LanguageForPath(string path)
    {
        if (path.EndsWith(".vb", StringComparison.OrdinalIgnoreCase)) return "visual-basic";
        if (path.EndsWith(".fs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".fsx", StringComparison.OrdinalIgnoreCase)) return "fsharp";
        if (path.EndsWith(".php", StringComparison.OrdinalIgnoreCase)) return "php";
        if (path.EndsWith(".il", StringComparison.OrdinalIgnoreCase)) return "il";
        return "csharp";
    }

    private static string OutputForLegacyTarget(string? target) => target?.Trim().ToLowerInvariant() switch
    {
        "il" => "il",
        "asm" or "jit asm" => "jit-asm",
        "ast" => "ast",
        "run" => "run",
        "run-il" or "run il" => "run-il",
        "verify" => "il-verify",
        "explain" => "explain",
        "vb" or "visual basic" => "compile-check",
        "cs" or "c#" or null or "" => "decompiled-csharp",
        _ => "compile-check"
    };

    private static bool IsMetadataFile(string path) =>
        path.EndsWith(".sharplab.json", StringComparison.OrdinalIgnoreCase);

    private static bool IsLegacySourceFile(string path)
    {
        if ((path.Length > 0 && path[0] == '\u200B') || IsMetadataFile(path))
            return false;
        if (path.EndsWith(".decompiled.cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".explained.json", StringComparison.OrdinalIgnoreCase))
            return false;
        return path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".vb", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".fs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".fsx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".php", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".il", StringComparison.OrdinalIgnoreCase);
    }

    private static string? StringProperty(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    [GeneratedRegex("^[0-9a-fA-F]{5,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex GistId();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProfileId();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 ._+()#@-]{0,199}$", RegexOptions.CultureInvariant)]
    private static partial Regex SimpleGistFileName();

    private sealed record SharpLabNextGistMetadata(
        int Version,
        string? Product,
        string? LanguageId,
        string? ToolchainId,
        string? ReferenceSetId,
        string? OutputId,
        string? RuntimeId,
        BuildConfiguration BuildMode,
        string? ReleaseId,
        string? ActiveFile,
        IReadOnlyList<string>? SourceOrder,
        IReadOnlyList<SharpLabNextGistFile>? Files,
        string? LegacyBranchId);

    private sealed record SharpLabNextGistFile(string Path, string GistFile);
}

public sealed class GistValidationException(string message) : Exception(message);

public sealed class GistAuthorizationException(string message) : Exception(message);
