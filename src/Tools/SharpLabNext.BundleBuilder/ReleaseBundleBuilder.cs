using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpLabNext.Catalog;
using SharpLabNext.Contracts;
using SharpLabNext.RuntimeProfile.Sdk;

namespace SharpLabNext.BundleBuilder;

public sealed class ReleaseBundleBuilder
{
    private readonly IDockerCli docker;
    private readonly IBundleSigner? signer;
    private readonly IRepositorySourceInspector? sourceInspector;
    private readonly IRuntimePromotionSourceInspector? runtimePromotionSourceInspector;
    private readonly IExternalSourceMaterialFetcher? externalSourceMaterialFetcher;
    private readonly IWineRuntimePackageManifestSnapshotProvider wineManifestProvider;
    private readonly RuntimePromotionPlanSignatureVerifier runtimePromotionPlanSignatureVerifier;

    public ReleaseBundleBuilder(
        IDockerCli docker,
        IBundleSigner? signer = null,
        IRepositorySourceInspector? sourceInspector = null,
        IRuntimePromotionSourceInspector? runtimePromotionSourceInspector = null,
        IExternalSourceMaterialFetcher? externalSourceMaterialFetcher = null)
        : this(
            docker,
            signer,
            sourceInspector,
            runtimePromotionSourceInspector,
            externalSourceMaterialFetcher,
            new RepositoryWineRuntimePackageManifestSnapshotProvider(),
            RuntimePromotionTrust.RuntimePromotionPlanSignatureTrust.ProductionVerifier)
    {
    }

    internal ReleaseBundleBuilder(
        IDockerCli docker,
        IBundleSigner? signer,
        IRepositorySourceInspector? sourceInspector,
        IRuntimePromotionSourceInspector? runtimePromotionSourceInspector,
        IExternalSourceMaterialFetcher? externalSourceMaterialFetcher,
        IWineRuntimePackageManifestSnapshotProvider wineManifestProvider,
        RuntimePromotionPlanSignatureVerifier runtimePromotionPlanSignatureVerifier)
    {
        this.docker = docker ?? throw new ArgumentNullException(nameof(docker));
        this.signer = signer;
        this.sourceInspector = sourceInspector;
        this.runtimePromotionSourceInspector = runtimePromotionSourceInspector;
        this.externalSourceMaterialFetcher = externalSourceMaterialFetcher;
        this.wineManifestProvider = wineManifestProvider ??
            throw new ArgumentNullException(nameof(wineManifestProvider));
        this.runtimePromotionPlanSignatureVerifier = runtimePromotionPlanSignatureVerifier ??
            throw new ArgumentNullException(nameof(runtimePromotionPlanSignatureVerifier));
    }

    public const string RuntimeCommitLabel = "io.sharplabnext.runtime.commit";
    public const string JitCommitLabel = "io.sharplabnext.jit.commit";
    public const string ReferenceSetLabelPrefix = "io.sharplabnext.reference-set.";
    public const string ComponentLabelPrefix = "io.sharplabnext.component.";
    public const string BaseImageLabelPrefix = "io.sharplabnext.base-image.";
    public const string DevelopmentImageInputsLabel = "io.sharplabnext.development-image-inputs";
    public const string ComposeEnvironmentFileName = ".env";
    public const string DisabledGitHubOAuthSecretFileName = "github-oauth-client-secret.disabled";
    public const string SecurityAssetsDirectoryName = "security";
    public const string PromotionEvidenceDirectoryName = "promotion-evidence";
    public const string WineNoticeArchiveBundleRelativePath =
        "notices/wine-coreclr-copyright-notices.tar";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NewLine = "\n",
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly JsonSerializerOptions SpdxJsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        NewLine = "\n",
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly JsonSerializerOptions RuntimeProfileJsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly string[] RequiredRuntimeProfileProperties =
    [
        "schemaVersion",
        "id",
        "image",
        "family",
        "acceptedRuntimeFamilies",
        "acceptedFrameworks",
        "runtimeVersion",
        "runtimeCommit",
        "jitVersion",
        "jitCommit",
        "runtimeImageId",
        "rid",
        "architecture",
        "cpuFeatureProfile",
        "acceptedArtifactFormats",
        "capabilities",
        "providedRuntimeFeatureTags",
        "providedMetadataFeatureTags",
        "allowedSecurityPolicyIds",
        "container",
        "operations",
        "layout",
        "securityPolicies"
    ];

    public async Task<BundleBuildResult> BuildAsync(
        BundleBuilderCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var effectiveSourceInspector = sourceInspector ??
            (command.SigningKeyPath is null
                ? new ContentRepositorySourceInspector()
                : new GitRepositorySourceInspector(allowFallback: false));
        var source = await RepositorySourceProvenanceResolver.ResolveAsync(
            command.RepositoryRoot,
            command.SourceRevision,
            command.AllowUncommittedSourceForDevelopment,
            effectiveSourceInspector,
            cancellationToken);
        if ((!source.IsVerified || command.AllowDevelopmentImageInputs) &&
            command.SigningKeyPath is not null)
        {
            throw new BundleValidationException(
                "An unverified source or development image inputs cannot be used to create a signed release bundle.");
        }
        EnsureInputFile(command.CatalogPath);
        EnsureInputFile(command.LockPath);
        EnsureInputFile(command.DeploymentImagesPath);
        var baseImagesPath = Path.Combine(command.RepositoryRoot, "profiles", "base-images.json");
        EnsureInputFile(baseImagesPath);
        EnsureInputFile(command.LicensePolicyPath);
        EnsureInputFile(command.ComposePath);
        EnsureInputFile(command.NoticesPath);
        var runtimeProfilesPath = command.RuntimeProfilesPath ??
            Path.Combine(command.RepositoryRoot, "profiles", "runtimes");
        EnsureInputDirectory(runtimeProfilesPath);
        EnsureInputDirectory(Path.Combine(command.RepositoryRoot, "deploy", SecurityAssetsDirectoryName));
        if (command.SigningKeyPath is not null)
        {
            EnsureInputFile(command.SigningKeyPath);
            EnsureInputFile(command.SigningPublicKeyPath!);
        }

        var catalogTask = CatalogLoader.LoadCatalogAsync(command.CatalogPath, cancellationToken);
        var lockTask = CatalogLoader.LoadReleaseLockAsync(command.LockPath, cancellationToken);
        var deploymentTask = LoadDeploymentImagesAsync(command.DeploymentImagesPath, cancellationToken);
        var baseImagesTask = LoadBaseImagesAsync(baseImagesPath, cancellationToken);
        var runtimeMatrixBaseImagesTask = RuntimeMatrixBaseImageBindings.LoadAsync(
            command.RepositoryRoot,
            cancellationToken);
        var runtimeProfilesTask = LoadRuntimeProfilesAsync(runtimeProfilesPath, cancellationToken);
        await Task.WhenAll(
            catalogTask,
            lockTask,
            deploymentTask,
            baseImagesTask,
            runtimeMatrixBaseImagesTask,
            runtimeProfilesTask);
        var catalog = await catalogTask;
        var releaseLock = await lockTask;
        var deployment = await deploymentTask;
        var baseImages = await baseImagesTask;
        var runtimeMatrixBaseImages = await runtimeMatrixBaseImagesTask;
        var activeRuntimeProfiles = await runtimeProfilesTask;
        var wineManifestSnapshot = await wineManifestProvider.LoadValidatedAsync(
            command.RepositoryRoot,
            releaseLock,
            cancellationToken);
        var wineManifest = wineManifestSnapshot.Manifest;
        ValidateBaseImages(baseImages);
        WineRuntimePackageManifestLoader.ValidateResolvedPackagesForBundle(wineManifest);
        WineRuntimePackageManifestLoader.ValidateBaseImage(wineManifest, baseImages);
        var maintainedProvenance = await MaintainedProvenanceLoader.LoadAsync(
            command.RepositoryRoot,
            releaseLock,
            baseImages,
            cancellationToken);
        var dependencies = (await DependencyInventory.LoadAsync(
            command.RepositoryRoot,
            command.LicensePolicyPath,
            cancellationToken)).Components;
        var expectedReferenceSetDigests = ValidateInputs(
            catalog,
            releaseLock,
            deployment,
            command.ImageOverrides);
        ValidateRuntimeProfileBindings(catalog, releaseLock, activeRuntimeProfiles);
        ValidateRuntimePromotionBindings(catalog, deployment, activeRuntimeProfiles);
        var promotionBoundRuntimeIds = activeRuntimeProfiles
            .Where(static profile => profile.PromotionReceipt is not null)
            .Select(static profile => profile.Id)
            .ToHashSet(StringComparer.Ordinal);

        var imagePlan = CreateImagePlan(
            catalog,
            deployment,
            command.ImagePrefix,
            command.ImageOverrides);
        var definitionsById = deployment.Images.ToDictionary(static image => image.Id, StringComparer.Ordinal);
        var pendingInspections = new List<(
            DeploymentImageDefinition Definition,
            string Reference,
            DockerImageInspection Inspection)>(imagePlan.Images.Count);
        foreach (var plannedImage in imagePlan.Images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var definition = definitionsById[plannedImage.Id];
            var reference = plannedImage.Reference;
            var inspection = await docker.InspectImageAsync(reference, cancellationToken);
            pendingInspections.Add((definition, reference, inspection));
        }

        var inspectedImages = pendingInspections.Select(item => new InspectedImage(
            item.Definition.Id,
            item.Reference,
            item.Inspection.ImageId,
            item.Inspection.OperatingSystem,
            item.Inspection.Architecture,
            item.Inspection.SizeBytes,
            item.Inspection.RepoDigests,
            item.Inspection.Labels,
            item.Definition.ComposeService,
            item.Definition.ToolchainId,
            item.Definition.RuntimeId,
            item.Definition.ArtifactProcessorId,
            item.Definition.LockComponentId ?? item.Definition.ToolchainId ?? item.Definition.RuntimeId ??
            item.Definition.ArtifactProcessorId ?? item.Definition.Id,
            item.Definition.ReleaseIdEnvironment,
            item.Definition.ImageIdEnvironment)).ToArray();
        releaseLock = ResolveDevelopmentFrameworkComponentIdentities(
            catalog,
            releaseLock,
            inspectedImages,
            command.AllowDevelopmentImageInputs);

        foreach (var (definition, reference, inspection) in pendingInspections)
        {
            ValidateInspection(
                definition,
                reference,
                inspection,
                catalog.ReleaseId,
                source,
                releaseLock,
                baseImages,
                runtimeMatrixBaseImages,
                catalog,
                expectedReferenceSetDigests,
                definition.RuntimeId is not null &&
                promotionBoundRuntimeIds.Contains(definition.RuntimeId),
                command.AllowDevelopmentImageInputs);
        }
        var runtimePromotionTrust = await RuntimePromotionTrust.CaptureAsync(
            command.RepositoryRoot,
            source,
            catalog,
            releaseLock,
            deployment,
            activeRuntimeProfiles,
            inspectedImages,
            docker,
            cancellationToken,
            runtimePromotionPlanSignatureVerifier);
        await RuntimePromotionMatrixBinding.ValidateAsync(
            command.RepositoryRoot,
            activeRuntimeProfiles,
            runtimePromotionTrust,
            cancellationToken);
        var runtimePromotionSourceClosure = await RuntimePromotionSourceClosure.CaptureAsync(
            command.RepositoryRoot,
            source,
            runtimePromotionTrust,
            runtimePromotionSourceInspector,
            cancellationToken);
        var releaseRuntimeProfiles = MaterializeRuntimeProfiles(
            catalog,
            releaseLock,
            inspectedImages,
            activeRuntimeProfiles);

        var output = Path.GetFullPath(command.OutputDirectory);
        if (Directory.Exists(output) || File.Exists(output))
        {
            throw new BundleValidationException($"Bundle output '{output}' already exists.");
        }

        var outputParent = Path.GetDirectoryName(output)
            ?? throw new BundleValidationException("Bundle output has no parent directory.");
        Directory.CreateDirectory(outputParent);
        var staging = Path.Combine(outputParent, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(staging);
            await WriteBundleAsync(
                command,
                catalog,
                releaseLock,
                inspectedImages,
                releaseRuntimeProfiles,
                dependencies,
                wineManifestSnapshot,
                baseImages,
                runtimeMatrixBaseImages,
                baseImagesPath,
                maintainedProvenance,
                source,
                effectiveSourceInspector,
                runtimePromotionTrust,
                runtimePromotionSourceClosure,
                staging,
                cancellationToken);
            Directory.Move(staging, output);
        }
        finally
        {
            DeleteStagingDirectory(staging, outputParent);
        }

        return new BundleBuildResult(
            output,
            catalog.ReleaseId,
            inspectedImages,
            !command.MetadataOnly,
            command.SigningKeyPath is not null);
    }

    public static async Task<ReleaseImagePlan> CreateImagePlanAsync(
        BundleBuilderCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureInputFile(command.CatalogPath);
        EnsureInputFile(command.LockPath);
        EnsureInputFile(command.DeploymentImagesPath);
        var baseImagesPath = Path.Combine(command.RepositoryRoot, "profiles", "base-images.json");
        EnsureInputFile(baseImagesPath);
        EnsureInputFile(command.LicensePolicyPath);
        EnsureInputFile(command.ComposePath);
        EnsureInputFile(command.NoticesPath);
        var runtimeProfilesPath = command.RuntimeProfilesPath ??
            Path.Combine(command.RepositoryRoot, "profiles", "runtimes");
        EnsureInputDirectory(runtimeProfilesPath);
        EnsureInputDirectory(Path.Combine(command.RepositoryRoot, "deploy", SecurityAssetsDirectoryName));
        var catalogTask = CatalogLoader.LoadCatalogAsync(command.CatalogPath, cancellationToken);
        var lockTask = CatalogLoader.LoadReleaseLockAsync(command.LockPath, cancellationToken);
        var deploymentTask = LoadDeploymentImagesAsync(command.DeploymentImagesPath, cancellationToken);
        var baseImagesTask = LoadBaseImagesAsync(baseImagesPath, cancellationToken);
        var runtimeProfilesTask = LoadRuntimeProfilesAsync(runtimeProfilesPath, cancellationToken);
        await Task.WhenAll(
            catalogTask,
            lockTask,
            deploymentTask,
            baseImagesTask,
            runtimeProfilesTask);
        var catalog = await catalogTask;
        var releaseLock = await lockTask;
        var deployment = await deploymentTask;
        var baseImages = await baseImagesTask;
        var runtimeProfiles = await runtimeProfilesTask;
        var wineManifestSnapshot = await new RepositoryWineRuntimePackageManifestSnapshotProvider()
            .LoadValidatedAsync(command.RepositoryRoot, releaseLock, cancellationToken);
        ValidateBaseImages(baseImages);
        WineRuntimePackageManifestLoader.ValidateResolvedPackagesForBundle(
            wineManifestSnapshot.Manifest);
        WineRuntimePackageManifestLoader.ValidateBaseImage(
            wineManifestSnapshot.Manifest,
            baseImages);
        _ = await MaintainedProvenanceLoader.LoadAsync(
            command.RepositoryRoot,
            releaseLock,
            baseImages,
            cancellationToken);
        _ = await DependencyInventory.LoadAsync(
            command.RepositoryRoot,
            command.LicensePolicyPath,
            cancellationToken);
        _ = ValidateInputs(catalog, releaseLock, deployment, command.ImageOverrides);
        ValidateRuntimeProfileBindings(catalog, releaseLock, runtimeProfiles);
        ValidateRuntimePromotionBindings(catalog, deployment, runtimeProfiles);
        return CreateImagePlan(
            catalog,
            deployment,
            command.ImagePrefix,
            command.ImageOverrides);
    }

    public static ReleaseImagePlan CreateImagePlan(
        CatalogDocument catalog,
        DeploymentImageManifest deployment,
        string? imagePrefix,
        IReadOnlyDictionary<string, string>? imageOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(deployment);
        imageOverrides ??= new Dictionary<string, string>(StringComparer.Ordinal);
        var selected = SelectImages(catalog, deployment);
        var images = selected.Select(definition =>
        {
            var reference = ResolveImageReference(
                definition,
                catalog.ReleaseId,
                imagePrefix,
                imageOverrides);
            return new ReleaseImagePlanEntry
            {
                Id = definition.Id,
                Reference = reference,
                RuntimeId = definition.RuntimeId,
                Producer = ImageProducer(definition)
            };
        }).ToArray();
        if (images.Select(static image => image.Id).Distinct(StringComparer.Ordinal).Count() != images.Length ||
            images.Select(static image => image.Reference).Distinct(StringComparer.Ordinal).Count() != images.Length)
        {
            throw new BundleValidationException(
                "Selected deployment images must have unique IDs and final references.");
        }

        return new ReleaseImagePlan
        {
            SchemaVersion = 1,
            ReleaseId = catalog.ReleaseId,
            ImagePrefix = imagePrefix ?? "sharplabnext",
            Images = images
        };
    }

