using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using SharpLabNext.Catalog;
using SharpLabNext.RuntimeProfile.Sdk;

namespace SharpLabNext.BundleBuilder;

internal static class RuntimePromotionTrust
{
    private const long MaximumReceiptBytes = 1024 * 1024;
    private const long MaximumEvidenceBytes = 1024 * 1024;
    private const long MaximumPerformancePolicyBytes = 1024 * 1024;
    private const long MaximumImageArtifactBytes = 256 * 1024 * 1024;
    private const string ReceiptDirectory = "profiles/runtime-promotion-receipts";
    private const string EvidenceDirectory = "profiles/runtime-promotion-evidence";
    private const string PerformancePolicyDirectory = "profiles/runtime-performance-policies";
    private const int MinimumColdPerformanceSamples = 3;
    private const int MaximumColdPerformanceSamples = 20;
    private const int MinimumWarmPerformanceSamples = 5;
    private const int MaximumWarmPerformanceSamples = 50;
    private const long MinimumPerformanceNanoCpus = 250_000_000;
    private const long MaximumPerformanceNanoCpus = 4_000_000_000;
    private const long MinimumPerformanceMemoryBytes = 134_217_728;
    private const long MaximumPerformanceMemoryBytes = 2_147_483_648;
    private const long MaximumPerformanceImageBytes = 17_179_869_184;
    private const double MaximumPerformanceP95Milliseconds = 60_000;
    private const double MaximumPerformanceSampleMilliseconds = 120_000;
    private static readonly JsonSerializerOptions ReceiptJsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<IReadOnlyList<RuntimePromotionTrustSnapshot>> CaptureAsync(
        string repositoryRoot,
        RepositorySourceProvenance source,
        CatalogDocument catalog,
        ReleaseLockDocument releaseLock,
        DeploymentImageManifest deployment,
        IReadOnlyList<RuntimeProfileDefinition> profiles,
        IReadOnlyList<InspectedImage> inspectedImages,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(releaseLock);
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(inspectedImages);

        var promotionProfiles = profiles.Where(static profile => profile.PromotionReceipt is not null)
            .OrderBy(static profile => profile.Id, StringComparer.Ordinal)
            .ToArray();
        var promotionIds = promotionProfiles.Select(static profile => profile.Id)
            .ToHashSet(StringComparer.Ordinal);
        var runtimeIndex = IndexUnique(
            catalog.Runtimes.Where(runtime => promotionIds.Contains(runtime.Id)),
            static runtime => runtime.Id,
            "Catalog runtime");
        var definitionIndex = IndexUnique(
            deployment.Images.Where(definition =>
                definition.RuntimeId is not null && promotionIds.Contains(definition.RuntimeId)),
            static definition => definition.RuntimeId!,
            "runtime deployment definition");
        var imageIndex = IndexUnique(
            inspectedImages.Where(image =>
                image.RuntimeId is not null && promotionIds.Contains(image.RuntimeId)),
            static image => image.RuntimeId!,
            "inspected runtime image");
        var result = new List<RuntimePromotionTrustSnapshot>();
        foreach (var profile in promotionProfiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!runtimeIndex.TryGetValue(profile.Id, out var runtime) || !runtime.Availability.IsSelectable)
            {
                throw new BundleValidationException(
                    $"Promotion-bound runtime profile '{profile.Id}' is not selectable in the Catalog.");
            }
            if (!releaseLock.Components.TryGetValue(profile.Id, out var component))
            {
                throw new BundleValidationException(
                    $"Promotion-bound runtime profile '{profile.Id}' has no release-lock component.");
            }
            if (!definitionIndex.TryGetValue(profile.Id, out var definition) ||
                definition.ImmutableReference is null)
            {
                throw new BundleValidationException(
                    $"Promotion-bound runtime profile '{profile.Id}' has no immutable deployment reference.");
            }
            if (!imageIndex.TryGetValue(profile.Id, out var image))
            {
                throw new BundleValidationException(
                    $"Promotion-bound runtime profile '{profile.Id}' has no inspected image.");
            }

            result.Add(await CaptureProfileAsync(
                repositoryRoot,
                source,
                runtime,
                component,
                definition,
                profile,
                image,
                cancellationToken));
        }

