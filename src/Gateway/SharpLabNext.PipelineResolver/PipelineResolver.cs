using System.Security.Cryptography;
using System.Text;
using SharpLabNext.Catalog;
using SharpLabNext.Contracts;

namespace SharpLabNext.PipelineResolver;

public static class PipelineResolver
{
    public static ResolveSelectionResponse Resolve(CatalogDocument catalog, ResolveSelectionRequest request, DateTimeOffset now)
    {
        if (!string.Equals(catalog.Revision, request.CatalogRevision, StringComparison.Ordinal))
        {
            throw SelectionResolutionException.StaleCatalog(request.CatalogRevision, catalog.Revision);
        }

        var changes = new List<SelectionChange>();
        var language = FindRequired(catalog.Languages, request.LanguageId, static item => item.Id, SelectionField.Language);
        var toolchain = ResolveToolchain(catalog, language, request.ToolchainId, changes);
        var referenceSet = ResolveReferenceSet(catalog, toolchain, request.ReferenceSetId, changes);
        var output = FindRequired(catalog.Outputs, request.OutputId, static item => item.Id, SelectionField.Output);
        var route = ResolveArtifactRoute(catalog, toolchain, referenceSet, output, request.RuntimeId, changes);
        var processor = route.OutputProcessor;
        var runtime = route.Runtime;
        EnsureOutputSupported(language, toolchain, processor, output, route);

        AddAvailabilityChange(SelectionField.Toolchain, toolchain.Id, toolchain.Availability, changes);
        AddAvailabilityChange(SelectionField.ReferenceSet, referenceSet.Id, referenceSet.Availability, changes);
        foreach (var transformProcessor in route.Transformations.Select(static transformation => transformation.Processor).DistinctBy(static item => item.Id, StringComparer.Ordinal))
            AddAvailabilityChange(SelectionField.Output, transformProcessor.Id, transformProcessor.Availability, changes);
        if (processor is not null)
        {
            AddAvailabilityChange(SelectionField.Output, processor.Id, processor.Availability, changes);
        }

        if (runtime is not null)
        {
            AddAvailabilityChange(SelectionField.Runtime, runtime.Id, runtime.Availability, changes);
        }

        var effectiveSelection = new ResolvedSelection(language.Id, toolchain.Id, referenceSet.Id, output.Id, runtime?.Id);
        var stages = BuildStages(toolchain, output, route);
        var plan = new PipelinePlanDescriptor(catalog.ReleaseId, toolchain.WorkerId, toolchain.WorkerId, referenceSet.Id, stages, runtime?.Id, SecurityPolicyId(output, runtime), []);
        var resolutionId = CreateResolutionId(catalog, effectiveSelection, request, stages);
        var capabilities = CreateCapabilities(catalog, language, toolchain, referenceSet, runtime);

        return new ResolveSelectionResponse(effectiveSelection, changes, capabilities, resolutionId, plan, now.AddHours(1));
    }

    private static string SecurityPolicyId(OutputManifest output, RuntimeManifest? runtime)
    {
        if (!output.RequiresRuntime)
            return "compiler-default";
        if (runtime is not null && string.Equals(runtime.Id, "wine-jsharp20-linux-x64", StringComparison.Ordinal))
        {
            return "runtime-job-wine-jsharp20";
        }
        return runtime is not null &&
               string.Equals(runtime.Family, "netfx-clr-wine", StringComparison.Ordinal)
            ? "runtime-job-wine-netfx" : "runtime-job-default";
    }

    private static ToolchainManifest ResolveToolchain(CatalogDocument catalog, LanguageManifest language, string? requestedId, List<SelectionChange> changes)
    {
        if (requestedId is null)
        {
            changes.Add(new SelectionChange(SelectionField.Toolchain, null, language.DefaultToolchainId, SelectionChangeReason.DefaultApplied, $"Selected the default toolchain for {language.DisplayName}."));
            return FindRequired(catalog.Toolchains, language.DefaultToolchainId, static item => item.Id, SelectionField.Toolchain);
        }

        var toolchain = FindByIdOrAlias(catalog.Toolchains, requestedId, static item => item.Id, static item => item.LegacyAliases);
        if (toolchain is null)
        {
            throw SelectionResolutionException.Unknown(SelectionField.Toolchain, requestedId);
        }

        if (!string.Equals(toolchain.Id, requestedId, StringComparison.Ordinal))
        {
            changes.Add(new SelectionChange(SelectionField.Toolchain, requestedId, toolchain.Id, SelectionChangeReason.LegacyAliasResolved, $"Legacy toolchain alias '{requestedId}' resolved to '{toolchain.Id}'."));
        }

        if (toolchain.SupportedLanguageIds.Contains(language.Id, StringComparer.Ordinal))
        {
            return toolchain;
        }

        var fallback = FindRequired(catalog.Toolchains, language.DefaultToolchainId, static item => item.Id, SelectionField.Toolchain);
        changes.Add(new SelectionChange(SelectionField.Toolchain, requestedId, fallback.Id, SelectionChangeReason.UnsupportedByLanguage, $"{toolchain.DisplayName} does not support {language.DisplayName}; selected {fallback.DisplayName}."));
        return fallback;
    }

