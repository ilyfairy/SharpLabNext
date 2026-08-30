using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Globalization;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using SharpLabNext.Catalog;
using SharpLabNext.RuntimeProfile.Sdk;

namespace SharpLabNext.BundleBuilder;

internal static class RuntimePromotionTrust
{
    private const long MaximumReceiptBytes = 1024 * 1024;
    private const long MaximumEvidenceBytes = 1024 * 1024;
    private const long MaximumPerformancePolicyBytes = 1024 * 1024;
    private const long MaximumImageArtifactBytes = 256 * 1024 * 1024;
    private const long MaximumPromotionPlanSignatureBytes = 4096;
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
    private const string MeasurementHelperImplementation =
        RuntimeMeasurementHelperContract.Implementation;
    private const string MeasurementHelperEntrypoint =
        RuntimeMeasurementHelperContract.Entrypoint;
    private const string MeasurementHelperContentSha256 =
        RuntimeMeasurementHelperContract.ContentSha256;
    private const string SourceContextLabel = "io.sharplabnext.source.context";
    private const string PromotionEligibleLabel = "com.sharplabnext.runtime-candidate.promotion-eligible";
    private const string CommittedSourceContext = "committed";
    private const string HttpsSourceUriPattern = "^https://(?![^/?#\\s]*@)[^/?#\\s]+(?:[/?][^#\\s]*)?$";
    private const string DockerSourceUriPattern =
        "^docker://(?:(?:[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?(?::[0-9]+)?)/)?" +
        "[a-z0-9]+(?:[._-][a-z0-9]+)*(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)*" +
        "(?::[A-Za-z0-9_][A-Za-z0-9_.-]{0,127})?@sha256:[0-9a-f]{64}$";
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions ReceiptJsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<IReadOnlyList<RuntimePromotionTrustSnapshot>> CaptureAsync(string repositoryRoot, RepositorySourceProvenance source, CatalogDocument catalog, ReleaseLockDocument releaseLock, DeploymentImageManifest deployment, IReadOnlyList<RuntimeProfileDefinition> profiles, IReadOnlyList<InspectedImage> inspectedImages, IDockerCli docker, CancellationToken cancellationToken, RuntimePromotionPlanSignatureVerifier? planSignatureVerifier = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(releaseLock);
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(inspectedImages);
        ArgumentNullException.ThrowIfNull(docker);

        var promotionProfiles = profiles.Where(static profile => profile.PromotionReceipt is not null).OrderBy(static profile => profile.Id, StringComparer.Ordinal).ToArray();
        var promotionIds = promotionProfiles.Select(static profile => profile.Id).ToHashSet(StringComparer.Ordinal);
        var runtimeIndex = IndexUnique(catalog.Runtimes.Where(runtime => promotionIds.Contains(runtime.Id)), static runtime => runtime.Id, "Catalog runtime");
        var definitionIndex = IndexUnique(deployment.Images.Where(definition => definition.RuntimeId is not null && promotionIds.Contains(definition.RuntimeId)), static definition => definition.RuntimeId!, "runtime deployment definition");
        var imageIndex = IndexUnique(inspectedImages.Where(image => image.RuntimeId is not null && promotionIds.Contains(image.RuntimeId)), static image => image.RuntimeId!, "inspected runtime image");
        var result = new List<RuntimePromotionTrustSnapshot>();
        foreach (var profile in promotionProfiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!runtimeIndex.TryGetValue(profile.Id, out var runtime) || !runtime.Availability.IsSelectable)
            {
                throw new BundleValidationException($"Promotion-bound runtime profile '{profile.Id}' is not selectable in the Catalog.");
            }
            if (!releaseLock.Components.TryGetValue(profile.Id, out var component))
            {
                throw new BundleValidationException($"Promotion-bound runtime profile '{profile.Id}' has no release-lock component.");
            }
            if (!definitionIndex.TryGetValue(profile.Id, out var definition) || definition.ImmutableReference is null)
            {
                throw new BundleValidationException($"Promotion-bound runtime profile '{profile.Id}' has no immutable deployment reference.");
            }
            if (!imageIndex.TryGetValue(profile.Id, out var image))
            {
                throw new BundleValidationException($"Promotion-bound runtime profile '{profile.Id}' has no inspected image.");
            }

            result.Add(await CaptureProfileAsync(repositoryRoot, source, runtime, component, definition, profile, image, docker, planSignatureVerifier ?? RuntimePromotionPlanSignatureTrust.ProductionVerifier, cancellationToken));
        }