        return result;
    }

    private static Dictionary<string, T> IndexUnique<T>(
        IEnumerable<T> values,
        Func<T, string> keySelector,
        string kind)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var key = keySelector(value);
            if (!result.TryAdd(key, value))
                throw new BundleValidationException($"Duplicate {kind} '{key}' in promotion-bound release material.");
        }
        return result;
    }

    public static async Task RevalidateAsync(
        string repositoryRoot,
        IReadOnlyList<RuntimePromotionTrustSnapshot> snapshots,
        IDockerCli docker,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(docker);

        foreach (var snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receipt = await ReadTrustedFileAsync(
                repositoryRoot,
                snapshot.Receipt.RelativePath,
                [ReceiptDirectory],
                MaximumReceiptBytes,
                cancellationToken);
            RequireDigestEqual(
                snapshot.Receipt.Sha256,
                receipt.Sha256,
                $"Runtime '{snapshot.RuntimeId}' promotion receipt changed before release finalization");

            foreach (var expectedEvidence in snapshot.Evidence)
            {
                var evidence = await ReadTrustedFileAsync(
                    repositoryRoot,
                    expectedEvidence.RelativePath,
                    [EvidenceDirectory, $"{EvidenceDirectory}/{snapshot.RuntimeId}"],
                    MaximumEvidenceBytes,
                    cancellationToken);
                RequireDigestEqual(
                    expectedEvidence.Sha256,
                    evidence.Sha256,
                    $"Runtime '{snapshot.RuntimeId}' promotion evidence '{expectedEvidence.RelativePath}' changed before release finalization");
            }

            var performancePolicy = await ReadTrustedFileAsync(
                repositoryRoot,
                snapshot.PerformancePolicy.RelativePath,
                [PerformancePolicyDirectory],
                MaximumPerformancePolicyBytes,
                cancellationToken);
            RequireDigestEqual(
                snapshot.PerformancePolicy.Sha256,
                performancePolicy.Sha256,
                $"Runtime '{snapshot.RuntimeId}' performance policy changed before release finalization");

            var currentImage = await docker.InspectImageAsync(snapshot.ImmutableReference, cancellationToken);
            if (!string.Equals(currentImage.ImageId, snapshot.ImageId, StringComparison.Ordinal) ||
                !currentImage.RepoDigests.Contains(snapshot.ImmutableReference, StringComparer.Ordinal))
            {
                throw new BundleValidationException(
                    $"Runtime '{snapshot.RuntimeId}' immutable reference no longer resolves to captured image ID '{snapshot.ImageId}'.");
            }
            if (!string.Equals(currentImage.OperatingSystem, "linux", StringComparison.Ordinal) ||
                !string.Equals(currentImage.Architecture, "amd64", StringComparison.Ordinal))
            {
                throw new BundleValidationException(
                    $"Runtime '{snapshot.RuntimeId}' immutable image changed platform before release finalization.");
            }
            if (currentImage.SizeBytes != snapshot.ImageSizeBytes)
            {
                throw new BundleValidationException(
                    $"Runtime '{snapshot.RuntimeId}' immutable image size changed before release finalization.");
            }
            if (!currentImage.Labels.TryGetValue(
                    RepositorySourceProvenanceResolver.ImageLabel,
                    out var buildRevision) ||
                !StringComparer.Ordinal.Equals(buildRevision, snapshot.BuildSourceRevision) ||
                !currentImage.Labels.TryGetValue("org.opencontainers.image.revision", out var ociRevision) ||
                !StringComparer.Ordinal.Equals(ociRevision, snapshot.BuildSourceRevision))
            {
                throw new BundleValidationException(
                    $"Runtime '{snapshot.RuntimeId}' immutable image build revision labels changed before release finalization.");
            }

            var retainedImagePaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var artifact in snapshot.ImageFiles)
            {
                if (!retainedImagePaths.Add(artifact.Path) || !HasCanonicalRetainedMetadata(artifact))
                {
                    throw new BundleValidationException(
                        $"Runtime '{snapshot.RuntimeId}' retained image file metadata changed before release finalization.");
                }
                var observed = await docker.InspectImageFileAsync(
                    snapshot.ImageId,
                    artifact.Path,
                    MaximumImageArtifactBytes,
                    cancellationToken);
                RequireDigestEqual(
                    artifact.Sha256,
                    observed.Sha256,
                    $"Runtime '{snapshot.RuntimeId}' image file '{artifact.Path}' changed before release finalization");
                if (observed.Length != artifact.SizeBytes)
                {
                    throw new BundleValidationException(
                        $"Runtime '{snapshot.RuntimeId}' image file '{artifact.Path}' size changed before release finalization.");
                }
            }
        }
    }

    private static async Task<RuntimePromotionTrustSnapshot> CaptureProfileAsync(
        string repositoryRoot,
        RepositorySourceProvenance source,
        RuntimeManifest runtime,
        LockedComponent component,
        DeploymentImageDefinition definition,
        RuntimeProfileDefinition profile,
        InspectedImage image,
        CancellationToken cancellationToken)
    {
        var reference = profile.PromotionReceipt
            ?? throw new BundleValidationException(
                $"Runtime profile '{profile.Id}' has no promotion receipt reference.");
        var expectedReceiptPath = $"{ReceiptDirectory}/{profile.Id}.json";
        if (!string.Equals(reference.Path, expectedReceiptPath, StringComparison.Ordinal))
        {
            throw new BundleValidationException(
                $"Runtime '{profile.Id}' promotion receipt path must be '{expectedReceiptPath}'.");
        }
        RequireSha256(reference.Sha256, $"Runtime '{profile.Id}' promotion receipt digest");

        var receiptFile = await ReadTrustedFileAsync(
            repositoryRoot,
            reference.Path,
            [ReceiptDirectory],
            MaximumReceiptBytes,
            cancellationToken);
        RequireDigestEqual(
            reference.Sha256,
            receiptFile.Sha256,
            $"Runtime '{profile.Id}' promotion receipt digest mismatch");

        var receipt = RuntimePromotionJson.Deserialize<RuntimePromotionReceiptDocument>(
            receiptFile.Bytes,
            ReceiptJsonOptions,
            $"Runtime '{profile.Id}' promotion receipt");

        ValidateReceiptIdentity(source, runtime, component, definition, profile, image, receipt);
        var operationFiles = ValidateOperationBindings(profile, receipt);
        var checks = await ValidateChecksAsync(
            repositoryRoot,
            profile,
            receipt,
            operationFiles,
            cancellationToken);
        var performance = await ValidatePerformanceAsync(
            repositoryRoot,
            profile,
            image,
            receipt,
            cancellationToken);
        RuntimePromotionFileSnapshot[] evidence = [.. checks.Evidence, performance.Evidence];
        return new RuntimePromotionTrustSnapshot(
            profile.Id,
            receipt.SourceRevision,
            receipt.PlanSha256,
            checks.PreflightProfileSha256,
            definition.ImmutableReference!,
            image.ImageId,
            image.SizeBytes,
            new RuntimePromotionFileSnapshot(receiptFile.RelativePath, receiptFile.Sha256),
            evidence,
            performance.Policy,
            checks.ImageFiles);
    }

    private static void ValidateReceiptIdentity(
        RepositorySourceProvenance source,
        RuntimeManifest runtime,
        LockedComponent component,
        DeploymentImageDefinition definition,
        RuntimeProfileDefinition profile,
        InspectedImage image,
        RuntimePromotionReceiptDocument receipt)
    {
        if (receipt.SchemaVersion != 2)
            throw new BundleValidationException($"Runtime '{profile.Id}' promotion receipt must use schema version 2.");
        RequireSha256(receipt.PlanSha256, $"Runtime '{profile.Id}' promotion receipt plan digest");
        RequireEqual(receipt.ProfileId, profile.Id, profile.Id, "profile ID");
        RequireEqual(receipt.MatrixTargetId, ExpectedMatrixTargetId(profile), profile.Id, "matrix target ID");
        RequireEqual(receipt.Platform, ExpectedPlatform(profile.Family), profile.Id, "platform");
        RequireEqual(receipt.Family, profile.Family, profile.Id, "family");
        RequireEqual(receipt.ResolvedVersion, profile.RuntimeVersion, profile.Id, "resolved version");

        if (!source.IsVerified || source.HeadRevision is null)
        {
            throw new BundleValidationException(
                $"Runtime '{profile.Id}' promotion receipt requires a clean, verified Git source revision.");
        }
        RequireCommit(receipt.SourceRevision, $"Runtime '{profile.Id}' promotion receipt source revision");
        if (StringComparer.Ordinal.Equals(receipt.SourceRevision, source.Revision))
        {
            throw new BundleValidationException(
                $"Runtime '{profile.Id}' promotion receipt implementation revision must be distinct " +
                "from the material release revision.");
        }
        if (!image.Labels.TryGetValue(
                RepositorySourceProvenanceResolver.ImageLabel,
                out var buildRevision) ||
            !StringComparer.Ordinal.Equals(buildRevision, receipt.SourceRevision) ||
            !image.Labels.TryGetValue("org.opencontainers.image.revision", out var ociRevision) ||
            !StringComparer.Ordinal.Equals(ociRevision, receipt.SourceRevision))
        {
            throw new BundleValidationException(
                $"Runtime '{profile.Id}' immutable image does not carry its receipt build source revision.");
        }

        if (receipt.Image is null)
            throw new BundleValidationException($"Runtime '{profile.Id}' promotion receipt has no image identity.");
        RequireImmutableReference(receipt.Image.Reference, $"Runtime '{profile.Id}' promotion receipt image reference");
        RequireSha256(receipt.Image.ImageId, $"Runtime '{profile.Id}' promotion receipt image ID");
        if (receipt.Image.SizeBytes <= 0)
            throw new BundleValidationException($"Runtime '{profile.Id}' promotion receipt image size is invalid.");
        RequireEqual(receipt.Image.Reference, definition.ImmutableReference, profile.Id, "image reference");
        RequireEqual(receipt.Image.Reference, profile.Image, profile.Id, "profile image reference");
        RequireEqual(receipt.Image.Reference, image.SourceReference, profile.Id, "inspected image reference");
        RequireEqual(receipt.Image.ImageId, image.ImageId, profile.Id, "inspected image ID");
        if (receipt.Image.SizeBytes != image.SizeBytes)
        {
            throw new BundleValidationException(
                $"Runtime '{profile.Id}' promotion receipt image size does not match the inspected image.");
        }
        if (!image.RepoDigests.Contains(receipt.Image.Reference, StringComparer.Ordinal))
        {
            throw new BundleValidationException(
                $"Runtime '{profile.Id}' promotion receipt reference is absent from the inspected image RepoDigests.");
        }

        if (receipt.ComponentIdentity is null)
            throw new BundleValidationException($"Runtime '{profile.Id}' promotion receipt has no component identity.");
        ValidateComponentIdentity(profile, component, receipt.ComponentIdentity);

        if (receipt.RuntimeIdentity is null)
            throw new BundleValidationException($"Runtime '{profile.Id}' promotion receipt has no runtime identity.");
        RequireEqual(receipt.RuntimeIdentity.RuntimeCommit, profile.RuntimeCommit, profile.Id, "runtime commit");
        RequireEqual(receipt.RuntimeIdentity.JitVersion, profile.JitVersion, profile.Id, "JIT version");
        RequireEqual(receipt.RuntimeIdentity.JitCommit, profile.JitCommit, profile.Id, "JIT commit");
        RequireEqual(runtime.ResolvedVersion, profile.RuntimeVersion, profile.Id, "Catalog runtime version");
    }

    private static void ValidateComponentIdentity(
        RuntimeProfileDefinition profile,
        LockedComponent component,
        RuntimePromotionComponentIdentity identity)
    {
        if (!IsImmutableSourceUri(identity.SourceUri))
        {
            throw new BundleValidationException(
                $"Runtime '{profile.Id}' promotion receipt component source URI is not immutable HTTPS or docker content.");
        }

        if (profile.Family is "coreclr" or "coreclr-wine")
        {
            if (component.SourceUri is null || component.Sha512 is null || !IsLowerHex(component.Sha512, 128))
            {
                throw new BundleValidationException(
                    $"Runtime '{profile.Id}' release-lock component has no immutable CoreCLR payload identity.");
            }
            RequireEqual(identity.SourceUri, component.SourceUri, profile.Id, "component source URI");
            RequireEqual(identity.SourceDigest, $"sha512:{component.Sha512}", profile.Id, "component source digest");
            return;
        }

        if (component.SourceUri is null || component.Digest is null)
        {
            throw new BundleValidationException(
                $"Runtime '{profile.Id}' release-lock component has no immutable operator identity.");
        }
        RequireSha256(component.Digest, $"Runtime '{profile.Id}' operator component digest");
        RequireEqual(identity.SourceUri, component.SourceUri, profile.Id, "component source URI");
        RequireEqual(identity.SourceDigest, component.Digest, profile.Id, "component source digest");
        if (identity.SourceUri.StartsWith("docker://", StringComparison.Ordinal) &&
            !identity.SourceUri.EndsWith("@" + identity.SourceDigest, StringComparison.Ordinal))
        {
            throw new BundleValidationException(
                $"Runtime '{profile.Id}' operator source URI and digest disagree.");
        }
    }

    internal static RuntimePromotionOperationFileIdentity[] ValidateOperationBindings(
        RuntimeProfileDefinition profile,
        RuntimePromotionReceiptDocument receipt)
    {
        var profileOperations = profile.Operations
            ?? throw new BundleValidationException(
                $"Runtime '{profile.Id}' has no explicit operation definitions.");
        var receiptOperations = receipt.Operations
            ?? throw new BundleValidationException(
                $"Runtime '{profile.Id}' promotion receipt has no operation bindings.");
        if (profileOperations.Run is null)
            throw new BundleValidationException($"Runtime '{profile.Id}' has no Run operation.");
        if (receiptOperations.Run is null)
            throw new BundleValidationException($"Runtime '{profile.Id}' promotion receipt has no Run helper.");

        var helpers = new Dictionary<string, string>(StringComparer.Ordinal);
        ValidateOperation(
            profile.Id,
            "run",
            profileOperations.Run,
            profile.Layout.RunnerAssemblyPath,
            expectedProfilerPath: null,
            receiptOperations.Run,
            helpers);

        if ((profileOperations.Jit is null) != (receiptOperations.Jit is null))
        {
            throw new BundleValidationException(
                $"Runtime '{profile.Id}' profile and promotion receipt disagree about the JIT operation.");
        }
        if (profileOperations.Jit is { } jit && receiptOperations.Jit is { } jitHelper)
        {
            ValidateOperation(
                profile.Id,
                "jit",
                jit,
                profile.Layout.JitInspectorAssemblyPath ?? profile.Layout.RunnerAssemblyPath,
                jit.ProfilerPath,
                jitHelper,
                helpers);
        }

        return helpers
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => new RuntimePromotionOperationFileIdentity(pair.Key, pair.Value))
            .ToArray();
    }

    private static void ValidateOperation(
        string profileId,
        string name,
        RuntimeOperationDefinition operation,
        string expectedAssemblyPath,
        string? expectedProfilerPath,
        RuntimePromotionOperationHelper helper,
        IDictionary<string, string> helpers)
    {
        RequireEqual(helper.Implementation, operation.ImplementationId, profileId, $"{name} implementation");
        RequireEqual(helper.AssemblyPath, expectedAssemblyPath, profileId, $"{name} assembly path");
        RequireCanonicalHelperPath(
            helper.AssemblyPath,
            StringComparer.Ordinal.Equals(
                operation.ImplementationId,
                RuntimeOperationImplementationIds.TargetRuntimeRunner)
                ? ".exe"
                : ".dll",
            profileId,
            name);
        RequireSha256(helper.AssemblySha256, $"Runtime '{profileId}' {name} assembly digest");
        AddHelper(helpers, profileId, helper.AssemblyPath, helper.AssemblySha256);

        if ((helper.ProfilerPath is null) != (helper.ProfilerSha256 is null))
        {
            throw new BundleValidationException(
                $"Runtime '{profileId}' {name} profiler path and digest must be declared together.");
        }
        RequireEqual(helper.ProfilerPath, expectedProfilerPath, profileId, $"{name} profiler path");
        if (helper.ProfilerPath is not null)
        {
            RequireCanonicalHelperPath(helper.ProfilerPath, ".so", profileId, name);
            RequireSha256(helper.ProfilerSha256!, $"Runtime '{profileId}' {name} profiler digest");
            AddHelper(helpers, profileId, helper.ProfilerPath, helper.ProfilerSha256!);
        }
    }

    private static async Task<RuntimePromotionChecksSnapshot> ValidateChecksAsync(
        string repositoryRoot,
        RuntimeProfileDefinition profile,
        RuntimePromotionReceiptDocument receipt,
        IReadOnlyList<RuntimePromotionOperationFileIdentity> operationFiles,
        CancellationToken cancellationToken)
    {
        if (receipt.Checks is null || receipt.Checks.Count is < 1 or > 4 ||
            receipt.Checks.Any(static check => check is null))
            throw new BundleValidationException($"Runtime '{profile.Id}' promotion receipt has no bounded capability checks.");
        var expected = profile.Capabilities.Order(StringComparer.Ordinal).ToArray();
        if (expected.Length != expected.Distinct(StringComparer.Ordinal).Count())
            throw new BundleValidationException($"Runtime '{profile.Id}' has duplicate capabilities.");
        var observed = receipt.Checks.Select(static check => check!.Capability).Order(StringComparer.Ordinal).ToArray();
        if (!expected.SequenceEqual(observed, StringComparer.Ordinal))
        {
            throw new BundleValidationException(
                $"Runtime '{profile.Id}' promotion checks do not exactly cover its declared capabilities.");
        }

        var evidenceSnapshots = new List<RuntimePromotionFileSnapshot>(receipt.Checks.Count);
        var imageFiles = new Dictionary<string, RuntimePromotionImageFileSnapshot>(StringComparer.Ordinal);
        var probeArtifacts = new Dictionary<string, RuntimeCapabilityProbeArtifactSnapshot>(StringComparer.Ordinal);
        foreach (var check in receipt.Checks.Select(static check => check!)
                     .OrderBy(static check => check.Capability, StringComparer.Ordinal))
        {
            if (!IsCapability(check.Capability) ||
                !string.Equals(check.Result, "passed", StringComparison.Ordinal) ||
                !check.NetworkDisabled ||
                !check.SupervisorSandbox ||
                !check.OutputLimitValidated)
            {
                throw new BundleValidationException(
                    $"Runtime '{profile.Id}' promotion check '{check.Capability}' is not complete and passing.");
            }
            ValidateMapping(profile, receipt, check);
            var expectedPath = $"{EvidenceDirectory}/{profile.Id}/{check.Capability}.json";
            if (!string.Equals(check.EvidencePath, expectedPath, StringComparison.Ordinal))
            {
                throw new BundleValidationException(
                    $"Runtime '{profile.Id}' promotion evidence path must be '{expectedPath}'.");
            }
            RequireSha256(check.EvidenceSha256, $"Runtime '{profile.Id}' {check.Capability} evidence digest");
            var evidence = await ReadTrustedFileAsync(
                repositoryRoot,
                check.EvidencePath,
                [EvidenceDirectory, $"{EvidenceDirectory}/{profile.Id}"],
                MaximumEvidenceBytes,
                cancellationToken);
            RequireDigestEqual(
                check.EvidenceSha256,
                evidence.Sha256,
                $"Runtime '{profile.Id}' {check.Capability} evidence digest mismatch");
            evidenceSnapshots.Add(new RuntimePromotionFileSnapshot(evidence.RelativePath, evidence.Sha256));
            var validatedArtifacts = RuntimeCapabilityEvidenceValidation.Validate(
                evidence.Bytes,
                profile,
                receipt,
                check,
                out var probeArtifact);
            probeArtifacts.Add(check.Capability, probeArtifact);
            foreach (var artifact in validatedArtifacts)
            {
                if (imageFiles.TryGetValue(artifact.Path, out var existing) &&
                    (!string.Equals(existing.Sha256, artifact.Sha256, StringComparison.Ordinal) ||
                     existing.SizeBytes != artifact.SizeBytes ||
                     !string.Equals(existing.Role, artifact.Role, StringComparison.Ordinal) ||
                     !string.Equals(existing.Format, artifact.Format, StringComparison.Ordinal) ||
                     !string.Equals(existing.Architecture, artifact.Architecture, StringComparison.Ordinal)))
                {
                    throw new BundleValidationException(
                        $"Runtime '{profile.Id}' capability evidence assigns conflicting path, byte, role, format, or architecture identities to image file '{artifact.Path}'.");
                }
                imageFiles[artifact.Path] = artifact;
            }
        }
        RuntimeCapabilityEvidenceValidation.ValidateProbeSet(profile.Id, probeArtifacts);

        foreach (var operationFile in operationFiles)
        {
            if (!imageFiles.TryGetValue(operationFile.Path, out var artifact))
            {
                throw new BundleValidationException(
                    $"Runtime '{profile.Id}' capability evidence does not retain operation file '{operationFile.Path}'.");
            }
            RequireDigestEqual(
                operationFile.Sha256,
                artifact.Sha256,
                $"Runtime '{profile.Id}' operation file '{operationFile.Path}' does not match capability evidence");
        }

        return new RuntimePromotionChecksSnapshot(
            evidenceSnapshots,
            imageFiles.Values.OrderBy(static file => file.Path, StringComparer.Ordinal).ToArray(),
            probeArtifacts["run"].PreflightProfileSha256);
    }

    private static async Task<RuntimePromotionPerformanceSnapshot> ValidatePerformanceAsync(
        string repositoryRoot,
        RuntimeProfileDefinition profile,
        InspectedImage image,
        RuntimePromotionReceiptDocument receipt,
        CancellationToken cancellationToken)
    {
        var binding = receipt.Performance
            ?? throw new BundleValidationException(
                $"Runtime '{profile.Id}' promotion receipt has no performance evidence binding.");
        if (!string.Equals(binding.Result, "passed", StringComparison.Ordinal))
        {
            throw new BundleValidationException(
                $"Runtime '{profile.Id}' performance evidence is not passing.");
        }
        RequireCanonicalId(binding.PolicyId, $"Runtime '{profile.Id}' performance policy ID");
        var expectedPolicyPath = $"{PerformancePolicyDirectory}/{binding.PolicyId}.json";
        if (!string.Equals(binding.PolicyPath, expectedPolicyPath, StringComparison.Ordinal))
        {
            throw new BundleValidationException(
                $"Runtime '{profile.Id}' performance policy path must be '{expectedPolicyPath}'.");
        }
        var expectedEvidencePath = $"{EvidenceDirectory}/{profile.Id}/performance.json";
        if (!string.Equals(binding.EvidencePath, expectedEvidencePath, StringComparison.Ordinal))
        {
            throw new BundleValidationException(
                $"Runtime '{profile.Id}' performance evidence path must be '{expectedEvidencePath}'.");
        }
        RequireSha256(binding.PolicySha256, $"Runtime '{profile.Id}' performance policy digest");
        RequireSha256(binding.EvidenceSha256, $"Runtime '{profile.Id}' performance evidence digest");

        var policyFile = await ReadTrustedFileAsync(
            repositoryRoot,
            binding.PolicyPath,
            [PerformancePolicyDirectory],
            MaximumPerformancePolicyBytes,
            cancellationToken);
        RequireDigestEqual(
            binding.PolicySha256,
            policyFile.Sha256,
            $"Runtime '{profile.Id}' performance policy digest mismatch");
        var evidenceFile = await ReadTrustedFileAsync(
            repositoryRoot,
            binding.EvidencePath,
            [EvidenceDirectory, $"{EvidenceDirectory}/{profile.Id}"],
            MaximumEvidenceBytes,
            cancellationToken);
        RequireDigestEqual(
            binding.EvidenceSha256,
            evidenceFile.Sha256,
            $"Runtime '{profile.Id}' performance evidence digest mismatch");

        var policy = DeserializePerformanceDocument<RuntimePerformancePolicyDocument>(
            policyFile.Bytes,
            profile.Id,
            "policy");
        var evidence = DeserializePerformanceDocument<RuntimePerformanceEvidenceDocument>(
            evidenceFile.Bytes,
            profile.Id,
            "evidence");
        ValidatePerformancePolicy(profile.Id, binding, policy);
        ValidatePerformanceEvidence(profile, image, receipt, binding, policy, evidence);
        return new RuntimePromotionPerformanceSnapshot(
            new RuntimePromotionFileSnapshot(policyFile.RelativePath, policyFile.Sha256),
            new RuntimePromotionFileSnapshot(evidenceFile.RelativePath, evidenceFile.Sha256));
    }

    private static T DeserializePerformanceDocument<T>(byte[] bytes, string profileId, string kind)
    {
        return RuntimePromotionJson.Deserialize<T>(
            bytes,
            ReceiptJsonOptions,
            $"Runtime '{profileId}' performance {kind}");
    }

    internal static void ValidatePerformancePolicy(
        string profileId,
        RuntimePromotionPerformanceBinding binding,
        RuntimePerformancePolicyDocument policy)
    {
        if (policy.SchemaVersion != 1 ||
            !string.Equals(policy.Id, binding.PolicyId, StringComparison.Ordinal))
        {
            throw new BundleValidationException(
                $"Runtime '{profileId}' performance policy identity is invalid.");
        }
        if (policy.SampleCounts is null ||
            policy.SampleCounts.Cold is < MinimumColdPerformanceSamples or > MaximumColdPerformanceSamples ||
            policy.SampleCounts.Warm is < MinimumWarmPerformanceSamples or > MaximumWarmPerformanceSamples)
        {
            throw new BundleValidationException(
                $"Runtime '{profileId}' performance policy has unsafe sample counts.");
        }
        if (policy.ResourceLimits is null ||
            policy.ResourceLimits.NanoCpus is < MinimumPerformanceNanoCpus or > MaximumPerformanceNanoCpus ||
            policy.ResourceLimits.AllowedMemoryBytes is null or { Count: < 1 or > 8 } ||
            policy.ResourceLimits.AllowedMemoryBytes.Any(static value =>
                value is < MinimumPerformanceMemoryBytes or > MaximumPerformanceMemoryBytes) ||
            policy.ResourceLimits.AllowedMemoryBytes.Distinct().Count() !=
            policy.ResourceLimits.AllowedMemoryBytes.Count ||
            !policy.ResourceLimits.AllowedMemoryBytes.SequenceEqual(
                policy.ResourceLimits.AllowedMemoryBytes.Order()))
        {
            throw new BundleValidationException(
                $"Runtime '{profileId}' performance policy has unsafe resource limits.");
        }
        if (policy.Image is null ||
            policy.Image.MaximumSizeBytes is < 1 or > MaximumPerformanceImageBytes)
        {
            throw new BundleValidationException(
                $"Runtime '{profileId}' performance policy has an unsafe image-size limit.");
        }
        if (policy.Scenarios is null ||
            policy.Scenarios.Run is null ||
            policy.Scenarios.Jit is null ||
            policy.Scenarios.Mapping is null)
        {
            throw new BundleValidationException(
                $"Runtime '{profileId}' performance policy does not define every scenario.");
        }
        ValidatePerformanceScenarioBudget(profileId, "run", policy.Scenarios.Run);
        ValidatePerformanceScenarioBudget(profileId, "jit", policy.Scenarios.Jit);
        ValidatePerformanceScenarioBudget(profileId, "mapping", policy.Scenarios.Mapping);
    }

    private static void ValidatePerformanceScenarioBudget(
        string profileId,
        string scenario,
        RuntimePerformanceScenarioBudget budget)
    {
        if (budget.Cold is null || budget.Warm is null)
        {
            throw new BundleValidationException(
                $"Runtime '{profileId}' performance policy scenario '{scenario}' is incomplete.");
        }
        ValidatePerformanceModeBudget(profileId, scenario, "cold", budget.Cold);
        ValidatePerformanceModeBudget(profileId, scenario, "warm", budget.Warm);
    }

    private static void ValidatePerformanceModeBudget(
        string profileId,
        string scenario,
        string mode,
        RuntimePerformanceModeBudget budget)
    {
        if (!double.IsFinite(budget.MaximumP95LatencyMilliseconds) ||
            budget.MaximumP95LatencyMilliseconds <= 0 ||
            budget.MaximumP95LatencyMilliseconds > MaximumPerformanceP95Milliseconds ||
            !double.IsFinite(budget.MaximumSampleLatencyMilliseconds) ||
            budget.MaximumSampleLatencyMilliseconds <= 0 ||
            budget.MaximumSampleLatencyMilliseconds > MaximumPerformanceSampleMilliseconds ||
            budget.MaximumP95LatencyMilliseconds > budget.MaximumSampleLatencyMilliseconds ||
            budget.MaximumPeakMemoryBytes is < 1 or > MaximumPerformanceMemoryBytes)
        {
            throw new BundleValidationException(
                $"Runtime '{profileId}' performance policy budget '{scenario}.{mode}' is unsafe.");
        }
    }

    internal static void ValidatePerformanceEvidence(
        RuntimeProfileDefinition profile,
        InspectedImage image,
        RuntimePromotionReceiptDocument receipt,
        RuntimePromotionPerformanceBinding binding,
        RuntimePerformancePolicyDocument policy,
        RuntimePerformanceEvidenceDocument evidence)
    {
        if (evidence.SchemaVersion != 1 ||
            !string.Equals(evidence.PlanSha256, receipt.PlanSha256, StringComparison.Ordinal) ||
            !string.Equals(evidence.ProfileId, profile.Id, StringComparison.Ordinal) ||
            !string.Equals(evidence.SourceRevision, receipt.SourceRevision, StringComparison.Ordinal) ||
            !string.Equals(evidence.Result, "passed", StringComparison.Ordinal))
        {
            throw new BundleValidationException(
                $"Runtime '{profile.Id}' performance evidence identity is invalid.");
        }
        if (!IsCanonicalUtcTimestamp(evidence.CompletedAtUtc))
        {
            throw new BundleValidationException(
                $"Runtime '{profile.Id}' performance evidence timestamp is not canonical UTC.");
        }
        if (evidence.Image is null ||
            !string.Equals(evidence.Image.Reference, receipt.Image.Reference, StringComparison.Ordinal) ||
            !string.Equals(evidence.Image.ImageId, receipt.Image.ImageId, StringComparison.Ordinal) ||
            evidence.Image.SizeBytes != receipt.Image.SizeBytes ||
            evidence.Image.SizeBytes != image.SizeBytes ||
            evidence.Image.SizeBytes <= 0 ||
            evidence.Image.SizeBytes > policy.Image.MaximumSizeBytes)
        {
            throw new BundleValidationException(
                $"Runtime '{profile.Id}' performance evidence image identity or size is invalid.");
        }
        if (evidence.Policy is null ||
            !string.Equals(evidence.Policy.Id, binding.PolicyId, StringComparison.Ordinal) ||
            !string.Equals(evidence.Policy.Sha256, binding.PolicySha256, StringComparison.Ordinal))
        {
            throw new BundleValidationException(
                $"Runtime '{profile.Id}' performance evidence policy identity is invalid.");
        }

        var expectedCapabilities = profile.Capabilities.Order(StringComparer.Ordinal).ToArray();
        if (evidence.Capabilities is null ||
            !expectedCapabilities.SequenceEqual(evidence.Capabilities, StringComparer.Ordinal))
        {
            throw new BundleValidationException(
                $"Runtime '{profile.Id}' performance evidence does not cover its exact capabilities.");
        }
        var jitCheck = receipt.Checks.SingleOrDefault(static check => check?.Capability == "jit-asm");
        var expectedMappingKind = jitCheck?.SourceMappingKind ?? "not-applicable";
        if (!string.Equals(evidence.SourceMappingKind, expectedMappingKind, StringComparison.Ordinal))
        {
            throw new BundleValidationException(
                $"Runtime '{profile.Id}' performance evidence mapping kind is invalid.");
        }
        if (evidence.Environment is null ||
            string.IsNullOrWhiteSpace(evidence.Environment.RunnerId) ||
            !IsCanonicalId(evidence.Environment.RunnerId) ||
            !string.Equals(evidence.Environment.OperatingSystem, "linux", StringComparison.Ordinal) ||
            !string.Equals(evidence.Environment.Architecture, "x64", StringComparison.Ordinal) ||
            evidence.Environment.NanoCpus != policy.ResourceLimits.NanoCpus ||
            !policy.ResourceLimits.AllowedMemoryBytes.Contains(evidence.Environment.MemoryLimitBytes))
        {
            throw new BundleValidationException(
                $"Runtime '{profile.Id}' performance evidence environment is invalid.");
        }
        if (evidence.Scenarios?.Run is null)
        {
            throw new BundleValidationException(
                $"Runtime '{profile.Id}' performance evidence has no Run scenario.");
        }

        // Operation IDs identify the individual Supervisor measurements. They must be
        // unique across the complete evidence document, not merely within one mode,
        // otherwise a replayed sample could be counted once per scenario.
        var operationIds = new HashSet<string>(StringComparer.Ordinal);

        var requiresJit = expectedCapabilities.Contains("jit-asm", StringComparer.Ordinal);
        var requiresMapping = expectedMappingKind is not ("none" or "not-applicable");
        if ((evidence.Scenarios.Jit is not null) != requiresJit ||
            (evidence.Scenarios.Mapping is not null) != requiresMapping)
        {
            throw new BundleValidationException(
                $"Runtime '{profile.Id}' performance evidence has an invalid scenario set.");
        }
        ValidatePerformanceScenario(
            profile.Id,
            "run",
            evidence.Scenarios.Run,
            policy.Scenarios.Run,
            policy.SampleCounts,
            evidence.Environment.MemoryLimitBytes,
            operationIds);
        if (evidence.Scenarios.Jit is { } jit)
        {
            ValidatePerformanceScenario(
                profile.Id,
                "jit",
                jit,
                policy.Scenarios.Jit,
                policy.SampleCounts,
                evidence.Environment.MemoryLimitBytes,
                operationIds);
        }
        if (evidence.Scenarios.Mapping is { } mapping)
        {
            ValidatePerformanceScenario(
                profile.Id,
                "mapping",
                mapping,
                policy.Scenarios.Mapping,
                policy.SampleCounts,
                evidence.Environment.MemoryLimitBytes,
                operationIds);
        }
    }

    private static void ValidatePerformanceScenario(
        string profileId,
        string name,
        RuntimePerformanceScenarioMeasurement scenario,
        RuntimePerformanceScenarioBudget budget,
        RuntimePerformanceSampleCounts counts,
        long memoryLimitBytes,
        HashSet<string> operationIds)
    {
        ValidatePerformanceSamples(
            profileId,
            name,
            "cold",
            scenario.Cold,
            counts.Cold,
            budget.Cold,
            memoryLimitBytes,
            operationIds);
        ValidatePerformanceSamples(
            profileId,
            name,
            "warm",
            scenario.Warm,
            counts.Warm,
            budget.Warm,
            memoryLimitBytes,
            operationIds);
    }

    private static void ValidatePerformanceSamples(
        string profileId,
        string scenario,
        string mode,
        List<RuntimePerformanceSample?>? samples,
        int expectedCount,
        RuntimePerformanceModeBudget budget,
        long memoryLimitBytes,
        HashSet<string> operationIds)
    {
        if (samples is null || samples.Count != expectedCount || samples.Any(static sample => sample is null))
        {
            throw new BundleValidationException(
                $"Runtime '{profileId}' performance evidence '{scenario}.{mode}' must contain " +
                $"exactly {expectedCount} samples.");
        }
        var latencies = new double[samples.Count];
        for (var index = 0; index < samples.Count; index++)
        {
            var sample = samples[index]!;
            if (!IsOperationId(sample.OperationId) ||
                sample.ResourceSampleCount is < 1 or > 1_000_000 ||
                !IsCanonicalUtcTimestamp(sample.CompletedAtUtc) ||
                !double.IsFinite(sample.LatencyMilliseconds) ||
                sample.LatencyMilliseconds <= 0 ||
                sample.LatencyMilliseconds > budget.MaximumSampleLatencyMilliseconds ||
                sample.PeakMemoryBytes <= 0 ||
                sample.PeakMemoryBytes > budget.MaximumPeakMemoryBytes ||
                sample.PeakMemoryBytes > memoryLimitBytes)
            {
                throw new BundleValidationException(
                    $"Runtime '{profileId}' performance sample '{scenario}.{mode}[{index}]' " +
                    "exceeds its latency or memory budget.");
            }
            latencies[index] = sample.LatencyMilliseconds;
            if (!operationIds.Add(sample.OperationId))
            {
                throw new BundleValidationException(
                    $"Runtime '{profileId}' performance evidence '{scenario}.{mode}' reuses an operation ID.");
            }
        }
        Array.Sort(latencies);
        var p95 = latencies[Math.Max(0, (int)Math.Ceiling(latencies.Length * 0.95) - 1)];
        if (p95 > budget.MaximumP95LatencyMilliseconds)
        {
            throw new BundleValidationException(
                $"Runtime '{profileId}' performance evidence '{scenario}.{mode}' exceeds its P95 budget.");
        }
    }

    private static void ValidateMapping(
        RuntimeProfileDefinition profile,
        RuntimePromotionReceiptDocument receipt,
        RuntimePromotionCapabilityCheck check)
    {
        if (!string.Equals(check.Capability, "jit-asm", StringComparison.Ordinal))
        {
            if (!string.Equals(check.SourceMappingKind, "not-applicable", StringComparison.Ordinal) ||
                !string.Equals(check.MappingSource, "not-applicable", StringComparison.Ordinal))
            {
                throw new BundleValidationException(
                    $"Runtime '{profile.Id}' non-JIT promotion check cannot claim source mapping.");
            }
            return;
        }

        var jit = profile.Operations?.Jit
            ?? throw new BundleValidationException(
                $"Runtime '{profile.Id}' promotes jit-asm without a JIT operation.");
        if (!string.Equals(check.SourceMappingKind, jit.SourceMappingKind, StringComparison.Ordinal))
        {
            throw new BundleValidationException(
                $"Runtime '{profile.Id}' JIT mapping kind disagrees with its promotion receipt.");
        }
        if (profile.Family == "netfx-clr-wine")
            throw new BundleValidationException($"Runtime '{profile.Id}' family cannot promote jit-asm.");
        if (string.Equals(check.SourceMappingKind, "linux-profiler", StringComparison.Ordinal))
        {
            if (profile.Family != "coreclr" ||
                check.MappingSource is not ("ordinary" or "rich") ||
                receipt.Operations?.Jit?.ProfilerPath is null)
            {
                throw new BundleValidationException(
                    $"Runtime '{profile.Id}' linux-profiler promotion lacks profiler-backed mapping evidence.");
            }
        }
        else if (string.Equals(
                     check.SourceMappingKind,
                     RuntimeJitSourceMappingKinds.CheckedJitDebugInfo,
                     StringComparison.Ordinal))
        {
            if (profile.Family != "coreclr" ||
                !string.Equals(
                    check.MappingSource,
                    RuntimeJitSourceMappingKinds.CheckedJitDebugInfo,
                    StringComparison.Ordinal) ||
                receipt.Operations?.Jit?.ProfilerPath is not null)
            {
                throw new BundleValidationException(
                    $"Runtime '{profile.Id}' checked-JIT promotion lacks debug-info mapping evidence.");
            }
        }
        else if (!string.Equals(check.SourceMappingKind, "none", StringComparison.Ordinal) ||
                 check.MappingSource is not ("none" or "method"))
        {
            throw new BundleValidationException(
                $"Runtime '{profile.Id}' mapping-free or method-level JIT promotion has invalid mapping evidence.");
        }
    }

    private static async Task<TrustedFile> ReadTrustedFileAsync(
        string repositoryRoot,
        string relativePath,
        IReadOnlyList<string> trustedDirectories,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            relativePath.Contains('\\') ||
            relativePath.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            throw new BundleValidationException($"Promotion material path '{relativePath}' is not canonical.");
        }

        var root = Path.GetFullPath(repositoryRoot);
        var absolutePath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var deepestDirectory = Path.GetFullPath(Path.Combine(
            root,
            trustedDirectories[^1].Replace('/', Path.DirectorySeparatorChar)));
        if (!IsPathInside(deepestDirectory, absolutePath))
            throw new BundleValidationException($"Promotion material '{relativePath}' escapes its trusted directory.");
        foreach (var relativeDirectory in trustedDirectories)
        {
            EnsureRegularDirectory(Path.Combine(
                root,
                relativeDirectory.Replace('/', Path.DirectorySeparatorChar)));
        }

        var info = new FileInfo(absolutePath);
        info.Refresh();
        if (!info.Exists || info.LinkTarget is not null ||
            info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new BundleValidationException(
                $"Promotion material '{relativePath}' must be a regular non-link file.");
        }
        if (info.Length > maximumBytes)
        {
            throw new BundleValidationException(
                $"Promotion material '{relativePath}' exceeds the {maximumBytes}-byte limit.");
        }

        byte[] bytes;
        try
        {
            await using var stream = new FileStream(
                absolutePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > maximumBytes)
            {
                throw new BundleValidationException(
                    $"Promotion material '{relativePath}' exceeds the {maximumBytes}-byte limit.");
            }
            bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            if (stream.ReadByte() != -1)
            {
                throw new BundleValidationException(
                    $"Promotion material '{relativePath}' changed while it was being read.");
            }
        }
        catch (BundleValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new BundleValidationException(
                $"Promotion material '{relativePath}' could not be read: {exception.Message}");
        }

        return new TrustedFile(
            relativePath,
            $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}",
            bytes);
    }

    private static void EnsureRegularDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        info.Refresh();
        if (!info.Exists || info.LinkTarget is not null ||
            info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new BundleValidationException(
                $"Promotion material directory '{path}' must be a regular non-link directory.");
        }
    }

    private static bool IsPathInside(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative.Length > 0 &&
               relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    internal static string ExpectedPlatform(string family) => family switch
    {
        "coreclr" => "linux",
        "coreclr-wine" => "wine",
        "mono" => "mono",
        "netfx-clr-wine" => "framework",
        _ => throw new BundleValidationException(
            $"Runtime family '{family}' cannot use a matrix promotion receipt.")
    };

    internal static string ExpectedMatrixTargetId(RuntimeProfileDefinition profile)
    {
        const string suffix = "-linux-x64";
        return profile.Family switch
        {
            "mono" => profile.Id,
            "coreclr" when profile.Id.EndsWith(suffix, StringComparison.Ordinal) =>
                profile.Id[..^suffix.Length],
            "coreclr-wine" or "netfx-clr-wine"
                when profile.Id.StartsWith("wine-", StringComparison.Ordinal) &&
                     profile.Id.EndsWith(suffix, StringComparison.Ordinal) =>
                profile.Id["wine-".Length..^suffix.Length],
            _ => throw new BundleValidationException(
                $"Runtime '{profile.Id}' cannot be bound to a canonical matrix target ID.")
        };
    }

    private static bool IsImmutableSourceUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
            return false;
        if (value.StartsWith("docker://", StringComparison.Ordinal))
            return IsImmutableReference(value["docker://".Length..]);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) &&
               uri.Host.Length > 0 &&
               uri.UserInfo.Length == 0;
    }

    private static void RequireImmutableReference(string? value, string label)
    {
        if (!IsImmutableReference(value))
            throw new BundleValidationException($"{label} must be repository@sha256:<64 lowercase hex>.");
    }

    private static bool IsImmutableReference(string? value)
    {
        if (value is null)
            return false;
        var marker = value.LastIndexOf("@sha256:", StringComparison.Ordinal);
        return marker > 0 &&
               value.IndexOf('@') == marker &&
               value.Length == marker + 8 + 64 &&
               !value.Any(char.IsWhiteSpace) &&
               IsLowerHex(value[(marker + 8)..], 64);
    }

    private static void RequireCanonicalHelperPath(
        string? value,
        string extension,
        string profileId,
        string operation)
    {
        const string prefix = "/opt/sharplabnext/";
        if (value is null ||
            !value.StartsWith(prefix, StringComparison.Ordinal) ||
            !value.EndsWith(extension, StringComparison.Ordinal) ||
            value[prefix.Length..].Length == extension.Length ||
            value[prefix.Length..].Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
        {
            throw new BundleValidationException(
                $"Runtime '{profileId}' {operation} helper path '{value}' is not canonical.");
        }
    }

    private static void AddHelper(
        IDictionary<string, string> helpers,
        string profileId,
        string path,
        string digest)
    {
        if (helpers.TryGetValue(path, out var existing) &&
            !string.Equals(existing, digest, StringComparison.Ordinal))
        {
            throw new BundleValidationException(
                $"Runtime '{profileId}' receipt assigns different digests to helper '{path}'.");
        }
        helpers[path] = digest;
    }

    private static void RequireEqual(
        string? actual,
        string? expected,
        string profileId,
        string field)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new BundleValidationException(
                $"Runtime '{profileId}' promotion receipt {field} does not match active release material.");
        }
    }

    private static void RequireCommit(string? value, string label)
    {
        if (!IsLowerHex(value, 40) && !IsLowerHex(value, 64))
            throw new BundleValidationException($"{label} must be a full lowercase Git commit.");
    }

    private static void RequireSha256(string? value, string label)
    {
        if (value is null ||
            value.Length != 71 ||
            !value.StartsWith("sha256:", StringComparison.Ordinal) ||
            !IsLowerHex(value[7..], 64))
        {
            throw new BundleValidationException($"{label} must be sha256:<64 lowercase hex>.");
        }
    }

    private static void RequireCanonicalId(string? value, string label)
    {
        if (!IsCanonicalId(value))
            throw new BundleValidationException($"{label} is not canonical.");
    }

    private static bool IsCanonicalUtcTimestamp(string? value) =>
        value is not null && DateTimeOffset.TryParseExact(
            value,
            ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out _);

    private static bool IsOperationId(string? value) =>
        value is { Length: 35 } && value.StartsWith("op_", StringComparison.Ordinal) &&
        value[3..].All(static character =>
            char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');

    private static bool IsCanonicalId(string? value) =>
        value is { Length: > 0 and <= 128 } &&
        value[0] is >= 'a' and <= 'z' or >= '0' and <= '9' &&
        value.All(static character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');

    private static bool IsLowerHex(string? value, int length) =>
        value is not null &&
        value.Length == length &&
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsCapability(string? value) =>
        value is "run" or "jit-asm" or "inspection" or "execution-flow";

    private static bool HasCanonicalRetainedMetadata(RuntimePromotionImageFileSnapshot artifact) =>
        artifact.Role switch
        {
            "helper" or "support-assembly" =>
                artifact.Format == "managed-pe" && artifact.Architecture == "anycpu",
            "control-host" or "runtime-host" or "jit-library" =>
                (artifact.Format is "elf" or "pe") && artifact.Architecture == "x64",
            "profiler" => artifact.Format == "elf" && artifact.Architecture == "x64",
            _ => false
        };

    private static void RequireDigestEqual(string? expected, string? actual, string label)
    {
        RequireSha256(expected, label);
        RequireSha256(actual, label);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected!),
                Encoding.ASCII.GetBytes(actual!)))
        {
            throw new BundleValidationException($"{label}; expected {expected}, observed {actual}.");
        }
    }

    private sealed record TrustedFile(string RelativePath, string Sha256, byte[] Bytes);

    private sealed record RuntimePromotionPerformanceSnapshot(
        RuntimePromotionFileSnapshot Policy,
        RuntimePromotionFileSnapshot Evidence);
}

