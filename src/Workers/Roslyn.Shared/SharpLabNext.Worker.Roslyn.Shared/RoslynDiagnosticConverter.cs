using System.Globalization;
using Microsoft.CodeAnalysis;
using SharpLabNext.Contracts;
using ContractDiagnostic = SharpLabNext.Contracts.Diagnostic;
using ContractDiagnosticSeverity = SharpLabNext.Contracts.DiagnosticSeverity;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;

namespace SharpLabNext.Worker.Roslyn;

internal static class RoslynDiagnosticConverter
{
    public static IReadOnlyList<ContractDiagnostic> Convert(IEnumerable<RoslynDiagnostic> diagnostics, long workspaceRevision, long selectionRevision, int maxDiagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        var converted = diagnostics.Select(diagnostic => Convert(diagnostic, workspaceRevision, selectionRevision)).DistinctBy(static diagnostic => new DiagnosticKey(diagnostic.Code, diagnostic.Severity, diagnostic.Message, diagnostic.FilePath, diagnostic.Range)).OrderBy(static diagnostic => diagnostic.FilePath, StringComparer.Ordinal).ThenBy(static diagnostic => diagnostic.Range?.StartLine ?? -1).ThenBy(static diagnostic => diagnostic.Range?.StartCharacter ?? -1).ThenBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal).ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal).ToArray();

        if (converted.Length <= maxDiagnostics)
            return converted;

        var limited = converted.Take(Math.Max(0, maxDiagnostics - 1)).ToList();
        limited.Add(new ContractDiagnostic("sharplabnext", "SLN1001", ContractDiagnosticSeverity.Warning, $"Diagnostic output was truncated at {maxDiagnostics} entries.", null, null, [], [], workspaceRevision, selectionRevision));
        return limited;
    }

    private static ContractDiagnostic Convert(RoslynDiagnostic diagnostic, long workspaceRevision, long selectionRevision)
    {
        var (filePath, range) = ConvertLocation(diagnostic.Location);
        var related = diagnostic.AdditionalLocations.Where(static location => location.IsInSource).Select(location =>
            {
                var (relatedPath, relatedRange) = ConvertLocation(location);
                return new DiagnosticRelatedInformation("Related location", relatedPath, relatedRange!);
            }).ToArray();

        var tags = new List<DiagnosticTag>(2);
        if (diagnostic.Descriptor.CustomTags.Contains(WellKnownDiagnosticTags.Unnecessary, StringComparer.Ordinal))
            tags.Add(DiagnosticTag.Unnecessary);
        if (diagnostic.Descriptor.CustomTags.Contains("Deprecated", StringComparer.Ordinal))
            tags.Add(DiagnosticTag.Deprecated);

        return new ContractDiagnostic(
            "roslyn",
            diagnostic.Id,
            diagnostic.Severity switch
            {
                Microsoft.CodeAnalysis.DiagnosticSeverity.Hidden => ContractDiagnosticSeverity.Hidden,
                Microsoft.CodeAnalysis.DiagnosticSeverity.Info => ContractDiagnosticSeverity.Information,
                Microsoft.CodeAnalysis.DiagnosticSeverity.Warning => ContractDiagnosticSeverity.Warning,
                Microsoft.CodeAnalysis.DiagnosticSeverity.Error => ContractDiagnosticSeverity.Error,
                _ => ContractDiagnosticSeverity.Information
            },
            diagnostic.GetMessage(CultureInfo.InvariantCulture),
            filePath,
            range,
            related,
            tags,
            workspaceRevision,
            selectionRevision);
    }

    private static (string? FilePath, TextRange? Range) ConvertLocation(Location location)
    {
        if (!location.IsInSource)
            return (null, null);

        var span = location.GetLineSpan();
        return (string.IsNullOrWhiteSpace(span.Path) ? null : span.Path.Replace('\\', '/'), new TextRange(span.StartLinePosition.Line, span.StartLinePosition.Character, span.EndLinePosition.Line, span.EndLinePosition.Character));
    }

    private sealed record DiagnosticKey(string Code, ContractDiagnosticSeverity Severity, string Message, string? FilePath, TextRange? Range);
}