        return result;
    }

    private static Dictionary<string, T> IndexUnique<T>(IEnumerable<T> values, Func<T, string> keySelector, string kind)
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

    public static async Task RevalidateAsync(string repositoryRoot, IReadOnlyList<RuntimePromotionTrustSnapshot> snapshots, IDockerCli docker, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(docker);

        foreach (var snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receipt = await ReadTrustedFileAsync(repositoryRoot, snapshot.Receipt.RelativePath, [ReceiptDirectory], MaximumReceiptBytes, cancellationToken);
            RequireDigestEqual(snapshot.Receipt.Sha256, receipt.Sha256, $"Runtime '{snapshot.RuntimeId}' promotion receipt changed before release finalization");
            await RuntimePromotionPlanSignatureTrust.RevalidateAsync(repositoryRoot, snapshot.RuntimeId, snapshot.BuildSourceRevision, snapshot.SignedPlan, cancellationToken);

            await WineCoreClrOperatorReceiptTrust.RevalidateAsync(repositoryRoot, snapshot.RuntimeId, snapshot.BuildSourceRevision, snapshot.WineOperatorReceipt, docker, cancellationToken);

            foreach (var expectedEvidence in snapshot.Evidence)
            {
                var evidence = await ReadTrustedFileAsync(repositoryRoot, expectedEvidence.RelativePath, [EvidenceDirectory, $"{EvidenceDirectory}/{snapshot.RuntimeId}"], MaximumEvidenceBytes, cancellationToken);
                RequireDigestEqual(expectedEvidence.Sha256, evidence.Sha256, $"Runtime '{snapshot.RuntimeId}' promotion evidence '{expectedEvidence.RelativePath}' changed before release finalization");
            }

            var performancePolicy = await ReadTrustedFileAsync(repositoryRoot, snapshot.PerformancePolicy.RelativePath, [PerformancePolicyDirectory], MaximumPerformancePolicyBytes, cancellationToken);
            RequireDigestEqual(snapshot.PerformancePolicy.Sha256, performancePolicy.Sha256, $"Runtime '{snapshot.RuntimeId}' performance policy changed before release finalization");

            var currentImage = await docker.InspectImageAsync(snapshot.ImmutableReference, cancellationToken);
            if (!string.Equals(currentImage.ImageId, snapshot.ImageId, StringComparison.Ordinal) || !currentImage.RepoDigests.Contains(snapshot.ImmutableReference, StringComparer.Ordinal))
            {
                throw new BundleValidationException($"Runtime '{snapshot.RuntimeId}' immutable reference no longer resolves to captured image ID '{snapshot.ImageId}'.");
            }
            if (!string.Equals(currentImage.OperatingSystem, "linux", StringComparison.Ordinal) || !string.Equals(currentImage.Architecture, "amd64", StringComparison.Ordinal))
            {
                throw new BundleValidationException($"Runtime '{snapshot.RuntimeId}' immutable image changed platform before release finalization.");
            }
            if (currentImage.SizeBytes != snapshot.ImageSizeBytes)
            {
                throw new BundleValidationException($"Runtime '{snapshot.RuntimeId}' immutable image size changed before release finalization.");
            }
            if (!currentImage.Labels.TryGetValue(RepositorySourceProvenanceResolver.ImageLabel, out var buildRevision) || !StringComparer.Ordinal.Equals(buildRevision, snapshot.BuildSourceRevision) || !currentImage.Labels.TryGetValue("org.opencontainers.image.revision", out var ociRevision) || !StringComparer.Ordinal.Equals(ociRevision, snapshot.BuildSourceRevision))
            {
                throw new BundleValidationException($"Runtime '{snapshot.RuntimeId}' immutable image build revision labels changed before release finalization.");
            }
            RequireCommittedPromotionEligibleImage(snapshot.RuntimeId, currentImage.Labels, "changed before release finalization");
            WineCoreClrOperatorReceiptTrust.ValidateCandidateImage(snapshot.RuntimeId, currentImage, snapshot.WineOperatorReceipt);

            var helper = snapshot.MeasurementHelper;
            var currentHelper = await docker.InspectImageAsync(helper.Reference, cancellationToken);
            if (!string.Equals(currentHelper.ImageId, helper.ImageId, StringComparison.Ordinal) ||
                !currentHelper.RepoDigests.Contains(helper.Reference, StringComparer.Ordinal) ||
                !string.Equals(currentHelper.OperatingSystem, "linux", StringComparison.Ordinal) ||
                !string.Equals(currentHelper.Architecture, "amd64", StringComparison.Ordinal) ||
                currentHelper.SizeBytes != helper.SizeBytes ||
                !currentHelper.Labels.TryGetValue(RepositorySourceProvenanceResolver.ImageLabel, out var helperBuildRevision) ||
                !StringComparer.Ordinal.Equals(helperBuildRevision, helper.SourceRevision) ||
                !currentHelper.Labels.TryGetValue("org.opencontainers.image.revision", out var helperOciRevision) ||
                !StringComparer.Ordinal.Equals(helperOciRevision, helper.SourceRevision))
            {
                throw new BundleValidationException($"Runtime '{snapshot.RuntimeId}' measurement helper image changed before release finalization.");
            }

            var helperFile = await docker.InspectImageFileAsync(helper.ImageId, helper.Entrypoint, MaximumImageArtifactBytes, cancellationToken);
            RequireDigestEqual(helper.ContentSha256, helperFile.Sha256, $"Runtime '{snapshot.RuntimeId}' measurement helper file changed before release finalization");

            var retainedImagePaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var artifact in snapshot.ImageFiles)
            {
                if (!retainedImagePaths.Add(artifact.Path) || !HasCanonicalRetainedMetadata(artifact))
                {
                    throw new BundleValidationException($"Runtime '{snapshot.RuntimeId}' retained image file metadata changed before release finalization.");
                }
                var observed = await docker.InspectImageFileAsync(snapshot.ImageId, artifact.Path, MaximumImageArtifactBytes, cancellationToken);
                RequireDigestEqual(artifact.Sha256, observed.Sha256, $"Runtime '{snapshot.RuntimeId}' image file '{artifact.Path}' changed before release finalization");
                if (observed.Length != artifact.SizeBytes)
                {
                    throw new BundleValidationException($"Runtime '{snapshot.RuntimeId}' image file '{artifact.Path}' size changed before release finalization.");
                }
            }
        }
    }

    private static async Task<RuntimePromotionTrustSnapshot> CaptureProfileAsync(string repositoryRoot, RepositorySourceProvenance source, RuntimeManifest runtime, LockedComponent component, DeploymentImageDefinition definition, RuntimeProfileDefinition profile, InspectedImage image, IDockerCli docker, RuntimePromotionPlanSignatureVerifier planSignatureVerifier, CancellationToken cancellationToken)
    {
        var reference = profile.PromotionReceipt ?? throw new BundleValidationException($"Runtime profile '{profile.Id}' has no promotion receipt reference.");
        var expectedReceiptPath = $"{ReceiptDirectory}/{profile.Id}.json";
        if (!string.Equals(reference.Path, expectedReceiptPath, StringComparison.Ordinal))
        {
            throw new BundleValidationException($"Runtime '{profile.Id}' promotion receipt path must be '{expectedReceiptPath}'.");
        }
        RequireSha256(reference.Sha256, $"Runtime '{profile.Id}' promotion receipt digest");

        var receiptFile = await ReadTrustedFileAsync(repositoryRoot, reference.Path, [ReceiptDirectory], MaximumReceiptBytes, cancellationToken);
        RequireDigestEqual(reference.Sha256, receiptFile.Sha256, $"Runtime '{profile.Id}' promotion receipt digest mismatch");

        var receipt = RuntimePromotionJson.Deserialize<RuntimePromotionReceiptDocument>(receiptFile.Bytes, ReceiptJsonOptions, $"Runtime '{profile.Id}' promotion receipt");

        ValidateReceiptIdentity(source, runtime, component, definition, profile, image, receipt);
        var signedPlan = await RuntimePromotionPlanSignatureTrust.CaptureAsync(repositoryRoot, profile, receipt, planSignatureVerifier, cancellationToken);
        var operatorReceipt = await WineCoreClrOperatorReceiptTrust.CaptureAsync(repositoryRoot, source, profile, receipt, image, docker, cancellationToken);
        var operationFiles = ValidateOperationBindings(profile, receipt);
        var checks = await ValidateChecksAsync(repositoryRoot, profile, receipt, operationFiles, cancellationToken);
        var performance = await ValidatePerformanceAsync(repositoryRoot, profile, image, receipt, cancellationToken);
        var helperFile = await docker.InspectImageFileAsync(performance.MeasurementHelper.ImageId, performance.MeasurementHelper.Entrypoint, MaximumImageArtifactBytes, cancellationToken);
        RequireDigestEqual(performance.MeasurementHelper.ContentSha256, helperFile.Sha256, $"Runtime '{profile.Id}' measurement helper file does not match its pinned content digest");
        RuntimePromotionFileSnapshot[] evidence = [..checks.Evidence, performance.Evidence];
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
            performance.MeasurementHelper,
            checks.ImageFiles,
            operatorReceipt,
            signedPlan);
    }

    private static void ValidateReceiptIdentity(RepositorySourceProvenance source, RuntimeManifest runtime, LockedComponent component, DeploymentImageDefinition definition, RuntimeProfileDefinition profile, InspectedImage image, RuntimePromotionReceiptDocument receipt)
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
            throw new BundleValidationException($"Runtime '{profile.Id}' promotion receipt requires a clean, verified Git source revision.");
        }
        RequireCommit(receipt.SourceRevision, $"Runtime '{profile.Id}' promotion receipt source revision");
        if (StringComparer.Ordinal.Equals(receipt.SourceRevision, source.Revision))
        {
            throw new BundleValidationException($"Runtime '{profile.Id}' promotion receipt implementation revision must be distinct " + "from the material release revision.");
        }
        if (!image.Labels.TryGetValue(RepositorySourceProvenanceResolver.ImageLabel, out var buildRevision) || !StringComparer.Ordinal.Equals(buildRevision, receipt.SourceRevision) || !image.Labels.TryGetValue("org.opencontainers.image.revision", out var ociRevision) || !StringComparer.Ordinal.Equals(ociRevision, receipt.SourceRevision))
        {
            throw new BundleValidationException($"Runtime '{profile.Id}' immutable image does not carry its receipt build source revision.");
        }
        RequireCommittedPromotionEligibleImage(profile.Id, image.Labels, "is not eligible for promotion");

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
            throw new BundleValidationException($"Runtime '{profile.Id}' promotion receipt image size does not match the inspected image.");
        }
        if (!image.RepoDigests.Contains(receipt.Image.Reference, StringComparer.Ordinal))
        {
            throw new BundleValidationException($"Runtime '{profile.Id}' promotion receipt reference is absent from the inspected image RepoDigests.");
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

    private static void RequireCommittedPromotionEligibleImage(string runtimeId, IReadOnlyDictionary<string, string> labels, string state)
    {
        if (!labels.TryGetValue(SourceContextLabel, out var sourceContext) || !StringComparer.Ordinal.Equals(sourceContext, CommittedSourceContext) || !labels.TryGetValue(PromotionEligibleLabel, out var promotionEligible) || !StringComparer.Ordinal.Equals(promotionEligible, "true"))
        {
            throw new BundleValidationException($"Runtime '{runtimeId}' immutable image is not a committed promotion-eligible candidate ({state}).");
        }
    }

    private static void ValidateComponentIdentity(RuntimeProfileDefinition profile, LockedComponent component, RuntimePromotionComponentIdentity identity)
    {
        if (!IsImmutableSourceUri(identity.SourceUri))
        {
            throw new BundleValidationException($"Runtime '{profile.Id}' promotion receipt component source URI is not immutable HTTPS or docker content.");
        }

        if (profile.Family is "coreclr" or "coreclr-wine")
        {
            if (component.SourceUri is null || component.Sha512 is null || !IsLowerHex(component.Sha512, 128))
            {
                throw new BundleValidationException($"Runtime '{profile.Id}' release-lock component has no immutable CoreCLR payload identity.");
            }
            RequireEqual(identity.SourceUri, component.SourceUri, profile.Id, "component source URI");
            RequireEqual(identity.SourceDigest, $"sha512:{component.Sha512}", profile.Id, "component source digest");
            return;
        }

        if (component.SourceUri is null || component.Digest is null)
        {
            throw new BundleValidationException($"Runtime '{profile.Id}' release-lock component has no immutable operator identity.");
        }
        RequireSha256(component.Digest, $"Runtime '{profile.Id}' operator component digest");
        RequireEqual(identity.SourceUri, component.SourceUri, profile.Id, "component source URI");
        RequireEqual(identity.SourceDigest, component.Digest, profile.Id, "component source digest");
        if (identity.SourceUri.StartsWith("docker://", StringComparison.Ordinal) && !identity.SourceUri.EndsWith("@" + identity.SourceDigest, StringComparison.Ordinal))
        {
            throw new BundleValidationException($"Runtime '{profile.Id}' operator source URI and digest disagree.");
        }
    }

    internal static RuntimePromotionOperationFileIdentity[] ValidateOperationBindings(RuntimeProfileDefinition profile, RuntimePromotionReceiptDocument receipt)
    {
        var profileOperations = profile.Operations ?? throw new BundleValidationException($"Runtime '{profile.Id}' has no explicit operation definitions.");
        var receiptOperations = receipt.Operations ?? throw new BundleValidationException($"Runtime '{profile.Id}' promotion receipt has no operation bindings.");
        if (profileOperations.Run is null)
            throw new BundleValidationException($"Runtime '{profile.Id}' has no Run operation.");
        if (receiptOperations.Run is null)
            throw new BundleValidationException($"Runtime '{profile.Id}' promotion receipt has no Run helper.");

        var helpers = new Dictionary<string, string>(StringComparer.Ordinal);
        ValidateOperation(profile.Id, "run", profileOperations.Run, profile.Layout.RunnerAssemblyPath, expectedProfilerPath: null, receiptOperations.Run, helpers);

        if ((profileOperations.Jit is null) != (receiptOperations.Jit is null))
        {
            throw new BundleValidationException($"Runtime '{profile.Id}' profile and promotion receipt disagree about the JIT operation.");
        }
        if (profileOperations.Jit is { } jit && receiptOperations.Jit is { } jitHelper)
        {
            _ = RequirePermittedJitOperation(profile);
            ValidateOperation(profile.Id, "jit", jit, profile.Layout.JitInspectorAssemblyPath ?? profile.Layout.RunnerAssemblyPath, jit.ProfilerPath, jitHelper, helpers);
        }

        return helpers.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => new RuntimePromotionOperationFileIdentity(pair.Key, pair.Value)).ToArray();
    }

    private static void ValidateOperation(string profileId, string name, RuntimeOperationDefinition operation, string expectedAssemblyPath, string? expectedProfilerPath, RuntimePromotionOperationHelper helper, IDictionary<string, string> helpers)
    {
        RequireEqual(helper.Implementation, operation.ImplementationId, profileId, $"{name} implementation");
        RequireEqual(helper.AssemblyPath, expectedAssemblyPath, profileId, $"{name} assembly path");
        RequireCanonicalHelperPath(helper.AssemblyPath, StringComparer.Ordinal.Equals(operation.ImplementationId, RuntimeOperationImplementationIds.TargetRuntimeRunner) ? ".exe" : ".dll", profileId, name);
        RequireSha256(helper.AssemblySha256, $"Runtime '{profileId}' {name} assembly digest");
        AddHelper(helpers, profileId, helper.AssemblyPath, helper.AssemblySha256);

        if ((helper.ProfilerPath is null) != (helper.ProfilerSha256 is null))
        {
            throw new BundleValidationException($"Runtime '{profileId}' {name} profiler path and digest must be declared together.");
        }
        RequireEqual(helper.ProfilerPath, expectedProfilerPath, profileId, $"{name} profiler path");
        if (helper.ProfilerPath is not null)
        {
            RequireCanonicalHelperPath(helper.ProfilerPath, ".so", profileId, name);
            RequireSha256(helper.ProfilerSha256!, $"Runtime '{profileId}' {name} profiler digest");
            AddHelper(helpers, profileId, helper.ProfilerPath, helper.ProfilerSha256!);
        }
    }

    private static async Task<RuntimePromotionChecksSnapshot> ValidateChecksAsync(string repositoryRoot, RuntimeProfileDefinition profile, RuntimePromotionReceiptDocument receipt, IReadOnlyList<RuntimePromotionOperationFileIdentity> operationFiles, CancellationToken cancellationToken)
    {
        if (receipt.Checks is null || receipt.Checks.Count is < 1 or > 4 || receipt.Checks.Any(static check => check is null))
            throw new BundleValidationException($"Runtime '{profile.Id}' promotion receipt has no bounded capability checks.");
        var expected = profile.Capabilities.Order(StringComparer.Ordinal).ToArray();
        if (expected.Length != expected.Distinct(StringComparer.Ordinal).Count())
            throw new BundleValidationException($"Runtime '{profile.Id}' has duplicate capabilities.");
        var observed = receipt.Checks.Select(static check => check!.Capability).Order(StringComparer.Ordinal).ToArray();
        if (!expected.SequenceEqual(observed, StringComparer.Ordinal))
        {
            throw new BundleValidationException($"Runtime '{profile.Id}' promotion checks do not exactly cover its declared capabilities.");
        }

        var evidenceSnapshots = new List<RuntimePromotionFileSnapshot>(receipt.Checks.Count);
        var imageFiles = new Dictionary<string, RuntimePromotionImageFileSnapshot>(StringComparer.Ordinal);
        var probeArtifacts = new Dictionary<string, RuntimeCapabilityProbeArtifactSnapshot>(StringComparer.Ordinal);
        foreach (var check in receipt.Checks.Select(static check => check!).OrderBy(static check => check.Capability, StringComparer.Ordinal))
        {
            if (!IsCapability(check.Capability) || !string.Equals(check.Result, "passed", StringComparison.Ordinal) || !check.NetworkDisabled || !check.SupervisorSandbox || !check.OutputLimitValidated)
            {
                throw new BundleValidationException($"Runtime '{profile.Id}' promotion check '{check.Capability}' is not complete and passing.");
            }
            ValidateMapping(profile, receipt, check);
            var expectedPath = $"{EvidenceDirectory}/{profile.Id}/{check.Capability}.json";
            if (!string.Equals(check.EvidencePath, expectedPath, StringComparison.Ordinal))
            {
                throw new BundleValidationException($"Runtime '{profile.Id}' promotion evidence path must be '{expectedPath}'.");
            }
            RequireSha256(check.EvidenceSha256, $"Runtime '{profile.Id}' {check.Capability} evidence digest");
            var evidence = await ReadTrustedFileAsync(repositoryRoot, check.EvidencePath, [EvidenceDirectory, $"{EvidenceDirectory}/{profile.Id}"], MaximumEvidenceBytes, cancellationToken);
            RequireDigestEqual(check.EvidenceSha256, evidence.Sha256, $"Runtime '{profile.Id}' {check.Capability} evidence digest mismatch");
            evidenceSnapshots.Add(new RuntimePromotionFileSnapshot(evidence.RelativePath, evidence.Sha256));
            var validatedArtifacts = RuntimeCapabilityEvidenceValidation.Validate(evidence.Bytes, profile, receipt, check, out var probeArtifact);
            probeArtifacts.Add(check.Capability, probeArtifact);
            foreach (var artifact in validatedArtifacts)
            {
                if (imageFiles.TryGetValue(artifact.Path, out var existing) && (!string.Equals(existing.Sha256, artifact.Sha256, StringComparison.Ordinal) || existing.SizeBytes != artifact.SizeBytes || !string.Equals(existing.Role, artifact.Role, StringComparison.Ordinal) || !string.Equals(existing.Format, artifact.Format, StringComparison.Ordinal) || !string.Equals(existing.Architecture, artifact.Architecture, StringComparison.Ordinal)))
                {
                    throw new BundleValidationException($"Runtime '{profile.Id}' capability evidence assigns conflicting path, byte, role, format, or architecture identities to image file '{artifact.Path}'.");
                }
                imageFiles[artifact.Path] = artifact;
            }
        }
        RuntimeCapabilityEvidenceValidation.ValidateProbeSet(profile.Id, probeArtifacts);

        foreach (var operationFile in operationFiles)
        {
            if (!imageFiles.TryGetValue(operationFile.Path, out var artifact))
            {
                throw new BundleValidationException($"Runtime '{profile.Id}' capability evidence does not retain operation file '{operationFile.Path}'.");
            }
            RequireDigestEqual(operationFile.Sha256, artifact.Sha256, $"Runtime '{profile.Id}' operation file '{operationFile.Path}' does not match capability evidence");
        }

        return new RuntimePromotionChecksSnapshot(evidenceSnapshots, imageFiles.Values.OrderBy(static file => file.Path, StringComparer.Ordinal).ToArray(), probeArtifacts["run"].PreflightProfileSha256);
    }

    private static async Task<RuntimePromotionPerformanceSnapshot> ValidatePerformanceAsync(string repositoryRoot, RuntimeProfileDefinition profile, InspectedImage image, RuntimePromotionReceiptDocument receipt, CancellationToken cancellationToken)
    {
        var binding = receipt.Performance ?? throw new BundleValidationException($"Runtime '{profile.Id}' promotion receipt has no performance evidence binding.");
        if (!string.Equals(binding.Result, "passed", StringComparison.Ordinal))
        {
            throw new BundleValidationException($"Runtime '{profile.Id}' performance evidence is not passing.");
        }
        RequireCanonicalId(binding.PolicyId, $"Runtime '{profile.Id}' performance policy ID");
        var expectedPolicyPath = $"{PerformancePolicyDirectory}/{binding.PolicyId}.json";
        if (!string.Equals(binding.PolicyPath, expectedPolicyPath, StringComparison.Ordinal))
        {
            throw new BundleValidationException($"Runtime '{profile.Id}' performance policy path must be '{expectedPolicyPath}'.");
        }
        var expectedEvidencePath = $"{EvidenceDirectory}/{profile.Id}/performance.json";
        if (!string.Equals(binding.EvidencePath, expectedEvidencePath, StringComparison.Ordinal))
        {
            throw new BundleValidationException($"Runtime '{profile.Id}' performance evidence path must be '{expectedEvidencePath}'.");
        }
        RequireSha256(binding.PolicySha256, $"Runtime '{profile.Id}' performance policy digest");
        RequireSha256(binding.EvidenceSha256, $"Runtime '{profile.Id}' performance evidence digest");

        var policyFile = await ReadTrustedFileAsync(repositoryRoot, binding.PolicyPath, [PerformancePolicyDirectory], MaximumPerformancePolicyBytes, cancellationToken);
        RequireDigestEqual(binding.PolicySha256, policyFile.Sha256, $"Runtime '{profile.Id}' performance policy digest mismatch");
        var evidenceFile = await ReadTrustedFileAsync(repositoryRoot, binding.EvidencePath, [EvidenceDirectory, $"{EvidenceDirectory}/{profile.Id}"], MaximumEvidenceBytes, cancellationToken);
        RequireDigestEqual(binding.EvidenceSha256, evidenceFile.Sha256, $"Runtime '{profile.Id}' performance evidence digest mismatch");

        var policy = DeserializePerformanceDocument<RuntimePerformancePolicyDocument>(policyFile.Bytes, profile.Id, "policy");
        var evidence = DeserializePerformanceDocument<RuntimePerformanceEvidenceDocument>(evidenceFile.Bytes, profile.Id, "evidence");
        ValidatePerformancePolicy(profile.Id, binding, policy);
        var measurementHelper = ValidatePerformanceEvidence(profile, image, receipt, binding, policy, evidence);
        return new RuntimePromotionPerformanceSnapshot(new RuntimePromotionFileSnapshot(policyFile.RelativePath, policyFile.Sha256), new RuntimePromotionFileSnapshot(evidenceFile.RelativePath, evidenceFile.Sha256), measurementHelper);
    }

    private static T DeserializePerformanceDocument<T>(byte[] bytes, string profileId, string kind)
    {
        return RuntimePromotionJson.Deserialize<T>(bytes, ReceiptJsonOptions, $"Runtime '{profileId}' performance {kind}");
    }

    internal static void ValidatePerformancePolicy(string profileId, RuntimePromotionPerformanceBinding binding, RuntimePerformancePolicyDocument policy)
    {
        if (policy.SchemaVersion != 1 || !string.Equals(policy.Id, binding.PolicyId, StringComparison.Ordinal))
        {
            throw new BundleValidationException($"Runtime '{profileId}' performance policy identity is invalid.");
        }
        if (policy.SampleCounts is null || policy.SampleCounts.Cold is < MinimumColdPerformanceSamples or > MaximumColdPerformanceSamples || policy.SampleCounts.Warm is < MinimumWarmPerformanceSamples or > MaximumWarmPerformanceSamples)
        {
            throw new BundleValidationException($"Runtime '{profileId}' performance policy has unsafe sample counts.");
        }
        if (policy.ResourceLimits is null ||
            policy.ResourceLimits.NanoCpus is < MinimumPerformanceNanoCpus or > MaximumPerformanceNanoCpus ||
            policy.ResourceLimits.AllowedMemoryBytes is null or { Count: < 1 or > 8 } ||
            policy.ResourceLimits.AllowedMemoryBytes.Any(static value => value is < MinimumPerformanceMemoryBytes or > MaximumPerformanceMemoryBytes) ||
            policy.ResourceLimits.AllowedMemoryBytes.Distinct().Count() !=
            policy.ResourceLimits.AllowedMemoryBytes.Count ||
            !policy.ResourceLimits.AllowedMemoryBytes.SequenceEqual(policy.ResourceLimits.AllowedMemoryBytes.Order()))
        {
            throw new BundleValidationException($"Runtime '{profileId}' performance policy has unsafe resource limits.");
        }
        if (policy.Image is null || policy.Image.MaximumSizeBytes is < 1 or > MaximumPerformanceImageBytes)
        {
            throw new BundleValidationException($"Runtime '{profileId}' performance policy has an unsafe image-size limit.");
        }
        if (policy.Scenarios is null || policy.Scenarios.Run is null || policy.Scenarios.Jit is null || policy.Scenarios.Mapping is null)
        {
            throw new BundleValidationException($"Runtime '{profileId}' performance policy does not define every scenario.");
        }
        ValidatePerformanceScenarioBudget(profileId, "run", policy.Scenarios.Run);
        ValidatePerformanceScenarioBudget(profileId, "jit", policy.Scenarios.Jit);
        ValidatePerformanceScenarioBudget(profileId, "mapping", policy.Scenarios.Mapping);
    }

    private static void ValidatePerformanceScenarioBudget(string profileId, string scenario, RuntimePerformanceScenarioBudget budget)
    {
        if (budget.Cold is null || budget.Warm is null)
        {
            throw new BundleValidationException($"Runtime '{profileId}' performance policy scenario '{scenario}' is incomplete.");
        }
        ValidatePerformanceModeBudget(profileId, scenario, "cold", budget.Cold);
        ValidatePerformanceModeBudget(profileId, scenario, "warm", budget.Warm);
    }

    private static void ValidatePerformanceModeBudget(string profileId, string scenario, string mode, RuntimePerformanceModeBudget budget)
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
            throw new BundleValidationException($"Runtime '{profileId}' performance policy budget '{scenario}.{mode}' is unsafe.");
        }
    }

    internal static RuntimePromotionMeasurementHelperSnapshot ValidatePerformanceEvidence(RuntimeProfileDefinition profile, InspectedImage image, RuntimePromotionReceiptDocument receipt, RuntimePromotionPerformanceBinding binding, RuntimePerformancePolicyDocument policy, RuntimePerformanceEvidenceDocument evidence)
    {
        if (evidence.SchemaVersion != 1 || !string.Equals(evidence.PlanSha256, receipt.PlanSha256, StringComparison.Ordinal) || !string.Equals(evidence.ProfileId, profile.Id, StringComparison.Ordinal) || !string.Equals(evidence.SourceRevision, receipt.SourceRevision, StringComparison.Ordinal) || !string.Equals(evidence.Result, "passed", StringComparison.Ordinal))
        {
            throw new BundleValidationException($"Runtime '{profile.Id}' performance evidence identity is invalid.");
        }
        if (!IsCanonicalUtcTimestamp(evidence.CompletedAtUtc))
        {
            throw new BundleValidationException($"Runtime '{profile.Id}' performance evidence timestamp is not canonical UTC.");
        }
        if (evidence.Image is null || !string.Equals(evidence.Image.Reference, receipt.Image.Reference, StringComparison.Ordinal) || !string.Equals(evidence.Image.ImageId, receipt.Image.ImageId, StringComparison.Ordinal) || evidence.Image.SizeBytes != receipt.Image.SizeBytes || evidence.Image.SizeBytes != image.SizeBytes || evidence.Image.SizeBytes <= 0 || evidence.Image.SizeBytes > policy.Image.MaximumSizeBytes)
        {
            throw new BundleValidationException($"Runtime '{profile.Id}' performance evidence image identity or size is invalid.");
        }
        if (evidence.Policy is null || !string.Equals(evidence.Policy.Id, binding.PolicyId, StringComparison.Ordinal) || !string.Equals(evidence.Policy.Sha256, binding.PolicySha256, StringComparison.Ordinal))
        {
            throw new BundleValidationException($"Runtime '{profile.Id}' performance evidence policy identity is invalid.");
        }

        var measurementHelper = ValidateMeasurementHelper(profile.Id, receipt, evidence);

        var expectedCapabilities = profile.Capabilities.Order(StringComparer.Ordinal).ToArray();
        if (evidence.Capabilities is null || !expectedCapabilities.SequenceEqual(evidence.Capabilities, StringComparer.Ordinal))
        {
            throw new BundleValidationException($"Runtime '{profile.Id}' performance evidence does not cover its exact capabilities.");
        }
        var jitCheck = receipt.Checks.SingleOrDefault(static check => check?.Capability == "jit-asm");
        var expectedMappingKind = jitCheck?.SourceMappingKind ?? "not-applicable";
        if (!string.Equals(evidence.SourceMappingKind, expectedMappingKind, StringComparison.Ordinal))
        {
            throw new BundleValidationException($"Runtime '{profile.Id}' performance evidence mapping kind is invalid.");
        }
        if (evidence.Environment is null || !string.Equals(evidence.Environment.RunnerId, "runtime-preflight-linux-x64-v2", StringComparison.Ordinal) || !string.Equals(evidence.Environment.OperatingSystem, "linux", StringComparison.Ordinal) || !string.Equals(evidence.Environment.Architecture, "x64", StringComparison.Ordinal) || evidence.Environment.NanoCpus != policy.ResourceLimits.NanoCpus || !policy.ResourceLimits.AllowedMemoryBytes.Contains(evidence.Environment.MemoryLimitBytes))
        {
            throw new BundleValidationException($"Runtime '{profile.Id}' performance evidence environment is invalid.");
        }
        if (evidence.Scenarios?.Run is null)
        {
            throw new BundleValidationException($"Runtime '{profile.Id}' performance evidence has no Run scenario.");
        }

        // Operation IDs identify the individual Supervisor measurements. They must be
        // unique across the complete evidence document, not merely within one mode,
        // otherwise a replayed sample could be counted once per scenario.
        var operationIds = new HashSet<string>(StringComparer.Ordinal);

        var requiresJit = expectedCapabilities.Contains("jit-asm", StringComparer.Ordinal);
        var requiresMapping = expectedMappingKind is not ("none" or "not-applicable");
        if ((evidence.Scenarios.Jit is not null) != requiresJit || (evidence.Scenarios.Mapping is not null) != requiresMapping)
        {
            throw new BundleValidationException($"Runtime '{profile.Id}' performance evidence has an invalid scenario set.");
        }
        ValidatePerformanceScenario(profile.Id, "run", evidence.Scenarios.Run, policy.Scenarios.Run, policy.SampleCounts, evidence.Environment.MemoryLimitBytes, operationIds);
        if (evidence.Scenarios.Jit is { } jit)
        {
            ValidatePerformanceScenario(profile.Id, "jit", jit, policy.Scenarios.Jit, policy.SampleCounts, evidence.Environment.MemoryLimitBytes, operationIds);
        }
        if (evidence.Scenarios.Mapping is { } mapping)
        {
            ValidatePerformanceScenario(profile.Id, "mapping", mapping, policy.Scenarios.Mapping, policy.SampleCounts, evidence.Environment.MemoryLimitBytes, operationIds);
        }
        return measurementHelper;
    }

    private static RuntimePromotionMeasurementHelperSnapshot ValidateMeasurementHelper(string profileId, RuntimePromotionReceiptDocument receipt, RuntimePerformanceEvidenceDocument evidence)
    {
        var helper = evidence.MeasurementHelper;
        if (helper is null || !string.Equals(helper.Implementation, MeasurementHelperImplementation, StringComparison.Ordinal) || !string.Equals(helper.Entrypoint, MeasurementHelperEntrypoint, StringComparison.Ordinal) || !string.Equals(helper.ContentSha256, MeasurementHelperContentSha256, StringComparison.Ordinal) || !string.Equals(helper.SourceRevision, evidence.SourceRevision, StringComparison.Ordinal) || !string.Equals(helper.SourceRevision, receipt.SourceRevision, StringComparison.Ordinal) || helper.Image is null)
        {
            throw new BundleValidationException($"Runtime '{profileId}' performance measurement helper identity is invalid.");
        }
        RequireCommit(helper.SourceRevision, $"Runtime '{profileId}' performance measurement helper source revision");
        RequireImmutableReference(helper.Image.Reference, $"Runtime '{profileId}' performance measurement helper image reference");
        RequireSha256(helper.Image.ImageId, $"Runtime '{profileId}' performance measurement helper image ID");
        if (!IsRuntimeSupervisorReference(helper.Image.Reference) || helper.Image.SizeBytes is < 1 or > MaximumPerformanceImageBytes || string.Equals(helper.Image.Reference, evidence.Image.Reference, StringComparison.Ordinal) || string.Equals(helper.Image.ImageId, evidence.Image.ImageId, StringComparison.Ordinal))
        {
            throw new BundleValidationException($"Runtime '{profileId}' performance measurement helper image is invalid or not independent.");
        }
        return new RuntimePromotionMeasurementHelperSnapshot(helper.Implementation, helper.Image.Reference, helper.Image.ImageId, helper.Image.SizeBytes, helper.Entrypoint, helper.SourceRevision, helper.ContentSha256);
    }

    private static void ValidatePerformanceScenario(string profileId, string name, RuntimePerformanceScenarioMeasurement scenario, RuntimePerformanceScenarioBudget budget, RuntimePerformanceSampleCounts counts, long memoryLimitBytes, HashSet<string> operationIds)
    {
        ValidatePerformanceSamples(profileId, name, "cold", scenario.Cold, counts.Cold, budget.Cold, memoryLimitBytes, operationIds);
        ValidatePerformanceSamples(profileId, name, "warm", scenario.Warm, counts.Warm, budget.Warm, memoryLimitBytes, operationIds);
    }

    private static void ValidatePerformanceSamples(string profileId, string scenario, string mode, List<RuntimePerformanceSample?>? samples, int expectedCount, RuntimePerformanceModeBudget budget, long memoryLimitBytes, HashSet<string> operationIds)
    {
        if (samples is null || samples.Count != expectedCount || samples.Any(static sample => sample is null))
        {
            throw new BundleValidationException($"Runtime '{profileId}' performance evidence '{scenario}.{mode}' must contain " + $"exactly {expectedCount} samples.");
        }
        var latencies = new double[samples.Count];
        for (var index = 0; index < samples.Count; index++)
        {
            var sample = samples[index]!;
            if (!IsOperationId(sample.OperationId) ||
                sample.ResourceSampleCount is < 1 or > 1_000_000 ||
                sample.PostCompletionResourceSampleCount is < 1 or > 1_000_000 ||
                sample.ResourceSampleCount < sample.PostCompletionResourceSampleCount ||
                !IsCanonicalUtcTimestamp(sample.CompletedAtUtc) ||
                !double.IsFinite(sample.LatencyMilliseconds) ||
                sample.LatencyMilliseconds <= 0 ||
                sample.LatencyMilliseconds > budget.MaximumSampleLatencyMilliseconds ||
                sample.PeakMemoryBytes <= 0 ||
                sample.CompletionPeakMemoryBytes <= 0 ||
                sample.PeakMemoryBytes < sample.CompletionPeakMemoryBytes ||
                sample.PeakMemoryBytes > budget.MaximumPeakMemoryBytes ||
                sample.PeakMemoryBytes > memoryLimitBytes)
            {
                throw new BundleValidationException($"Runtime '{profileId}' performance sample '{scenario}.{mode}[{index}]' " + "has invalid latency, resource-sample, or memory evidence.");
            }
            latencies[index] = sample.LatencyMilliseconds;
            if (!operationIds.Add(sample.OperationId))
            {
                throw new BundleValidationException($"Runtime '{profileId}' performance evidence '{scenario}.{mode}' reuses an operation ID.");
            }
        }
        Array.Sort(latencies);
        var p95 = latencies[Math.Max(0, (int)Math.Ceiling(latencies.Length * 0.95) - 1)];
        if (p95 > budget.MaximumP95LatencyMilliseconds)
        {
            throw new BundleValidationException($"Runtime '{profileId}' performance evidence '{scenario}.{mode}' exceeds its P95 budget.");
        }
    }

    private static void ValidateMapping(RuntimeProfileDefinition profile, RuntimePromotionReceiptDocument receipt, RuntimePromotionCapabilityCheck check)
    {
        if (!string.Equals(check.Capability, "jit-asm", StringComparison.Ordinal))
        {
            if (!string.Equals(check.SourceMappingKind, "not-applicable", StringComparison.Ordinal) || !string.Equals(check.MappingSource, "not-applicable", StringComparison.Ordinal))
            {
                throw new BundleValidationException($"Runtime '{profile.Id}' non-JIT promotion check cannot claim source mapping.");
            }
            return;
        }

        var jit = RequirePermittedJitOperation(profile);
        if (!string.Equals(check.SourceMappingKind, jit.SourceMappingKind, StringComparison.Ordinal))
        {
            throw new BundleValidationException($"Runtime '{profile.Id}' JIT mapping kind disagrees with its promotion receipt.");
        }
        if (string.Equals(check.SourceMappingKind, "linux-profiler", StringComparison.Ordinal))
        {
            if (profile.Family != "coreclr" || check.MappingSource is not ("ordinary" or "rich") || receipt.Operations?.Jit?.ProfilerPath is null)
            {
                throw new BundleValidationException($"Runtime '{profile.Id}' linux-profiler promotion lacks profiler-backed mapping evidence.");
            }
        }
        else if (string.Equals(check.SourceMappingKind, RuntimeJitSourceMappingKinds.CheckedJitDebugInfo, StringComparison.Ordinal))
        {
            if (profile.Family != "coreclr" || !string.Equals(check.MappingSource, RuntimeJitSourceMappingKinds.CheckedJitDebugInfo, StringComparison.Ordinal) || receipt.Operations?.Jit?.ProfilerPath is not null)
            {
                throw new BundleValidationException($"Runtime '{profile.Id}' checked-JIT promotion lacks debug-info mapping evidence.");
            }
        }
        else if (!string.Equals(check.SourceMappingKind, "none", StringComparison.Ordinal) || check.MappingSource is not ("none" or "method"))
        {
            throw new BundleValidationException($"Runtime '{profile.Id}' mapping-free or method-level JIT promotion has invalid mapping evidence.");
        }
    }

    internal static RuntimeJitOperationDefinition RequirePermittedJitOperation(RuntimeProfileDefinition profile)
    {
        var jit = profile.Operations?.Jit ?? throw new BundleValidationException($"Runtime '{profile.Id}' promotes jit-asm without a JIT operation.");
        if (profile.Family != "netfx-clr-wine")
            return jit;
        if (!StringComparer.Ordinal.Equals(jit.ImplementationId, RuntimeOperationImplementationIds.DesktopClrJitInspector))
        {
            throw new BundleValidationException($"Runtime '{profile.Id}' Framework jit-asm requires implementation '{RuntimeOperationImplementationIds.DesktopClrJitInspector}'.");
        }
        if (!StringComparer.Ordinal.Equals(jit.SourceMappingKind, RuntimeJitSourceMappingKinds.None) || jit.ProfilerPath is not null)
        {
            throw new BundleValidationException($"Runtime '{profile.Id}' Framework jit-asm requires source mapping kind '{RuntimeJitSourceMappingKinds.None}' without a profiler.");
        }
        return jit;
    }

    private static async Task<TrustedFile> ReadTrustedFileAsync(string repositoryRoot, string relativePath, IReadOnlyList<string> trustedDirectories, long maximumBytes, CancellationToken cancellationToken, bool requireCanonicalJson = true)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains('\\') || relativePath.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            throw new BundleValidationException($"Promotion material path '{relativePath}' is not canonical.");
        }

        var root = Path.GetFullPath(repositoryRoot);
        var absolutePath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var deepestDirectory = Path.GetFullPath(Path.Combine(root, trustedDirectories[^1].Replace('/', Path.DirectorySeparatorChar)));
        if (!IsPathInside(deepestDirectory, absolutePath))
            throw new BundleValidationException($"Promotion material '{relativePath}' escapes its trusted directory.");
        foreach (var relativeDirectory in trustedDirectories)
        {
            EnsureRegularDirectory(Path.Combine(root, relativeDirectory.Replace('/', Path.DirectorySeparatorChar)));
        }

        var info = new FileInfo(absolutePath);
        info.Refresh();
        if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new BundleValidationException($"Promotion material '{relativePath}' must be a regular non-link file.");
        }
        if (info.Length < 1 || info.Length > maximumBytes)
        {
            throw new BundleValidationException($"Promotion material '{relativePath}' exceeds the {maximumBytes}-byte limit.");
        }

        byte[] bytes;
        try
        {
            await using var stream = new FileStream(absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length < 1 || stream.Length > maximumBytes)
            {
                throw new BundleValidationException($"Promotion material '{relativePath}' exceeds the {maximumBytes}-byte limit.");
            }
            bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            if (stream.ReadByte() != -1)
            {
                throw new BundleValidationException($"Promotion material '{relativePath}' changed while it was being read.");
            }
        }
        catch (BundleValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new BundleValidationException($"Promotion material '{relativePath}' could not be read: {exception.Message}");
        }

        if (requireCanonicalJson)
            ValidateCanonicalJsonEncoding(bytes, relativePath);
        return new TrustedFile(relativePath, $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}", bytes);
    }

    private static void ValidateCanonicalJsonEncoding(ReadOnlySpan<byte> bytes, string relativePath)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new BundleValidationException($"Promotion material '{relativePath}' must use valid UTF-8: {exception.Message}");
        }

        if (text.Length == 0 || text[0] == '\uFEFF' || text[^1] != '\n' || bytes.IndexOf((byte)'\r') >= 0)
        {
            throw new BundleValidationException($"Promotion material '{relativePath}' must be UTF-8 without a BOM, use LF-only line endings, and end with LF.");
        }
    }

    private static void EnsureRegularDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        info.Refresh();
        if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new BundleValidationException($"Promotion material directory '{path}' must be a regular non-link directory.");
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
        _ => throw new BundleValidationException($"Runtime family '{family}' cannot use a matrix promotion receipt.")
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
            _ => throw new BundleValidationException($"Runtime '{profile.Id}' cannot be bound to a canonical matrix target ID.")
        };
    }

    internal static bool IsImmutableSourceUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value != value.Trim() || value.Any(char.IsWhiteSpace))
        {
            return false;
        }
        if (value.StartsWith("docker://", StringComparison.Ordinal))
        {
            return Regex.IsMatch(value, DockerSourceUriPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        if (!value.StartsWith("https://", StringComparison.Ordinal))
            return false;
        return Regex.IsMatch(value, HttpsSourceUriPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) &&
               Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) &&
               uri.Host.Length > 0 &&
               uri.UserInfo.Length == 0 &&
               uri.Fragment.Length == 0;
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

    private static bool IsRuntimeSupervisorReference(string? value)
    {
        if (!IsImmutableReference(value))
            return false;
        var digest = value!.LastIndexOf("@sha256:", StringComparison.Ordinal);
        var repository = value.AsSpan(0, digest);
        var separator = repository.LastIndexOf('/');
        return repository[(separator + 1)..].SequenceEqual("runtime-supervisor");
    }

    private static void RequireCanonicalHelperPath(string? value, string extension, string profileId, string operation)
    {
        const string prefix = "/opt/sharplabnext/";
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal) || !value.EndsWith(extension, StringComparison.Ordinal) || value[prefix.Length..].Length == extension.Length || value[prefix.Length..].Any(static character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
        {
            throw new BundleValidationException($"Runtime '{profileId}' {operation} helper path '{value}' is not canonical.");
        }
    }

    private static void AddHelper(IDictionary<string, string> helpers, string profileId, string path, string digest)
    {
        if (helpers.TryGetValue(path, out var existing) && !string.Equals(existing, digest, StringComparison.Ordinal))
        {
            throw new BundleValidationException($"Runtime '{profileId}' receipt assigns different digests to helper '{path}'.");
        }
        helpers[path] = digest;
    }

    private static void RequireEqual(string? actual, string? expected, string profileId, string field)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new BundleValidationException($"Runtime '{profileId}' promotion receipt {field} does not match active release material.");
        }
    }

    private static void RequireCommit(string? value, string label)
    {
        if (!IsLowerHex(value, 40) && !IsLowerHex(value, 64))
            throw new BundleValidationException($"{label} must be a full lowercase Git commit.");
    }

    private static void RequireSha256(string? value, string label)
    {
        if (value is null || value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal) || !IsLowerHex(value[7..], 64))
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
        value is not null && DateTimeOffset.TryParseExact(value, ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _);

    private static bool IsOperationId(string? value) =>
        value is { Length: 35 } && value.StartsWith("op_", StringComparison.Ordinal) &&
        value[3..].All(static character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');

    private static bool IsCanonicalId(string? value) =>
        value is { Length: > 0 and <= 128 } &&
        value[0] is >= 'a' and <= 'z' or >= '0' and <= '9' &&
        value.All(static character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');

    private static bool IsLowerHex(string? value, int length) =>
        value is not null &&
        value.Length == length &&
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsCapability(string? value) => value is "run" or "jit-asm" or "inspection" or "execution-flow";

    private static bool HasCanonicalRetainedMetadata(RuntimePromotionImageFileSnapshot artifact) =>
        artifact.Role switch
        {
            "helper" or "desktop-helper" or "support-assembly" =>
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
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected!), Encoding.ASCII.GetBytes(actual!)))
        {
            throw new BundleValidationException($"{label}; expected {expected}, observed {actual}.");
        }
    }

    private sealed record TrustedFile(string RelativePath, string Sha256, byte[] Bytes);

    private sealed record RuntimePromotionPerformanceSnapshot(RuntimePromotionFileSnapshot Policy, RuntimePromotionFileSnapshot Evidence, RuntimePromotionMeasurementHelperSnapshot MeasurementHelper);

    internal static class RuntimePromotionPlanSignatureTrust
    {
        private const long MaximumPlanBytes = RuntimePromotionPlanWorkflow.MaximumPromotionDocumentBytes;
        private static readonly byte[] StrictSpki = Convert.FromBase64String("MCowBQYDK2VwAyEAFIFqMcFLVGn2aoQl0+CkTVtMS/QQlcZwUpSiag+hrRs=");
        private static readonly byte[] StrictPem = Encoding.ASCII.GetBytes("-----BEGIN PUBLIC KEY-----\n" + "MCowBQYDK2VwAyEAFIFqMcFLVGn2aoQl0+CkTVtMS/QQlcZwUpSiag+hrRs=\n" + "-----END PUBLIC KEY-----\n");
        internal static readonly RuntimePromotionPlanSignatureVerifier ProductionVerifier = new("sha256:d07b3d023359dfea9b8994115095768f9070ba6312092404b132e83d0e45d200", "eng/profiles/trust/runtime-promotion-plan-public.pem", StrictPem, StrictSpki, null);

        internal static void VerifyDetachedForTests(byte[] planBytes, byte[] signatureBytes)
        {
            ArgumentNullException.ThrowIfNull(planBytes);
            ArgumentNullException.ThrowIfNull(signatureBytes);
            if (planBytes.Length == 0 || planBytes[0] == 0xef || planBytes.Contains((byte)'\r') || planBytes[^1] != (byte)'\n')
                throw new BundleValidationException("Runtime promotion plan must be canonical LF UTF-8 JSON.");
            JsonDocument document;
            try { document = JsonDocument.Parse(planBytes); }
            catch (JsonException exception)
            {
                throw new BundleValidationException($"Runtime promotion plan is not valid JSON: {exception.Message}");
            }
            using (document)
            {
                if (!planBytes.AsSpan().SequenceEqual(Canonicalize(document.RootElement)))
                    throw new BundleValidationException("Runtime promotion plan JSON is not strict canonical sorted-key UTF-8 LF encoding.");
            }
            VerifySignature(planBytes, signatureBytes, ProductionVerifier);
        }

        public static async Task<RuntimePromotionPlanSignatureSnapshot> CaptureAsync(string repositoryRoot, RuntimeProfileDefinition profile, RuntimePromotionReceiptDocument receipt, RuntimePromotionPlanSignatureVerifier verifier, CancellationToken cancellationToken)
        {
            var binding = receipt.PlanSignature ?? throw new BundleValidationException($"Runtime '{profile.Id}' promotion receipt has no signed promotion plan binding.");
            var expectedPlanPath = $"profiles/runtime-promotion-plans/{profile.Id}.json";
            var expectedSignaturePath = expectedPlanPath + ".sig";
            if (!StringComparer.Ordinal.Equals(binding.Path, expectedSignaturePath) || !IsSha256Digest(binding.Sha256) || !StringComparer.Ordinal.Equals(binding.KeyId, verifier.KeyId))
            {
                throw new BundleValidationException($"Runtime '{profile.Id}' promotion receipt plan signature binding is not canonical.");
            }

            var plan = await ReadTrustedFileAsync(repositoryRoot, expectedPlanPath, ["profiles/runtime-promotion-plans"], MaximumPlanBytes, cancellationToken);
            var signature = await ReadTrustedFileAsync(repositoryRoot, expectedSignaturePath, ["profiles/runtime-promotion-plans"], MaximumPromotionPlanSignatureBytes, cancellationToken, requireCanonicalJson: false);
            var publicKey = await ReadTrustedFileAsync(repositoryRoot, verifier.PublicKeyPath, ["eng", "eng/profiles", "eng/profiles/trust"], MaximumPromotionPlanSignatureBytes, cancellationToken, requireCanonicalJson: false);
            if (!StringComparer.Ordinal.Equals(receipt.PlanSha256, plan.Sha256) || !StringComparer.Ordinal.Equals(binding.Sha256, signature.Sha256) || !publicKey.Bytes.AsSpan().SequenceEqual(verifier.PublicKeyPem))
            {
                throw new BundleValidationException($"Runtime '{profile.Id}' promotion receipt does not bind the committed signed plan bytes.");
            }

            await VerifyAsync(repositoryRoot, profile.Id, receipt.SourceRevision, plan.Bytes, signature.Bytes, publicKey.Bytes, verifier, cancellationToken);
            return new RuntimePromotionPlanSignatureSnapshot(new RuntimePromotionFileSnapshot(plan.RelativePath, plan.Sha256), new RuntimePromotionFileSnapshot(signature.RelativePath, signature.Sha256), new RuntimePromotionFileSnapshot(publicKey.RelativePath, publicKey.Sha256), plan.Bytes, signature.Bytes, publicKey.Bytes, verifier);
        }

        public static async Task RevalidateAsync(string repositoryRoot, string runtimeId, string buildSourceRevision, RuntimePromotionPlanSignatureSnapshot? expected, CancellationToken cancellationToken)
        {
            if (expected is null)
                throw new BundleValidationException($"Runtime '{runtimeId}' has no captured signed promotion plan.");
            var plan = await ReadTrustedFileAsync(repositoryRoot, expected.Plan.RelativePath, ["profiles/runtime-promotion-plans"], MaximumPlanBytes, cancellationToken);
            var signature = await ReadTrustedFileAsync(repositoryRoot, expected.Signature.RelativePath, ["profiles/runtime-promotion-plans"], MaximumPromotionPlanSignatureBytes, cancellationToken, requireCanonicalJson: false);
            var publicKey = await ReadTrustedFileAsync(repositoryRoot, expected.PublicKey.RelativePath, ["eng", "eng/profiles", "eng/profiles/trust"], MaximumPromotionPlanSignatureBytes, cancellationToken, requireCanonicalJson: false);
            if (!StringComparer.Ordinal.Equals(plan.Sha256, expected.Plan.Sha256) || !StringComparer.Ordinal.Equals(signature.Sha256, expected.Signature.Sha256) || !StringComparer.Ordinal.Equals(publicKey.Sha256, expected.PublicKey.Sha256) || !plan.Bytes.AsSpan().SequenceEqual(expected.PlanBytes) || !signature.Bytes.AsSpan().SequenceEqual(expected.SignatureBytes) || !publicKey.Bytes.AsSpan().SequenceEqual(expected.PublicKeyBytes))
            {
                throw new BundleValidationException($"Runtime '{runtimeId}' signed promotion plan material changed before release finalization.");
            }
            await VerifyAsync(repositoryRoot, runtimeId, buildSourceRevision, plan.Bytes, signature.Bytes, publicKey.Bytes, expected.Verifier, cancellationToken);
        }

        private static async Task VerifyAsync(string repositoryRoot, string runtimeId, string sourceRevision, byte[] planBytes, byte[] signatureText, byte[] publicKey, RuntimePromotionPlanSignatureVerifier verifier, CancellationToken cancellationToken)
        {
            if (!publicKey.AsSpan().SequenceEqual(verifier.PublicKeyPem))
                throw new BundleValidationException("Runtime promotion plan public key is not the committed strict Ed25519 SPKI PEM.");
            if (planBytes.Length == 0 || planBytes[0] == 0xef || planBytes.Contains((byte)'\r') || planBytes[^1] != (byte)'\n')
                throw new BundleValidationException("Runtime promotion plan must be canonical LF UTF-8 JSON.");
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(planBytes, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            }
            catch (JsonException exception)
            {
                throw new BundleValidationException($"Runtime promotion plan is not valid JSON: {exception.Message}");
            }
            using (document)
            {
                var canonical = Canonicalize(document.RootElement);
                if (!planBytes.AsSpan().SequenceEqual(canonical))
                    throw new BundleValidationException("Runtime promotion plan JSON is not strict canonical sorted-key UTF-8 LF encoding.");
            }

            RuntimePromotionPlanDocument plan;
            try
            {
                plan = RuntimePromotionJson.Deserialize<RuntimePromotionPlanDocument>(planBytes, RuntimePromotionPlanWorkflowInput.JsonOptions, $"Runtime '{runtimeId}' signed promotion plan");
            }
            catch (BundleValidationException)
            {
                throw;
            }
            if (!StringComparer.Ordinal.Equals(plan.ProfileId, runtimeId) || !StringComparer.Ordinal.Equals(plan.SourceRevision, sourceRevision) || !IsExpectedCandidateTarget(plan.CandidateTarget, plan.Family) || !IsGitObject(plan.SourceTree) || plan.BuildInputs is null || plan.BuildInputs.Count is < 1 or > 64 || plan.BuildInputs.Any(static item => !IsCanonicalInput(item.Key, item.Value)) || !HasRequiredBuildBindings(plan) || !StringComparer.Ordinal.Equals(plan.BuildInputsSha256, DigestCanonical(plan.BuildInputs)))
            {
                throw new BundleValidationException($"Runtime '{runtimeId}' signed promotion plan contract is invalid.");
            }

            if (verifier.SourceTreeVerifier is null)
                await VerifySourceTreeAsync(repositoryRoot, sourceRevision, plan.SourceTree, cancellationToken);
            else
                await verifier.SourceTreeVerifier(sourceRevision, plan.SourceTree, cancellationToken);
            VerifySignature(planBytes, signatureText, verifier);
        }

        private static void VerifySignature(byte[] planBytes, byte[] signatureText, RuntimePromotionPlanSignatureVerifier verifier)
        {
            string text;
            try { text = StrictUtf8.GetString(signatureText); }
            catch (DecoderFallbackException exception)
            {
                throw new BundleValidationException($"Runtime promotion plan signature is not UTF-8: {exception.Message}");
            }
            if (text.Contains('\r') || (text.Contains('\n') && (!text.EndsWith('\n') || text[..^1].Contains('\n'))))
                throw new BundleValidationException("Runtime promotion plan signature must be canonical Base64 text.");
            var base64 = text.EndsWith('\n') ? text[..^1] : text;
            if (base64.Length != 88 || !base64.EndsWith("==", StringComparison.Ordinal) || base64.Any(static value => !char.IsAsciiLetterOrDigit(value) && value is not '+' and not '/' and not '='))
            {
                throw new BundleValidationException("Runtime promotion plan signature must be one canonical 64-byte Ed25519 signature.");
            }
            byte[] signature;
            try { signature = Convert.FromBase64String(base64); }
            catch (FormatException exception)
            {
                throw new BundleValidationException($"Runtime promotion plan signature is not Base64: {exception.Message}");
            }
            if (signature.Length != 64 || !StringComparer.Ordinal.Equals(Convert.ToBase64String(signature), base64))
                throw new BundleValidationException("Runtime promotion plan signature must be one canonical 64-byte Ed25519 signature.");
            var signer = new Ed25519Signer();
            signer.Init(false, new Ed25519PublicKeyParameters(verifier.PublicKeySpki.AsSpan(12, 32).ToArray(), 0));
            signer.BlockUpdate(planBytes, 0, planBytes.Length);
            if (!signer.VerifySignature(signature))
                throw new BundleValidationException("Runtime promotion plan signature is invalid.");
        }

        private static async Task VerifySourceTreeAsync(string repositoryRoot, string revision, string expectedTree, CancellationToken cancellationToken)
        {
            using var process = new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo { FileName = "git", WorkingDirectory = Path.GetFullPath(repositoryRoot), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
            process.StartInfo.ArgumentList.Add("rev-parse");
            process.StartInfo.ArgumentList.Add(revision + "^{tree}");
            try { process.Start(); }
            catch (System.ComponentModel.Win32Exception exception)
            {
                throw new BundleValidationException($"Git is required to verify promotion plan source tree: {exception.Message}");
            }
            var output = ReadBoundedAsync(process.StandardOutput.BaseStream, 4096, "output", cancellationToken);
            var error = ReadBoundedAsync(process.StandardError.BaseStream, 4096, "error", cancellationToken);
            try
            {
                await Task.WhenAll(output, error);
                await process.WaitForExitAsync(cancellationToken);
            }
            catch
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                try { await Task.WhenAll(output, error); } catch { }
                throw;
            }
            if (process.ExitCode != 0 || !StringComparer.Ordinal.Equals(StrictUtf8.GetString(output.Result).Trim(), expectedTree))
                throw new BundleValidationException("Runtime promotion plan source tree does not match its source revision.");
        }

        private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, string streamName, CancellationToken cancellationToken)
        {
            await using var output = new MemoryStream();
            var buffer = new byte[1024];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    return output.ToArray();
                if (output.Length + read > maximumBytes)
                    throw new BundleValidationException($"Git {streamName} exceeded the promotion plan source-tree limit.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }

        private static byte[] Canonicalize(JsonElement value)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                WriteCanonical(writer, value);
            }
            stream.WriteByte((byte)'\n');
            return stream.ToArray();
        }

        private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in value.EnumerateObject().OrderBy(static item => item.Name, StringComparer.Ordinal))
                    {
                        if (!names.Add(property.Name))
                            throw new BundleValidationException("Runtime promotion plan JSON contains duplicate object properties.");
                        writer.WritePropertyName(property.Name);
                        WriteCanonical(writer, property.Value);
                    }
                    writer.WriteEndObject();
                    return;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
                    writer.WriteEndArray();
                    return;
                case JsonValueKind.String: writer.WriteStringValue(value.GetString()); return;
                case JsonValueKind.True: writer.WriteBooleanValue(true); return;
                case JsonValueKind.False: writer.WriteBooleanValue(false); return;
                case JsonValueKind.Null: writer.WriteNullValue(); return;
                case JsonValueKind.Number when value.TryGetInt64(out var integer): writer.WriteNumberValue(integer); return;
                default: throw new BundleValidationException("Runtime promotion plan has an unsupported JSON value.");
            }
        }

        private static string DigestCanonical(IReadOnlyDictionary<string, string> values)
        {
            var node = JsonSerializer.SerializeToElement(values);
            return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Canonicalize(node)));
        }

        private static bool IsCanonicalInput(string key, string value) =>
            key.Length is > 0 and <= 128 &&
            key.All(static character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_') &&
            !key.Contains("SECRET", StringComparison.Ordinal) &&
            !key.Contains("TOKEN", StringComparison.Ordinal) &&
            !key.Contains("PASSWORD", StringComparison.Ordinal) &&
            !key.Contains("PRIVATE", StringComparison.Ordinal) &&
            !key.Contains("PATH", StringComparison.Ordinal) &&
            value.Length is > 0 and <= 4096 &&
            value.All(static character => character is >= ' ' and <= '~');

        private static bool HasRequiredBuildBindings(RuntimePromotionPlanDocument plan) =>
            plan.BuildInputs.TryGetValue("IMAGE_PREFIX", out var imagePrefix) && imagePrefix.Length > 0 &&
            plan.BuildInputs.TryGetValue("RELEASE_ID", out var releaseId) && releaseId.Length > 0 &&
            plan.BuildInputs.TryGetValue("SOURCE_DATE_EPOCH", out var epoch) &&
            epoch.All(static character => char.IsAsciiDigit(character)) &&
            plan.BuildInputs.TryGetValue("RUNTIME_MATRIX_PROFILE_ID", out var profileId) &&
            StringComparer.Ordinal.Equals(profileId, plan.ProfileId) &&
            plan.BuildInputs.TryGetValue("RUNTIME_MATRIX_RUNTIME_VERSION", out var runtimeVersion) &&
            StringComparer.Ordinal.Equals(runtimeVersion, plan.ResolvedVersion) &&
            !plan.BuildInputs.ContainsKey("SOURCE_REVISION") &&
            (!plan.BuildInputs.TryGetValue("RUNTIME_MATRIX_RUNTIME_COMMIT", out var runtimeCommit) || StringComparer.Ordinal.Equals(runtimeCommit, plan.RuntimeIdentity.RuntimeCommit)) &&
            (!plan.BuildInputs.TryGetValue("RUNTIME_MATRIX_JIT_COMMIT", out var jitCommit) || StringComparer.Ordinal.Equals(jitCommit, plan.RuntimeIdentity.JitCommit));

        private static bool IsExpectedCandidateTarget(string? target, string? family) =>
            (target, family) switch
            {
                ("runtime-dotnet-matrix-candidate", "coreclr") => true,
                ("runtime-mono-matrix-candidate", "mono") => true,
                ("runtime-wine-dotnet-matrix-candidate", "coreclr-wine") => true,
                ("runtime-wine-framework-matrix-candidate", "netfx-clr-wine") => true,
                ("runtime-wine-framework-matrix-shared-candidate", "netfx-clr-wine") => true,
                _ => false
            };

        private static bool IsCanonicalId(string? value) =>
            value is { Length: > 0 and <= 128 } &&
            value[0] is >= 'a' and <= 'z' or >= '0' and <= '9' &&
            value.All(static character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');

        private static bool IsGitObject(string? value) => value is { Length: 40 or 64 } && value.All(static character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');

        private static bool IsSha256Digest(string? value) =>
            value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
            value.AsSpan(7).ToArray().All(static character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');
    }
}

internal static class WineCoreClrOperatorReceiptTrust
{
    internal const string KeyId = "sha256:16cdb3dd05ddc65de942187de063606b06c7c56c60e1a3394d166724d649e5a1";
    internal const string PublicKeyPath = "eng/profiles/trust/wine-coreclr-operator-receipt-public.pem";

    private const long MaximumReceiptBytes = 1024 * 1024;
    private const long MaximumSignatureBytes = 4096;
    private const long MaximumSourceFileBytes = 4 * 1024 * 1024;
    private const long MaximumGitErrorBytes = 64 * 1024;
    private static readonly byte[] StrictSpki = Convert.FromBase64String("MCowBQYDK2VwAyEAPyakAl2BdwqPhaYOUyfpQkjlCv9OrSLZ45InOqybNYY=");
    private static readonly byte[] StrictPem = Encoding.ASCII.GetBytes("-----BEGIN PUBLIC KEY-----\n" + "MCowBQYDK2VwAyEAPyakAl2BdwqPhaYOUyfpQkjlCv9OrSLZ45InOqybNYY=\n" + "-----END PUBLIC KEY-----\n");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex Sha256 = new("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex GitObject = new("^(?:[0-9a-f]{40}|[0-9a-f]{64})$", RegexOptions.CultureInvariant);
    private static readonly Regex ImmutableImage = new("^[^@\\s]+@sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    public static async Task<RuntimePromotionWineOperatorReceiptSnapshot?> CaptureAsync(string repositoryRoot, RepositorySourceProvenance source, RuntimeProfileDefinition profile, RuntimePromotionReceiptDocument promotionReceipt, InspectedImage candidateImage, IDockerCli docker, CancellationToken cancellationToken)
    {
        var isWine = profile.Family is "coreclr-wine" or "netfx-clr-wine";
        if (!isWine)
        {
            if (promotionReceipt.WineOperator is not null)
            {
                throw new BundleValidationException($"Runtime '{profile.Id}' is not Wine and cannot bind a Wine operator receipt.");
            }
            return null;
        }

        var binding = promotionReceipt.WineOperator ?? throw new BundleValidationException($"Wine runtime '{profile.Id}' promotion receipt has no signed operator receipt binding.");
        ValidateBinding(profile.Id, binding, profile.Family);
        var snapshot = await LoadAsync(repositoryRoot, profile.Id, promotionReceipt.SourceRevision, binding, docker, cancellationToken);
        ValidateCandidateImage(profile.Id, candidateImage, snapshot);
        return snapshot;
    }

    public static async Task RevalidateAsync(string repositoryRoot, string runtimeId, string buildSourceRevision, RuntimePromotionWineOperatorReceiptSnapshot? expected, IDockerCli docker, CancellationToken cancellationToken)
    {
        if (expected is null)
            return;

        var observed = await LoadAsync(repositoryRoot, runtimeId, buildSourceRevision, expected.Binding, docker, cancellationToken);
        if (!Equivalent(observed, expected))
        {
            throw new BundleValidationException($"Wine runtime '{runtimeId}' signed operator receipt changed before release finalization.");
        }
    }

    public static void ValidateCandidateImage(string runtimeId, InspectedImage image, RuntimePromotionWineOperatorReceiptSnapshot? receipt) => ValidateCandidateLabels(runtimeId, image.Labels, receipt);

    public static void ValidateCandidateImage(string runtimeId, DockerImageInspection image, RuntimePromotionWineOperatorReceiptSnapshot? receipt) => ValidateCandidateLabels(runtimeId, image.Labels, receipt);

    private static void ValidateCandidateLabels(string runtimeId, IReadOnlyDictionary<string, string> labels, RuntimePromotionWineOperatorReceiptSnapshot? receipt)
    {
        if (receipt is null)
            return;
        RequireEqual(labels, "io.sharplabnext.operator.receipt-sha256", receipt.Binding.ReceiptSha256, runtimeId);
        RequireEqual(labels, "io.sharplabnext.operator.receipt-key-id", receipt.Binding.KeyId, runtimeId);
        RequireEqual(labels, "io.sharplabnext.operator.userspace-reference", receipt.Binding.Reference, runtimeId);
        var finalOperatorReference = receipt.Binding.LineageKind == "framework-row"
            ? receipt.Binding.IntermediaryReference! : receipt.Binding.Reference;
        RequireEqual(labels, "io.sharplabnext.operator-image.wine", finalOperatorReference, runtimeId);
        if (receipt.Binding.LineageKind == "framework-parent")
        {
            RequireEqual(labels, "io.sharplabnext.framework.matrix-parent", receipt.Binding.IntermediaryReference!, runtimeId);
        }
        RequireEqual(labels, "io.sharplabnext.component.wine-coreclr-userspace.version", receipt.UserspaceVersion, runtimeId);
        RequireEqual(labels, "io.sharplabnext.component.wine-coreclr-userspace.digest", receipt.UserspaceDigest, runtimeId);
        RequireEqual(labels, "io.sharplabnext.component.wine-coreclr-userspace.source-uri", receipt.UserspaceSourceUri, runtimeId);
        RequireEqual(labels, "io.sharplabnext.operator.root", receipt.BaseImage, runtimeId);
    }

    private static bool Equivalent(RuntimePromotionWineOperatorReceiptSnapshot left, RuntimePromotionWineOperatorReceiptSnapshot right) =>
        left.Binding == right.Binding &&
        left.Receipt == right.Receipt &&
        left.Signature == right.Signature &&
        left.PublicKey == right.PublicKey &&
        StringComparer.Ordinal.Equals(left.SourceRevision, right.SourceRevision) &&
        StringComparer.Ordinal.Equals(left.SourceTree, right.SourceTree) &&
        StringComparer.Ordinal.Equals(left.OperatorImageId, right.OperatorImageId) &&
        left.OperatorImageSizeBytes == right.OperatorImageSizeBytes &&
        StringComparer.Ordinal.Equals(left.UserspaceVersion, right.UserspaceVersion) &&
        StringComparer.Ordinal.Equals(left.UserspaceDigest, right.UserspaceDigest) &&
        StringComparer.Ordinal.Equals(left.UserspaceSourceUri, right.UserspaceSourceUri) &&
        StringComparer.Ordinal.Equals(left.BaseImage, right.BaseImage) &&
        left.OperatorLabels.OrderBy(static item => item.Key, StringComparer.Ordinal).SequenceEqual(right.OperatorLabels.OrderBy(static item => item.Key, StringComparer.Ordinal));

    private static async Task<RuntimePromotionWineOperatorReceiptSnapshot> LoadAsync(string repositoryRoot, string runtimeId, string buildSourceRevision, RuntimePromotionWineOperatorBinding binding, IDockerCli docker, CancellationToken cancellationToken)
    {
        ValidateBinding(runtimeId, binding);
        var receiptFile = await ReadFileAsync(repositoryRoot, binding.ReceiptPath, MaximumReceiptBytes, cancellationToken);
        var signatureFile = await ReadFileAsync(repositoryRoot, binding.SignaturePath, MaximumSignatureBytes, cancellationToken);
        var publicKeyFile = await ReadFileAsync(repositoryRoot, PublicKeyPath, MaximumSignatureBytes, cancellationToken);
        if (!StringComparer.Ordinal.Equals(receiptFile.Sha256, binding.ReceiptSha256) || !StringComparer.Ordinal.Equals(signatureFile.Sha256, binding.SignatureSha256))
        {
            throw new BundleValidationException($"Wine runtime '{runtimeId}' operator receipt digest does not match its plan binding.");
        }
        if (!publicKeyFile.Bytes.AsSpan().SequenceEqual(StrictPem))
            throw new BundleValidationException("Wine operator receipt public key is not the committed strict Ed25519 SPKI PEM.");

        var receipt = ParseAndVerifyReceipt(receiptFile.Bytes, signatureFile.Bytes, publicKeyFile.Bytes);
        if (!StringComparer.Ordinal.Equals(KeyId, binding.KeyId) || !StringComparer.Ordinal.Equals(receipt.Operator.Reference, binding.Reference) || !StringComparer.Ordinal.Equals(receipt.SourceRevision, buildSourceRevision) || !StringComparer.Ordinal.Equals(receipt.SourceRevision, binding.SourceRevision) || !StringComparer.Ordinal.Equals(receipt.SourceTree, binding.SourceTree) || !StringComparer.Ordinal.Equals(receipt.Operator.ImageId, binding.ImageId) || receipt.Operator.SizeBytes != binding.SizeBytes)
        {
            throw new BundleValidationException($"Wine runtime '{runtimeId}' operator receipt does not bind the promotion plan source or operator.");
        }
        await VerifyCommittedSourceAsync(repositoryRoot, receipt, cancellationToken);

        var operatorImage = await docker.InspectImageAsync(receipt.Operator.Reference, cancellationToken);
        if (!StringComparer.Ordinal.Equals(operatorImage.ImageId, receipt.Operator.ImageId) ||
            operatorImage.SizeBytes != receipt.Operator.SizeBytes ||
            !StringComparer.Ordinal.Equals(operatorImage.OperatingSystem, "linux") ||
            !StringComparer.Ordinal.Equals(operatorImage.Architecture, "amd64") ||
            !operatorImage.RepoDigests.Contains(receipt.Operator.Reference, StringComparer.Ordinal) ||
            !operatorImage.Labels.OrderBy(static pair => pair.Key, StringComparer.Ordinal).SequenceEqual(receipt.Operator.Labels.OrderBy(static pair => pair.Key, StringComparer.Ordinal)))
        {
            throw new BundleValidationException($"Wine runtime '{runtimeId}' signed operator receipt does not match its inspected immutable clean operator.");
        }
        if (binding.IntermediaryReference is { } intermediaryReference)
        {
            var intermediary = await docker.InspectImageAsync(intermediaryReference, cancellationToken);
            var intermediaryOperatorLabel = binding.LineageKind == "framework-row"
                ? "io.sharplabnext.operator-base" : "io.sharplabnext.operator-image.wine";
            if (!StringComparer.Ordinal.Equals(intermediary.ImageId, binding.IntermediaryImageId) || intermediary.SizeBytes != binding.IntermediarySizeBytes || !intermediary.RepoDigests.Contains(intermediaryReference, StringComparer.Ordinal) || !intermediary.Labels.TryGetValue(intermediaryOperatorLabel, out var intermediaryOperator) || !StringComparer.Ordinal.Equals(intermediaryOperator, receipt.Operator.Reference))
            {
                throw new BundleValidationException($"Wine runtime '{runtimeId}' Framework intermediary does not retain the signed clean operator lineage.");
            }
        }

        return new RuntimePromotionWineOperatorReceiptSnapshot(
            binding,
            new RuntimePromotionFileSnapshot(receiptFile.RelativePath, receiptFile.Sha256),
            new RuntimePromotionFileSnapshot(signatureFile.RelativePath, signatureFile.Sha256),
            new RuntimePromotionFileSnapshot(publicKeyFile.RelativePath, publicKeyFile.Sha256),
            receipt.SourceRevision,
            receipt.SourceTree,
            receipt.Operator.ImageId,
            receipt.Operator.SizeBytes,
            receipt.Operator.Labels,
            receipt.Operator.UserspaceVersion,
            receipt.Operator.UserspaceDigest,
            receipt.Operator.UserspaceSourceUri,
            receipt.Operator.BaseImage);
    }

    internal static void ValidateBinding(string runtimeId, RuntimePromotionWineOperatorBinding binding, string? family = null)
    {
        var expectedPath = $"profiles/runtime-operator-receipts/wine-coreclr-{binding.SourceRevision}.json";
        if (!StringComparer.Ordinal.Equals(binding.ReceiptPath, expectedPath) ||
            !StringComparer.Ordinal.Equals(binding.SignaturePath, expectedPath + ".sig") ||
            !Sha256.IsMatch(binding.ReceiptSha256) || !Sha256.IsMatch(binding.SignatureSha256) ||
            !StringComparer.Ordinal.Equals(binding.KeyId, KeyId) ||
            !ImmutableImage.IsMatch(binding.Reference) || !Sha256.IsMatch(binding.ImageId) ||
            binding.SizeBytes <= 0 || !GitObject.IsMatch(binding.SourceRevision) ||
            !GitObject.IsMatch(binding.SourceTree) ||
            binding.LineageKind is not ("direct" or "framework-row" or "framework-parent") ||
            (binding.LineageKind == "direct" ? binding.IntermediaryReference is not null || binding.IntermediaryImageId is not null || binding.IntermediarySizeBytes is not null : !ImmutableImage.IsMatch(binding.IntermediaryReference ?? string.Empty) || !Sha256.IsMatch(binding.IntermediaryImageId ?? string.Empty) || binding.IntermediarySizeBytes <= 0))
        {
            throw new BundleValidationException($"Wine runtime '{runtimeId}' operator receipt binding is not canonical.");
        }
        if (family is not null && (family == "coreclr-wine" && binding.LineageKind != "direct" || family == "netfx-clr-wine" && binding.LineageKind == "direct"))
        {
            throw new BundleValidationException($"Wine runtime '{runtimeId}' operator receipt lineage does not match family '{family}'.");
        }
    }

    private static ParsedReceipt ParseAndVerifyReceipt(byte[] bytes, byte[] signatureBytes, byte[] publicKeyPem)
    {
        if (bytes.Length == 0 || bytes[0] == 0xef || bytes.Contains((byte)'\r') || bytes[^1] != (byte)'\n')
            throw new BundleValidationException("Wine operator receipt must be canonical LF UTF-8 JSON.");
        JsonDocument document;
        try
        {
            _ = StrictUtf8.GetString(bytes);
            document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (Exception exception) when (exception is DecoderFallbackException or JsonException)
        {
            throw new BundleValidationException($"Wine operator receipt is not valid UTF-8 JSON: {exception.Message}");
        }
        using (document)
        {
            var root = document.RootElement;
            RequireProperties(root, "Wine operator receipt", "schemaVersion", "keyId", "source", "operator");
            RequireNumber(root.GetProperty("schemaVersion"), 1, "Wine operator receipt schemaVersion");
            RequireString(root.GetProperty("keyId"), KeyId, "Wine operator receipt key ID", 72);
            var source = root.GetProperty("source");
            RequireProperties(source, "Wine operator receipt source", "revision", "tree", "files");
            var revision = RequireString(source.GetProperty("revision"), null, "Wine operator receipt source revision", 64);
            var tree = RequireString(source.GetProperty("tree"), null, "Wine operator receipt source tree", 64);
            if (!GitObject.IsMatch(revision) || !GitObject.IsMatch(tree))
                throw new BundleValidationException("Wine operator receipt source must use full lowercase Git identities.");
            var files = source.GetProperty("files");
            RequireProperties(files, "Wine operator receipt source files", RequiredSourceFiles);
            var sourceFiles = RequiredSourceFiles.ToDictionary(static path => path, path => RequireSha256(files.GetProperty(path), $"Wine operator receipt source file '{path}'"), StringComparer.Ordinal);

            var operatorElement = root.GetProperty("operator");
            RequireProperties(operatorElement, "Wine operator receipt operator", "reference", "imageId", "sizeBytes", "platform", "userspace", "baseImage", "labels");
            var reference = RequireString(operatorElement.GetProperty("reference"), null, "Wine operator receipt reference", 512);
            var imageId = RequireSha256(operatorElement.GetProperty("imageId"), "Wine operator receipt image ID");
            var sizeBytes = RequirePositiveInteger(operatorElement.GetProperty("sizeBytes"), "Wine operator receipt size");
            RequireString(operatorElement.GetProperty("platform"), "linux/amd64", "Wine operator receipt platform", 32);
            var baseImage = RequireString(operatorElement.GetProperty("baseImage"), null, "Wine operator receipt base image", 512);
            if (!ImmutableImage.IsMatch(reference) || !ImmutableImage.IsMatch(baseImage))
                throw new BundleValidationException("Wine operator receipt image references must be immutable.");
            var userspace = operatorElement.GetProperty("userspace");
            RequireProperties(userspace, "Wine operator receipt userspace", "version", "digest", "sourceUri");
            var userspaceVersion = RequireString(userspace.GetProperty("version"), null, "Wine operator receipt userspace version", 256);
            var userspaceDigest = RequireSha256(userspace.GetProperty("digest"), "Wine operator receipt userspace digest");
            var userspaceUri = RequireString(userspace.GetProperty("sourceUri"), null, "Wine operator receipt userspace URI", 2048);
            if (userspaceVersion.Length == 0 || !Uri.TryCreate(userspaceUri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new BundleValidationException("Wine operator receipt userspace must bind a version, SHA-256 digest, and HTTPS URI.");
            }
            var labels = operatorElement.GetProperty("labels");
            if (labels.ValueKind != JsonValueKind.Object || !labels.EnumerateObject().Any())
                throw new BundleValidationException("Wine operator receipt labels must be a non-empty string map.");
            var labelValues = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var label in labels.EnumerateObject())
            {
                RequireCanonicalAscii(label.Name, "Wine operator receipt label name", 256);
                var labelValue = RequireString(label.Value, null, "Wine operator receipt label");
                if (!labelValues.TryAdd(label.Name, labelValue))
                    throw new BundleValidationException("Wine operator receipt labels contain duplicate keys.");
            }

            var canonical = Canonicalize(root);
            if (!bytes.AsSpan().SequenceEqual(canonical))
                throw new BundleValidationException("Wine operator receipt JSON is not strict canonical sorted-key UTF-8 LF encoding.");
            VerifySignature(canonical, signatureBytes, publicKeyPem);
            return new ParsedReceipt(revision, tree, sourceFiles, new ParsedOperator(reference, imageId, sizeBytes, labelValues, userspaceVersion, userspaceDigest, userspaceUri, baseImage));
        }
    }

    private static void VerifySignature(byte[] canonical, byte[] signatureText, byte[] publicKeyPem)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(signatureText);
        }
        catch (DecoderFallbackException exception)
        {
            throw new BundleValidationException($"Wine operator receipt signature is not UTF-8: {exception.Message}");
        }
        if (text.Contains('\r') || (text.Contains('\n') && (!text.EndsWith('\n') || text[..^1].Contains('\n'))))
            throw new BundleValidationException("Wine operator receipt signature must be canonical Base64 text.");
        var base64 = text.EndsWith('\n') ? text[..^1] : text;
        if (base64.Length != 88 || !base64.EndsWith("==", StringComparison.Ordinal) || base64.Any(static value => !char.IsAsciiLetterOrDigit(value) && value is not '+' and not '/' and not '='))
        {
            throw new BundleValidationException("Wine operator receipt signature must be one canonical 64-byte Ed25519 signature.");
        }
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(base64);
        }
        catch (FormatException exception)
        {
            throw new BundleValidationException($"Wine operator receipt signature is not Base64: {exception.Message}");
        }
        if (signature.Length != 64 || !StringComparer.Ordinal.Equals(Convert.ToBase64String(signature), base64) || !publicKeyPem.AsSpan().SequenceEqual(StrictPem))
        {
            throw new BundleValidationException("Wine operator receipt signature or public key is not canonical.");
        }
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(StrictSpki.AsSpan(12, 32).ToArray(), 0));
        verifier.BlockUpdate(canonical, 0, canonical.Length);
        if (!verifier.VerifySignature(signature))
            throw new BundleValidationException("Wine operator receipt signature is invalid.");
    }

    private static async Task VerifyCommittedSourceAsync(string repositoryRoot, ParsedReceipt receipt, CancellationToken cancellationToken)
    {
        var tree = StrictUtf8.GetString(await RunGitAsync(repositoryRoot, ["rev-parse", receipt.SourceRevision + "^{tree}"], 1024, cancellationToken)).TrimEnd('\n');
        if (!StringComparer.Ordinal.Equals(tree, receipt.SourceTree))
            throw new BundleValidationException("Wine operator receipt source tree does not match its source revision.");
        foreach (var path in RequiredSourceFiles)
        {
            var bytes = await RunGitAsync(repositoryRoot, ["show", receipt.SourceRevision + ":" + path], MaximumSourceFileBytes, cancellationToken);
            var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!StringComparer.Ordinal.Equals(digest, receipt.SourceFiles[path]))
            {
                throw new BundleValidationException($"Wine operator receipt committed file digest does not match '{path}'.");
            }
        }
    }

    private static async Task<ReadFile> ReadFileAsync(string repositoryRoot, string relativePath, long maximumBytes, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains('\\') || Path.IsPathRooted(relativePath) || relativePath.Split('/').Any(static part => part is "" or "." or ".."))
        {
            throw new BundleValidationException("Wine operator receipt path is not canonical.");
        }
        var root = Path.GetFullPath(repositoryRoot);
        var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new BundleValidationException("Wine operator receipt path escapes the repository.");
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory is not null && !StringComparer.OrdinalIgnoreCase.Equals(directory.FullName, root); directory = directory.Parent)
        {
            directory.Refresh();
            if (!directory.Exists || directory.LinkTarget is not null || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new BundleValidationException($"Wine operator receipt directory '{directory.FullName}' is not a regular directory.");
            }
        }
        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length < 1 || info.Length > maximumBytes)
        {
            throw new BundleValidationException($"Wine operator receipt material '{relativePath}' must be a bounded regular non-link file.");
        }
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.LongLength != info.Length)
            throw new BundleValidationException($"Wine operator receipt material '{relativePath}' changed while reading.");
        return new ReadFile(relativePath, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes);
    }

    private static async Task<byte[]> RunGitAsync(string repositoryRoot, IReadOnlyList<string> arguments, long maximumBytes, CancellationToken cancellationToken)
    {
        using var process = new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo { FileName = "git", WorkingDirectory = Path.GetFullPath(repositoryRoot), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new BundleValidationException($"Git is required to verify Wine operator source closure: {exception.Message}");
        }
        var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, maximumBytes, "output", cancellationToken);
        var stderr = ReadBoundedAsync(process.StandardError.BaseStream, MaximumGitErrorBytes, "error", cancellationToken);
        try
        {
            await Task.WhenAll(stdout, stderr);
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (GitOutputLimitException exception)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            try { await Task.WhenAll(stdout, stderr); } catch { }
            throw new BundleValidationException(exception.Message);
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            try { await Task.WhenAll(stdout, stderr); } catch { }
            throw;
        }
        if (process.ExitCode != 0)
        {
            var message = Encoding.UTF8.GetString(stderr.Result).ReplaceLineEndings(" ").Trim();
            throw new BundleValidationException($"Could not verify Wine operator source closure: {message}");
        }
        return stdout.Result;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, long maximumBytes, string streamName, CancellationToken cancellationToken)
    {
        await using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return output.ToArray();
            if (output.Length + read > maximumBytes)
            {
                throw new GitOutputLimitException($"Git {streamName} exceeded the Wine operator source closure limit.");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static byte[] Canonicalize(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            WriteCanonical(writer, value);
        }
        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                return;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                return;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                return;
            case JsonValueKind.Number when value.TryGetInt64(out var integer):
                writer.WriteNumberValue(integer);
                return;
            default:
                throw new BundleValidationException("Wine operator receipt has an unsupported JSON value.");
        }
    }

    private static void RequireProperties(JsonElement value, string description, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new BundleValidationException($"{description} must be an object.");
        var actual = value.EnumerateObject().Select(static item => item.Name).ToArray();
        if (actual.Length != names.Length || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length || !actual.Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new BundleValidationException($"{description} has missing, duplicate, or unknown properties.");
        }
    }

    private static string RequireString(JsonElement value, string? expected, string description, int maximumLength = 2048)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } result ||
            (expected is not null && !StringComparer.Ordinal.Equals(result, expected)))
        {
            throw new BundleValidationException($"{description} is invalid.");
        }
        RequireCanonicalAscii(result, description, maximumLength);
        return result;
    }

    private static void RequireCanonicalAscii(string value, string description, int maximumLength = 2048)
    {
        if (value.Length == 0 || value.Length > maximumLength || value.Any(static character => character is < ' ' or > '~'))
        {
            throw new BundleValidationException($"{description} must be bounded printable ASCII.");
        }
    }

    private static string RequireSha256(JsonElement value, string description)
    {
        var result = RequireString(value, null, description, 72);
        if (!Sha256.IsMatch(result))
            throw new BundleValidationException($"{description} is not SHA-256.");
        return result;
    }

    private static long RequirePositiveInteger(JsonElement value, string description)
    {
        if (!value.TryGetInt64(out var result) || result <= 0 || result > 9_007_199_254_740_991)
            throw new BundleValidationException($"{description} must be a positive integer.");
        return result;
    }

    private static void RequireNumber(JsonElement value, long expected, string description)
    {
        if (!value.TryGetInt64(out var result) || result != expected)
            throw new BundleValidationException($"{description} is invalid.");
    }

    private static void RequireEqual(IReadOnlyDictionary<string, string> labels, string name, string expected, string runtimeId)
    {
        if (!labels.TryGetValue(name, out var actual) || !StringComparer.Ordinal.Equals(actual, expected))
        {
            throw new BundleValidationException($"Wine runtime '{runtimeId}' candidate label '{name}' does not match its signed operator receipt.");
        }
    }

    private static readonly string[] RequiredSourceFiles =
    [
        "deploy/docker/Dockerfile.operator-wine-coreclr",
        "eng/bake.hcl",
        "profiles/lock.json",
        "profiles/runtime-wine-packages.json"
    ];

    private sealed record ReadFile(string RelativePath, string Sha256, byte[] Bytes);
    private sealed record ParsedReceipt(string SourceRevision, string SourceTree, IReadOnlyDictionary<string, string> SourceFiles, ParsedOperator Operator);
    private sealed record ParsedOperator(string Reference, string ImageId, long SizeBytes, IReadOnlyDictionary<string, string> Labels, string UserspaceVersion, string UserspaceDigest, string UserspaceSourceUri, string BaseImage);

    private sealed class GitOutputLimitException(string message) : Exception(message);
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
    RuntimePromotionMeasurementHelperSnapshot MeasurementHelper,
    IReadOnlyList<RuntimePromotionImageFileSnapshot> ImageFiles,
    RuntimePromotionWineOperatorReceiptSnapshot? WineOperatorReceipt = null,
    RuntimePromotionPlanSignatureSnapshot? SignedPlan = null);

