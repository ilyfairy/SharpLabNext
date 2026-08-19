using SharpLabNext.Catalog;
using SharpLabNext.Contracts;
using SharpLabNext.PipelineResolver;
using Resolver = SharpLabNext.PipelineResolver.PipelineResolver;

namespace SharpLabNext.CompatibilityCli;

public enum CompatibilityDisposition
{
    Supported,
    Unavailable,
    Normalized,
    Rejected
}

public sealed record CompatibilityMatrixEntry(
    string LanguageId,
    string ToolchainId,
    string ReferenceSetId,
    string OutputId,
    string? RuntimeId,
    CompatibilityDisposition Disposition,
    ResolvedSelection? EffectiveSelection,
    IReadOnlyList<SelectionChange> Changes,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<PipelineStageDescriptor> Stages);

public sealed record CompatibilityAuditReport(
    int SchemaVersion,
    string CatalogRevision,
    string ReleaseId,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<string> Issues,
    IReadOnlyList<CompatibilityMatrixEntry> Matrix)
{
    public bool IsValid => Issues.Count == 0;
}

public static class CompatibilityAuditor
{
    public static CompatibilityAuditReport Audit(
        CatalogDocument catalog,
        ReleaseLockDocument releaseLock,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(releaseLock);
        var issues = new List<string>();
        if (!string.Equals(catalog.ReleaseId, releaseLock.ReleaseId, StringComparison.Ordinal))
        {
            issues.Add($"Catalog release '{catalog.ReleaseId}' does not match lock release '{releaseLock.ReleaseId}'.");
        }

        var matrix = Enumerate(catalog, now);
        ValidatePresets(catalog, now, issues);
        ValidateCompileChecks(catalog, matrix, issues);
        ValidateRuntimeEdges(catalog, matrix, issues);
        ValidateProcessorEdges(catalog, matrix, issues);
        ValidateCrossVersionGoals(catalog, matrix, issues);
        return new CompatibilityAuditReport(
            1,
            catalog.Revision,
            catalog.ReleaseId,
            now,
            issues.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            matrix);
    }

    public static IReadOnlyList<CompatibilityMatrixEntry> Enumerate(
        CatalogDocument catalog,
        DateTimeOffset now)
    {
        var entries = new List<CompatibilityMatrixEntry>();
        foreach (var toolchain in catalog.Toolchains.OrderBy(static item => item.Id, StringComparer.Ordinal))
        {
            foreach (var languageId in toolchain.SupportedLanguageIds.Order(StringComparer.Ordinal))
            {
                foreach (var referenceSetId in toolchain.AllowedReferenceSetIds.Order(StringComparer.Ordinal))
                {
                    foreach (var output in catalog.Outputs.OrderBy(static item => item.Id, StringComparer.Ordinal))
                    {
                        if (output.RequiresRuntime)
                        {
                            foreach (var runtime in catalog.Runtimes.OrderBy(static item => item.Id, StringComparer.Ordinal))
                            {
                                entries.Add(Resolve(
                                    catalog,
                                    languageId,
                                    toolchain.Id,
                                    referenceSetId,
                                    output.Id,
                                    runtime.Id,
                                    now));
                            }
                        }
                        else
                        {
                            entries.Add(Resolve(
                                catalog,
                                languageId,
                                toolchain.Id,
                                referenceSetId,
                                output.Id,
                                null,
                                now));
                        }
                    }
                }
            }
        }

        return entries;
    }

    private static CompatibilityMatrixEntry Resolve(
        CatalogDocument catalog,
        string languageId,
        string toolchainId,
        string referenceSetId,
        string outputId,
        string? runtimeId,
        DateTimeOffset now)
    {
        try
        {
            var response = Resolver.Resolve(
                catalog,
                new ResolveSelectionRequest(
                    languageId,
                    toolchainId,
                    referenceSetId,
                    outputId,
                    runtimeId,
                    BuildConfiguration.Release,
                    catalog.Revision,
                    1),
                now);
            var exact = response.EffectiveSelection.LanguageId == languageId &&
                        response.EffectiveSelection.ToolchainId == toolchainId &&
                        response.EffectiveSelection.ReferenceSetId == referenceSetId &&
                        response.EffectiveSelection.OutputId == outputId &&
                        response.EffectiveSelection.RuntimeId == runtimeId;
            var unavailable = response.SelectionChanges.Any(static change =>
                change.Reason == SelectionChangeReason.ProfileUnavailable);
            var disposition = unavailable
                ? CompatibilityDisposition.Unavailable
                : exact
                    ? CompatibilityDisposition.Supported
                    : CompatibilityDisposition.Normalized;
            return new CompatibilityMatrixEntry(
                languageId,
                toolchainId,
                referenceSetId,
                outputId,
                runtimeId,
                disposition,
                response.EffectiveSelection,
                response.SelectionChanges,
                null,
                null,
                response.PipelinePlan.Stages);
        }
        catch (SelectionResolutionException exception)
        {
            return new CompatibilityMatrixEntry(
                languageId,
                toolchainId,
                referenceSetId,
                outputId,
                runtimeId,
                CompatibilityDisposition.Rejected,
                null,
                [],
                exception.Code,
                exception.Message,
                []);
        }
    }