    private static ReleaseImageProducer ImageProducer(DeploymentImageDefinition definition)
    {
        if (definition.ImmutableReference is not null)
        {
            return new ReleaseImageProducer
            {
                Kind = "pull",
                Id = definition.ImmutableReference
            };
        }

        if (definition.RuntimeId is not null)
        {
            return definition.RuntimeId switch
            {
                "const-generics-linux-x64" => new ReleaseImageProducer
                {
                    Kind = "bake",
                    Id = "runtime-const-generics"
                },
                "wine-jsharp20-linux-x64" => new ReleaseImageProducer
                {
                    Kind = "bake",
                    Id = "runtime-wine-jsharp20"
                },
                _ => new ReleaseImageProducer
                {
                    Kind = "runtime-candidate",
                    Id = definition.RuntimeId
                }
            };
        }

        return new ReleaseImageProducer
        {
            Kind = "bake",
            Id = definition.Id
        };
    }

    public static IReadOnlyList<DeploymentImageDefinition> SelectImages(
        CatalogDocument catalog,
        DeploymentImageManifest deployment)
    {
        var toolchains = catalog.Toolchains.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        var runtimes = catalog.Runtimes.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        var processors = catalog.ArtifactProcessors.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        var result = new List<DeploymentImageDefinition>();
        foreach (var definition in deployment.Images)
        {
            var selected = definition.Always;
            if (definition.ToolchainId is not null)
            {
                var workerId = toolchains[definition.ToolchainId].WorkerId;
                selected |= catalog.Toolchains.Any(toolchain =>
                    string.Equals(toolchain.WorkerId, workerId, StringComparison.Ordinal) &&
                    toolchain.Availability.IsSelectable);
            }
            else if (definition.RuntimeId is not null)
            {
                selected |= runtimes[definition.RuntimeId].Availability.IsSelectable;
            }
            else if (definition.ArtifactProcessorId is not null)
            {
                selected |= processors[definition.ArtifactProcessorId].Availability.IsSelectable;
            }

            if (selected)
            {
                result.Add(definition);
            }
        }

        return result.OrderBy(static item => item.Id, StringComparer.Ordinal).ToArray();
    }