internal sealed record RuntimePromotionPlanSignatureSnapshot(
    RuntimePromotionFileSnapshot Plan,
    RuntimePromotionFileSnapshot Signature,
    RuntimePromotionFileSnapshot PublicKey,
    byte[] PlanBytes,
    byte[] SignatureBytes,
    byte[] PublicKeyBytes,
    RuntimePromotionPlanSignatureVerifier Verifier);

internal sealed record RuntimePromotionPlanSignatureVerifier(string KeyId, string PublicKeyPath, byte[] PublicKeyPem, byte[] PublicKeySpki, Func<string, string, CancellationToken, Task>? SourceTreeVerifier);

internal sealed record RuntimePromotionWineOperatorReceiptSnapshot(
    RuntimePromotionWineOperatorBinding Binding,
    RuntimePromotionFileSnapshot Receipt,
    RuntimePromotionFileSnapshot Signature,
    RuntimePromotionFileSnapshot PublicKey,
    string SourceRevision,
    string SourceTree,
    string OperatorImageId,
    long OperatorImageSizeBytes,
    IReadOnlyDictionary<string, string> OperatorLabels,
    string UserspaceVersion,
    string UserspaceDigest,
    string UserspaceSourceUri,
    string BaseImage);

internal sealed record RuntimePromotionMeasurementHelperSnapshot(string Implementation, string Reference, string ImageId, long SizeBytes, string Entrypoint, string SourceRevision, string ContentSha256);