    private static ReferenceSetManifest ResolveReferenceSet(CatalogDocument catalog, ToolchainManifest toolchain, string? requestedId, List<SelectionChange> changes)
    {
        var effectiveId = requestedId ?? toolchain.DefaultReferenceSetId;
        if (requestedId is null)
        {
            changes.Add(new SelectionChange(SelectionField.ReferenceSet, null, effectiveId, SelectionChangeReason.DefaultApplied, $"Selected the default reference set for {toolchain.DisplayName}."));
        }

        var referenceSet = catalog.ReferenceSets.FirstOrDefault(item => string.Equals(item.Id, effectiveId, StringComparison.Ordinal));
        var isAllowed = referenceSet is not null && toolchain.AllowedReferenceSetIds.Contains(referenceSet.Id, StringComparer.Ordinal);
        var hasRule = catalog.Compatibility.Any(rule => rule.Kind == CompatibilityRuleKind.ToolchainReferenceSet && rule.Allowed && string.Equals(rule.FromId, toolchain.Id, StringComparison.Ordinal) && string.Equals(rule.ToId, effectiveId, StringComparison.Ordinal));
        if (isAllowed && hasRule)
        {
            return referenceSet!;
        }

        var fallback = FindRequired(catalog.ReferenceSets, toolchain.DefaultReferenceSetId, static item => item.Id, SelectionField.ReferenceSet);
        if (requestedId is not null)
        {
            changes.Add(new SelectionChange(SelectionField.ReferenceSet, requestedId, fallback.Id, SelectionChangeReason.IncompatibleReferenceSet, $"Reference set '{requestedId}' is not compatible with {toolchain.DisplayName}; selected {fallback.DisplayName}."));
        }

        return fallback;
    }

    private static ArtifactRoute ResolveArtifactRoute(CatalogDocument catalog, ToolchainManifest toolchain, ReferenceSetManifest referenceSet, OutputManifest output, string? requestedRuntimeId, List<SelectionChange> changes)
    {
        if (toolchain.ProducesArtifactFormats.Count == 0)
        {
            throw SelectionResolutionException.Incompatible(SelectionField.Output, output.Id, $"Toolchain '{toolchain.Id}' does not declare an artifact format.");
        }

        if (!output.RequiresRuntime && requestedRuntimeId is not null)
        {
            changes.Add(new SelectionChange(SelectionField.Runtime, requestedRuntimeId, null, SelectionChangeReason.RuntimeNotRequired, $"Output '{output.DisplayName}' does not use a runtime."));
        }

        if (output.RequiresRuntime)
        {
            return ResolveRuntimeRoute(catalog, toolchain, referenceSet, output, requestedRuntimeId, changes);
        }

        if (NeedsArtifactProcessor(catalog, output))
        {
            return ResolveProcessorRoute(catalog, toolchain, referenceSet, output);
        }

        return new ArtifactRoute(toolchain.ProducesArtifactFormats[0], toolchain.ProducesArtifactFormats[0], [], null, null);
    }

