namespace SharpLabNext.Catalog;

public static class CatalogValidator
{
    public static void ValidateAndThrow(CatalogDocument catalog)
    {
        var errors = Validate(catalog);
        if (errors.Count > 0)
        {
            throw new CatalogValidationException(errors);
        }
    }

    public static IReadOnlyList<string> Validate(CatalogDocument catalog)
    {
        var errors = new List<string>();
        if (catalog.SchemaVersion != 1)
        {
            errors.Add($"Unsupported catalog schema version {catalog.SchemaVersion}.");
        }

        Require(catalog.Revision, "revision", errors);
        Require(catalog.ReleaseId, "releaseId", errors);

        var languages = Index(catalog.Languages, static item => item.Id, "language", errors);
        var toolchains = Index(catalog.Toolchains, static item => item.Id, "toolchain", errors);
        var referenceSets = Index(catalog.ReferenceSets, static item => item.Id, "reference set", errors);
        var runtimes = Index(catalog.Runtimes, static item => item.Id, "runtime", errors);
        var processors = Index(catalog.ArtifactProcessors, static item => item.Id, "artifact processor", errors);
        var outputs = Index(catalog.Outputs, static item => item.Id, "output", errors);
        _ = Index(catalog.Presets, static item => item.Id, "preset", errors);
        _ = Index(catalog.Compatibility, static item => item.Id, "compatibility rule", errors);

        foreach (var language in catalog.Languages)
        {
            if (!toolchains.ContainsKey(language.DefaultToolchainId))
            {
                errors.Add($"Language '{language.Id}' references missing default toolchain '{language.DefaultToolchainId}'.");
            }
        }

        foreach (var toolchain in catalog.Toolchains)
        {
            if (!referenceSets.ContainsKey(toolchain.DefaultReferenceSetId))
            {
                errors.Add($"Toolchain '{toolchain.Id}' references missing default reference set '{toolchain.DefaultReferenceSetId}'.");
            }

            AddMissing(toolchain.SupportedLanguageIds, languages, $"Toolchain '{toolchain.Id}' language", errors);
            AddMissing(toolchain.AllowedReferenceSetIds, referenceSets, $"Toolchain '{toolchain.Id}' reference set", errors);
        }

        foreach (var referenceSet in catalog.ReferenceSets)
        {
            ValidateLifecycle(
                referenceSet.SupportStatus,
                referenceSet.Visibility,
                $"Reference set '{referenceSet.Id}'",
                errors);
            if (referenceSet.ReplacementReferenceSetId is not { } replacementId)
                continue;
            if (string.Equals(referenceSet.Id, replacementId, StringComparison.Ordinal))
            {
                errors.Add($"Reference set '{referenceSet.Id}' cannot replace itself.");
                continue;
            }
            RequireReference(
                replacementId,
                referenceSets,
                $"Reference set '{referenceSet.Id}' replacement",
                errors);
        }

        foreach (var runtime in catalog.Runtimes)
        {
            ValidateLifecycle(
                runtime.SupportStatus,
                runtime.Visibility,
                $"Runtime '{runtime.Id}'",
                errors);
        }

        foreach (var processor in catalog.ArtifactProcessors)
        {
            var transformationIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var transformation in processor.Transformations)
            {
                if (!transformationIds.Add(transformation.Id))
                {
                    errors.Add($"Artifact processor '{processor.Id}' has duplicate transformation id '{transformation.Id}'.");
                }
                if (!processor.AcceptsArtifactFormats.Contains(transformation.InputArtifactFormat, StringComparer.Ordinal))
                {
                    errors.Add(
                        $"Artifact processor '{processor.Id}' transformation '{transformation.Id}' does not declare input format '{transformation.InputArtifactFormat}' in acceptsArtifactFormats.");
                }
                if (!processor.ProducesArtifactFormats.Contains(transformation.OutputArtifactFormat, StringComparer.Ordinal))
                {
                    errors.Add(
                        $"Artifact processor '{processor.Id}' transformation '{transformation.Id}' does not declare output format '{transformation.OutputArtifactFormat}' in producesArtifactFormats.");
                }
            }
        }