internal sealed record RuntimePromotionFileSnapshot(string RelativePath, string Sha256);

internal sealed record RuntimePromotionOperationFileIdentity(string Path, string Sha256);

internal sealed record RuntimePromotionImageFileSnapshot(string Path, string Sha256, long SizeBytes, string Role, string Format, string Architecture);

internal sealed record RuntimePromotionChecksSnapshot(IReadOnlyList<RuntimePromotionFileSnapshot> Evidence, IReadOnlyList<RuntimePromotionImageFileSnapshot> ImageFiles, string PreflightProfileSha256);

internal sealed class RuntimePromotionReceiptDocument
{
    public required int SchemaVersion { get; init; }
    public required string PlanSha256 { get; init; }
    public RuntimePromotionPlanSignatureBinding? PlanSignature { get; init; }
    public required string ProfileId { get; init; }
    public required string MatrixTargetId { get; init; }
    public required string Platform { get; init; }
    public required string Family { get; init; }
    public required string ResolvedVersion { get; init; }
    public required RuntimePromotionImageIdentity Image { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("wineOperator")]
    public RuntimePromotionWineOperatorBinding? WineOperator { get; init; }

    public required RuntimePromotionComponentIdentity ComponentIdentity { get; init; }
    public required RuntimePromotionRuntimeIdentity RuntimeIdentity { get; init; }
    public required RuntimePromotionOperations Operations { get; init; }
    public required RuntimePromotionPerformanceBinding Performance { get; init; }
    public required string SourceRevision { get; init; }
    public required List<RuntimePromotionCapabilityCheck?> Checks { get; init; }
}