    private static void ValidatePresets(
        CatalogDocument catalog,
        DateTimeOffset now,
        List<string> issues)
    {
        foreach (var preset in catalog.Presets.Where(static item => item.Availability.IsSelectable))
        {
            try
            {
                var response = Resolver.Resolve(
                    catalog,
                    new ResolveSelectionRequest(
                        preset.LanguageId,
                        preset.ToolchainId,
                        preset.ReferenceSetId,
                        preset.DefaultOutputId,
                        preset.DefaultRuntimeId,
                        BuildConfiguration.Release,
                        catalog.Revision,
                        1),
                    now);
                if (response.EffectiveSelection.LanguageId != preset.LanguageId ||
                    response.EffectiveSelection.ToolchainId != preset.ToolchainId ||
                    response.EffectiveSelection.ReferenceSetId != preset.ReferenceSetId ||
                    response.EffectiveSelection.OutputId != preset.DefaultOutputId ||
                    response.SelectionChanges.Any(static change => change.Reason == SelectionChangeReason.ProfileUnavailable))
                {
                    issues.Add($"Selectable preset '{preset.Id}' does not resolve to its declared healthy selection.");
                }
            }
            catch (SelectionResolutionException exception)
            {
                issues.Add($"Selectable preset '{preset.Id}' is rejected: {exception.Message}");
            }
        }
    }

    private static void ValidateCompileChecks(
        CatalogDocument catalog,
        IReadOnlyList<CompatibilityMatrixEntry> matrix,
        List<string> issues)
    {
        foreach (var toolchain in catalog.Toolchains.Where(static item => item.Availability.IsSelectable))
        {
            foreach (var languageId in toolchain.SupportedLanguageIds)
            {
                foreach (var referenceSetId in toolchain.AllowedReferenceSetIds)
                {
                    var referenceSet = catalog.ReferenceSets.Single(item => item.Id == referenceSetId);
                    if (!referenceSet.Availability.IsSelectable || !HasReferenceEdge(catalog, toolchain.Id, referenceSetId))
                    {
                        continue;
                    }

                    if (!matrix.Any(entry =>
                            entry.LanguageId == languageId &&
                            entry.ToolchainId == toolchain.Id &&
                            entry.ReferenceSetId == referenceSetId &&
                            entry.OutputId == "compile-check" &&
                            entry.Disposition == CompatibilityDisposition.Supported))
                    {
                        issues.Add(
                            $"Healthy compiler selection '{languageId}/{toolchain.Id}/{referenceSetId}' cannot perform Compile Check.");
                    }
                }
            }
        }
    }

    private static void ValidateRuntimeEdges(
        CatalogDocument catalog,
        IReadOnlyList<CompatibilityMatrixEntry> matrix,
        List<string> issues)
    {
        foreach (var runtime in catalog.Runtimes.Where(static item => item.Availability.IsSelectable))
        {
            if (runtime.Capabilities.Contains("run", StringComparer.Ordinal) &&
                !matrix.Any(entry =>
                    entry.RuntimeId == runtime.Id &&
                    entry.OutputId == "run" &&
                    entry.Disposition == CompatibilityDisposition.Supported))
            {
                issues.Add($"Healthy runtime '{runtime.Id}' is not reachable by any Run pipeline.");
            }

            if (runtime.Capabilities.Contains("jit-asm", StringComparer.Ordinal) &&
                !matrix.Any(entry =>
                    entry.RuntimeId == runtime.Id &&
                    entry.OutputId == "jit-asm" &&
                    entry.Disposition == CompatibilityDisposition.Supported))
            {
                issues.Add($"Healthy runtime '{runtime.Id}' is not reachable by any JIT ASM pipeline.");
            }
        }
    }

    private static void ValidateProcessorEdges(
        CatalogDocument catalog,
        IReadOnlyList<CompatibilityMatrixEntry> matrix,
        List<string> issues)
    {
        foreach (var processor in catalog.ArtifactProcessors.Where(static item => item.Availability.IsSelectable))
        {
            foreach (var capability in processor.Capabilities.Where(static value =>
                         value is "il" or "decompiled-csharp" or "il-verify" or "run-il"))
            {
                if (!matrix.Any(entry =>
                        entry.OutputId == capability &&
                        entry.Disposition == CompatibilityDisposition.Supported &&
                        entry.Stages.Any(stage => stage.ProviderId == processor.WorkerId)))
                {
                    issues.Add($"Healthy processor '{processor.Id}' capability '{capability}' is not reachable.");
                }
            }
        }
    }

    private static void ValidateCrossVersionGoals(
        CatalogDocument catalog,
        IReadOnlyList<CompatibilityMatrixEntry> matrix,
        List<string> issues)
    {
        var runEntries = matrix.Where(static entry =>
            entry.OutputId == "run" && entry.Disposition == CompatibilityDisposition.Supported).ToArray();
        var compilerAcrossRuntimes = runEntries
            .GroupBy(static entry => new { entry.LanguageId, entry.ToolchainId, entry.ReferenceSetId })
            .Any(group => group.Select(static entry => entry.RuntimeId).Distinct(StringComparer.Ordinal).Count() >= 2);
        if (catalog.Runtimes.Count(static item => item.Availability.IsSelectable) >= 2 && !compilerAcrossRuntimes)
        {
            issues.Add("No healthy compiler/reference selection can run on two compatible runtimes.");
        }

        var runtimeAcrossCompilers = runEntries
            .GroupBy(static entry => entry.RuntimeId, StringComparer.Ordinal)
            .Any(group => group.Select(static entry => entry.ToolchainId).Distinct(StringComparer.Ordinal).Count() >= 2);
        if (catalog.Toolchains.Count(static item => item.Availability.IsSelectable) >= 2 && !runtimeAcrossCompilers)
        {
            issues.Add("No healthy runtime accepts artifacts from two compiler toolchains.");
        }
    }

    private static bool HasReferenceEdge(CatalogDocument catalog, string toolchainId, string referenceSetId) =>
        catalog.Compatibility.Any(rule =>
            rule.Kind == CompatibilityRuleKind.ToolchainReferenceSet &&
            rule.Allowed &&
            rule.FromId == toolchainId &&
            rule.ToId == referenceSetId);
}