internal sealed record RuntimePromotionTrustSnapshot(
    string RuntimeId,
    string BuildSourceRevision,
    string PlanSha256,
    string PreflightProfileSha256,
    string ImmutableReference,
    string ImageId,
    long ImageSizeBytes,
    RuntimePromotionFileSnapshot Receipt,
    IReadOnlyList<RuntimePromotionFileSnapshot> Evidence,
    RuntimePromotionFileSnapshot PerformancePolicy,
    IReadOnlyList<RuntimePromotionImageFileSnapshot> ImageFiles);

internal sealed record RuntimePromotionFileSnapshot(string RelativePath, string Sha256);

internal sealed record RuntimePromotionOperationFileIdentity(string Path, string Sha256);

internal sealed record RuntimePromotionImageFileSnapshot(
    string Path,
    string Sha256,
    long SizeBytes,
    string Role,
    string Format,
    string Architecture);

internal sealed record RuntimePromotionChecksSnapshot(
    IReadOnlyList<RuntimePromotionFileSnapshot> Evidence,
    IReadOnlyList<RuntimePromotionImageFileSnapshot> ImageFiles,
    string PreflightProfileSha256);

internal sealed class RuntimePromotionReceiptDocument
{
    public required int SchemaVersion { get; init; }
    public required string PlanSha256 { get; init; }
    public required string ProfileId { get; init; }
    public required string MatrixTargetId { get; init; }
    public required string Platform { get; init; }
    public required string Family { get; init; }
    public required string ResolvedVersion { get; init; }
    public required RuntimePromotionImageIdentity Image { get; init; }
    public required RuntimePromotionComponentIdentity ComponentIdentity { get; init; }
    public required RuntimePromotionRuntimeIdentity RuntimeIdentity { get; init; }
    public required RuntimePromotionOperations Operations { get; init; }
    public required RuntimePromotionPerformanceBinding Performance { get; init; }
    public required string SourceRevision { get; init; }
    public required List<RuntimePromotionCapabilityCheck?> Checks { get; init; }
}