        foreach (var preset in catalog.Presets)
        {
            ValidateLifecycle(
                preset.SupportStatus,
                preset.Visibility,
                $"Preset '{preset.Id}'",
                errors);
            RequireReference(preset.LanguageId, languages, $"Preset '{preset.Id}' language", errors);
            RequireReference(preset.ToolchainId, toolchains, $"Preset '{preset.Id}' toolchain", errors);
            RequireReference(preset.ReferenceSetId, referenceSets, $"Preset '{preset.Id}' reference set", errors);
            RequireReference(preset.DefaultOutputId, outputs, $"Preset '{preset.Id}' output", errors);
            if (preset.DefaultRuntimeId is not null)
            {
                RequireReference(preset.DefaultRuntimeId, runtimes, $"Preset '{preset.Id}' runtime", errors);
            }
        }

        foreach (var rule in catalog.Compatibility)
        {
            switch (rule.Kind)
            {
                case CompatibilityRuleKind.ToolchainReferenceSet:
                    RequireReference(rule.FromId, toolchains, $"Rule '{rule.Id}' source", errors);
                    RequireReference(rule.ToId, referenceSets, $"Rule '{rule.Id}' target", errors);
                    break;
                case CompatibilityRuleKind.ArtifactProcessor:
                    RequireReference(rule.ToId, processors, $"Rule '{rule.Id}' target", errors);
                    if (processors.TryGetValue(rule.ToId, out var processor) &&
                        !processor.AcceptsArtifactFormats.Contains(rule.FromId, StringComparer.Ordinal))
                    {
                        errors.Add(
                            $"Rule '{rule.Id}' source format '{rule.FromId}' is not accepted by artifact processor '{processor.Id}'.");
                    }
                    break;
                case CompatibilityRuleKind.ArtifactRuntime:
                    RequireReference(rule.ToId, runtimes, $"Rule '{rule.Id}' target", errors);
                    if (runtimes.TryGetValue(rule.ToId, out var runtime) &&
                        !runtime.AcceptedArtifactFormats.Contains(rule.FromId, StringComparer.Ordinal))
                    {
                        errors.Add(
                            $"Rule '{rule.Id}' source format '{rule.FromId}' is not accepted by runtime '{runtime.Id}'.");
                    }
                    break;
                default:
                    errors.Add($"Rule '{rule.Id}' has unsupported kind '{rule.Kind}'.");
                    break;
            }
        }

        return errors;
    }

    private static void ValidateLifecycle(
        string supportStatus,
        string visibility,
        string description,
        List<string> errors)
    {
        if (supportStatus is not ("active" or "maintenance" or "preview" or "legacy" or "experimental"))
            errors.Add($"{description} has unsupported support status '{supportStatus}'.");
        if (visibility is not ("visible" or "hidden"))
            errors.Add($"{description} has unsupported visibility '{visibility}'.");
        if (string.Equals(supportStatus, "legacy", StringComparison.Ordinal) &&
            !string.Equals(visibility, "hidden", StringComparison.Ordinal))
        {
            errors.Add($"{description} is legacy and must be hidden by default.");
        }
    }

    private static Dictionary<string, T> Index<T>(
        IEnumerable<T> items,
        Func<T, string> getId,
        string kind,
        List<string> errors)
    {
        var index = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var id = getId(item);
            Require(id, $"{kind} id", errors);
            if (!index.TryAdd(id, item))
            {
                errors.Add($"Duplicate {kind} id '{id}'.");
            }
        }

        return index;
    }

    private static void AddMissing<T>(
        IEnumerable<string> ids,
        IReadOnlyDictionary<string, T> index,
        string description,
        List<string> errors)
    {
        foreach (var id in ids)
        {
            RequireReference(id, index, description, errors);
        }
    }

    private static void RequireReference<T>(
        string id,
        IReadOnlyDictionary<string, T> index,
        string description,
        List<string> errors)
    {
        if (!index.ContainsKey(id))
        {
            errors.Add($"{description} references missing id '{id}'.");
        }
    }

    private static void Require(string value, string description, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"Catalog {description} is required.");
        }
    }
}
