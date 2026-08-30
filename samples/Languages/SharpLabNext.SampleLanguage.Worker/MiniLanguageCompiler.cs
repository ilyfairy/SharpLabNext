using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpLabNext.Contracts;

namespace SharpLabNext.SampleLanguage.Worker;

public static partial class MiniLanguageCompiler
{
    public const string LanguageId = "minilang";
    public const string ToolchainId = "minilang-stable";
    public const string Version = "1.0.0";
    public const string DefaultFileName = "Program.mini";
    public const string ArtifactFormat = "cil-text-v1";
    public const string GeneratedFileName = "Program.il";

    public static MiniLanguageCompilation Compile(WorkspaceSnapshot workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var diagnostics = new List<Diagnostic>();
        var values = new List<string>();
        var files = workspace.Files.ToDictionary(static file => file.Path, StringComparer.Ordinal);
        var sourceOrder = workspace.SourceOrder.Count > 0
            ? workspace.SourceOrder : workspace.Files.Select(static file => file.Path).ToArray();

        foreach (var path in sourceOrder)
        {
            if (!files.TryGetValue(path, out var file))
            {
                diagnostics.Add(CreateDiagnostic("MINI1003", $"Source order refers to missing file '{path}'.", path, null, workspace));
                continue;
            }
            ParseFile(file, workspace, values, diagnostics);
        }

        var cil = diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            ? null : EmitCil(values, workspace.BuildOptions.OutputKind == BuildOutputKind.Console);
        return new MiniLanguageCompilation(diagnostics, cil);
    }

    public static IReadOnlyList<Diagnostic> GetDiagnostics(string path, string text, long workspaceRevision, long selectionRevision)
    {
        var options = new BuildOptions(BuildConfiguration.Release, Optimize: true, BuildOutputKind.Console, AllowUnsafe: false, EmitPortablePdb: false);
        var workspace = new WorkspaceSnapshot(ContractSchemaVersions.WorkspaceSnapshot, workspaceRevision, selectionRevision, LanguageId, [new WorkspaceFile(path, workspaceRevision, text)], path, [path], "net10-ref", options);
        return Compile(workspace).Diagnostics;
    }

    private static void ParseFile(WorkspaceFile file, WorkspaceSnapshot workspace, List<string> values, List<Diagnostic> diagnostics)
    {
        var lines = file.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var match = PrintStatementRegex().Match(line);
            if (!match.Success)
            {
                diagnostics.Add(CreateDiagnostic("MINI1001", "Expected a statement in the form: print \"text\".", file.Path, new TextRange(lineIndex, 0, lineIndex, line.Length), workspace));
                continue;
            }

            try
            {
                values.Add(JsonSerializer.Deserialize<string>(match.Groups["literal"].Value) ?? string.Empty);
            }
            catch (JsonException)
            {
                diagnostics.Add(CreateDiagnostic("MINI1002", "The print argument is not a valid quoted string.", file.Path, new TextRange(lineIndex, 0, lineIndex, line.Length), workspace));
            }
        }
    }

    private static string EmitCil(IReadOnlyList<string> values, bool includeEntryPoint)
    {
        var cil = new StringBuilder();
        cil.AppendLine(".assembly extern System.Runtime {}").AppendLine(".assembly extern System.Console {}").AppendLine(".assembly MiniLanguageProgram {}").AppendLine(".module MiniLanguageProgram.dll").AppendLine().AppendLine(".class public auto ansi abstract sealed Program extends [System.Runtime]System.Object").AppendLine("{").AppendLine("  .method public hidebysig static void Main() cil managed").AppendLine("  {").AppendLine("    .maxstack 1");
        if (includeEntryPoint)
            cil.AppendLine("    .entrypoint");
        foreach (var value in values)
        {
            cil.Append("    ldstr \"").Append(EscapeCilString(value)).AppendLine("\"").AppendLine("    call void [System.Console]System.Console::WriteLine(string)");
        }
        cil.AppendLine("    ret").AppendLine("  }").AppendLine("}");
        return cil.ToString();
    }

    private static string EscapeCilString(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);

    private static Diagnostic CreateDiagnostic(string code, string message, string? path, TextRange? range, WorkspaceSnapshot workspace) => new("minilang", code, DiagnosticSeverity.Error, message, path, range, [], [], workspace.Revision, workspace.SelectionRevision);

    [GeneratedRegex("^\\s*print\\s+(?<literal>\"(?:[^\"\\\\]|\\\\[\"\\\\/bfnrt]|\\\\u[0-9a-fA-F]{4})*\")\\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex PrintStatementRegex();
}

public sealed record MiniLanguageCompilation(IReadOnlyList<Diagnostic> Diagnostics, string? GeneratedCil)
{
    public bool Succeeded => GeneratedCil is not null;
}