internal sealed class RuntimePromotionPlanSignatureBinding
{
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
    public required string KeyId { get; init; }
}

internal sealed class RuntimePromotionImageIdentity
{
    public required string Reference { get; init; }
    public required string ImageId { get; init; }
    public required long SizeBytes { get; init; }
}

internal sealed record RuntimePromotionWineOperatorBinding
{
    public required string ReceiptPath { get; init; }
    public required string ReceiptSha256 { get; init; }
    public required string SignaturePath { get; init; }
    public required string SignatureSha256 { get; init; }
    public required string KeyId { get; init; }
    public required string Reference { get; init; }
    public required string ImageId { get; init; }
    public required long SizeBytes { get; init; }
    public required string SourceRevision { get; init; }
    public required string SourceTree { get; init; }
    public required string LineageKind { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IntermediaryReference { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IntermediaryImageId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? IntermediarySizeBytes { get; init; }
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
    public required RuntimePerformanceMeasurementHelper MeasurementHelper { get; init; }
    public required string SourceRevision { get; init; }
    public required RuntimePerformancePolicyIdentity Policy { get; init; }
    public required List<string> Capabilities { get; init; }
    public required string SourceMappingKind { get; init; }
    public required RuntimePerformanceEnvironment Environment { get; init; }
    public required string CompletedAtUtc { get; init; }
    public required string Result { get; init; }
    public required RuntimePerformanceEvidenceScenarios Scenarios { get; init; }
}

internal sealed class RuntimePerformanceMeasurementHelper
{
    public required string Implementation { get; init; }
    public required RuntimePromotionImageIdentity Image { get; init; }
    public required string Entrypoint { get; init; }
    public required string SourceRevision { get; init; }
    public required string ContentSha256 { get; init; }
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
    public required long CompletionPeakMemoryBytes { get; init; }
    public required string OperationId { get; init; }
    public required int ResourceSampleCount { get; init; }
    public required int PostCompletionResourceSampleCount { get; init; }
    public required string CompletedAtUtc { get; init; }
}