    private static ArtifactRoute ResolveProcessorRoute(CatalogDocument catalog, ToolchainManifest toolchain, ReferenceSetManifest referenceSet, OutputManifest output)
    {
        var requiredTags = RequiredMetadataTags(toolchain, referenceSet);
        ProcessorRouteCandidate? best = null;
        foreach (var processor in catalog.ArtifactProcessors)
        {
            if (!processor.Capabilities.Contains(output.Id, StringComparer.Ordinal) || !ContainsAll(processor.AcceptedMetadataFeatureTags, requiredTags))
            {
                continue;
            }

            var targetFormats = processor.AcceptsArtifactFormats.Where(format => output.AcceptedArtifactFormats.Count == 0 || output.AcceptedArtifactFormats.Contains(format, StringComparer.Ordinal));
            foreach (var targetFormat in targetFormats)
            {
                if (!HasProcessorCompatibility(catalog, targetFormat, processor, requiredTags))
                    continue;
                var conversion = FindConversionRoute(catalog, toolchain.ProducesArtifactFormats, [targetFormat], requiredTags);
                if (conversion is null)
                    continue;
                var candidate = new ProcessorRouteCandidate(processor, conversion);
                if (best is null || candidate.Conversion.Transformations.Count < best.Conversion.Transformations.Count)
                    best = candidate;
            }
        }

        if (best is null)
        {
            throw SelectionResolutionException.Incompatible(SelectionField.Output, output.Id, $"No approved artifact route provides output '{output.Id}' for toolchain '{toolchain.Id}'.");
        }

        return new ArtifactRoute(best.Conversion.SourceFormat, best.Conversion.FinalFormat, best.Conversion.Transformations, best.Processor, null);
    }

    private static ArtifactRoute ResolveRuntimeRoute(CatalogDocument catalog, ToolchainManifest toolchain, ReferenceSetManifest referenceSet, OutputManifest output, string? requestedId, List<SelectionChange> changes)
    {
        RuntimeManifest[] candidates;
        if (requestedId is null)
        {
            candidates = catalog.Runtimes.ToArray();
            Array.Sort(candidates, (left, right) => CompareRuntimePreference(referenceSet, left, right));
        }
        else
        {
            var requested = FindByIdOrAlias(catalog.Runtimes, requestedId, static item => item.Id, static item => item.LegacyAliases) ?? throw SelectionResolutionException.Unknown(SelectionField.Runtime, requestedId);
            candidates = [requested];
            if (!string.Equals(requested.Id, requestedId, StringComparison.Ordinal))
            {
                changes.Add(new SelectionChange(SelectionField.Runtime, requestedId, requested.Id, SelectionChangeReason.LegacyAliasResolved, $"Legacy runtime alias '{requestedId}' resolved to '{requested.Id}'."));
            }
        }

        var requiredTags = RequiredMetadataTags(toolchain, referenceSet);
        foreach (var runtime in candidates)
        {
            ConversionRoute? bestConversion = null;
            ArtifactProcessorManifest? bestProcessor = null;
            foreach (var targetFormat in runtime.AcceptedArtifactFormats)
            {
                if (output.AcceptedArtifactFormats.Count > 0 && !output.AcceptedArtifactFormats.Contains(targetFormat, StringComparer.Ordinal))
                {
                    continue;
                }
                if (!IsRuntimeCompatible(catalog, toolchain, referenceSet, output, targetFormat, runtime))
                    continue;

                ArtifactProcessorManifest? processor = null;
                if (NeedsArtifactProcessor(catalog, output))
                {
                    processor = FindOutputProcessorForFormat(catalog, output, targetFormat, requiredTags);
                    if (processor is null)
                        continue;
                }

                var conversion = FindConversionRoute(catalog, toolchain.ProducesArtifactFormats, [targetFormat], requiredTags);
                if (conversion is null)
                    continue;
                if (bestConversion is null || conversion.Transformations.Count < bestConversion.Transformations.Count)
                {
                    bestConversion = conversion;
                    bestProcessor = processor;
                }
            }

            if (bestConversion is null)
                continue;
            if (requestedId is null)
            {
                changes.Add(new SelectionChange(SelectionField.Runtime, null, runtime.Id, SelectionChangeReason.DefaultApplied, $"Selected compatible runtime {runtime.DisplayName}."));
            }
            return new ArtifactRoute(bestConversion.SourceFormat, bestConversion.FinalFormat, bestConversion.Transformations, bestProcessor, runtime);
        }

        throw SelectionResolutionException.Incompatible(SelectionField.Runtime, requestedId, requestedId is null ? "No compatible runtime and artifact conversion route is registered." : $"Runtime '{requestedId}' is not compatible with toolchain '{toolchain.Id}' and reference set '{referenceSet.Id}'.");
    }

    private static ArtifactProcessorManifest? FindOutputProcessorForFormat(CatalogDocument catalog, OutputManifest output, string artifactFormat, IReadOnlyList<string> requiredTags) =>
        catalog.ArtifactProcessors.FirstOrDefault(processor => processor.Capabilities.Contains(output.Id, StringComparer.Ordinal) && processor.AcceptsArtifactFormats.Contains(artifactFormat, StringComparer.Ordinal) && ContainsAll(processor.AcceptedMetadataFeatureTags, requiredTags) && HasProcessorCompatibility(catalog, artifactFormat, processor, requiredTags));