internal sealed class RuntimePromotionImageIdentity
{
    public required string Reference { get; init; }
    public required string ImageId { get; init; }
    public required long SizeBytes { get; init; }
}

internal sealed class RuntimePromotionPerformanceBinding
{
    public required string Result { get; init; }
    public required string PolicyId { get; init; }
    public required string PolicyPath { get; init; }
    public required string PolicySha256 { get; init; }
    public required string EvidencePath { get; init; }
    public required string EvidenceSha256 { get; init; }
}

internal sealed class RuntimePromotionComponentIdentity
{
    public required string SourceUri { get; init; }
    public required string SourceDigest { get; init; }
}

internal sealed class RuntimePromotionRuntimeIdentity
{
    public required string RuntimeCommit { get; init; }
    public required string JitVersion { get; init; }
    public required string JitCommit { get; init; }
}

internal sealed class RuntimePromotionOperations
{
    public required RuntimePromotionOperationHelper Run { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimePromotionOperationHelper? Jit { get; init; }
}

internal sealed class RuntimePromotionOperationHelper
{
    public required string Implementation { get; init; }
    public required string AssemblyPath { get; init; }
    public required string AssemblySha256 { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProfilerPath { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProfilerSha256 { get; init; }
}

internal sealed class RuntimePromotionCapabilityCheck
{
    public required string Capability { get; init; }
    public required string Result { get; init; }
    public required bool NetworkDisabled { get; init; }
    public required bool SupervisorSandbox { get; init; }
    public required bool OutputLimitValidated { get; init; }
    public required string SourceMappingKind { get; init; }
    public required string MappingSource { get; init; }
    public required string EvidencePath { get; init; }
    public required string EvidenceSha256 { get; init; }
}

internal sealed class RuntimePerformancePolicyDocument
{
    public required int SchemaVersion { get; init; }
    public required string Id { get; init; }
    public required RuntimePerformanceSampleCounts SampleCounts { get; init; }
    public required RuntimePerformanceResourceLimits ResourceLimits { get; init; }
    public required RuntimePerformanceImageBudget Image { get; init; }
    public required RuntimePerformancePolicyScenarios Scenarios { get; init; }
}

internal sealed class RuntimePerformanceSampleCounts
{
    public required int Cold { get; init; }
    public required int Warm { get; init; }
}

internal sealed class RuntimePerformanceResourceLimits
{
    public required long NanoCpus { get; init; }
    public required List<long> AllowedMemoryBytes { get; init; }
}

internal sealed class RuntimePerformanceImageBudget
{
    public required long MaximumSizeBytes { get; init; }
}

internal sealed class RuntimePerformancePolicyScenarios
{
    public required RuntimePerformanceScenarioBudget Run { get; init; }
    public required RuntimePerformanceScenarioBudget Jit { get; init; }
    public required RuntimePerformanceScenarioBudget Mapping { get; init; }
}

internal sealed class RuntimePerformanceScenarioBudget
{
    public required RuntimePerformanceModeBudget Cold { get; init; }
    public required RuntimePerformanceModeBudget Warm { get; init; }
}

internal sealed class RuntimePerformanceModeBudget
{
    public required double MaximumP95LatencyMilliseconds { get; init; }
    public required double MaximumSampleLatencyMilliseconds { get; init; }
    public required long MaximumPeakMemoryBytes { get; init; }
}

internal sealed class RuntimePerformanceEvidenceDocument
{
    public required int SchemaVersion { get; init; }
    public required string PlanSha256 { get; init; }
    public required string ProfileId { get; init; }
    public required RuntimePromotionImageIdentity Image { get; init; }
    public required string SourceRevision { get; init; }
    public required RuntimePerformancePolicyIdentity Policy { get; init; }
    public required List<string> Capabilities { get; init; }
    public required string SourceMappingKind { get; init; }
    public required RuntimePerformanceEnvironment Environment { get; init; }
    public required string CompletedAtUtc { get; init; }
    public required string Result { get; init; }
    public required RuntimePerformanceEvidenceScenarios Scenarios { get; init; }
}

internal sealed class RuntimePerformancePolicyIdentity
{
    public required string Id { get; init; }
    public required string Sha256 { get; init; }
}

internal sealed class RuntimePerformanceEnvironment
{
    public required string RunnerId { get; init; }
    public required string OperatingSystem { get; init; }
    public required string Architecture { get; init; }
    public required long NanoCpus { get; init; }
    public required long MemoryLimitBytes { get; init; }
}

internal sealed class RuntimePerformanceEvidenceScenarios
{
    public required RuntimePerformanceScenarioMeasurement Run { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimePerformanceScenarioMeasurement? Jit { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimePerformanceScenarioMeasurement? Mapping { get; init; }
}

internal sealed class RuntimePerformanceScenarioMeasurement
{
    public required List<RuntimePerformanceSample?> Cold { get; init; }
    public required List<RuntimePerformanceSample?> Warm { get; init; }
}

internal sealed class RuntimePerformanceSample
{
    public required double LatencyMilliseconds { get; init; }
    public required long PeakMemoryBytes { get; init; }
    public required string OperationId { get; init; }
    public required int ResourceSampleCount { get; init; }
    public required string CompletedAtUtc { get; init; }
}