    public static string CreateImageReference(
        DeploymentImageDefinition definition,
        string releaseId,
        string? imagePrefix)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseId);
        if (imagePrefix is null)
        {
            return $"{definition.Repository}:{releaseId}";
        }

        var separator = definition.Repository.LastIndexOf('/');
        var imageName = separator < 0 ? definition.Repository : definition.Repository[(separator + 1)..];
        return $"{imagePrefix}/{imageName}:{releaseId}";
    }

    private static string ResolveImageReference(
        DeploymentImageDefinition definition,
        string releaseId,
        string? imagePrefix,
        IReadOnlyDictionary<string, string> imageOverrides)
    {
        if (imageOverrides.TryGetValue(definition.Id, out var configured))
        {
            if (definition.ImmutableReference is not null &&
                !string.Equals(configured, definition.ImmutableReference, StringComparison.Ordinal))
            {
                throw new BundleValidationException(
                    $"Deployment image '{definition.Id}' is promotion-bound to " +
                    $"'{definition.ImmutableReference}' and cannot be overridden with '{configured}'.");
            }

            return configured;
        }

        return definition.ImmutableReference ??
            CreateImageReference(definition, releaseId, imagePrefix);
    }

    public static string CreateComposeOverlay(
        CatalogDocument catalog,
        IReadOnlyList<InspectedImage> images,
        IReadOnlyList<RuntimeProfileDefinition>? runtimeProfiles = null)
    {
        var builder = new StringBuilder();
        var toolchainWorkerImages = IndexToolchainWorkerImages(catalog, images);
        var toolchainWorkerExpectations = catalog.Toolchains
            .Where(static toolchain => toolchain.Availability.IsSelectable)
            .Select(static toolchain => toolchain.WorkerId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static workerId => workerId, StringComparer.Ordinal)
            .Select(workerId => new
            {
                WorkerId = workerId,
                ImageId = toolchainWorkerImages.TryGetValue(workerId, out var image)
                    ? image.ImageId
                    : throw new BundleValidationException(
                        $"Selectable language worker '{workerId}' has no inspected deployment image.")
            })
            .ToArray();
        var artifactWorkerImages = images
            .Where(static image => image.ArtifactProcessorId is not null)
            .ToDictionary(static image => image.ArtifactProcessorId!, StringComparer.Ordinal);
        var artifactWorkerExpectations = catalog.ArtifactProcessors
            .Where(static processor => processor.Availability.IsSelectable)
            .OrderBy(static processor => processor.WorkerId, StringComparer.Ordinal)
            .Select(processor => new
            {
                processor.WorkerId,
                ImageId = artifactWorkerImages[processor.Id].ImageId
            })
            .ToArray();
        builder.AppendLine("# Generated by SharpLabNext.BundleBuilder. Do not edit.");
        builder.AppendLine("services:");
        foreach (var image in images.Where(static item =>
                         item.ComposeService is not null && item.ComposeService != "runtime-supervisor")
                     .OrderBy(static item => item.ComposeService, StringComparer.Ordinal))
        {
            builder.Append("  ").Append(image.ComposeService).AppendLine(":");
            builder.Append("    image: \"").Append(image.ImageId).AppendLine("\"");
            builder.AppendLine("    pull_policy: never");
            var isGateway = string.Equals(image.ComposeService, "gateway", StringComparison.Ordinal);
            if (image.ReleaseIdEnvironment is not null ||
                image.ImageIdEnvironment is not null ||
                isGateway && (toolchainWorkerExpectations.Length > 0 || artifactWorkerExpectations.Length > 0))
            {
                builder.AppendLine("    environment:");
                if (image.ReleaseIdEnvironment is not null)
                {
                    builder.Append("      ").Append(image.ReleaseIdEnvironment).Append(": \"")
                        .Append(EscapeYaml(catalog.ReleaseId)).AppendLine("\"");
                }
                if (image.ImageIdEnvironment is not null)
                {
                    builder.Append("      ").Append(image.ImageIdEnvironment).Append(": \"")
                        .Append(image.ImageId).AppendLine("\"");
                }
                if (isGateway)
                {
                    foreach (var expectation in toolchainWorkerExpectations)
                    {
                        builder.Append("      Services__LanguageWorkers__")
                            .Append(expectation.WorkerId)
                            .Append("__ExpectedWorkerImageId: \"")
                            .Append(expectation.ImageId)
                            .AppendLine("\"");
                    }
                    foreach (var expectation in artifactWorkerExpectations)
                    {
                        builder.Append("      Services__ArtifactWorkers__")
                            .Append(expectation.WorkerId)
                            .Append("__ExpectedWorkerImageId: \"")
                            .Append(expectation.ImageId)
                            .AppendLine("\"");
                    }
                }
            }
        }

        var runtimes = catalog.Runtimes
            .Where(static runtime => runtime.Availability.IsSelectable)
            .ToArray();
        runtimeProfiles ??= [];
        var runtimeProfileIndex = IndexRuntimeProfiles(runtimeProfiles);
        var selectableRuntimeIds = runtimes
            .Select(static runtime => runtime.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var profile in runtimeProfiles)
        {
            if (!selectableRuntimeIds.Contains(profile.Id))
            {
                throw new BundleValidationException(
                    $"Runtime profile '{profile.Id}' is not a selectable Catalog runtime.");
            }
        }
        foreach (var runtime in runtimes)
        {
            if (!runtimeProfileIndex.ContainsKey(runtime.Id))
            {
                throw new BundleValidationException(
                    $"Selectable runtime '{runtime.Id}' has no materialized active runtime profile.");
            }
        }
        var securityPolicies = MergeRuntimeSecurityPolicies(runtimeProfiles);
        var supervisorImage = images.SingleOrDefault(static item => item.ComposeService == "runtime-supervisor");
        if (supervisorImage is not null || runtimes.Length > 0)
        {
            builder.AppendLine("  runtime-supervisor:");
            if (supervisorImage is not null)
            {
                builder.Append("    image: \"").Append(supervisorImage.ImageId).AppendLine("\"");
                builder.AppendLine("    pull_policy: never");
            }
        }
        if (runtimes.Length > 0)
        {
            builder.AppendLine("    environment:");
            builder.AppendLine("      RuntimeSupervisor__RequireDigestPinnedImages: \"true\"");
            builder.AppendLine("      RuntimeSupervisorProfileOverlay__Enabled: \"true\"");
            for (var index = 0; index < runtimes.Length; index++)
            {
                var runtime = runtimes[index];
                AppendRuntimeProfile(builder, index, runtimeProfileIndex[runtime.Id]);
            }
            for (var index = 0; index < securityPolicies.Length; index++)
            {
                AppendSecurityPolicy(
                    builder,
                    $"RuntimeSupervisorProfileOverlay__SecurityPolicies__{index}",
                    securityPolicies[index]);
            }
        }

        return builder.ToString().ReplaceLineEndings("\n");
    }

    public static string CreateComposeEnvironment(string releaseId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseId);
        if (releaseId.Contains('\r') || releaseId.Contains('\n'))
        {
            throw new ArgumentException("Release ID cannot contain a line break.", nameof(releaseId));
        }

        return string.Join(
            "\n",
            [
                "# Docker Compose reads this file automatically.",
                "# Keep credentials outside the bundle.",
                "COMPOSE_PROJECT_NAME=sharplabnext",
                "COMPOSE_PATH_SEPARATOR=:",
                "COMPOSE_FILE=compose.prod.yaml:compose.generated.yaml",
                $"SHARPLABNEXT_RELEASE_ID={releaseId}",
                "SHARPLABNEXT_BIND_ADDRESS=127.0.0.1",
                "SHARPLABNEXT_HTTP_PORT=8080",
                "# deploy.sh creates this file during the first installation.",
                "SHARPLABNEXT_INTERNAL_SERVICE_TOKEN_FILE=./secrets/internal-service-token",
                "SHARPLABNEXT_GITHUB_OAUTH_ENABLED=false",
                "SHARPLABNEXT_RUNTIME_SESSION_REUSE_ENABLED=true",
                "SHARPLABNEXT_RUNTIME_APPARMOR_PROFILE=",
                "# On Linux, use the group ID from: stat -c '%g' /var/run/docker.sock",
                "DOCKER_GID=0",
                ""
            ]);
    }

    private static Dictionary<string, InspectedImage> IndexToolchainWorkerImages(
        CatalogDocument catalog,
        IReadOnlyList<InspectedImage> images)
    {
        var toolchains = catalog.Toolchains.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        var byWorkerId = new Dictionary<string, InspectedImage>(StringComparer.Ordinal);
        foreach (var image in images.Where(static item => item.ToolchainId is not null))
        {
            if (!toolchains.TryGetValue(image.ToolchainId!, out var toolchain))
            {
                throw new BundleValidationException(
                    $"Inspected image '{image.Id}' references missing toolchain '{image.ToolchainId}'.");
            }
            if (!byWorkerId.TryAdd(toolchain.WorkerId, image))
            {
                throw new BundleValidationException(
                    $"Language worker '{toolchain.WorkerId}' is represented by more than one inspected image.");
            }
        }
        return byWorkerId;
    }

    public static async Task WriteChecksumsAsync(
        string root,
        CancellationToken cancellationToken = default)
    {
        var checksumPath = Path.Combine(root, "checksums.sha256");
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(path, checksumPath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.Ordinal)
            .ToArray();
        var builder = new StringBuilder();
        foreach (var path in files)
        {
            await using var stream = File.OpenRead(path);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            builder.Append(Convert.ToHexStringLower(hash)).Append("  ").AppendLine(relative);
        }

        await File.WriteAllTextAsync(checksumPath, builder.ToString().ReplaceLineEndings("\n"), cancellationToken);
    }

    public static Task WriteDisabledGitHubOAuthSecretPlaceholderAsync(
        string root,
        CancellationToken cancellationToken = default) =>
        File.WriteAllBytesAsync(
            Path.Combine(root, DisabledGitHubOAuthSecretFileName),
            [],
            cancellationToken);

    private async Task WriteBundleAsync(
        BundleBuilderCommand command,
        CatalogDocument catalog,
        ReleaseLockDocument releaseLock,
        IReadOnlyList<InspectedImage> images,
        IReadOnlyList<RuntimeProfileDefinition> runtimeProfiles,
        IReadOnlyList<DependencyComponent> dependencies,
        WineRuntimePackageManifestSnapshot wineManifestSnapshot,
        BaseImageManifest baseImages,
        RuntimeMatrixBaseImageBindings runtimeMatrixBaseImages,
        string baseImagesPath,
        IReadOnlyList<MaintainedProvenanceInput> maintainedProvenance,
        RepositorySourceProvenance source,
        IRepositorySourceInspector effectiveSourceInspector,
        IReadOnlyList<RuntimePromotionTrustSnapshot> runtimePromotionTrust,
        RuntimePromotionSourceClosureSnapshot? runtimePromotionSourceClosure,
        string staging,
        CancellationToken cancellationToken)
    {
        var wineManifest = wineManifestSnapshot.Manifest;
        Directory.CreateDirectory(Path.Combine(staging, "sbom"));
        Directory.CreateDirectory(Path.Combine(staging, "provenance"));
        Directory.CreateDirectory(Path.Combine(staging, "provenance", "maintained"));
        Directory.CreateDirectory(Path.Combine(staging, "sources"));
        File.Copy(command.CatalogPath, Path.Combine(staging, "catalog.json"));
        File.Copy(command.ComposePath, Path.Combine(staging, "compose.prod.yaml"));
        File.Copy(baseImagesPath, Path.Combine(staging, "base-images.json"));
        File.Copy(command.NoticesPath, Path.Combine(staging, "THIRD-PARTY-NOTICES.md"));
        foreach (var provenance in maintainedProvenance)
        {
            File.Copy(
                provenance.FullPath,
                Path.Combine(staging, "provenance", "maintained", Path.GetFileName(provenance.FullPath)));
        }
        CopyDirectory(
            Path.Combine(command.RepositoryRoot, "deploy", SecurityAssetsDirectoryName),
            Path.Combine(staging, SecurityAssetsDirectoryName));
        await WriteDisabledGitHubOAuthSecretPlaceholderAsync(staging, cancellationToken);
        await WriteProfileUpdateStatusAsync(command, catalog.ReleaseId, staging, cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(staging, "compose.generated.yaml"),
            CreateComposeOverlay(catalog, images, runtimeProfiles),
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(staging, ComposeEnvironmentFileName),
            CreateComposeEnvironment(catalog.ReleaseId),
            cancellationToken);

        var bundleLock = CreateBundleLock(releaseLock, images);
        await WriteJsonAsync(Path.Combine(staging, "lock.json"), bundleLock, cancellationToken);
        var signingPublicKeySha256 = command.SigningPublicKeyPath is null
            ? null
            : await ComputeFileSha256Async(command.SigningPublicKeyPath, cancellationToken);
        var runtimeCatalog = catalog.Runtimes.ToDictionary(static runtime => runtime.Id, StringComparer.Ordinal);
        var bundle = new ReleaseBundleDocument
        {
            SchemaVersion = 1,
            ReleaseId = catalog.ReleaseId,
            CreatedAt = DateTimeOffset.UtcNow,
            Platform = "linux/amd64",
            ContainsImages = !command.MetadataOnly,
            HasSignature = command.SigningKeyPath is not null,
            Source = new BundleSourceDocument
            {
                Revision = source.Revision,
                HeadRevision = source.HeadRevision,
                Dirty = source.IsDirty,
                Verified = source.IsVerified,
                DevelopmentOverrideUsed = source.DevelopmentOverrideUsed,
                DevelopmentImageInputsUsed = images.Any(static image =>
                    image.Labels.TryGetValue(DevelopmentImageInputsLabel, out var value) &&
                    StringComparer.Ordinal.Equals(value, "true"))
            },
            SignatureAlgorithm = command.SigningKeyPath is null ? null : "ed25519",
            SignatureKeyId = command.SigningKeyPath is null
                ? null
                : command.SigningKeyId ?? $"sha256:{signingPublicKeySha256}",
            SigningPublicKeySha256 = signingPublicKeySha256,
            Images = images.Select(image => new BundleImageDocument
            {
                Id = image.Id,
                SourceReference = image.SourceReference,
                ImageId = image.ImageId,
                OperatingSystem = image.OperatingSystem,
                Architecture = image.Architecture,
                RepoDigests = image.RepoDigests,
                ComposeService = image.ComposeService,
                RuntimeId = image.RuntimeId,
                RuntimeCommit = image.RuntimeId is null ||
                    !RequiresCommitIdentity(runtimeCatalog[image.RuntimeId])
                        ? null
                        : RuntimeIdentity(image, RuntimeCommitLabel),
                JitCommit = image.RuntimeId is null ||
                    !RequiresCommitIdentity(runtimeCatalog[image.RuntimeId])
                        ? null
                        : RuntimeIdentity(image, JitCommitLabel)
            }).ToArray()
        };
        await WriteJsonAsync(Path.Combine(staging, "bundle.json"), bundle, cancellationToken);
        var expectedImages = string.Join(
            '\n',
            images.OrderBy(static image => image.Id, StringComparer.Ordinal)
                .Select(static image => $"{image.Id} {image.ImageId}")) + '\n';
        await File.WriteAllTextAsync(
            Path.Combine(staging, "images.expected"),
            expectedImages,
            cancellationToken);
        await WriteJsonAsync(
            Path.Combine(staging, "sbom", "dependencies.json"),
            new DependencyInventoryDocument(1, DateTimeOffset.UtcNow, dependencies),
            cancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(staging, "sbom", "runtime-wine-packages.json"),
            wineManifestSnapshot.ManifestBytes,
            cancellationToken);
        await WriteJsonAsync(
            Path.Combine(staging, "sbom", "release.spdx.json"),
            CreateSpdx(catalog.ReleaseId, bundleLock, images, dependencies, wineManifest),
            SpdxJsonOptions,
            cancellationToken);
        await WriteJsonAsync(
            Path.Combine(staging, "sbom", "release.cdx.json"),
            CreateCycloneDx(catalog.ReleaseId, bundleLock, images, dependencies, wineManifest),
            cancellationToken);
        await WriteJsonAsync(
            Path.Combine(staging, "provenance", "release.slsa.json"),
            CreateProvenance(
                command,
                catalog,
                releaseLock,
                images,
                dependencies,
                wineManifestSnapshot,
                baseImages,
                runtimeMatrixBaseImages,
                maintainedProvenance,
                source),
            cancellationToken);
        await WriteWeakCopyleftSourcesAsync(
            command.RepositoryRoot,
            dependencies,
            wineManifest,
            staging,
            cancellationToken);
        await WriteWineNoticeArchiveAsync(
            wineManifestSnapshot,
            images,
            staging,
            cancellationToken);
        await WriteRuntimePromotionEvidenceAsync(
            runtimePromotionTrust,
            runtimePromotionSourceClosure,
            staging,
            cancellationToken);
        var deploymentScriptBindings = GetDeploymentScriptBindings(runtimePromotionTrust);
        foreach (var scriptName in DeploymentScriptNames)
        {
            await WriteDeploymentScriptAsync(
                staging,
                scriptName,
                deploymentScriptBindings,
                cancellationToken);
        }
        if (command.SigningPublicKeyPath is not null)
        {
            File.Copy(command.SigningPublicKeyPath, Path.Combine(staging, "signing-public-key.pem"));
        }

        if (!command.MetadataOnly)
        {
            await docker.SaveImagesAsync(
                images.Select(static image => image.ImageId).ToArray(),
                Path.Combine(staging, "images.tar"),
                cancellationToken);
        }

        await RevalidateRuntimePromotionTrustAsync(
            command,
            source,
            effectiveSourceInspector,
            runtimePromotionTrust,
            runtimePromotionSourceClosure,
            cancellationToken);
        await WriteChecksumsAsync(staging, cancellationToken);
        if (command.SigningKeyPath is not null)
        {
            var effectiveSigner = signer ?? new OpenSslBundleSigner(command.OpenSslCommand);
            await effectiveSigner.SignAndVerifyAsync(
                Path.Combine(staging, "checksums.sha256"),
                Path.Combine(staging, "checksums.sha256.sig"),
                command.SigningKeyPath,
                Path.Combine(staging, "signing-public-key.pem"),
                cancellationToken);
        }
    }

    private static async Task WriteRuntimePromotionEvidenceAsync(
        IReadOnlyList<RuntimePromotionTrustSnapshot> trust,
        RuntimePromotionSourceClosureSnapshot? sourceClosure,
        string staging,
        CancellationToken cancellationToken)
    {
        if (trust.Count == 0)
            return;
        if (sourceClosure is null)
        {
            throw new BundleValidationException(
                "Promotion-bound release material has no captured source closure for offline evidence.");
        }

        var snapshotFiles = sourceClosure.Files.ToDictionary(
            static item => item.RelativePath,
            StringComparer.Ordinal);
        var captured = sourceClosure.CapturedFiles.ToDictionary(
            static item => item.RelativePath,
            StringComparer.Ordinal);
        if (snapshotFiles.Count != sourceClosure.Files.Count ||
            captured.Count != sourceClosure.CapturedFiles.Count ||
            captured.Count < snapshotFiles.Count)
        {
            throw new BundleValidationException(
                "Promotion source closure has duplicate or incomplete captured files.");
        }

        foreach (var snapshot in snapshotFiles.Values)
        {
            if (!captured.TryGetValue(snapshot.RelativePath, out var bytes) ||
                !StringComparer.Ordinal.Equals(snapshot.Sha256, bytes.Sha256) ||
                !StringComparer.Ordinal.Equals(
                    snapshot.Sha256,
                    $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes.Bytes))}"))
            {
                throw new BundleValidationException(
                    $"Promotion source closure capture is incomplete or corrupted for '{snapshot.RelativePath}'.");
            }
        }

        var bindings = new Dictionary<string, PromotionEvidenceBinding>(StringComparer.Ordinal);
        foreach (var runtime in trust.OrderBy(static item => item.RuntimeId, StringComparer.Ordinal))
        {
            AddBinding(bindings, runtime.Receipt.RelativePath, runtime.RuntimeId, "receipt");
            AddBinding(
                bindings,
                $"profiles/runtimes/candidates/{runtime.RuntimeId}.json",
                runtime.RuntimeId,
                "candidate-profile");
            var signedPlan = runtime.SignedPlan
                ?? throw new BundleValidationException(
                    $"Runtime '{runtime.RuntimeId}' has no captured signed promotion plan.");
            AddBinding(bindings, signedPlan.Signature.RelativePath, runtime.RuntimeId, "plan-signature");
            AddBinding(bindings, signedPlan.PublicKey.RelativePath, runtime.RuntimeId, "plan-signature-public-key");
            if (runtime.WineOperatorReceipt is { } wineOperatorReceipt)
            {
                AddBinding(bindings, wineOperatorReceipt.Receipt.RelativePath, runtime.RuntimeId, "operator-receipt");
                AddBinding(bindings, wineOperatorReceipt.Signature.RelativePath, runtime.RuntimeId, "operator-receipt-signature");
                AddBinding(bindings, wineOperatorReceipt.PublicKey.RelativePath, runtime.RuntimeId, "operator-receipt-public-key");
            }
            AddBinding(bindings, runtime.PerformancePolicy.RelativePath, runtime.RuntimeId, "performance-policy");
            foreach (var evidence in runtime.Evidence)
            {
                AddBinding(
                    bindings,
                    evidence.RelativePath,
                    runtime.RuntimeId,
                    evidence.RelativePath.EndsWith("/performance.json", StringComparison.Ordinal)
                        ? "performance-evidence"
                        : "capability-evidence");
            }
            AddBinding(
                bindings,
                $"profiles/runtime-promotion-plans/{runtime.RuntimeId}.json",
                runtime.RuntimeId,
                "plan");
            AddBinding(
                bindings,
                $"profiles/runtime-promotion-plans/{runtime.RuntimeId}.profile.json",
                runtime.RuntimeId,
                "preflight-profile");
            AddBinding(
                bindings,
                $"profiles/runtimes/{runtime.RuntimeId}.json",
                runtime.RuntimeId,
                "active-profile");
        }
        var expectedCapturedPaths = snapshotFiles.Keys
            .Concat(bindings.Keys)
            .ToHashSet(StringComparer.Ordinal);
        if (!expectedCapturedPaths.SetEquals(captured.Keys))
        {
            throw new BundleValidationException(
                "Promotion source closure has unexpected or missing captured evidence inputs.");
        }

        var root = Path.Combine(staging, PromotionEvidenceDirectoryName);
        var sourceRoot = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceRoot);
        var expectedRuntimeIds = trust.Select(static item => item.RuntimeId)
            .OrderBy(static item => item, StringComparer.Ordinal)
            .ToArray();
        var entries = new List<PromotionEvidenceManifestEntry>(captured.Count);
        foreach (var file in captured.Values.OrderBy(static item => item.RelativePath, StringComparer.Ordinal))
        {
            ValidatePromotionEvidencePath(file.RelativePath);
            var bundlePath = $"source/{file.RelativePath}";
            var destination = Path.Combine(
                sourceRoot,
                file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var destinationDirectory = Path.GetDirectoryName(destination)
                ?? throw new BundleValidationException("Promotion evidence destination has no parent directory.");
            Directory.CreateDirectory(destinationDirectory);
            await File.WriteAllBytesAsync(destination, file.Bytes, cancellationToken);

            bindings.TryGetValue(file.RelativePath, out var binding);
            entries.Add(new PromotionEvidenceManifestEntry
            {
                Kind = binding?.Kind ?? "source-closure",
                SourcePath = file.RelativePath,
                BundlePath = bundlePath,
                Sha256 = file.Sha256,
                SizeBytes = file.Bytes.LongLength,
                ProfileIds = binding?.RuntimeIds.ToArray() ?? expectedRuntimeIds,
                RuntimeIds = binding?.RuntimeIds.ToArray() ?? expectedRuntimeIds
            });
        }

        var actualRuntimeIds = entries.Where(static item => item.RuntimeIds is not null)
            .SelectMany(static item => item.RuntimeIds!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static item => item, StringComparer.Ordinal)
            .ToArray();
        if (!expectedRuntimeIds.SequenceEqual(actualRuntimeIds, StringComparer.Ordinal))
        {
            throw new BundleValidationException(
                "Promotion evidence manifest does not bind every promoted runtime.");
        }

        var manifestPath = Path.Combine(root, "manifest.json");
        await WriteJsonAsync(
            manifestPath,
            new PromotionEvidenceManifest
            {
                SchemaVersion = 1,
                BuildSourceRevision = sourceClosure.BuildSourceRevision,
                ReleaseSourceRevision = sourceClosure.ReleaseSourceRevision,
                PromotedRuntimeIds = expectedRuntimeIds,
                Entries = entries
            },
            cancellationToken);
        await WritePromotionEvidenceVerificationManifestAsync(
            Path.Combine(root, "manifest.tsv"),
            sourceClosure,
            $"sha256:{await ComputeFileSha256Async(manifestPath, cancellationToken)}",
            expectedRuntimeIds,
            entries,
            cancellationToken);
    }

    private static Task WritePromotionEvidenceVerificationManifestAsync(
        string path,
        RuntimePromotionSourceClosureSnapshot sourceClosure,
        string manifestJsonSha256,
        IReadOnlyList<string> promotedRuntimeIds,
        IReadOnlyList<PromotionEvidenceManifestEntry> entries,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>(entries.Count + 4)
        {
            "schemaVersion\t1",
            $"buildSourceRevision\t{sourceClosure.BuildSourceRevision}",
            $"releaseSourceRevision\t{sourceClosure.ReleaseSourceRevision}",
            $"manifestJsonSha256\t{manifestJsonSha256}",
            $"promotedRuntimeIds\t{string.Join(',', promotedRuntimeIds)}"
        };
        foreach (var entry in entries)
        {
            lines.Add(string.Join(
                '\t',
                "entry",
                entry.Kind,
                entry.ProfileIds.Length == 0 ? "-" : string.Join(',', entry.ProfileIds),
                entry.RuntimeIds.Length == 0 ? "-" : string.Join(',', entry.RuntimeIds),
                entry.SourcePath,
                entry.BundlePath,
                entry.Sha256,
                entry.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        return File.WriteAllTextAsync(
            path,
            (string.Join('\n', lines) + "\n").ReplaceLineEndings("\n"),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
    }

    private static void AddBinding(
        Dictionary<string, PromotionEvidenceBinding> bindings,
        string path,
        string runtimeId,
        string kind)
    {
        ValidatePromotionEvidencePath(path);
        if (bindings.TryGetValue(path, out var existing))
        {
            if (!StringComparer.Ordinal.Equals(existing.Kind, kind))
            {
                throw new BundleValidationException(
                    $"Promotion evidence source '{path}' has conflicting evidence kinds.");
            }
            existing.RuntimeIds.Add(runtimeId);
            return;
        }
        bindings[path] = new PromotionEvidenceBinding(
            kind,
            new SortedSet<string>(StringComparer.Ordinal) { runtimeId });
    }

    private static void ValidatePromotionEvidencePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains('\t') ||
            path.Contains('\r') || path.Contains('\n') || Path.IsPathRooted(path) ||
            path.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            throw new BundleValidationException($"Promotion evidence path '{path}' is not canonical.");
        }
    }

    private async Task RevalidateRuntimePromotionTrustAsync(
        BundleBuilderCommand command,
        RepositorySourceProvenance source,
        IRepositorySourceInspector effectiveSourceInspector,
        IReadOnlyList<RuntimePromotionTrustSnapshot> runtimePromotionTrust,
        RuntimePromotionSourceClosureSnapshot? runtimePromotionSourceClosure,
        CancellationToken cancellationToken)
    {
        if (runtimePromotionTrust.Count == 0)
            return;

        var currentSource = await RepositorySourceProvenanceResolver.ResolveAsync(
            command.RepositoryRoot,
            command.SourceRevision,
            command.AllowUncommittedSourceForDevelopment,
            effectiveSourceInspector,
            cancellationToken);
        if (currentSource != source)
        {
            throw new BundleValidationException(
                "Repository source identity changed while promotion-bound release material was being built.");
        }

        if (runtimePromotionSourceClosure is null)
        {
            throw new BundleValidationException(
                "Promotion-bound release material has no captured A-to-B source closure.");
        }

        await RuntimePromotionSourceClosure.RevalidateAsync(
            command.RepositoryRoot,
            runtimePromotionSourceClosure,
            runtimePromotionSourceInspector,
            cancellationToken);

        await RuntimePromotionTrust.RevalidateAsync(
            command.RepositoryRoot,
            runtimePromotionTrust,
            docker,
            cancellationToken);
    }

    private static async Task WriteProfileUpdateStatusAsync(
        BundleBuilderCommand command,
        string releaseId,
        string staging,
        CancellationToken cancellationToken)
    {
        const int maximumStatusBytes = 64 * 1024;
        var defaultPath = Path.Combine(
            command.RepositoryRoot,
            "artifacts",
            "profile-updater",
            "status.public.json");
        var inputPath = command.ProfileUpdateStatusPath ?? defaultPath;
        ProfileUpdateStatusDocument status;
        if (!File.Exists(inputPath))
        {
            if (command.ProfileUpdateStatusPath is not null)
            {
                throw new BundleValidationException(
                    "The configured public profile update status file does not exist.");
            }

            status = CreateUnknownProfileUpdateStatus(releaseId);
        }
        else
        {
            var length = new FileInfo(inputPath).Length;
            if (length is <= 0 or > maximumStatusBytes)
                throw new BundleValidationException("The public profile update status file has an invalid size.");
            try
            {
                await using var stream = new FileStream(
                    inputPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    16 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                status = await JsonSerializer.DeserializeAsync<ProfileUpdateStatusDocument>(
                    stream,
                    JsonOptions,
                    cancellationToken)
                    ?? throw new BundleValidationException(
                        "The public profile update status file is empty.");
            }
            catch (JsonException)
            {
                throw new BundleValidationException(
                    "The public profile update status file is invalid JSON.");
            }

            if (status.SchemaVersion != 1 ||
                !string.Equals(status.Active.ReleaseId, releaseId, StringComparison.Ordinal))
            {
                throw new BundleValidationException(
                    "The public profile update status does not match the bundle release.");
            }
            status = CreatePublicStatusProjection(status);
        }

        await WriteJsonAsync(
            Path.Combine(staging, "profile-update-status.json"),
            status,
            cancellationToken);
    }

    private static ProfileUpdateStatusDocument CreateUnknownProfileUpdateStatus(string releaseId) => new()
    {
        SchemaVersion = 1,
        Status = ProfileUpdateStatusKind.Unknown,
        Checked = false,
        Active = new ProfileUpdateReleaseStatus { ReleaseId = releaseId },
        UpdatedAt = DateTimeOffset.UtcNow,
        LastStage = new ProfileUpdatePublicStageStatus
        {
            Stage = ProfileUpdatePublicStage.None,
            Outcome = ProfileUpdatePublicStageOutcome.NotChecked
        }
    };

    private static ProfileUpdateStatusDocument CreatePublicStatusProjection(
        ProfileUpdateStatusDocument source) => new()
    {
        SchemaVersion = source.SchemaVersion,
        Status = source.Status,
        Checked = source.Checked,
        Active = CopyReleaseStatus(source.Active)!,
        LastKnownGood = CopyReleaseStatus(source.LastKnownGood),
        Candidate = CopyReleaseStatus(source.Candidate),
        UpdateAvailable = source.UpdateAvailable,
        CheckedAt = source.CheckedAt,
        UpdatedAt = source.UpdatedAt,
        LastStage = new ProfileUpdatePublicStageStatus
        {
            Stage = source.LastStage.Stage,
            Outcome = source.LastStage.Outcome,
            StartedAt = source.LastStage.StartedAt,
            CompletedAt = source.LastStage.CompletedAt,
            Error = source.LastStage.Outcome == ProfileUpdatePublicStageOutcome.Failed
                ? CreatePublicProfileUpdateError(source.LastStage.Error)
                : null
        }
    };

    private static ProfileUpdateReleaseStatus? CopyReleaseStatus(ProfileUpdateReleaseStatus? source) =>
        source is null
            ? null
            : new ProfileUpdateReleaseStatus
            {
                ReleaseId = source.ReleaseId,
                LockDigest = source.LockDigest
            };

    private static ProfileUpdatePublicError CreatePublicProfileUpdateError(
        ProfileUpdatePublicError? source) => source?.Code switch
    {
        "profile-update.check-failed" => PublicProfileUpdateError(
            source.Code,
            "Profile update check failed; update availability is unknown."),
        "profile-update.resolve-failed" => PublicProfileUpdateError(
            source.Code,
            "Profile candidate resolution failed; the approved release remains active."),
        "profile-update.build-failed" => PublicProfileUpdateError(
            source.Code,
            "Profile candidate build failed; the approved release remains active."),
        "profile-update.test-failed" => PublicProfileUpdateError(
            source.Code,
            "Profile candidate validation failed; the approved release remains active."),
        "profile-update.promote-failed" => PublicProfileUpdateError(
            source.Code,
            "Profile candidate promotion failed; the previous approved release remains active."),
        "profile-update.failed" => PublicProfileUpdateError(
            source.Code,
            "Profile update failed; the approved release remains active."),
        _ => PublicProfileUpdateError(
            "profile-update.failed",
            "The profile update check did not complete successfully.")
    };

    private static ProfileUpdatePublicError PublicProfileUpdateError(string code, string message) => new()
    {
        Code = code,
        Message = message
    };

    private async Task WriteWeakCopyleftSourcesAsync(
        string repositoryRoot,
        IReadOnlyList<DependencyComponent> dependencies,
        WineRuntimePackageManifest wineManifest,
        string staging,
        CancellationToken cancellationToken)
    {
        var materials = new List<SourceMaterialComponent>();
        foreach (var dependency in dependencies.Where(static dependency =>
                     !dependency.Optional &&
                     (dependency.License.Contains("LGPL-", StringComparison.OrdinalIgnoreCase) ||
                      dependency.License.Contains("MPL-", StringComparison.OrdinalIgnoreCase))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(dependency.PackageManager, "npm", StringComparison.Ordinal))
            {
                throw new BundleValidationException(
                    $"Weak-copyleft dependency '{dependency.PackageManager}:{dependency.Name}@{dependency.Version}' has no approved offline source material provider.");
            }

            var nodeModules = Path.GetFullPath(Path.Combine(repositoryRoot, "frontend", "node_modules"));
            var packagePath = Path.GetFullPath(Path.Combine(
                nodeModules,
                dependency.Name.Replace('/', Path.DirectorySeparatorChar)));
            var nodeModulesPrefix = nodeModules.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!packagePath.StartsWith(nodeModulesPrefix, StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(packagePath))
            {
                throw new BundleValidationException(
                    $"Weak-copyleft npm source material is missing for '{dependency.Name}@{dependency.Version}'. Run npm ci before bundling.");
            }

            var relative = Path.Combine(
                    "sources",
                    "npm",
                    $"{SafeFileName(dependency.Name)}-{SafeFileName(dependency.Version)}")
                .Replace('\\', '/');
            await CopyDirectoryBoundedAsync(
                packagePath,
                Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)),
                maximumBytes: 256L * 1024 * 1024,
                maximumFiles: 20_000,
                cancellationToken);
            materials.Add(new SourceMaterialComponent(
                dependency.PackageManager,
                dependency.Name,
                dependency.Version,
                dependency.License,
                dependency.SourceUri,
                relative));
        }

        await WriteOperatingSystemSourcesAsync(
            wineManifest,
            staging,
            materials,
            cancellationToken);

        await WriteJsonAsync(
            Path.Combine(staging, "sources", "manifest.json"),
            new SourceMaterialDocument(1, DateTimeOffset.UtcNow, materials),
            cancellationToken);
    }

    internal async Task WriteOperatingSystemSourcesAsync(
        WineRuntimePackageManifest manifest,
        string staging,
        List<SourceMaterialComponent> materials,
        CancellationToken cancellationToken)
    {
        var sourceRoot = Path.Combine(staging, "sources", "ubuntu");
        Directory.CreateDirectory(sourceRoot);
        var materialized = new HashSet<string>(StringComparer.Ordinal);
        var fileCount = 0;
        long totalBytes = 0;
        foreach (var sourcePackage in manifest.SourcePackages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = $"{sourcePackage.Name}\0{sourcePackage.Version}";
            if (!materialized.Add(identity))
                throw new BundleValidationException("Wine source package was requested more than once.");

            var sourceDirectory = sourcePackage.Files[0].Path[..sourcePackage.Files[0].Path.LastIndexOf('/')];
            if (sourcePackage.Files.Any(file =>
                    !file.Path.StartsWith(sourceDirectory + "/", StringComparison.Ordinal)))
            {
                throw new BundleValidationException(
                    $"Operating-system source package '{sourcePackage.Name}@{sourcePackage.Version}' spans multiple pool directories.");
            }
            var relative = $"sources/ubuntu/{sourcePackage.ArchiveSnapshotId}/{sourceDirectory}";
            var destination = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(destination);
            foreach (var file in sourcePackage.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceUri = WineRuntimePackageManifestLoader.ArchiveUri(
                    manifest,
                    sourcePackage.ArchiveSnapshotId,
                    file.Path);
                await using var material = await (externalSourceMaterialFetcher ??
                    new HttpClientExternalSourceMaterialFetcher()).FetchAsync(sourceUri, cancellationToken);
                if (Uri.Compare(
                        material.FinalUri,
                        sourceUri,
                        UriComponents.AbsoluteUri,
                        UriFormat.UriEscaped,
                        StringComparison.Ordinal) != 0)
                {
                    throw new BundleValidationException(
                        $"Operating-system source material '{file.Path}' redirected away from its reviewed snapshot URL.");
                }
                if (material.ContentLength is { } contentLength && contentLength != file.SizeBytes)
                {
                    throw new BundleValidationException(
                        $"Operating-system source material '{file.Path}' has an unexpected content length.");
                }

                var destinationPath = Path.Combine(destination, Path.GetFileName(file.Path));
                var inspection = await CopyExternalSourceMaterialAsync(
                    material.Content,
                    destinationPath,
                    file.SizeBytes,
                    WineRuntimePackageManifestLoader.MaximumClosureSourceTotalBytes - totalBytes,
                    cancellationToken);
                if (!string.Equals(inspection.Sha256, file.Sha256, StringComparison.Ordinal) ||
                    inspection.SizeBytes != file.SizeBytes)
                {
                    throw new BundleValidationException(
                        $"Operating-system source material '{file.Path}' does not match its reviewed SHA-256 identity.");
                }
                fileCount = checked(fileCount + 1);
                totalBytes = checked(totalBytes + inspection.SizeBytes);
            }

            var descriptor = sourcePackage.Files.Single(static file =>
                file.Path.EndsWith(".dsc", StringComparison.Ordinal));
            materials.Add(new SourceMaterialComponent(
                "apt-source",
                sourcePackage.Name,
                sourcePackage.Version,
                string.Equals(sourcePackage.Name, "wine", StringComparison.Ordinal)
                    ? "LGPL-2.1+"
                    : "NOASSERTION",
                WineRuntimePackageManifestLoader.ArchiveUri(
                    manifest,
                    sourcePackage.ArchiveSnapshotId,
                    descriptor.Path).AbsoluteUri,
                relative));
        }

        var expectedTotal = manifest.SourcePackages.Sum(static package =>
            package.Files.Sum(static file => file.SizeBytes));
        if (materialized.Count != WineRuntimePackageManifestLoader.RequiredSourcePackageCount ||
            fileCount != WineRuntimePackageManifestLoader.RequiredSourceFileCount ||
            totalBytes != expectedTotal || totalBytes > WineRuntimePackageManifestLoader.MaximumClosureSourceTotalBytes)
        {
            throw new BundleValidationException(
                "Operating-system source material did not produce the exact reviewed 162-source/526-file closure.");
        }
    }

    internal async Task WriteWineNoticeArchiveAsync(
        WineRuntimePackageManifestSnapshot manifestSnapshot,
        IReadOnlyList<InspectedImage> images,
        string staging,
        CancellationToken cancellationToken)
    {
        var manifest = manifestSnapshot.Manifest;
        var componentDigest = $"sha256:{WineManifestSha256(manifestSnapshot)}";
        var labelPrefix = ComponentLabelPrefix + manifest.Component.Id + ".";
        var sourceImage = images
            .Where(image =>
                image.Labels.TryGetValue(labelPrefix + "version", out var version) &&
                string.Equals(version, manifest.Component.ResolvedVersion, StringComparison.Ordinal) &&
                image.Labels.TryGetValue(labelPrefix + "digest", out var digest) &&
                string.Equals(digest, componentDigest, StringComparison.Ordinal) &&
                image.Labels.TryGetValue(labelPrefix + "source-uri", out var sourceUri) &&
                string.Equals(sourceUri, manifest.Component.SourceUri, StringComparison.Ordinal))
            .OrderBy(static image => image.Id, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new BundleValidationException(
                "No inspected final Wine image carries the exact userspace component identity required for notice extraction.");

        var destination = Path.Combine(
            staging,
            WineNoticeArchiveBundleRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var inspection = await docker.CopyImageFileAsync(
            sourceImage.ImageId,
            manifest.NoticeArchive.ImagePath,
            destination,
            WineRuntimePackageManifestLoader.MaximumNoticeArchiveBytes,
            cancellationToken);
        if (!string.Equals(
                inspection.Sha256,
                $"sha256:{manifest.NoticeArchive.Sha256}",
                StringComparison.Ordinal) ||
            inspection.Length != manifest.NoticeArchive.SizeBytes)
        {
            throw new BundleValidationException(
                "Wine notice archive copied from the final image does not match its manifest identity.");
        }

        await ValidateWineNoticeArchiveAsync(destination, manifest, cancellationToken);
    }

    internal static async Task ValidateWineNoticeArchiveAsync(
        string archivePath,
        WineRuntimePackageManifest manifest,
        CancellationToken cancellationToken)
    {
        var expected = new Dictionary<string, (string Sha256, long SizeBytes)>(StringComparer.Ordinal);
        foreach (var package in manifest.ResolvedPackages)
        {
            var path = package.CopyrightPath[1..];
            if (expected.TryGetValue(path, out var existing) &&
                (existing.SizeBytes != package.CopyrightSizeBytes ||
                 !string.Equals(existing.Sha256, package.CopyrightSha256, StringComparison.Ordinal)))
            {
                throw new BundleValidationException(
                    $"Wine notice '{package.CopyrightPath}' has conflicting package identities.");
            }
            expected[path] = (package.CopyrightSha256, package.CopyrightSizeBytes);
        }
        if (expected.Count != manifest.NoticeArchive.EntryCount)
            throw new BundleValidationException("Wine notice archive entry count does not match the package inventory.");

        var actual = new HashSet<string>(StringComparer.Ordinal);
        await using var stream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new TarReader(stream, leaveOpen: false);
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            if (entry is not UstarTarEntry ustar ||
                entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) ||
                entry.DataStream is null ||
                entry.ModificationTime != DateTimeOffset.UnixEpoch ||
                ustar.Uid != 0 || ustar.Gid != 0 ||
                !IsCanonicalBundlePath(entry.Name) ||
                !actual.Add(entry.Name) ||
                !expected.TryGetValue(entry.Name, out var identity) ||
                entry.Length != identity.SizeBytes)
            {
                throw new BundleValidationException(
                    "Wine notice archive must contain only the exact deterministic regular-file copyright closure.");
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long length = 0;
            while (true)
            {
                var read = await entry.DataStream.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                length = checked(length + read);
                if (length > identity.SizeBytes)
                    throw new BundleValidationException($"Wine notice '{entry.Name}' exceeds its reviewed size.");
                hash.AppendData(buffer.AsSpan(0, read));
            }
            var sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (length != identity.SizeBytes || !string.Equals(sha256, identity.Sha256, StringComparison.Ordinal))
                throw new BundleValidationException($"Wine notice '{entry.Name}' does not match its reviewed identity.");
        }

        if (!actual.SetEquals(expected.Keys))
            throw new BundleValidationException("Wine notice archive is missing reviewed copyright entries.");
    }

    private static async Task<ExternalSourceMaterialInspection> CopyExternalSourceMaterialAsync(
        Stream input,
        string destination,
        long expectedSize,
        long remainingTotalBytes,
        CancellationToken cancellationToken)
    {
        if (remainingTotalBytes < expectedSize)
            throw new BundleValidationException("Wine source material exceeds the offline bundle limit.");

        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024];
        long sizeBytes = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;

            sizeBytes = checked(sizeBytes + read);
            if (sizeBytes > expectedSize || sizeBytes > remainingTotalBytes)
                throw new BundleValidationException("Wine source material exceeds its reviewed size limit.");
            hash.AppendData(buffer.AsSpan(0, read));
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return new ExternalSourceMaterialInspection(
            Convert.ToHexStringLower(hash.GetHashAndReset()),
            sizeBytes);
    }

    private sealed record ExternalSourceMaterialInspection(string Sha256, long SizeBytes);

    private static async Task CopyDirectoryBoundedAsync(
        string source,
        string destination,
        long maximumBytes,
        int maximumFiles,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };
        long totalBytes = 0;
        var fileCount = 0;
        foreach (var sourcePath in Directory.EnumerateFiles(source, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(sourcePath);
            fileCount = checked(fileCount + 1);
            totalBytes = checked(totalBytes + info.Length);
            if (fileCount > maximumFiles || totalBytes > maximumBytes)
                throw new BundleValidationException("Weak-copyleft source material exceeds the offline bundle limit.");
            var relative = Path.GetRelativePath(source, sourcePath);
            if (relative.StartsWith("..", StringComparison.Ordinal))
                throw new BundleValidationException("Source material escaped its package directory.");
            var destinationPath = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var input = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    private static string SafeFileName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'
                ? character
                : '_');
        }
        return builder.Length > 0 ? builder.ToString() : "component";
    }

    private static bool IsCanonicalBundlePath(string value) =>
        value.Length is > 0 and <= 512 &&
        value.StartsWith("usr/share/doc/", StringComparison.Ordinal) &&
        value.EndsWith("/copyright", StringComparison.Ordinal) &&
        !value.Contains('\\') &&
        value.Split('/').All(static segment => segment.Length > 0 && segment is not "." and not ".." &&
            segment.All(static character => !char.IsControl(character)));

    private static ReleaseLockDocument CreateBundleLock(
        ReleaseLockDocument releaseLock,
        IReadOnlyList<InspectedImage> images)
    {
        var components = releaseLock.Components.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        foreach (var image in images)
        {
            var digest = SelectDigest(image);
            components.TryGetValue(image.LockComponentId, out var existing);
            components[image.LockComponentId] = existing is null
                ? new LockedComponent
                {
                    Kind = "image",
                    ResolvedVersion = releaseLock.ReleaseId,
                    Digest = digest,
                    ImageId = image.ImageId
                }
                : existing with { Digest = digest, ImageId = image.ImageId };
        }

        return releaseLock with { Components = components };
    }

    private static string SelectDigest(InspectedImage image)
    {
        var digest = image.RepoDigests
            .Select(static value => value[(value.LastIndexOf('@') + 1)..])
            .FirstOrDefault(static value => IsSha256(value));
        return digest ?? image.ImageId;
    }

    private static object CreateSpdx(
        string releaseId,
        ReleaseLockDocument releaseLock,
        IReadOnlyList<InspectedImage> images,
        IReadOnlyList<DependencyComponent> dependencies,
        WineRuntimePackageManifest wineManifest)
    {
        var packages = new List<object>();
        packages.AddRange(releaseLock.Components.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => (object)new
            {
                SPDXID = $"SPDXRef-Component-{SafeSpdxId(pair.Key)}",
                name = pair.Key,
                versionInfo = pair.Value.ResolvedVersion,
                downloadLocation = pair.Value.SourceUri ?? "NOASSERTION",
                filesAnalyzed = false,
                licenseConcluded = "NOASSERTION",
                licenseDeclared = "NOASSERTION",
                checksums = ComponentChecksums(pair.Value)
            }));
        packages.AddRange(dependencies.Select(dependency => (object)new
        {
            SPDXID = $"SPDXRef-{SafeSpdxId(dependency.PackageManager)}-{SafeSpdxId(dependency.Name)}-{SafeSpdxId(dependency.Version)}",
            name = dependency.Name,
            versionInfo = dependency.Version,
            downloadLocation = dependency.SourceUri ?? "NOASSERTION",
            filesAnalyzed = false,
            licenseConcluded = dependency.License,
            licenseDeclared = dependency.License,
            checksums = IntegrityChecksums(dependency.Integrity, spdx: true)
        }));
        packages.AddRange(wineManifest.ResolvedPackages.Select(package => (object)new
        {
            SPDXID = $"SPDXRef-OS-apt-{SafeSpdxId(package.Name)}-{SafeSpdxId(package.Version)}",
            name = package.Name,
            versionInfo = package.Version,
            downloadLocation = WineRuntimePackageManifestLoader.ArchiveUri(
                wineManifest,
                package.ArchiveSnapshotId,
                package.Path).AbsoluteUri,
            filesAnalyzed = false,
            licenseConcluded = "NOASSERTION",
            licenseDeclared = "NOASSERTION",
            checksums = (object[])[new { algorithm = "SHA256", checksumValue = package.Sha256 }],
            comment = $"Ubuntu {package.Architecture} binary; source {package.SourcePackage}@{package.SourceVersion}; " +
                $"snapshot {package.ArchiveSnapshotId}/{package.ArchiveSuite}; copyright {package.CopyrightPath} sha256:{package.CopyrightSha256}"
        }));
        packages.AddRange(images.Select(image => (object)new
        {
            SPDXID = $"SPDXRef-Image-{SafeSpdxId(image.Id)}",
            name = image.Id,
            versionInfo = releaseId,
            downloadLocation = "NOASSERTION",
            filesAnalyzed = false,
            licenseConcluded = "NOASSERTION",
            licenseDeclared = "NOASSERTION",
            checksums = (object[])[new { algorithm = "SHA256", checksumValue = image.ImageId[7..] }]
        }));
        return new
        {
            spdxVersion = "SPDX-2.3",
            dataLicense = "CC0-1.0",
            SPDXID = "SPDXRef-DOCUMENT",
            name = $"SharpLabNext-{releaseId}",
            documentNamespace = $"https://sharplabnext.dev/sbom/{Uri.EscapeDataString(releaseId)}",
            creationInfo = new
            {
                created = DateTimeOffset.UtcNow,
                creators = new[] { "Tool: SharpLabNext.BundleBuilder" }
            },
            packages
        };
    }

    private static object CreateCycloneDx(
        string releaseId,
        ReleaseLockDocument releaseLock,
        IReadOnlyList<InspectedImage> images,
        IReadOnlyList<DependencyComponent> dependencies,
        WineRuntimePackageManifest wineManifest)
    {
        var components = new List<object>();
        components.AddRange(releaseLock.Components.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => (object)new
            {
                type = pair.Value.Kind == "runtime" ? "framework" : "library",
                name = pair.Key,
                version = pair.Value.ResolvedVersion,
                purl = PackageUrl(pair.Value),
                hashes = CycloneHashes(pair.Value)
            }));
        components.AddRange(dependencies.Select(dependency => (object)new
        {
            type = "library",
            name = dependency.Name,
            version = dependency.Version,
            purl = DependencyPackageUrl(dependency),
            hashes = IntegrityChecksums(dependency.Integrity, spdx: false),
            licenses = new[] { new { expression = dependency.License } },
            properties = new[] { new { name = "sharplabnext:direct", value = dependency.Direct.ToString().ToLowerInvariant() } }
        }));
        components.AddRange(wineManifest.ResolvedPackages.Select(package => (object)new
        {
            type = "library",
            group = "ubuntu",
            name = package.Name,
            version = package.Version,
            purl = OsPackageUrl(package),
            hashes = (object[])[new { alg = "SHA-256", content = package.Sha256 }],
            properties = new[]
            {
                new { name = "sharplabnext:scope", value = "os-package" },
                new { name = "sharplabnext:archive-snapshot", value = package.ArchiveSnapshotId },
                new { name = "sharplabnext:archive-suite", value = package.ArchiveSuite },
                new { name = "sharplabnext:source-package", value = package.SourcePackage },
                new { name = "sharplabnext:source-version", value = package.SourceVersion },
                new { name = "sharplabnext:copyright-path", value = package.CopyrightPath },
                new { name = "sharplabnext:copyright-sha256", value = package.CopyrightSha256 }
            }
        }));
        components.AddRange(images.Select(image => (object)new
        {
            type = "container",
            name = image.Id,
            version = releaseId,
            purl = (string?)null,
            hashes = (object[])[new { alg = "SHA-256", content = image.ImageId[7..] }]
        }));
        return new
        {
            bomFormat = "CycloneDX",
            specVersion = "1.6",
            serialNumber = $"urn:uuid:{Guid.NewGuid()}",
            version = 1,
            metadata = new
            {
                timestamp = DateTimeOffset.UtcNow,
                component = new { type = "application", name = "SharpLabNext", version = releaseId },
                tools = new[] { new { vendor = "SharpLabNext", name = "BundleBuilder", version = "1" } }
            },
            components
        };
    }

    private static object CreateProvenance(
        BundleBuilderCommand command,
        CatalogDocument catalog,
        ReleaseLockDocument releaseLock,
        IReadOnlyList<InspectedImage> images,
        IReadOnlyList<DependencyComponent> dependencies,
        WineRuntimePackageManifestSnapshot wineManifestSnapshot,
        BaseImageManifest baseImages,
        RuntimeMatrixBaseImageBindings runtimeMatrixBaseImages,
        IReadOnlyList<MaintainedProvenanceInput> maintainedProvenance,
        RepositorySourceProvenance source)
    {
        var resolvedDependencies = new List<object>();
        resolvedDependencies.AddRange(images.Select(image => (object)new
        {
            uri = $"pkg:docker/{Uri.EscapeDataString(image.Id)}",
            digest = new Dictionary<string, string> { ["sha256"] = image.ImageId[7..] }
        }));
        foreach (var operatorImage in images
                     .Select(static image => image.Labels.TryGetValue(
                         "io.sharplabnext.framework.row-operator-image",
                         out var reference)
                             ? reference
                             : null)
                     .OfType<string>()
                     .Distinct(StringComparer.Ordinal))
        {
            var operatorDigest = BaseImageDigest(operatorImage);
            var separator = operatorImage.LastIndexOf('@');
            resolvedDependencies.Add(new
            {
                uri = $"pkg:docker/{Uri.EscapeDataString(operatorImage[..separator])}",
                digest = new Dictionary<string, string>
                {
                    ["sha256"] = operatorDigest
                }
            });
        }
        resolvedDependencies.AddRange(dependencies.Select(dependency => (object)new
        {
            uri = $"pkg:{dependency.PackageManager}/{Uri.EscapeDataString(dependency.Name)}@{Uri.EscapeDataString(dependency.Version)}",
            digest = IntegrityDigest(dependency.Integrity)
        }));
        resolvedDependencies.AddRange(wineManifestSnapshot.Manifest.ResolvedPackages.Select(package => (object)new
        {
            uri = OsPackageUrl(package),
            downloadLocation = WineRuntimePackageManifestLoader.ArchiveUri(
                wineManifestSnapshot.Manifest,
                package.ArchiveSnapshotId,
                package.Path).AbsoluteUri,
            digest = new Dictionary<string, string> { ["sha256"] = package.Sha256 },
            sourcePackage = package.SourcePackage,
            sourceVersion = package.SourceVersion
        }));
        resolvedDependencies.AddRange(wineManifestSnapshot.Manifest.SourcePackages.SelectMany(sourcePackage =>
            sourcePackage.Files.Select(file => (object)new
            {
                uri = WineRuntimePackageManifestLoader.ArchiveUri(
                    wineManifestSnapshot.Manifest,
                    sourcePackage.ArchiveSnapshotId,
                    file.Path).AbsoluteUri,
                digest = new Dictionary<string, string> { ["sha256"] = file.Sha256 },
                sourcePackage = sourcePackage.Name,
                sourceVersion = sourcePackage.Version
            })));
        resolvedDependencies.Add(new
        {
            uri = $"https://github.com/sharplabnext/SharpLabNext/blob/{source.Revision}/" +
                WineRuntimePackageManifestLoader.ManifestRelativePath,
            digest = new Dictionary<string, string>
            {
                ["sha256"] = WineManifestSha256(wineManifestSnapshot)
            }
        });
        var resolvedBaseImageReferences = baseImages.Images
            .Select(static image => image.Reference)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var image in images)
        {
            if (image.RuntimeId is not null &&
                runtimeMatrixBaseImages.LinuxRuntimeBaseImages.TryGetValue(image.RuntimeId, out var reference))
            {
                resolvedBaseImageReferences.Add(reference);
            }
        }
        resolvedDependencies.AddRange(resolvedBaseImageReferences.Order(StringComparer.Ordinal).Select(reference => (object)new
        {
            uri = $"pkg:docker/{Uri.EscapeDataString(reference[..reference.LastIndexOf('@')])}",
            digest = new Dictionary<string, string> { ["sha256"] = BaseImageDigest(reference) }
        }));
        resolvedDependencies.AddRange(maintainedProvenance
            .SelectMany(static provenance => provenance.ReferencedComponentIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(componentId =>
            {
                var component = releaseLock.Components[componentId];
                return (object)new
                {
                    name = componentId,
                    uri = component.SourceUri ?? PackageUrl(component) ?? $"urn:sharplabnext:component:{componentId}",
                    digest = LockedComponentDigest(component)
                };
            }));
        var maintainedParameters = maintainedProvenance.Select(provenance => new
        {
            manifest = provenance.RelativePath,
            provenance.ComponentId,
            provenance.SourceComponentId,
            provenance.License,
            provenance.BuilderImageId,
            provenance.PatchSeriesDigest,
            patches = provenance.PatchPaths,
            component = LockIdentity(provenance.ComponentId, releaseLock.Components[provenance.ComponentId]),
            source = LockIdentity(provenance.SourceComponentId, releaseLock.Components[provenance.SourceComponentId]),
            relatedComponents = provenance.ReferencedComponentIds
                .Where(id =>
                    !string.Equals(id, provenance.ComponentId, StringComparison.Ordinal) &&
                    !string.Equals(id, provenance.SourceComponentId, StringComparison.Ordinal))
                .Select(id => LockIdentity(id, releaseLock.Components[id]))
                .ToArray()
        }).ToArray();
        return new
        {
            _type = "https://in-toto.io/Statement/v1",
            subject = images.Select(image => new
            {
                name = image.Id,
                digest = new Dictionary<string, string> { ["sha256"] = image.ImageId[7..] }
            }).ToArray(),
            predicateType = "https://slsa.dev/provenance/v1",
            predicate = new
            {
                buildDefinition = new
                {
                    buildType = "https://sharplabnext.dev/build-types/offline-bundle/v1",
                    externalParameters = new
                    {
                        releaseId = catalog.ReleaseId,
                        metadataOnly = command.MetadataOnly,
                        sourceRevision = source.Revision,
                        sourceHeadRevision = source.HeadRevision,
                        sourceDirty = source.IsDirty,
                        sourceVerified = source.IsVerified,
                        developmentSourceOverride = source.DevelopmentOverrideUsed,
                        developmentImageInputs = images.Any(static image =>
                            image.Labels.TryGetValue(DevelopmentImageInputsLabel, out var value) &&
                            StringComparer.Ordinal.Equals(value, "true")),
                        deploymentManifest = Path.GetRelativePath(command.RepositoryRoot, command.DeploymentImagesPath).Replace('\\', '/'),
                        baseImageManifest = "profiles/base-images.json",
                        maintainedProvenance = maintainedParameters
                    },
                    resolvedDependencies
                },
                runDetails = new
                {
                    builder = new { id = "https://sharplabnext.dev/builders/bundle-builder/v1" },
                    metadata = new { invocationId = Guid.NewGuid(), startedOn = DateTimeOffset.UtcNow }
                }
            }
        };
    }

    private static object LockIdentity(string componentId, LockedComponent component) => new
    {
        componentId,
        component.Kind,
        component.ResolvedVersion,
        component.Commit,
        component.JitCommit,
        component.Digest,
        component.SourceUri,
        component.Package,
        component.PackageContentHash,
        component.Sha512
    };

    private static Dictionary<string, string> LockedComponentDigest(LockedComponent component)
    {
        if (component.Digest is { } digest && IsSha256(digest))
            return new Dictionary<string, string> { ["sha256"] = digest[7..] };
        if (component.Sha512 is { Length: 128 } sha512)
            return new Dictionary<string, string> { ["sha512"] = sha512 };
        return IntegrityDigest(component.PackageContentHash);
    }

    private static object[] IntegrityChecksums(string? integrity, bool spdx)
    {
        var digest = IntegrityDigest(integrity);
        if (digest.Count != 1)
        {
            return [];
        }

        var pair = digest.Single();
        if (spdx)
        {
            return [new { algorithm = pair.Key.ToUpperInvariant(), checksumValue = pair.Value }];
        }

        var algorithm = pair.Key.ToUpperInvariant() switch
        {
            "SHA1" => "SHA-1",
            "SHA256" => "SHA-256",
            "SHA512" => "SHA-512",
            var value => value
        };
        return [new { alg = algorithm, content = pair.Value }];
    }

    private static Dictionary<string, string> IntegrityDigest(string? integrity)
    {
        if (string.IsNullOrWhiteSpace(integrity))
        {
            return new Dictionary<string, string>();
        }

        var separator = integrity.IndexOf('-');
        if (separator <= 0 || separator == integrity.Length - 1)
        {
            return new Dictionary<string, string>();
        }

        var algorithm = integrity[..separator].ToLowerInvariant();
        try
        {
            var bytes = Convert.FromBase64String(integrity[(separator + 1)..]);
            return new Dictionary<string, string> { [algorithm] = Convert.ToHexStringLower(bytes) };
        }
        catch (FormatException)
        {
            return new Dictionary<string, string>();
        }
    }

    private static string WineManifestSha256(WineRuntimePackageManifestSnapshot snapshot)
    {
        var actual = Convert.ToHexStringLower(SHA256.HashData(snapshot.ManifestBytes.Span));
        if (!string.Equals(snapshot.ManifestSha256, $"sha256:{actual}", StringComparison.Ordinal))
        {
            throw new BundleValidationException(
                "Wine package inventory bytes changed after their release identity was validated.");
        }
        return actual;
    }

    private static object[] ComponentChecksums(LockedComponent component)
    {
        if (component.Sha512 is not null)
        {
            return [new { algorithm = "SHA512", checksumValue = component.Sha512 }];
        }

        if (component.ImageId is not null && IsSha256(component.ImageId))
        {
            return [new { algorithm = "SHA256", checksumValue = component.ImageId[7..] }];
        }

        return [];
    }

    private static object[] CycloneHashes(LockedComponent component)
    {
        if (component.Sha512 is not null)
        {
            return [new { alg = "SHA-512", content = component.Sha512 }];
        }

        if (component.ImageId is not null && IsSha256(component.ImageId))
        {
            return [new { alg = "SHA-256", content = component.ImageId[7..] }];
        }

        return [];
    }

    private static string? PackageUrl(LockedComponent component) => component.Package is null
        ? null
        : $"pkg:nuget/{Uri.EscapeDataString(component.Package)}@{Uri.EscapeDataString(component.ResolvedVersion)}";

    private static string DependencyPackageUrl(DependencyComponent component)
    {
        var name = component.PackageManager == "github"
            ? string.Join('/', component.Name.Split('/').Select(Uri.EscapeDataString))
            : Uri.EscapeDataString(component.Name);
        return $"pkg:{component.PackageManager}/{name}@{Uri.EscapeDataString(component.Version)}";
    }

    private static string OsPackageUrl(WineResolvedPackage package) =>
        $"pkg:deb/ubuntu/{Uri.EscapeDataString(package.Name)}@{Uri.EscapeDataString(package.Version)}" +
        $"?arch={Uri.EscapeDataString(package.Architecture)}&distro=ubuntu-24.04";

    private static string SafeSpdxId(string value) =>
        new(value.Select(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-'
            ? character
            : '-').ToArray());

    private static string RuntimeIdentity(InspectedImage image, string label) =>
        image.Labels.TryGetValue(label, out var commit) && IsCommit(commit)
            ? commit
            : throw new BundleValidationException(
                $"Runtime image '{image.Id}' does not carry a valid '{label}' identity.");

    private static void AppendRuntimeProfile(
        StringBuilder builder,
        int index,
        RuntimeProfileDefinition profile)
    {
        var prefix = $"RuntimeSupervisorProfileOverlay__Profiles__{index}";
        AppendConfiguration(builder, prefix + "__SchemaVersion", profile.SchemaVersion);
        AppendConfiguration(builder, prefix + "__Id", profile.Id);
        AppendConfiguration(builder, prefix + "__Image", profile.Image);
        AppendConfiguration(builder, prefix + "__Family", profile.Family);
        AppendList(builder, prefix + "__AcceptedRuntimeFamilies", profile.AcceptedRuntimeFamilies);
        for (var frameworkIndex = 0; frameworkIndex < profile.AcceptedFrameworks.Count; frameworkIndex++)
        {
            var framework = profile.AcceptedFrameworks[frameworkIndex];
            var frameworkPrefix = $"{prefix}__AcceptedFrameworks__{frameworkIndex}";
            AppendConfiguration(builder, frameworkPrefix + "__Name", framework.Name);
            AppendConfiguration(builder, frameworkPrefix + "__MinimumVersion", framework.MinimumVersion);
            AppendConfiguration(builder, frameworkPrefix + "__MaximumVersion", framework.MaximumVersion);
            AppendConfiguration(builder, frameworkPrefix + "__ExactVersion", framework.ExactVersion);
        }
        AppendConfiguration(builder, prefix + "__RuntimeVersion", profile.RuntimeVersion);
        AppendConfiguration(builder, prefix + "__RuntimeCommit", profile.RuntimeCommit);
        AppendConfiguration(builder, prefix + "__JitVersion", profile.JitVersion);
        AppendConfiguration(builder, prefix + "__JitCommit", profile.JitCommit);
        AppendConfiguration(builder, prefix + "__RuntimeImageId", profile.RuntimeImageId);
        AppendConfiguration(builder, prefix + "__Rid", profile.Rid);
        AppendConfiguration(builder, prefix + "__Architecture", profile.Architecture);
        AppendConfiguration(builder, prefix + "__CpuFeatureProfile", profile.CpuFeatureProfile);
        AppendList(builder, prefix + "__AcceptedArtifactFormats", profile.AcceptedArtifactFormats);
        AppendList(builder, prefix + "__Capabilities", profile.Capabilities);
        AppendList(builder, prefix + "__ProvidedRuntimeFeatureTags", profile.ProvidedRuntimeFeatureTags);
        AppendList(builder, prefix + "__ProvidedMetadataFeatureTags", profile.ProvidedMetadataFeatureTags);
        AppendList(builder, prefix + "__AllowedSecurityPolicyIds", profile.AllowedSecurityPolicyIds);
        AppendConfiguration(builder, prefix + "__Container__IsolationKind", profile.Container.IsolationKind);
        AppendConfiguration(builder, prefix + "__Container__EnvironmentKind", profile.Container.EnvironmentKind);
        AppendConfiguration(builder, prefix + "__Container__ExecutionUser", profile.Container.ExecutionUser);
        AppendConfiguration(builder, prefix + "__Container__WinePrefixPath", profile.Container.WinePrefixPath);
        AppendConfiguration(builder, prefix + "__Layout__DotNetHostPath", profile.Layout.DotNetHostPath);
        AppendConfiguration(builder, prefix + "__Layout__RunnerKind", profile.Layout.RunnerKind);
        AppendConfiguration(builder, prefix + "__Layout__RunnerAssemblyPath", profile.Layout.RunnerAssemblyPath);
        AppendConfiguration(
            builder,
            prefix + "__Layout__JitInspectorAssemblyPath",
            profile.Layout.JitInspectorAssemblyPath);
        AppendConfiguration(builder, prefix + "__Layout__WineHostPath", profile.Layout.WineHostPath);
        AppendConfiguration(builder, prefix + "__Layout__WinePrefixPath", profile.Layout.WinePrefixPath);
        if (profile.Operations?.Run is { } run)
            AppendRuntimeOperation(builder, prefix + "__Operations__Run", run);
        if (profile.Operations?.Jit is { } jit)
        {
            AppendRuntimeOperation(builder, prefix + "__Operations__Jit", jit);
            AppendConfiguration(builder, prefix + "__Operations__Jit__SourceMappingKind", jit.SourceMappingKind);
            AppendConfiguration(builder, prefix + "__Operations__Jit__ProfilerPath", jit.ProfilerPath);
        }
        for (var policyIndex = 0; policyIndex < profile.SecurityPolicies.Count; policyIndex++)
        {
            AppendSecurityPolicy(
                builder,
                $"{prefix}__SecurityPolicies__{policyIndex}",
                profile.SecurityPolicies[policyIndex]);
        }
    }

    private static void AppendRuntimeOperation(
        StringBuilder builder,
        string prefix,
        RuntimeOperationDefinition operation)
    {
        AppendConfiguration(builder, prefix + "__ImplementationId", operation.ImplementationId);
        AppendConfiguration(builder, prefix + "__PathStyle", operation.PathStyle);
        AppendConfiguration(builder, prefix + "__Command__Executable", operation.Command.Executable);
        AppendList(builder, prefix + "__Command__Argv", operation.Command.Argv);
    }

    private static void AppendSecurityPolicy(
        StringBuilder builder,
        string prefix,
        RuntimeSecurityPolicyDefinition policy)
    {
        AppendConfiguration(builder, prefix + "__Id", policy.Id);
        AppendConfiguration(builder, prefix + "__MemoryBytes", policy.MemoryBytes);
        AppendConfiguration(builder, prefix + "__NanoCpus", policy.NanoCpus);
        AppendConfiguration(builder, prefix + "__PidsLimit", policy.PidsLimit);
        AppendConfiguration(builder, prefix + "__MaximumDurationSeconds", policy.MaximumDurationSeconds);
        AppendConfiguration(builder, prefix + "__MaximumArtifactBytes", policy.MaximumArtifactBytes);
        AppendConfiguration(builder, prefix + "__MaximumOutputBytes", policy.MaximumOutputBytes);
        AppendConfiguration(builder, prefix + "__TmpfsBytes", policy.TmpfsBytes);
    }

    private static void AppendConfiguration(StringBuilder builder, string key, long value) =>
        AppendConfiguration(builder, key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static void AppendConfiguration(StringBuilder builder, string key, string? value)
    {
        if (value is null)
            return;
        builder.Append("      ")
            .Append(key)
            .Append(": \"")
            .Append(EscapeYaml(value))
            .AppendLine("\"");
    }

    private static void AppendList(
        StringBuilder builder,
        string prefix,
        List<string> values)
    {
        for (var valueIndex = 0; valueIndex < values.Count; valueIndex++)
        {
            AppendConfiguration(builder, $"{prefix}__{valueIndex}", values[valueIndex]);
        }
    }

    private static string EscapeYaml(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", string.Empty, StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    public static async Task<IReadOnlyList<RuntimeProfileDefinition>> LoadRuntimeProfilesAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        EnsureInputDirectory(directory);
        var profiles = new List<RuntimeProfileDefinition>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(static path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    16 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var document = await JsonDocument.ParseAsync(
                    stream,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow
                    },
                    cancellationToken);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new BundleValidationException(
                        $"Active runtime profile '{Path.GetFileName(path)}' must be a JSON object.");
                }
                foreach (var property in RequiredRuntimeProfileProperties)
                {
                    if (!document.RootElement.TryGetProperty(property, out _))
                    {
                        throw new BundleValidationException(
                            $"Active runtime profile '{Path.GetFileName(path)}' is incomplete: missing '{property}'.");
                    }
                }

                var profile = document.RootElement.Deserialize<RuntimeProfileDefinition>(RuntimeProfileJsonOptions)
                    ?? throw new BundleValidationException(
                        $"Active runtime profile '{Path.GetFileName(path)}' is empty.");
                var failures = RuntimeProfileValidation.ValidatePackage(
                    profile,
                    requireDigestPinnedImage: false).ToList();
                if (profile.Operations is null)
                {
                    failures.Add(
                        $"Active runtime profile '{profile.Id}' must use explicit operation definitions.");
                }
                if (profile.SecurityPolicies.Count == 0)
                {
                    failures.Add(
                        $"Active runtime profile '{profile.Id}' must include its security policy definitions.");
                }
                if (failures.Count > 0)
                {
                    throw new BundleValidationException(
                        $"Active runtime profile '{Path.GetFileName(path)}' is invalid: " +
                        string.Join(" ", failures));
                }
                profiles.Add(profile);
            }
            catch (JsonException exception)
            {
                throw new BundleValidationException(
                    $"Active runtime profile '{Path.GetFileName(path)}' is invalid JSON: {exception.Message}");
            }
        }

        _ = IndexRuntimeProfiles(profiles);
        return profiles;
    }

    internal static void ValidateRuntimeProfileBindings(
        CatalogDocument catalog,
        ReleaseLockDocument releaseLock,
        IReadOnlyList<RuntimeProfileDefinition> profiles)
    {
        var profileIndex = IndexRuntimeProfiles(profiles);
        var selectableRuntimes = catalog.Runtimes
            .Where(static runtime => runtime.Availability.IsSelectable)
            .ToDictionary(static runtime => runtime.Id, StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            if (!selectableRuntimes.ContainsKey(profile.Id))
            {
                throw new BundleValidationException(
                    $"Active runtime profile '{profile.Id}' does not map to a selectable Catalog runtime.");
            }
        }

        foreach (var runtime in selectableRuntimes.Values)
        {
            if (!profileIndex.TryGetValue(runtime.Id, out var profile))
            {
                throw new BundleValidationException(
                    $"Selectable runtime '{runtime.Id}' has no active profile in profiles/runtimes.");
            }
            if (!releaseLock.Components.TryGetValue(runtime.Id, out var component) ||
                !StringComparer.Ordinal.Equals(component.Kind, "runtime"))
            {
                throw new BundleValidationException(
                    $"Selectable runtime '{runtime.Id}' has no matching runtime component in the release lock.");
            }

            RequireRuntimeProfileValue(runtime.Id, "family", runtime.Family, profile.Family);
            RequireRuntimeProfileValue(runtime.Id, "runtime version", runtime.ResolvedVersion, profile.RuntimeVersion);
            RequireRuntimeProfileValue(runtime.Id, "locked runtime version", component.ResolvedVersion, profile.RuntimeVersion);
            RequireRuntimeProfileValue(runtime.Id, "RID", runtime.Rid, profile.Rid);
            RequireRuntimeProfileValue(runtime.Id, "architecture", runtime.Architecture, profile.Architecture);
            RequireRuntimeProfileValues(
                runtime.Id,
                "accepted artifact formats",
                runtime.AcceptedArtifactFormats,
                profile.AcceptedArtifactFormats);
            RequireRuntimeProfileValues(
                runtime.Id,
                "capabilities",
                runtime.Capabilities,
                profile.Capabilities);
            RequireRuntimeProfileValues(
                runtime.Id,
                "accepted runtime families",
                runtime.AcceptedRuntimeFamilies,
                profile.AcceptedRuntimeFamilies);
            RequireRuntimeProfileValues(
                runtime.Id,
                "runtime feature tags",
                runtime.ProvidedRuntimeFeatureTags,
                profile.ProvidedRuntimeFeatureTags);
            RequireRuntimeProfileValues(
                runtime.Id,
                "metadata feature tags",
                runtime.ProvidedMetadataFeatureTags,
                profile.ProvidedMetadataFeatureTags);
            if (runtime.AcceptedFrameworks.Count > 0)
                RequireRuntimeProfileFrameworks(runtime, profile);
            if (runtime.ContainerIsolationKind is { } isolationKind)
                RequireRuntimeProfileValue(runtime.Id, "container isolation kind", isolationKind, profile.Container.IsolationKind);
            if (runtime.ContainerEnvironmentKind is { } environmentKind)
                RequireRuntimeProfileValue(runtime.Id, "container environment kind", environmentKind, profile.Container.EnvironmentKind);
            if (runtime.JitSourceMappingKind is { } sourceMappingKind)
            {
                RequireRuntimeProfileValue(
                    runtime.Id,
                    "JIT source mapping kind",
                    sourceMappingKind,
                    profile.Operations?.Jit?.SourceMappingKind);
            }

            if (RequiresCommitIdentity(runtime))
            {
                RequireRuntimeProfileValue(
                    runtime.Id,
                    "runtime commit",
                    component.Commit,
                    profile.RuntimeCommit,
                    StringComparison.OrdinalIgnoreCase);
                RequireRuntimeProfileValue(
                    runtime.Id,
                    "JIT version",
                    runtime.JitVersion ?? component.ResolvedVersion,
                    profile.JitVersion);
                RequireRuntimeProfileValue(
                    runtime.Id,
                    "JIT commit",
                    component.JitCommit,
                    profile.JitCommit,
                    StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                RequireRuntimeProfileValue(runtime.Id, "runtime commit", "not-applicable", profile.RuntimeCommit);
                RequireRuntimeProfileValue(runtime.Id, "JIT version", "not-applicable", profile.JitVersion);
                RequireRuntimeProfileValue(runtime.Id, "JIT commit", "not-applicable", profile.JitCommit);
            }
            if (runtime.RuntimeCommit is { } catalogRuntimeCommit)
            {
                RequireRuntimeProfileValue(
                    runtime.Id,
                    "Catalog runtime commit",
                    catalogRuntimeCommit,
                    profile.RuntimeCommit,
                    StringComparison.OrdinalIgnoreCase);
            }
            if (runtime.JitVersion is { } catalogJitVersion)
                RequireRuntimeProfileValue(runtime.Id, "Catalog JIT version", catalogJitVersion, profile.JitVersion);
            if (runtime.JitCommit is { } catalogJitCommit)
            {
                RequireRuntimeProfileValue(
                    runtime.Id,
                    "Catalog JIT commit",
                    catalogJitCommit,
                    profile.JitCommit,
                    StringComparison.OrdinalIgnoreCase);
            }
            if (runtime.RuntimeImageId is { } catalogImageId)
                RequireRuntimeProfileValue(runtime.Id, "Catalog runtime image ID", catalogImageId, profile.RuntimeImageId);
        }
    }

    private static List<RuntimeProfileDefinition> MaterializeRuntimeProfiles(
        CatalogDocument catalog,
        ReleaseLockDocument releaseLock,
        IReadOnlyList<InspectedImage> images,
        IReadOnlyList<RuntimeProfileDefinition> profiles)
    {
        var profileIndex = IndexRuntimeProfiles(profiles);
        var runtimeImages = images
            .Where(static image => image.RuntimeId is not null)
            .ToDictionary(static image => image.RuntimeId!, StringComparer.Ordinal);
        var result = new List<RuntimeProfileDefinition>();
        foreach (var runtime in catalog.Runtimes.Where(static runtime => runtime.Availability.IsSelectable))
        {
            var source = profileIndex[runtime.Id];
            var profile = JsonSerializer.SerializeToElement(source, RuntimeProfileJsonOptions)
                .Deserialize<RuntimeProfileDefinition>(RuntimeProfileJsonOptions)
                ?? throw new BundleValidationException(
                    $"Active runtime profile '{runtime.Id}' could not be materialized.");
            if (!runtimeImages.TryGetValue(runtime.Id, out var image))
            {
                throw new BundleValidationException(
                    $"Selectable runtime '{runtime.Id}' has no inspected deployment image.");
            }
            var component = releaseLock.Components[runtime.Id];
            profile.Image = image.ImageId;
            profile.RuntimeImageId = image.ImageId;
            // The receipt and registry reference bind source promotion. The release
            // overlay runs only the image ID captured after that trust check.
            profile.PromotionReceipt = null;
            profile.RuntimeVersion = component.ResolvedVersion;
            if (RequiresCommitIdentity(runtime))
            {
                profile.RuntimeCommit = RuntimeIdentity(image, RuntimeCommitLabel);
                profile.JitVersion = runtime.JitVersion ?? component.ResolvedVersion;
                profile.JitCommit = RuntimeIdentity(image, JitCommitLabel);
            }
            else
            {
                profile.RuntimeCommit = "not-applicable";
                profile.JitVersion = "not-applicable";
                profile.JitCommit = "not-applicable";
            }

            var failures = RuntimeProfileValidation.ValidatePackage(
                profile,
                requireDigestPinnedImage: true);
            if (failures.Count > 0)
            {
                throw new BundleValidationException(
                    $"Materialized runtime profile '{runtime.Id}' is invalid: {string.Join(" ", failures)}");
            }
            result.Add(profile);
        }
        return result;
    }

    private static Dictionary<string, RuntimeProfileDefinition> IndexRuntimeProfiles(
        IReadOnlyList<RuntimeProfileDefinition> profiles)
    {
        var result = new Dictionary<string, RuntimeProfileDefinition>(StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            if (!result.TryAdd(profile.Id, profile))
            {
                throw new BundleValidationException(
                    $"Active runtime profile '{profile.Id}' is declared more than once.");
            }
        }
        return result;
    }

    private static RuntimeSecurityPolicyDefinition[] MergeRuntimeSecurityPolicies(
        IReadOnlyList<RuntimeProfileDefinition> profiles)
    {
        var policies = new Dictionary<string, RuntimeSecurityPolicyDefinition>(StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            foreach (var policy in profile.SecurityPolicies)
            {
                if (policies.TryGetValue(policy.Id, out var existing))
                {
                    if (!RuntimeSecurityPoliciesEqual(existing, policy))
                    {
                        throw new BundleValidationException(
                            $"Runtime security policy '{policy.Id}' has conflicting active profile definitions.");
                    }
                    continue;
                }
                policies.Add(policy.Id, policy);
            }
        }
        return policies.Values.OrderBy(static policy => policy.Id, StringComparer.Ordinal).ToArray();
    }

    private static bool RuntimeSecurityPoliciesEqual(
        RuntimeSecurityPolicyDefinition left,
        RuntimeSecurityPolicyDefinition right) =>
        StringComparer.Ordinal.Equals(left.Id, right.Id) &&
        left.MemoryBytes == right.MemoryBytes &&
        left.NanoCpus == right.NanoCpus &&
        left.PidsLimit == right.PidsLimit &&
        left.MaximumDurationSeconds == right.MaximumDurationSeconds &&
        left.MaximumArtifactBytes == right.MaximumArtifactBytes &&
        left.MaximumOutputBytes == right.MaximumOutputBytes &&
        left.TmpfsBytes == right.TmpfsBytes;

    private static void RequireRuntimeProfileFrameworks(
        RuntimeManifest runtime,
        RuntimeProfileDefinition profile)
    {
        if (runtime.AcceptedFrameworks.Count != profile.AcceptedFrameworks.Count)
        {
            throw new BundleValidationException(
                $"Active runtime profile '{runtime.Id}' accepted frameworks do not match the Catalog.");
        }
        foreach (var expected in runtime.AcceptedFrameworks)
        {
            var actual = profile.AcceptedFrameworks.SingleOrDefault(framework =>
                StringComparer.Ordinal.Equals(framework.Name, expected.Name));
            if (actual is null ||
                !StringComparer.Ordinal.Equals(actual.MinimumVersion, expected.MinimumVersion) ||
                !StringComparer.Ordinal.Equals(actual.MaximumVersion, expected.MaximumVersion) ||
                !StringComparer.Ordinal.Equals(actual.ExactVersion, expected.ExactVersion))
            {
                throw new BundleValidationException(
                    $"Active runtime profile '{runtime.Id}' accepted framework '{expected.Name}' does not match the Catalog.");
            }
        }
    }

    private static void RequireRuntimeProfileValues(
        string runtimeId,
        string field,
        IReadOnlyList<string> expected,
        List<string> actual)
    {
        if (expected.Count == actual.Count && expected.ToHashSet(StringComparer.Ordinal).SetEquals(actual))
            return;
        throw new BundleValidationException(
            $"Active runtime profile '{runtimeId}' {field} do not match the Catalog.");
    }

    private static void RequireRuntimeProfileValue(
        string runtimeId,
        string field,
        string? expected,
        string? actual,
        StringComparison comparison = StringComparison.Ordinal)
    {
        if (expected is not null && string.Equals(expected, actual, comparison))
            return;
        throw new BundleValidationException(
            $"Active runtime profile '{runtimeId}' {field} is '{actual}', but requires '{expected}'.");
    }

    private static async Task<DeploymentImageManifest> LoadDeploymentImagesAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync<DeploymentImageManifest>(
            stream,
            JsonOptions,
            cancellationToken);
        return manifest ?? throw new BundleValidationException("Deployment image manifest is empty.");
    }

    private static async Task<BaseImageManifest> LoadBaseImagesAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync<BaseImageManifest>(
            stream,
            JsonOptions,
            cancellationToken);
        return manifest ?? throw new BundleValidationException("Base image manifest is empty.");
    }

    private static void ValidateBaseImages(BaseImageManifest manifest)
    {
        if (manifest.SchemaVersion != 1 || manifest.Images.Count == 0)
            throw new BundleValidationException("Unsupported or empty base image manifest.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var variables = new HashSet<string>(StringComparer.Ordinal);
        foreach (var image in manifest.Images)
        {
            if (!ids.Add(image.Id) || !variables.Add(image.BakeVariable))
                throw new BundleValidationException("Base image IDs and Bake variables must be unique.");
            _ = BaseImageDigest(image.Reference);
        }
    }

    internal static string BaseImageDigest(string reference)
    {
        var separator = reference.LastIndexOf("@sha256:", StringComparison.Ordinal);
        if (separator <= 0 || separator + 72 != reference.Length)
            throw new BundleValidationException($"Base image reference '{reference}' is not pinned by SHA-256 digest.");
        var digest = reference[(separator + 8)..];
        if (digest.Length != 64 || digest.Any(static character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new BundleValidationException($"Base image reference '{reference}' has an invalid SHA-256 digest.");
        }
        return digest;
    }

    private static IReadOnlyDictionary<string, string> ValidateInputs(
        CatalogDocument catalog,
        ReleaseLockDocument releaseLock,
        DeploymentImageManifest deployment,
        IReadOnlyDictionary<string, string> imageOverrides)
    {
        if (!string.Equals(catalog.ReleaseId, releaseLock.ReleaseId, StringComparison.Ordinal))
        {
            throw new BundleValidationException("Catalog and release lock IDs do not match.");
        }
        IReadOnlyDictionary<string, string> expectedReferenceSetDigests;
        try
        {
            expectedReferenceSetDigests = ReferenceSetIdentityResolver.ResolveExpectedDigests(catalog, releaseLock);
        }
        catch (CatalogValidationException exception)
        {
            throw new BundleValidationException(exception.Message);
        }

        if (deployment.SchemaVersion != 1 || deployment.Images.Count == 0)
        {
            throw new BundleValidationException("Unsupported or empty deployment image manifest.");
        }

        if (catalog.ReleaseId.Length is 0 or > 128 ||
            !char.IsAsciiLetterOrDigit(catalog.ReleaseId[0]) ||
            catalog.ReleaseId.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
        {
            throw new BundleValidationException("Release ID cannot be used as a Docker tag.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var toolchains = catalog.Toolchains.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        var runtimes = catalog.Runtimes.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        var processors = catalog.ArtifactProcessors.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        var toolchainImagesByWorkerId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var image in deployment.Images)
        {
            if (!ids.Add(image.Id))
            {
                throw new BundleValidationException($"Duplicate deployment image ID '{image.Id}'.");
            }

            var selectors = new[] { image.ToolchainId, image.RuntimeId, image.ArtifactProcessorId }
                .Count(static value => value is not null);
            if (!image.Always && selectors != 1)
            {
                throw new BundleValidationException(
                    $"Deployment image '{image.Id}' must be always-on or select exactly one catalog component.");
            }

            if (selectors > 1)
            {
                throw new BundleValidationException($"Deployment image '{image.Id}' has conflicting selectors.");
            }

            if (image.ComposeService is not null &&
                (image.ToolchainId is not null || image.ArtifactProcessorId is not null) &&
                (image.ReleaseIdEnvironment is null || image.ImageIdEnvironment is null))
            {
                throw new BundleValidationException(
                    $"Worker image '{image.Id}' must declare release and immutable image identity environment keys.");
            }

            if (image.ToolchainId is not null && !toolchains.ContainsKey(image.ToolchainId) ||
                image.RuntimeId is not null && !runtimes.ContainsKey(image.RuntimeId) ||
                image.ArtifactProcessorId is not null && !processors.ContainsKey(image.ArtifactProcessorId))
            {
                throw new BundleValidationException($"Deployment image '{image.Id}' references a missing catalog component.");
            }

            if (image.ImmutableReference is not null)
            {
                if (image.RuntimeId is null)
                {
                    throw new BundleValidationException(
                        $"Only a runtime image can carry promotion-bound immutable reference '{image.ImmutableReference}'.");
                }
                ValidateImmutableImageReference(image);
                if (imageOverrides.TryGetValue(image.Id, out var overridden) &&
                    !string.Equals(overridden, image.ImmutableReference, StringComparison.Ordinal))
                {
                    throw new BundleValidationException(
                        $"Deployment image '{image.Id}' is promotion-bound and cannot use a different override.");
                }
            }

            if (image.ToolchainId is not null)
            {
                var workerId = toolchains[image.ToolchainId].WorkerId;
                if (!toolchainImagesByWorkerId.TryAdd(workerId, image.Id))
                {
                    throw new BundleValidationException(
                        $"Language worker '{workerId}' is represented by both deployment images " +
                        $"'{toolchainImagesByWorkerId[workerId]}' and '{image.Id}'.");
                }
            }

            var componentIds = ComponentIds(image);
            if (componentIds.Count != componentIds.Distinct(StringComparer.Ordinal).Count())
                throw new BundleValidationException($"Deployment image '{image.Id}' contains duplicate lock component IDs.");
            if (image.ToolchainId is not null)
            {
                var workerId = toolchains[image.ToolchainId].WorkerId;
                foreach (var profileId in toolchains.Values
                             .Where(toolchain => string.Equals(
                                 toolchain.WorkerId,
                                 workerId,
                                 StringComparison.Ordinal))
                             .Select(static toolchain => toolchain.Id))
                {
                    if (!componentIds.Contains(profileId, StringComparer.Ordinal))
                    {
                        throw new BundleValidationException(
                            $"Language worker image '{image.Id}' does not declare lock component " +
                            $"'{profileId}' for its hosted toolchain profile.");
                    }
                }
            }
            foreach (var componentId in componentIds)
            {
                if (!releaseLock.Components.ContainsKey(componentId))
                {
                    throw new BundleValidationException(
                        $"Deployment image '{image.Id}' references missing lock component '{componentId}'.");
                }
            }
        }

        foreach (var workerId in catalog.Toolchains
                     .Where(static item => item.Availability.IsSelectable)
                     .Select(static item => item.WorkerId)
                     .Distinct(StringComparer.Ordinal))
        {
            if (!toolchainImagesByWorkerId.ContainsKey(workerId))
            {
                throw new BundleValidationException(
                    $"Selectable language worker '{workerId}' has no deployment image definition.");
            }
        }
        foreach (var runtime in catalog.Runtimes.Where(static item => item.Availability.IsSelectable))
        {
            RequireDefinition(deployment, static (item, id) => item.RuntimeId == id, runtime.Id, "runtime");
        }
        foreach (var processor in catalog.ArtifactProcessors.Where(static item => item.Availability.IsSelectable))
        {
            RequireDefinition(deployment, static (item, id) => item.ArtifactProcessorId == id, processor.Id, "artifact processor");
        }
        foreach (var overrideId in imageOverrides.Keys)
        {
            if (!ids.Contains(overrideId))
            {
                throw new BundleValidationException($"Image override '{overrideId}' is not declared in deploy/images.json.");
            }
        }

        return expectedReferenceSetDigests;
    }

    private static void ValidateRuntimePromotionBindings(
        CatalogDocument catalog,
        DeploymentImageManifest deployment,
        IReadOnlyList<RuntimeProfileDefinition> profiles)
    {
        var profileIndex = IndexRuntimeProfiles(profiles);
        foreach (var runtime in catalog.Runtimes.Where(static item => item.Availability.IsSelectable))
        {
            var definitions = deployment.Images
                .Where(item => string.Equals(item.RuntimeId, runtime.Id, StringComparison.Ordinal))
                .ToArray();
            if (definitions.Length != 1)
            {
                throw new BundleValidationException(
                    $"Selectable runtime '{runtime.Id}' must have exactly one deployment image definition.");
            }

            var definition = definitions[0];
            var profile = profileIndex[runtime.Id];
            var receipt = profile.PromotionReceipt;
            if ((receipt is null) != (definition.ImmutableReference is null))
            {
                throw new BundleValidationException(
                    $"Runtime '{runtime.Id}' must declare its promotion receipt and immutable deployment reference together.");
            }
            if (receipt is null)
            {
                continue;
            }

            var expectedPath = $"profiles/runtime-promotion-receipts/{runtime.Id}.json";
            if (!string.Equals(receipt.Path, expectedPath, StringComparison.Ordinal))
            {
                throw new BundleValidationException(
                    $"Runtime '{runtime.Id}' promotion receipt path must be '{expectedPath}'.");
            }
            if (!IsSha256(receipt.Sha256))
            {
                throw new BundleValidationException(
                    $"Runtime '{runtime.Id}' promotion receipt has an invalid SHA-256 digest.");
            }

            ValidateImmutableImageReference(definition);
            if (!string.Equals(profile.Image, definition.ImmutableReference, StringComparison.Ordinal))
            {
                throw new BundleValidationException(
                    $"Runtime '{runtime.Id}' profile image does not match its promotion-bound deployment reference.");
            }
        }
    }

    private static void ValidateImmutableImageReference(DeploymentImageDefinition definition)
    {
        var reference = definition.ImmutableReference
            ?? throw new BundleValidationException(
                $"Deployment image '{definition.Id}' has no immutable reference.");
        var expectedPrefix = definition.Repository + "@sha256:";
        if (!reference.StartsWith(expectedPrefix, StringComparison.Ordinal) ||
            reference.Length != expectedPrefix.Length + 64 ||
            reference.AsSpan(expectedPrefix.Length).ToArray().Any(static character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new BundleValidationException(
                $"Deployment image '{definition.Id}' immutable reference must pin its declared repository by SHA-256.");
        }
    }

    private static void RequireDefinition(
        DeploymentImageManifest deployment,
        Func<DeploymentImageDefinition, string, bool> matches,
        string id,
        string kind)
    {
        if (!deployment.Images.Any(item => matches(item, id)))
        {
            throw new BundleValidationException($"Selectable {kind} '{id}' has no deployment image definition.");
        }
    }

    private static void ValidateInspection(
        DeploymentImageDefinition definition,
        string reference,
        DockerImageInspection inspection,
        string releaseId,
        RepositorySourceProvenance source,
        ReleaseLockDocument releaseLock,
        BaseImageManifest baseImages,
        RuntimeMatrixBaseImageBindings runtimeMatrixBaseImages,
        CatalogDocument catalog,
        IReadOnlyDictionary<string, string> expectedReferenceSetDigests,
        bool promotionBoundRuntime,
        bool allowDevelopmentImageInputs)
    {
        if (!IsSha256(inspection.ImageId))
        {
            throw new BundleValidationException($"Image '{reference}' has an invalid immutable image ID.");
        }

        if (!string.Equals(inspection.OperatingSystem, "linux", StringComparison.Ordinal) ||
            !string.Equals(inspection.Architecture, "amd64", StringComparison.Ordinal))
        {
            throw new BundleValidationException(
                $"Image '{reference}' is {inspection.OperatingSystem}/{inspection.Architecture}; linux/amd64 is required.");
        }

        if (definition.ImmutableReference is not null &&
            !inspection.RepoDigests.Contains(definition.ImmutableReference, StringComparer.Ordinal))
        {
            throw new BundleValidationException(
                $"Promotion-bound image '{definition.Id}' does not resolve its immutable reference " +
                $"'{definition.ImmutableReference}' to inspected image ID '{inspection.ImageId}'.");
        }

        if (!inspection.Labels.TryGetValue("org.opencontainers.image.version", out var version) ||
            !string.Equals(version, releaseId, StringComparison.Ordinal))
        {
            throw new BundleValidationException(
                $"Image '{definition.Id}' does not carry release label '{releaseId}'.");
        }

        if (inspection.Labels.TryGetValue(DevelopmentImageInputsLabel, out var developmentInputs))
        {
            if (developmentInputs is not ("true" or "false"))
            {
                throw new BundleValidationException(
                    $"Image '{definition.Id}' has invalid development image-input label '{developmentInputs}'.");
            }
            if (StringComparer.Ordinal.Equals(developmentInputs, "true") &&
                !allowDevelopmentImageInputs)
            {
                throw new BundleValidationException(
                    $"Image '{definition.Id}' uses development image inputs; " +
                    "--allow-development-image-inputs is required for an unsigned bundle.");
            }
        }

        ValidateInspectionSourceRevision(
            definition,
            inspection.Labels,
            source.Revision,
            promotionBoundRuntime);

        ValidateComponentIdentityLabels(definition, inspection.Labels, releaseLock);
        ValidateDeclaredReferenceSetLabels(
            definition,
            inspection.Labels,
            releaseLock,
            expectedReferenceSetDigests);
        ValidateBaseImageLabels(definition, inspection.Labels, baseImages, runtimeMatrixBaseImages);

        if (definition.RuntimeId is not null)
        {
            var componentId = definition.LockComponentId ?? definition.RuntimeId;
            if (!releaseLock.Components.TryGetValue(componentId, out var component))
                throw new BundleValidationException($"Runtime image '{definition.Id}' has no locked component '{componentId}'.");
            var runtime = catalog.Runtimes.Single(item =>
                string.Equals(item.Id, definition.RuntimeId, StringComparison.Ordinal));
            if (RequiresCommitIdentity(runtime))
            {
                ValidateRuntimeIdentityLabel(definition.Id, inspection.Labels, RuntimeCommitLabel, component.Commit);
                ValidateRuntimeIdentityLabel(definition.Id, inspection.Labels, JitCommitLabel, component.JitCommit);
            }
            else if (component.Digest is null || !IsSha256(component.Digest))
            {
                throw new BundleValidationException(
                    $"Runtime image '{definition.Id}' must have an exact locked digest when commit identity is not applicable.");
            }
        }

        if (definition.ToolchainId is not null)
        {
            ValidateToolchainReferenceSetLabels(
                definition,
                inspection.Labels,
                catalog,
                expectedReferenceSetDigests);
        }
    }

    internal static void ValidateInspectionSourceRevision(
        DeploymentImageDefinition definition,
        IReadOnlyDictionary<string, string> labels,
        string releaseRevision,
        bool promotionBoundRuntime)
    {
        if (!labels.TryGetValue(RepositorySourceProvenanceResolver.ImageLabel, out var sourceRevision))
        {
            throw new BundleValidationException(
                $"Image '{definition.Id}' does not carry source revision label '{releaseRevision}'.");
        }

        if (promotionBoundRuntime)
        {
            if (definition.RuntimeId is null || definition.ImmutableReference is null ||
                !IsCommit(sourceRevision) ||
                !labels.TryGetValue("org.opencontainers.image.revision", out var ociRevision) ||
                !StringComparer.Ordinal.Equals(ociRevision, sourceRevision))
            {
                throw new BundleValidationException(
                    $"Promotion-bound runtime image '{definition.Id}' does not carry one canonical, " +
                    "matching implementation revision in both source labels.");
            }
            return;
        }

        if (!StringComparer.Ordinal.Equals(sourceRevision, releaseRevision))
        {
            throw new BundleValidationException(
                $"Image '{definition.Id}' does not carry source revision label '{releaseRevision}'.");
        }
        if (definition.ImmutableReference is not null &&
            (!labels.TryGetValue("org.opencontainers.image.revision", out var releaseOciRevision) ||
             !StringComparer.Ordinal.Equals(releaseOciRevision, releaseRevision)))
        {
            throw new BundleValidationException(
                $"Immutable image '{definition.Id}' does not carry OCI source revision '{releaseRevision}'.");
        }
    }

    private static void ValidateBaseImageLabels(
        DeploymentImageDefinition definition,
        IReadOnlyDictionary<string, string> labels,
        BaseImageManifest baseImages,
        RuntimeMatrixBaseImageBindings runtimeMatrixBaseImages)
    {
        var expected = baseImages.Images.ToDictionary(static image => image.Id, static image => image.Reference, StringComparer.Ordinal);
        string? matrixBaseImage = null;
        var matrixBaseImageBound = definition.RuntimeId is not null &&
            runtimeMatrixBaseImages.LinuxRuntimeBaseImages.TryGetValue(
                definition.RuntimeId,
                out matrixBaseImage);
        if (matrixBaseImageBound)
            expected[RuntimeMatrixBaseImageBindings.LinuxRuntimeBaseImageId] = matrixBaseImage!;

        var observed = labels
            .Where(static pair => pair.Key.StartsWith(BaseImageLabelPrefix, StringComparison.Ordinal))
            .ToArray();
        if (observed.Length == 0)
            throw new BundleValidationException($"Image '{definition.Id}' does not declare any pinned base image labels.");
        foreach (var pair in observed)
        {
            var baseImageId = pair.Key[BaseImageLabelPrefix.Length..];
            if (!expected.TryGetValue(baseImageId, out var reference))
                throw new BundleValidationException($"Image '{definition.Id}' declares unknown base image '{baseImageId}'.");
            if (!string.Equals(reference, pair.Value, StringComparison.Ordinal))
            {
                var requiredBy = matrixBaseImageBound &&
                    StringComparer.Ordinal.Equals(
                        baseImageId,
                        RuntimeMatrixBaseImageBindings.LinuxRuntimeBaseImageId)
                            ? "runtime matrix"
                            : "base image manifest";
                throw new BundleValidationException(
                    $"Image '{definition.Id}' base image '{baseImageId}' is '{pair.Value}', " +
                    $"but the {requiredBy} requires '{reference}'.");
            }
        }
    }

    private static void ValidateComponentIdentityLabels(
        DeploymentImageDefinition definition,
        IReadOnlyDictionary<string, string> labels,
        ReleaseLockDocument releaseLock)
    {
        foreach (var componentId in ComponentIds(definition))
        {
            var component = releaseLock.Components[componentId];
            ValidateComponentIdentityLabel(
                definition.Id,
                componentId,
                labels,
                "version",
                component.ResolvedVersion,
                ignoreCase: false);
            ValidateOptionalComponentIdentityLabel(
                definition.Id,
                componentId,
                labels,
                "commit",
                component.Commit,
                ignoreCase: true);
            ValidateOptionalComponentIdentityLabel(
                definition.Id,
                componentId,
                labels,
                "digest",
                component.Digest,
                ignoreCase: false);
            ValidateOptionalComponentIdentityLabel(
                definition.Id,
                componentId,
                labels,
                "source-uri",
                component.SourceUri,
                ignoreCase: false);
            ValidateOptionalComponentIdentityLabel(
                definition.Id,
                componentId,
                labels,
                "patch-digest",
                component.PatchDigest,
                ignoreCase: false);
        }
    }

    private static ReleaseLockDocument ResolveDevelopmentFrameworkComponentIdentities(
        CatalogDocument catalog,
        ReleaseLockDocument releaseLock,
        IReadOnlyList<InspectedImage> images,
        bool allowDevelopmentImageInputs)
    {
        Dictionary<string, LockedComponent>? components = null;
        foreach (var image in images)
        {
            if (image.RuntimeId is null)
                continue;

            var runtime = catalog.Runtimes.Single(item =>
                StringComparer.Ordinal.Equals(item.Id, image.RuntimeId));
            if (!StringComparer.Ordinal.Equals(runtime.Family, "netfx-clr-wine"))
                continue;

            var component = releaseLock.Components[image.LockComponentId];
            var componentPrefix = $"{ComponentLabelPrefix}{image.LockComponentId}.";
            if (!image.Labels.TryGetValue(componentPrefix + "digest", out var observedDigest) ||
                !image.Labels.TryGetValue(componentPrefix + "source-uri", out var observedSourceUri))
            {
                continue;
            }
            var identityDiffers = !StringComparer.Ordinal.Equals(observedDigest, component.Digest) ||
                !StringComparer.Ordinal.Equals(observedSourceUri, component.SourceUri);
            var hasRowBinding = image.Labels.ContainsKey(
                "io.sharplabnext.framework.row-operator-image");
            if (!identityDiffers && !hasRowBinding)
                continue;

            var rowOperatorImage = RequiredImageLabel(
                image,
                "io.sharplabnext.framework.row-operator-image");
            var operatorDigest = $"sha256:{BaseImageDigest(rowOperatorImage)}";
            var operatorSourceUri = $"docker://{rowOperatorImage}";
            RequireImageLabel(
                image,
                "io.sharplabnext.framework.matrix-selector",
                "true");
            RequireImageLabel(
                image,
                $"{ComponentLabelPrefix}runtime-matrix.profile-id",
                image.RuntimeId);
            RequireImageLabel(
                image,
                $"{ComponentLabelPrefix}runtime-matrix.version",
                component.ResolvedVersion);
            RequireImageLabel(
                image,
                $"{ComponentLabelPrefix}runtime-matrix.digest",
                operatorDigest);
            RequireImageLabel(
                image,
                $"{ComponentLabelPrefix}runtime-matrix.source-uri",
                operatorSourceUri);
            RequireImageLabel(image, componentPrefix + "digest", operatorDigest);
            RequireImageLabel(image, componentPrefix + "source-uri", operatorSourceUri);

            var rowDigest = RequiredImageLabel(image, "io.sharplabnext.framework.row-digest");
            if (!IsSha256(rowDigest))
            {
                throw new BundleValidationException(
                    $"Image '{image.Id}' has invalid Framework row digest '{rowDigest}'.");
            }
            _ = BaseImageDigest(RequiredImageLabel(
                image,
                "io.sharplabnext.framework.matrix-parent"));
            var matrixSourceUri = RequiredImageLabel(
                image,
                "io.sharplabnext.framework.matrix-source-uri");
            if (!matrixSourceUri.StartsWith("docker://", StringComparison.Ordinal))
            {
                throw new BundleValidationException(
                    $"Image '{image.Id}' Framework matrix source is not an immutable docker URI.");
            }
            _ = BaseImageDigest(matrixSourceUri["docker://".Length..]);

            if (!identityDiffers)
                continue;
            if (!allowDevelopmentImageInputs ||
                !image.Labels.TryGetValue(DevelopmentImageInputsLabel, out var developmentInputs) ||
                !StringComparer.Ordinal.Equals(developmentInputs, "true"))
            {
                continue;
            }

            components ??= releaseLock.Components.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal);
            components[image.LockComponentId] = component with
            {
                Digest = operatorDigest,
                SourceUri = operatorSourceUri
            };
        }

        return components is null ? releaseLock : releaseLock with { Components = components };
    }

    private static string RequiredImageLabel(InspectedImage image, string label)
    {
        if (!image.Labels.TryGetValue(label, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new BundleValidationException(
                $"Image '{image.Id}' does not carry required label '{label}'.");
        }
        return value;
    }

    private static void RequireImageLabel(InspectedImage image, string label, string expected)
    {
        var actual = RequiredImageLabel(image, label);
        if (!StringComparer.Ordinal.Equals(actual, expected))
        {
            throw new BundleValidationException(
                $"Image '{image.Id}' label '{label}' is '{actual}', but its verified Framework row requires '{expected}'.");
        }
    }

    private static void ValidateOptionalComponentIdentityLabel(
        string imageId,
        string componentId,
        IReadOnlyDictionary<string, string> labels,
        string field,
        string? expected,
        bool ignoreCase)
    {
        if (!string.IsNullOrWhiteSpace(expected))
            ValidateComponentIdentityLabel(imageId, componentId, labels, field, expected, ignoreCase);
    }

    private static void ValidateComponentIdentityLabel(
        string imageId,
        string componentId,
        IReadOnlyDictionary<string, string> labels,
        string field,
        string expected,
        bool ignoreCase)
    {
        var label = $"{ComponentLabelPrefix}{componentId}.{field}";
        if (!labels.TryGetValue(label, out var actual))
            throw new BundleValidationException($"Image '{imageId}' does not carry required label '{label}'.");
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(expected, actual, comparison))
        {
            throw new BundleValidationException(
                $"Image '{imageId}' label '{label}' is '{actual}', but lock requires '{expected}'.");
        }
    }

    private static IReadOnlyList<string> ComponentIds(DeploymentImageDefinition definition)
    {
        var primary = definition.LockComponentId ?? definition.ToolchainId ?? definition.RuntimeId ??
            definition.ArtifactProcessorId;
        return primary is null
            ? definition.LockComponentIds
            : [primary, .. definition.LockComponentIds];
    }

    private static void ValidateToolchainReferenceSetLabels(
        DeploymentImageDefinition definition,
        IReadOnlyDictionary<string, string> labels,
        CatalogDocument catalog,
        IReadOnlyDictionary<string, string> expectedReferenceSetDigests)
    {
        var toolchain = catalog.Toolchains.Single(item =>
            string.Equals(item.Id, definition.ToolchainId, StringComparison.Ordinal));
        var referenceSetIds = catalog.Toolchains
            .Where(item => string.Equals(item.WorkerId, toolchain.WorkerId, StringComparison.Ordinal))
            .SelectMany(static item => item.AllowedReferenceSetIds)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static id => id, StringComparer.Ordinal);
        foreach (var referenceSetId in referenceSetIds)
        {
            if (!expectedReferenceSetDigests.TryGetValue(referenceSetId, out var expectedDigest))
            {
                throw new BundleValidationException(
                    $"Toolchain image '{definition.Id}' has no locked identity for reference set '{referenceSetId}'.");
            }

            var label = ReferenceSetLabelPrefix + referenceSetId;
            if (!labels.TryGetValue(label, out var actualDigest))
            {
                throw new BundleValidationException(
                    $"Toolchain image '{definition.Id}' does not carry required label '{label}'.");
            }
            if (!string.Equals(actualDigest, expectedDigest, StringComparison.Ordinal))
            {
                throw new BundleValidationException(
                    $"Toolchain image '{definition.Id}' label '{label}' is '{actualDigest}', but lock requires '{expectedDigest}'.");
            }
        }
    }

    private static void ValidateDeclaredReferenceSetLabels(
        DeploymentImageDefinition definition,
        IReadOnlyDictionary<string, string> labels,
        ReleaseLockDocument releaseLock,
        IReadOnlyDictionary<string, string> expectedReferenceSetDigests)
    {
        foreach (var referenceSetId in ComponentIds(definition).Where(componentId =>
                     StringComparer.Ordinal.Equals(
                         releaseLock.Components[componentId].Kind,
                         "reference-set")))
        {
            if (!expectedReferenceSetDigests.TryGetValue(referenceSetId, out var expectedDigest))
            {
                throw new BundleValidationException(
                    $"Image '{definition.Id}' has no locked identity for declared reference set '{referenceSetId}'.");
            }

            var label = ReferenceSetLabelPrefix + referenceSetId;
            if (!labels.TryGetValue(label, out var actualDigest))
            {
                throw new BundleValidationException(
                    $"Image '{definition.Id}' does not carry required label '{label}'.");
            }
            if (!StringComparer.Ordinal.Equals(actualDigest, expectedDigest))
            {
                throw new BundleValidationException(
                    $"Image '{definition.Id}' label '{label}' is '{actualDigest}', but lock requires '{expectedDigest}'.");
            }
        }
    }

    private static void ValidateRuntimeIdentityLabel(
        string imageId,
        IReadOnlyDictionary<string, string> labels,
        string label,
        string? expected)
    {
        if (!IsCommit(expected))
            throw new BundleValidationException($"Runtime image '{imageId}' has no exact locked identity for '{label}'.");
        if (!labels.TryGetValue(label, out var actual) || !IsCommit(actual))
            throw new BundleValidationException($"Runtime image '{imageId}' does not carry valid label '{label}'.");
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new BundleValidationException(
                $"Runtime image '{imageId}' label '{label}' is '{actual}', but lock requires '{expected}'.");
        }
    }

    private static bool IsCommit(string? value) =>
        value is { Length: 40 or 64 } &&
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool RequiresCommitIdentity(RuntimeManifest runtime) =>
        !string.Equals(runtime.Family, "netfx-clr-wine", StringComparison.Ordinal) &&
        !string.Equals(runtime.Family, "mono", StringComparison.Ordinal);

    private static bool IsSha256(string value)
    {
        if (value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in value.AsSpan(7))
        {
            if (!(character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static void EnsureInputFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new BundleValidationException($"Required input '{path}' does not exist.");
        }
    }

    private static void EnsureInputDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new BundleValidationException($"Required input directory '{path}' does not exist.");
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        var sourceRoot = Path.GetFullPath(source);
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new BundleValidationException(
                    $"Security asset directory '{directory}' cannot be a reparse point.");
            }
        }

        Directory.CreateDirectory(destination);
        foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            if (File.GetAttributes(sourcePath).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new BundleValidationException(
                    $"Security asset '{sourcePath}' cannot be a reparse point.");
            }

            var relative = Path.GetRelativePath(sourceRoot, sourcePath);
            var destinationPath = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath);
        }
    }

    private static Task WriteJsonAsync(string path, object value, CancellationToken cancellationToken) =>
        WriteJsonAsync(path, value, JsonOptions, cancellationToken);

    private static async Task WriteJsonAsync(
        string path,
        object value,
        JsonSerializerOptions options,
        CancellationToken cancellationToken)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, value.GetType(), options, cancellationToken);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
    }

    private static async Task<string> ComputeFileSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static DeploymentScriptBindings GetDeploymentScriptBindings(
        IReadOnlyList<RuntimePromotionTrustSnapshot> trust)
    {
        var production = RuntimePromotionTrust.RuntimePromotionPlanSignatureTrust.ProductionVerifier;
        var bindings = trust.Select(runtime =>
        {
            var signedPlan = runtime.SignedPlan ?? throw new BundleValidationException(
                $"Runtime '{runtime.RuntimeId}' has no captured signed promotion plan.");
            return new DeploymentScriptBindings(
                signedPlan.Verifier.KeyId,
                signedPlan.PublicKey.RelativePath,
                signedPlan.PublicKey.Sha256);
        }).Distinct().ToArray();
        if (bindings.Length > 1)
        {
            throw new BundleValidationException(
                "Promotion-bound release material uses multiple plan-signature trust roots.");
        }

        return bindings.Length == 1
            ? bindings[0]
            : new DeploymentScriptBindings(
                production.KeyId,
                production.PublicKeyPath,
                $"sha256:{Convert.ToHexStringLower(SHA256.HashData(production.PublicKeyPem))}");
    }

    private static async Task WriteDeploymentScriptAsync(
        string staging,
        string scriptName,
        DeploymentScriptBindings bindings,
        CancellationToken cancellationToken)
    {
        var resourceName = $"SharpLabNext.BundleBuilder.DeploymentScripts.{scriptName}";
        await using var input = typeof(ReleaseBundleBuilder).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new BundleValidationException($"Embedded deployment script '{scriptName}' is missing.");
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var script = await reader.ReadToEndAsync(cancellationToken);
        script = script
            .Replace("__RUNTIME_PROMOTION_PLAN_KEY_ID__", bindings.KeyId, StringComparison.Ordinal)
            .Replace("__RUNTIME_PROMOTION_PLAN_PUBLIC_KEY_PATH__", bindings.PublicKeyPath, StringComparison.Ordinal)
            .Replace("__RUNTIME_PROMOTION_PLAN_PUBLIC_KEY_SHA256__", bindings.PublicKeySha256, StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            Path.Combine(staging, scriptName),
            script,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
    }

    private static readonly string[] DeploymentScriptNames =
    [
        "verify.ps1",
        "verify.sh",
        "smoke.ps1",
        "smoke.sh",
        "deployment-common.ps1",
        "deployment-common.sh",
        "deploy.sh",
        "install.ps1",
        "install.sh",
        "rollback.ps1",
        "rollback.sh"
    ];

    private sealed record PromotionEvidenceBinding(string Kind, SortedSet<string> RuntimeIds);

    private sealed record DeploymentScriptBindings(
        string KeyId,
        string PublicKeyPath,
        string PublicKeySha256);

    private sealed class PromotionEvidenceManifest
    {
        public required int SchemaVersion { get; init; }
        public required string BuildSourceRevision { get; init; }
        public required string ReleaseSourceRevision { get; init; }
        public required IReadOnlyList<string> PromotedRuntimeIds { get; init; }
        public required IReadOnlyList<PromotionEvidenceManifestEntry> Entries { get; init; }
    }

    private sealed class PromotionEvidenceManifestEntry
    {
        public required string Kind { get; init; }
        public required string SourcePath { get; init; }
        public required string BundlePath { get; init; }
        public required string Sha256 { get; init; }
        public required long SizeBytes { get; init; }

        public required string[] ProfileIds { get; init; }

        public required string[] RuntimeIds { get; init; }
    }

    private static void DeleteStagingDirectory(string staging, string parent)
    {
        if (!Directory.Exists(staging))
        {
            return;
        }

        var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullStaging = Path.GetFullPath(staging);
        if (!fullStaging.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullStaging).StartsWith('.'))
        {
            throw new BundleValidationException("Refusing to delete an unsafe staging path.");
        }

        Directory.Delete(fullStaging, recursive: true);
    }

}