    private static ConversionRoute? FindConversionRoute(CatalogDocument catalog, IReadOnlyList<string> sourceFormats, IReadOnlyList<string> targetFormats, IReadOnlyList<string> requiredTags)
    {
        var targets = targetFormats.ToHashSet(StringComparer.Ordinal);
        var queue = new Queue<ConversionRoute>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourceFormat in sourceFormats)
        {
            if (!visited.Add(sourceFormat))
                continue;
            var initial = new ConversionRoute(sourceFormat, sourceFormat, []);
            if (targets.Contains(sourceFormat))
                return initial;
            queue.Enqueue(initial);
        }

        while (queue.TryDequeue(out var current))
        {
            foreach (var processor in catalog.ArtifactProcessors)
            {
                if (!ContainsAll(processor.AcceptedMetadataFeatureTags, requiredTags) || !HasProcessorCompatibility(catalog, current.FinalFormat, processor, requiredTags))
                {
                    continue;
                }
                foreach (var transformation in processor.Transformations)
                {
                    if (!string.Equals(transformation.InputArtifactFormat, current.FinalFormat, StringComparison.Ordinal) || !visited.Add(transformation.OutputArtifactFormat))
                    {
                        continue;
                    }
                    var next = new ConversionRoute(current.SourceFormat, transformation.OutputArtifactFormat, [..current.Transformations, new ArtifactTransformStep(processor, transformation)]);
                    if (targets.Contains(next.FinalFormat))
                        return next;
                    queue.Enqueue(next);
                }
            }
        }

        return null;
    }

    private static bool HasProcessorCompatibility(CatalogDocument catalog, string artifactFormat, ArtifactProcessorManifest processor, IReadOnlyList<string> artifactMetadataTags) =>
        catalog.Compatibility.Any(rule => rule.Kind == CompatibilityRuleKind.ArtifactProcessor && rule.Allowed && string.Equals(rule.FromId, artifactFormat, StringComparison.Ordinal) && string.Equals(rule.ToId, processor.Id, StringComparison.Ordinal) && ContainsAll(artifactMetadataTags, rule.RequiredMetadataFeatureTags));

    private static string[] RequiredMetadataTags(ToolchainManifest toolchain, ReferenceSetManifest referenceSet) =>
        toolchain.MetadataFeatureTags.Concat(referenceSet.MetadataFeatureTags).Distinct(StringComparer.Ordinal).ToArray();

    private static bool IsRuntimeCompatible(CatalogDocument catalog, ToolchainManifest toolchain, ReferenceSetManifest referenceSet, OutputManifest output, string artifactFormat, RuntimeManifest runtime)
    {
        if (!runtime.Availability.IsSelectable)
            return false;

        var artifactMetadataTags = RequiredMetadataTags(toolchain, referenceSet);
        var hasEdge = catalog.Compatibility.Any(rule => rule.Kind == CompatibilityRuleKind.ArtifactRuntime && rule.Allowed && string.Equals(rule.FromId, artifactFormat, StringComparison.Ordinal) && string.Equals(rule.ToId, runtime.Id, StringComparison.Ordinal) && ContainsAll(artifactMetadataTags, rule.RequiredMetadataFeatureTags) && ContainsAll(runtime.ProvidedMetadataFeatureTags, rule.RequiredMetadataFeatureTags));
        return hasEdge &&
               runtime.AcceptedArtifactFormats.Contains(artifactFormat, StringComparer.Ordinal) &&
               HasCompatibleRuntimeContract(referenceSet, runtime) &&
               ContainsAll(runtime.ProvidedMetadataFeatureTags, toolchain.MetadataFeatureTags) &&
               ContainsAll(runtime.ProvidedRuntimeFeatureTags, referenceSet.RequiredRuntimeFeatureTags) &&
               ContainsAll(runtime.ProvidedMetadataFeatureTags, referenceSet.MetadataFeatureTags) &&
               output.RequiredCapabilities.Where(IsRuntimeCapability).All(capability => runtime.Capabilities.Contains(capability, StringComparer.Ordinal));
    }

    private static bool HasCompatibleRuntimeContract(ReferenceSetManifest referenceSet, RuntimeManifest runtime)
    {
        if (runtime.AcceptedRuntimeFamilies.Count > 0 && !runtime.AcceptedRuntimeFamilies.Contains(referenceSet.RuntimeFamily, StringComparer.Ordinal))
        {
            return false;
        }

        if (!TryGetReferenceFramework(referenceSet, out var referenceFramework))
            return runtime.AcceptedFrameworks.Count == 0;

        if (CanRollForwardCoreClr(referenceSet, runtime, referenceFramework))
            return true;

        if (runtime.AcceptedFrameworks.Count > 0)
        {
            return runtime.AcceptedFrameworks.Any(accepted => AcceptsReferenceFramework(accepted, referenceFramework));
        }

        // Catalogs published before acceptedFrameworks existed still rely on
        // artifact-runtime edges and feature tags. CoreCLR is the exception:
        // its TFM unambiguously prevents a net11 artifact from reaching net10.
        return !string.Equals(referenceSet.RuntimeFamily, "coreclr", StringComparison.Ordinal) ||
               !string.Equals(runtime.Family, "coreclr", StringComparison.Ordinal) ||
               VersionMatchesTargetFramework(runtime.ResolvedVersion, referenceFramework.Version);
    }

    private static bool CanRollForwardCoreClr(ReferenceSetManifest referenceSet, RuntimeManifest runtime, ReferenceFramework referenceFramework) =>
        string.Equals(referenceSet.RuntimeFamily, "coreclr", StringComparison.Ordinal) &&
        string.Equals(referenceFramework.Name, "Microsoft.NETCore.App", StringComparison.Ordinal) &&
        runtime.Family is "coreclr" or "coreclr-wine" &&
        TryParseVersion(runtime.ResolvedVersion, out var runtimeVersion) &&
        CompareAtTargetFrameworkPrecision(referenceFramework.Version, runtimeVersion) <= 0;

    private static int CompareRuntimePreference(ReferenceSetManifest referenceSet, RuntimeManifest left, RuntimeManifest right)
    {
        if (!TryGetReferenceFramework(referenceSet, out var referenceFramework))
            return string.Compare(left.Id, right.Id, StringComparison.Ordinal);

        var specificityOrder = RuntimeVersionSpecificity(left, referenceFramework).CompareTo(RuntimeVersionSpecificity(right, referenceFramework));
        if (specificityOrder != 0)
            return specificityOrder;

        var leftFamilyOrder = string.Equals(left.Family, referenceSet.RuntimeFamily, StringComparison.Ordinal) ? 0 : 1;
        var rightFamilyOrder = string.Equals(right.Family, referenceSet.RuntimeFamily, StringComparison.Ordinal) ? 0 : 1;
        var familyOrder = leftFamilyOrder.CompareTo(rightFamilyOrder);
        if (familyOrder != 0)
            return familyOrder;

        if (TryParseVersion(left.ResolvedVersion, out var leftVersion) && TryParseVersion(right.ResolvedVersion, out var rightVersion))
        {
            var versionOrder = CompareVersionsDescending(leftVersion, rightVersion);
            if (versionOrder != 0)
                return versionOrder;
        }

        return string.Compare(left.Id, right.Id, StringComparison.Ordinal);
    }

    private static int RuntimeVersionSpecificity(RuntimeManifest runtime, ReferenceFramework referenceFramework) =>
        TryParseVersion(runtime.ResolvedVersion, out var runtimeVersion) &&
        VersionMatchesTargetFramework(runtimeVersion, referenceFramework.Version)
            ? runtimeVersion.Length - referenceFramework.Version.Length : int.MaxValue;

    private static int CompareVersionsDescending(int[] left, int[] right)
    {
        var length = Math.Max(left.Length, right.Length);
        for (var index = 0; index < length; index++)
        {
            var comparison = (index < right.Length ? right[index] : 0).CompareTo(index < left.Length ? left[index] : 0);
            if (comparison != 0)
                return comparison;
        }
        return 0;
    }

    private static bool TryGetReferenceFramework(ReferenceSetManifest referenceSet, out ReferenceFramework referenceFramework)
    {
        const string corePrefix = "netcoreapp";
        const string frameworkPrefix = "netframework";
        var targetFramework = referenceSet.TargetFramework;
        string frameworkName;
        string version;

        if (targetFramework.StartsWith(corePrefix, StringComparison.Ordinal))
        {
            frameworkName = "Microsoft.NETCore.App";
            version = targetFramework[corePrefix.Length..];
        }
        else if (targetFramework.StartsWith(frameworkPrefix, StringComparison.Ordinal))
        {
            frameworkName = ".NETFramework";
            version = targetFramework[frameworkPrefix.Length..];
        }
        else if (targetFramework.StartsWith("net", StringComparison.Ordinal))
        {
            version = targetFramework[3..];
            if (version.Length == 0)
            {
                referenceFramework = default;
                return false;
            }

            if (string.Equals(referenceSet.RuntimeFamily, "netfx-clr-wine", StringComparison.Ordinal))
            {
                frameworkName = ".NETFramework";
                if (version.IndexOf('.') < 0)
                    version = string.Join('.', version.Select(static character => character.ToString()));
            }
            else
            {
                frameworkName = "Microsoft.NETCore.App";
            }
        }
        else
        {
            referenceFramework = default;
            return false;
        }

        if (!TryParseVersion(version, out var parsedVersion))
        {
            referenceFramework = default;
            return false;
        }

        referenceFramework = new ReferenceFramework(frameworkName, parsedVersion);
        return true;
    }

    private static bool AcceptsReferenceFramework(RuntimeFrameworkManifest accepted, ReferenceFramework referenceFramework)
    {
        if (!string.Equals(accepted.Name, referenceFramework.Name, StringComparison.Ordinal))
            return false;

        var hasExactVersion = !string.IsNullOrWhiteSpace(accepted.ExactVersion);
        var hasRange = !string.IsNullOrWhiteSpace(accepted.MinimumVersion) ||
                       !string.IsNullOrWhiteSpace(accepted.MaximumVersion);
        if (hasExactVersion == hasRange)
            return false;

        if (hasExactVersion)
        {
            return TryParseVersion(accepted.ExactVersion!, out var exactVersion) &&
                   VersionMatchesTargetFramework(exactVersion, referenceFramework.Version);
        }

        return TryParseVersion(accepted.MinimumVersion, out var minimumVersion) &&
               TryParseVersion(accepted.MaximumVersion, out var maximumVersion) &&
               CompareAtTargetFrameworkPrecision(referenceFramework.Version, minimumVersion) >= 0 &&
               CompareAtTargetFrameworkPrecision(referenceFramework.Version, maximumVersion) <= 0;
    }

    private static bool VersionMatchesTargetFramework(string version, int[] targetFrameworkVersion) =>
        TryParseVersion(version, out var parsedVersion) &&
        VersionMatchesTargetFramework(parsedVersion, targetFrameworkVersion);

    private static bool VersionMatchesTargetFramework(int[] version, int[] targetFrameworkVersion) =>
        version.Length >= targetFrameworkVersion.Length &&
        version.Take(targetFrameworkVersion.Length).SequenceEqual(targetFrameworkVersion);

    private static int CompareAtTargetFrameworkPrecision(int[] targetFrameworkVersion, int[] boundVersion)
    {
        if (boundVersion.Length < targetFrameworkVersion.Length)
            return -1;

        for (var index = 0; index < targetFrameworkVersion.Length; index++)
        {
            var comparison = targetFrameworkVersion[index].CompareTo(boundVersion[index]);
            if (comparison != 0)
                return comparison;
        }
        return 0;
    }

    private static bool TryParseVersion(string? value, out int[] version)
    {
        version = [];
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var numericPart = value.Split(['-', '+'], 2)[0];
        var parts = numericPart.Split('.', StringSplitOptions.None);
        if (parts.Length is < 1 or > 4 || parts.Any(static part => !int.TryParse(part, out _)))
            return false;

        version = parts.Select(static part => int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        return true;
    }

    private readonly record struct ReferenceFramework(string Name, int[] Version);

    private static void EnsureOutputSupported(LanguageManifest language, ToolchainManifest toolchain, ArtifactProcessorManifest? processor, OutputManifest output, ArtifactRoute route)
    {
        foreach (var capability in output.RequiredCapabilities)
        {
            if (capability == "explain" && (!language.Capabilities.Contains(capability, StringComparer.Ordinal) || !toolchain.Capabilities.Contains(capability, StringComparer.Ordinal)))
            {
                throw SelectionResolutionException.Incompatible(SelectionField.Output, output.Id, $"Language '{language.Id}' and toolchain '{toolchain.Id}' do not jointly provide capability '{capability}'.");
            }
            if (language.Capabilities.Contains(capability, StringComparer.Ordinal) || toolchain.Capabilities.Contains(capability, StringComparer.Ordinal) || processor?.Capabilities.Contains(capability, StringComparer.Ordinal) == true || (capability == "managed-pe" && string.Equals(route.FinalFormat, "dotnet-managed-pe-v1", StringComparison.Ordinal)) || capability is "run" or "jit-asm")
            {
                continue;
            }

            throw SelectionResolutionException.Incompatible(SelectionField.Output, output.Id, $"Toolchain '{toolchain.Id}' does not provide capability '{capability}'.");
        }
    }

    private static List<PipelineStageDescriptor> BuildStages(ToolchainManifest toolchain, OutputManifest output, ArtifactRoute route)
    {
        if (output.Id == "explain")
        {
            return
            [
                new PipelineStageDescriptor("explain", PipelineStageKind.Explain, toolchain.WorkerId, null, "explanation-document-v1")
            ];
        }

        var stages = new List<PipelineStageDescriptor> { new("build", PipelineStageKind.Build, toolchain.WorkerId, null, route.SourceFormat) };
        foreach (var step in route.Transformations)
            stages.Add(new PipelineStageDescriptor(step.Transformation.Id, PipelineStageKind.Transform, step.Processor.WorkerId, step.Transformation.InputArtifactFormat, step.Transformation.OutputArtifactFormat));

        var processor = route.OutputProcessor;
        var runtime = route.Runtime;
        var artifactFormat = route.FinalFormat;
        if (output.Id is "execution-flow" or "run-il")
        {
            if (processor is null)
                throw new InvalidOperationException("The runtime instrumentation processor was not resolved.");
            var instrumentation = processor.Transformations.SingleOrDefault(transformation => string.Equals(transformation.Id, "runtime-instrumentation-v1", StringComparison.Ordinal) && string.Equals(transformation.InputArtifactFormat, artifactFormat, StringComparison.Ordinal));
            if (instrumentation is null)
                throw new InvalidOperationException("The runtime instrumentation transformation was not declared.");
            stages.Add(new PipelineStageDescriptor(instrumentation.Id, PipelineStageKind.Transform, processor.WorkerId, instrumentation.InputArtifactFormat, instrumentation.OutputArtifactFormat));
            artifactFormat = instrumentation.OutputArtifactFormat;
            if (output.Id == "run-il")
            {
                stages.Add(new PipelineStageDescriptor(output.Id, PipelineStageKind.Render, processor.WorkerId, artifactFormat, OutputFormat(output)));
            }
            else if (runtime is not null)
            {
                stages.Add(new PipelineStageDescriptor(output.Id, PipelineStageKind.Run, runtime.Id, artifactFormat, OutputFormat(output)));
            }
            return stages;
        }
        if (processor is not null)
        {
            var kind = output.Id == "il-verify" ? PipelineStageKind.Verify : PipelineStageKind.Render;
            stages.Add(new PipelineStageDescriptor(output.Id, kind, processor.WorkerId, artifactFormat, OutputFormat(output)));
        }

        if (runtime is not null)
        {
            var kind = output.Id == "jit-asm" ? PipelineStageKind.Jit : PipelineStageKind.Run;
            stages.Add(new PipelineStageDescriptor(output.Id, kind, runtime.Id, artifactFormat, OutputFormat(output)));
        }

        return stages;
    }

    private static EffectiveCapabilities CreateCapabilities(CatalogDocument catalog, LanguageManifest language, ToolchainManifest toolchain, ReferenceSetManifest referenceSet, RuntimeManifest? runtime)
    {
        var outputIds = catalog.Outputs.Where(output => CanOfferOutput(catalog, language, toolchain, referenceSet, output)).Select(static output => output.Id).ToArray();
        return new EffectiveCapabilities(language.Capabilities.Where(capability => capability is not ("ast" or "multi-file" or "source-order")).ToArray(), toolchain.Capabilities.ToArray(), outputIds, runtime?.Capabilities.ToArray() ?? []);
    }

    private static bool CanOfferOutput(CatalogDocument catalog, LanguageManifest language, ToolchainManifest toolchain, ReferenceSetManifest referenceSet, OutputManifest output)
    {
        if (output.RequiredCapabilities.Contains("explain", StringComparer.Ordinal) && (!language.Capabilities.Contains("explain", StringComparer.Ordinal) || !toolchain.Capabilities.Contains("explain", StringComparer.Ordinal)))
        {
            return false;
        }
        try
        {
            var route = ResolveArtifactRoute(catalog, toolchain, referenceSet, output, null, []);
            EnsureOutputSupported(language, toolchain, route.OutputProcessor, output, route);
            return true;
        }
        catch (SelectionResolutionException)
        {
            return false;
        }
    }

    private static void AddAvailabilityChange(SelectionField field, string id, ComponentAvailability availability, List<SelectionChange> changes)
    {
        if (availability.IsSelectable)
        {
            return;
        }

        changes.Add(new SelectionChange(field, id, id, SelectionChangeReason.ProfileUnavailable, availability.Reason ?? $"Profile '{id}' is not available in this release."));
    }

    private static T FindRequired<T>(IEnumerable<T> items, string id, Func<T, string> getId, SelectionField field)
    {
        return items.FirstOrDefault(item => string.Equals(getId(item), id, StringComparison.Ordinal)) ?? throw SelectionResolutionException.Unknown(field, id);
    }

    private static T? FindByIdOrAlias<T>(IEnumerable<T> items, string id, Func<T, string> getId, Func<T, IReadOnlyList<string>> getAliases)
        where T : class
    {
        return items.FirstOrDefault(item => string.Equals(getId(item), id, StringComparison.Ordinal) || getAliases(item).Contains(id, StringComparer.Ordinal));
    }

    private static bool ContainsAll(IEnumerable<string> available, IEnumerable<string> required)
    {
        var set = available.ToHashSet(StringComparer.Ordinal);
        return required.All(set.Contains);
    }

    private static bool NeedsArtifactProcessor(CatalogDocument catalog, OutputManifest output) =>
        catalog.ArtifactProcessors.Any(processor => processor.Capabilities.Contains(output.Id, StringComparer.Ordinal));

    private static bool IsRuntimeCapability(string capability) =>
        capability is "run" or "jit-asm" or "execution-flow" or "inspection";

    private static string? OutputFormat(OutputManifest output) => output.OutputArtifactFormat ?? output.Id switch
    {
        "generated-il" => "cil-text-v1",
        "il" or "run-il" => "il-text-v1",
        "decompiled-csharp" => "decompiled-csharp-v1",
        "il-verify" => "il-verification-v1",
        "jit-asm" => "native-asm-v1",
        "run" or "execution-flow" => "runtime-result-v1",
        "explain" => "explanation-document-v1",
        _ => null
    };

    private static string CreateResolutionId(CatalogDocument catalog, ResolvedSelection selection, ResolveSelectionRequest request, IReadOnlyList<PipelineStageDescriptor> stages)
    {
        var value = string.Join('|', catalog.ReleaseId, catalog.Revision, selection.LanguageId, selection.ToolchainId, selection.ReferenceSetId, selection.OutputId, selection.RuntimeId, request.BuildMode, request.WorkspaceRevision, string.Join(',', stages.Select(static stage => $"{stage.Kind}:{stage.ProviderId}")));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"pr_{Convert.ToHexStringLower(digest.AsSpan(0, 16))}";
    }

    private sealed record ArtifactRoute(string SourceFormat, string FinalFormat, IReadOnlyList<ArtifactTransformStep> Transformations, ArtifactProcessorManifest? OutputProcessor, RuntimeManifest? Runtime);

    private sealed record ArtifactTransformStep(ArtifactProcessorManifest Processor, ArtifactTransformationManifest Transformation);

    private sealed record ConversionRoute(string SourceFormat, string FinalFormat, IReadOnlyList<ArtifactTransformStep> Transformations);

    private sealed record ProcessorRouteCandidate(ArtifactProcessorManifest Processor, ConversionRoute Conversion);
}

public sealed class SelectionResolutionException : Exception
{
    private SelectionResolutionException(string code, SelectionField field, string? value, string message) : base(message)
    {
        Code = code;
        Field = field;
        Value = value;
    }

    public string Code { get; }
    public SelectionField Field { get; }
    public string? Value { get; }

    public static SelectionResolutionException Unknown(SelectionField field, string value) =>
        new("not-found", field, value, $"Unknown {field.ToString().ToLowerInvariant()} id '{value}'.");

    public static SelectionResolutionException Incompatible(SelectionField field, string? value, string message) =>
        new("unsupported-capability", field, value, message);

    public static SelectionResolutionException StaleCatalog(string requested, string current) =>
        new("stale-revision", SelectionField.Output, requested, $"Catalog revision '{requested}' is stale; current revision is '{current}'.");
}
