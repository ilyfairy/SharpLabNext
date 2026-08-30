using SharpLabNext.Catalog;
using SharpLabNext.Contracts;
using SharpLabNext.Gateway;

namespace SharpLabNext.IntegrationTests;

public sealed class GatewayDependencyHealthTests
{
    [Fact]
    public async Task HttpProbeReadsCompleteRuntimeProfileIdentity()
    {
        var handler = new DelegateHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/health/ready" => JsonResponse("{\"Status\":\"ready\"}"),
            "/api/v1/runtime/status" => JsonResponse(
                """
                {
                  "Profiles": [
                    {
                      "Id": "test-runtime",
                      "Family": "coreclr",
                      "RuntimeVersion": "10.0.9",
                      "Rid": "linux-x64",
                      "Architecture": "x64",
                      "AcceptedArtifactFormats": ["dotnet-managed-pe-v1"],
                      "Capabilities": ["run", "jit-asm"],
                      "ProvidedRuntimeFeatureTags": [],
                      "ProvidedMetadataFeatureTags": []
                    }
                  ]
                }
                """),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
        });
        var probe = new HttpGatewayDependencyProbe(new FixedHttpClientFactory(handler));
        var target = new GatewayDependencyTarget(
            GatewayDependencyHealthService.RuntimeSupervisorDependencyId,
            GatewayDependencyKind.RuntimeSupervisor,
            new Uri("http://runtime-supervisor.test/"),
            null);

        var result = await probe.ProbeAsync(target, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.True(result.Ready);
        var profile = Assert.Single(result.RuntimeProfiles).Value;
        Assert.Equal("coreclr", profile.Family);
        Assert.Equal("10.0.9", profile.RuntimeVersion);
        Assert.True(profile.Capabilities.SetEquals(["run", "jit-asm"]));
    }

    [Fact]
    public async Task HttpProbeRejectsIncompleteRuntimeProfileIdentity()
    {
        var handler = new DelegateHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/health/ready" => JsonResponse("{\"Status\":\"ready\"}"),
            "/api/v1/runtime/status" => JsonResponse(
                """
                {
                  "Profiles": [
                    {
                      "Id": "test-runtime",
                      "RuntimeVersion": "10.0.9",
                      "Rid": "linux-x64",
                      "Architecture": "x64",
                      "AcceptedArtifactFormats": ["dotnet-managed-pe-v1"],
                      "Capabilities": ["run"],
                      "ProvidedRuntimeFeatureTags": [],
                      "ProvidedMetadataFeatureTags": []
                    }
                  ]
                }
                """),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
        });
        var probe = new HttpGatewayDependencyProbe(new FixedHttpClientFactory(handler));
        var target = new GatewayDependencyTarget(
            GatewayDependencyHealthService.RuntimeSupervisorDependencyId,
            GatewayDependencyKind.RuntimeSupervisor,
            new Uri("http://runtime-supervisor.test/"),
            null);

        var result = await probe.ProbeAsync(target, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.False(result.Ready);
        Assert.Equal("Runtime profile probe returned malformed identity data.", result.Reason);
    }

    [Fact]
    public async Task HttpProbeReadsExtendedRuntimeIdentityWhenProvided()
    {
        var handler = new DelegateHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/health/ready" => JsonResponse("{\"Status\":\"ready\"}"),
            "/api/v1/runtime/status" => JsonResponse(
                """
                {
                  "Profiles": [
                    {
                      "Id": "test-runtime",
                      "Family": "coreclr",
                      "RuntimeVersion": "10.0.9",
                      "RuntimeCommit": "runtime-commit",
                      "JitVersion": "10.0.9",
                      "JitCommit": "jit-commit",
                      "RuntimeImageId": "sha256:test",
                      "Rid": "linux-x64",
                      "Architecture": "x64",
                      "AcceptedRuntimeFamilies": ["coreclr"],
                      "AcceptedFrameworks": [{"Name":"Microsoft.NETCore.App","ExactVersion":"10.0.9"}],
                      "AcceptedArtifactFormats": ["dotnet-managed-pe-v1"],
                      "Capabilities": ["run", "jit-asm"],
                      "ProvidedRuntimeFeatureTags": [],
                      "ProvidedMetadataFeatureTags": [],
                      "Container": {"IsolationKind":"standard","EnvironmentKind":"coreclr"},
                      "Operations": {"Jit": {"SourceMappingKind":"none"}}
                    }
                  ]
                }
                """),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
        });
        var probe = new HttpGatewayDependencyProbe(new FixedHttpClientFactory(handler));
        var target = new GatewayDependencyTarget(
            GatewayDependencyHealthService.RuntimeSupervisorDependencyId,
            GatewayDependencyKind.RuntimeSupervisor,
            new Uri("http://runtime-supervisor.test/"),
            null);

        var result = await probe.ProbeAsync(target, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        var profile = Assert.Single(result.RuntimeProfiles).Value;
        Assert.Equal("runtime-commit", profile.RuntimeCommit);
        Assert.Equal("jit-commit", profile.JitCommit);
        Assert.Equal("sha256:test", profile.RuntimeImageId);
        Assert.True(profile.AcceptedRuntimeFamilies!.SetEquals(["coreclr"]));
        Assert.Contains("Microsoft.NETCore.App|||10.0.9", profile.AcceptedFrameworks!);
        Assert.Equal("standard", profile.ContainerIsolationKind);
        Assert.Equal("coreclr", profile.ContainerEnvironmentKind);
        Assert.Equal("none", profile.JitSourceMappingKind);
    }

    [Fact]
    public void EstablishedHealthyDependencyRequiresTwoConsecutiveFailuresBeforeDowngrade()
    {
        const string dependencyId = "language-worker:test";
        var healthy = new GatewayDependencyProbeResult(dependencyId, GatewayDependencyKind.LanguageWorker, true, null, EmptyProfiles());
        var failed = healthy with { Ready = false, Reason = "Transient probe timeout." };
        var hysteresis = new GatewayDependencyFailureHysteresis();

        var initial = hysteresis.Apply(
            new Dictionary<string, GatewayDependencyProbeResult>(StringComparer.Ordinal)
            {
                [dependencyId] = healthy
            },
            previous: null);
        var transient = hysteresis.Apply(
            new Dictionary<string, GatewayDependencyProbeResult>(StringComparer.Ordinal)
            {
                [dependencyId] = failed
            },
            initial);
        var sustained = hysteresis.Apply(
            new Dictionary<string, GatewayDependencyProbeResult>(StringComparer.Ordinal)
            {
                [dependencyId] = failed
            },
            transient);
        var recovered = hysteresis.Apply(
            new Dictionary<string, GatewayDependencyProbeResult>(StringComparer.Ordinal)
            {
                [dependencyId] = healthy
            },
            sustained);
        var nextTransient = hysteresis.Apply(
            new Dictionary<string, GatewayDependencyProbeResult>(StringComparer.Ordinal)
            {
                [dependencyId] = failed
            },
            recovered);

        Assert.True(initial[dependencyId].Ready);
        Assert.True(transient[dependencyId].Ready);
        Assert.False(sustained[dependencyId].Ready);
        Assert.Equal("Transient probe timeout.", sustained[dependencyId].Reason);
        Assert.True(recovered[dependencyId].Ready);
        Assert.True(nextTransient[dependencyId].Ready);
    }

    [Fact]
    public void InitialFailureIsNotMaskedWithoutKnownHealthyState()
    {
        const string dependencyId = "artifact-store";
        var failed = new GatewayDependencyProbeResult(dependencyId, GatewayDependencyKind.ArtifactStore, false, "Store unavailable.", EmptyProfiles());
        var hysteresis = new GatewayDependencyFailureHysteresis();

        var result = hysteresis.Apply(
            new Dictionary<string, GatewayDependencyProbeResult>(StringComparer.Ordinal)
            {
                [dependencyId] = failed
            },
            previous: null);

        Assert.False(result[dependencyId].Ready);
    }

    [Fact]
    public async Task LanguageWorkerFailureDowngradesDependentCatalogEntriesAndChangesRevision()
    {
        var catalog = CreateCatalog();
        using var healthyService = CreateService(catalog);
        var healthy = await healthyService.GetSnapshotAsync(TestContext.Current.CancellationToken);

        var dependencyId = GatewayDependencyHealthService.LanguageDependencyId(CompilerWorkerId);
        var unavailableWorker = new GatewayDependencyProbeResult(dependencyId, GatewayDependencyKind.LanguageWorker, false, "Compiler worker is down.", EmptyProfiles());
        using var unavailableService = CreateService(
            catalog,
            new Dictionary<string, GatewayDependencyProbeResult>(StringComparer.Ordinal)
            {
                [dependencyId] = unavailableWorker
            });

        var unavailable = await unavailableService.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.True(healthy.Ready);
        Assert.False(unavailable.Ready);
        Assert.StartsWith($"{catalog.Revision}-h", healthy.Catalog.Revision, StringComparison.Ordinal);
        Assert.StartsWith($"{catalog.Revision}-h", unavailable.Catalog.Revision, StringComparison.Ordinal);
        Assert.NotEqual(healthy.Catalog.Revision, unavailable.Catalog.Revision);

        var toolchain = Assert.Single(unavailable.Catalog.Toolchains);
        AssertUnavailable(toolchain.Availability, "Compiler worker is down.");
        var referenceSet = Assert.Single(unavailable.Catalog.ReferenceSets);
        AssertUnavailable(referenceSet.Availability, $"No healthy toolchain provides reference set '{ReferenceSetId}'.");
        var preset = Assert.Single(unavailable.Catalog.Presets);
        AssertUnavailable(preset.Availability, $"Preset toolchain '{ToolchainId}' is unavailable.");
    }

    [Fact]
    public async Task MissingSupervisorProfileDowngradesRuntimeAndDependentPreset()
    {
        var catalog = CreateCatalog();
        using var service = CreateService(catalog, runtimeProfileIds: EmptyIds());

        var snapshot = await service.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.False(snapshot.Ready);
        Assert.True(snapshot.RuntimeSupervisorReady);
        var runtime = Assert.Single(snapshot.Catalog.Runtimes);
        AssertUnavailable(runtime.Availability, $"Runtime profile '{RuntimeId}' is not loaded by Runtime Supervisor.");
        var preset = Assert.Single(snapshot.Catalog.Presets);
        AssertUnavailable(preset.Availability, $"Preset runtime '{RuntimeId}' is unavailable.");
    }

    [Fact]
    public async Task MismatchedSupervisorProfileIdentityDowngradesRuntimeAndDependentPreset()
    {
        var catalog = CreateCatalog();
        var runtime = Assert.Single(catalog.Runtimes);
        var mismatched = RuntimeIdentity(runtime) with { RuntimeVersion = "9.9.9" };
        var supervisor = new GatewayDependencyProbeResult(
            GatewayDependencyHealthService.RuntimeSupervisorDependencyId,
            GatewayDependencyKind.RuntimeSupervisor,
            true,
            null,
            new Dictionary<string, RuntimeProfileProbeIdentity>(StringComparer.Ordinal)
            {
                [runtime.Id] = mismatched
            });
        using var service = CreateService(
            catalog,
            new Dictionary<string, GatewayDependencyProbeResult>(StringComparer.Ordinal)
            {
                [GatewayDependencyHealthService.RuntimeSupervisorDependencyId] = supervisor
            });

        var snapshot = await service.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.False(snapshot.Ready);
        var overlaidRuntime = Assert.Single(snapshot.Catalog.Runtimes);
        AssertUnavailable(overlaidRuntime.Availability, $"Runtime profile '{RuntimeId}' runtime version mismatch: catalog expects '10.0.0', Supervisor loaded '9.9.9'.");
        AssertUnavailable(Assert.Single(snapshot.Catalog.Presets).Availability, $"Preset runtime '{RuntimeId}' is unavailable.");
    }

    [Fact]
    public async Task MissingSupervisorCapabilityDowngradesRuntime()
    {
        var catalog = CreateCatalog();
        var runtime = Assert.Single(catalog.Runtimes);
        var mismatched = RuntimeIdentity(runtime) with { Capabilities = new HashSet<string>(StringComparer.Ordinal) };
        var supervisor = new GatewayDependencyProbeResult(
            GatewayDependencyHealthService.RuntimeSupervisorDependencyId,
            GatewayDependencyKind.RuntimeSupervisor,
            true,
            null,
            new Dictionary<string, RuntimeProfileProbeIdentity>(StringComparer.Ordinal)
            {
                [runtime.Id] = mismatched
            });
        using var service = CreateService(
            catalog,
            new Dictionary<string, GatewayDependencyProbeResult>(StringComparer.Ordinal)
            {
                [GatewayDependencyHealthService.RuntimeSupervisorDependencyId] = supervisor
            });

        var snapshot = await service.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.False(snapshot.Ready);
        AssertUnavailable(Assert.Single(snapshot.Catalog.Runtimes).Availability, $"Runtime profile '{RuntimeId}' capabilities do not match the catalog contract.");
    }

    [Fact]
    public async Task MismatchedSupervisorImageIdentityDowngradesRuntime()
    {
        var catalog = CreateCatalog();
        var runtime = Assert.Single(catalog.Runtimes) with
        {
            RuntimeCommit = "runtime-commit",
            JitVersion = "10.0.0",
            JitCommit = "jit-commit",
            RuntimeImageId = "sha256:expected",
            AcceptedRuntimeFamilies = ["dotnet"],
            AcceptedFrameworks =
            [
                new RuntimeFrameworkManifest { Name = "Microsoft.NETCore.App", ExactVersion = "10.0.0" }
            ],
            ContainerIsolationKind = "standard",
            ContainerEnvironmentKind = "coreclr",
            JitSourceMappingKind = "none"
        };
        catalog = catalog with { Runtimes = [runtime] };
        var loaded = RuntimeIdentity(runtime) with { RuntimeImageId = "sha256:other" };
        var supervisor = new GatewayDependencyProbeResult(
            GatewayDependencyHealthService.RuntimeSupervisorDependencyId,
            GatewayDependencyKind.RuntimeSupervisor,
            true,
            null,
            new Dictionary<string, RuntimeProfileProbeIdentity>(StringComparer.Ordinal)
            {
                [runtime.Id] = loaded
            });
        using var service = CreateService(
            catalog,
            new Dictionary<string, GatewayDependencyProbeResult>(StringComparer.Ordinal)
            {
                [GatewayDependencyHealthService.RuntimeSupervisorDependencyId] = supervisor
            });

        var snapshot = await service.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.False(snapshot.Ready);
        Assert.Equal("Runtime profile 'test-runtime' runtime image ID mismatch: catalog expects 'sha256:expected', Supervisor loaded 'sha256:other'.", Assert.Single(snapshot.Catalog.Runtimes).Availability.Reason);
    }

    [Fact]
    public async Task SuccessfulProbesDoNotPromoteStaticallyNotBuiltComponents()
    {
        var catalog = CreateCatalog(includeNotBuiltComponents: true);
        using var service = CreateService(catalog);

        var snapshot = await service.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Find(catalog.Toolchains, NotBuiltToolchainId).Availability, Find(snapshot.Catalog.Toolchains, NotBuiltToolchainId).Availability);
        Assert.Equal(Find(catalog.ReferenceSets, NotBuiltReferenceSetId).Availability, Find(snapshot.Catalog.ReferenceSets, NotBuiltReferenceSetId).Availability);
        Assert.Equal(Find(catalog.Runtimes, NotBuiltRuntimeId).Availability, Find(snapshot.Catalog.Runtimes, NotBuiltRuntimeId).Availability);
        Assert.Equal(Find(catalog.ArtifactProcessors, NotBuiltProcessorId).Availability, Find(snapshot.Catalog.ArtifactProcessors, NotBuiltProcessorId).Availability);
        Assert.Equal(Find(catalog.Presets, NotBuiltPresetId).Availability, Find(snapshot.Catalog.Presets, NotBuiltPresetId).Availability);
    }

    [Fact]
    public void PipelineAvailabilityRejectsRecordedProfileUnavailableSelection()
    {
        var snapshot = CreateSnapshot(CreateCatalog(), artifactStoreReady: true);
        var resolution = CreateResolution([new SelectionChange(SelectionField.Toolchain, ToolchainId, ToolchainId, SelectionChangeReason.ProfileUnavailable, "The requested profile is temporarily unavailable.")]);

        var reason = GatewayPipelineAvailability.GetUnavailableReason(snapshot, resolution, requireArtifactStore: false);

        Assert.Equal("The requested profile is temporarily unavailable.", reason);
    }

    [Fact]
    public void PipelineAvailabilityRejectsUnavailableArtifactStoreWhenRequired()
    {
        var snapshot = CreateSnapshot(CreateCatalog(), artifactStoreReady: false);
        var resolution = CreateResolution();

        var reason = GatewayPipelineAvailability.GetUnavailableReason(snapshot, resolution, requireArtifactStore: true);

        Assert.Equal("Artifact Store is unavailable.", reason);
        Assert.Null(GatewayPipelineAvailability.GetUnavailableReason(snapshot, resolution, requireArtifactStore: false));
    }

    [Fact]
    public void PipelineAvailabilityRejectsUnavailableSelectedArtifactProcessor()
    {
        var catalog = CreateCatalog();
        catalog = catalog with
        {
            ArtifactProcessors =
            [
                Find(catalog.ArtifactProcessors, ProcessorId) with { Availability = Unavailable("Selected processor is down.") }
            ]
        };
        var snapshot = CreateSnapshot(catalog, artifactStoreReady: true);
        var resolution = CreateResolution(
            stages:
            [
                BuildStage(),
                new PipelineStageDescriptor("render", PipelineStageKind.Render, ArtifactWorkerId, "dotnet-managed-pe-v1", "text-v1")
            ]);

        var reason = GatewayPipelineAvailability.GetUnavailableReason(snapshot, resolution, requireArtifactStore: false);

        Assert.Equal("Selected processor is down.", reason);
    }

    private static GatewayDependencyHealthService CreateService(CatalogDocument catalog, IReadOnlyDictionary<string, GatewayDependencyProbeResult>? probeResults = null, IReadOnlySet<string>? runtimeProfileIds = null)
    {
        var languageWorkers = new LanguageWorkerEndpointRegistry(catalog.Toolchains.Select(static toolchain => toolchain.WorkerId).Distinct(StringComparer.Ordinal).Select(static workerId => new LanguageWorkerEndpoint(
                workerId,
                new Uri($"http://{workerId}.test/"),
                "test-release",
                null,
                null)));
        var artifactWorkers = new ArtifactWorkerEndpointRegistry(catalog.ArtifactProcessors.Select(static processor => processor.WorkerId).Distinct(StringComparer.Ordinal).Select(static workerId => new ArtifactWorkerEndpoint(
                workerId,
                new Uri($"http://{workerId}.test/"),
                "test-release",
                null,
                null)));
        var options = new GatewayDependencyHealthOptions(
            Enabled: true,
            new Uri("http://artifact-store.test/"),
            new Uri("http://runtime-supervisor.test/"),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(100));
        var selectedRuntimeProfileIds = runtimeProfileIds ?? catalog.Runtimes.Select(static runtime => runtime.Id).ToHashSet(StringComparer.Ordinal);
        var loadedRuntimeProfiles = catalog.Runtimes.Where(runtime => selectedRuntimeProfileIds.Contains(runtime.Id)).ToDictionary(static runtime => runtime.Id, RuntimeIdentity, StringComparer.Ordinal);
        return new GatewayDependencyHealthService(catalog, languageWorkers, artifactWorkers, options, new FakeGatewayDependencyProbe(probeResults, loadedRuntimeProfiles));
    }

    private static GatewayDependencySnapshot CreateSnapshot(CatalogDocument catalog, bool artifactStoreReady)
    {
        var dependencies = new Dictionary<string, GatewayDependencyProbeResult>(StringComparer.Ordinal)
        {
            [GatewayDependencyHealthService.ArtifactStoreDependencyId] = new GatewayDependencyProbeResult(GatewayDependencyHealthService.ArtifactStoreDependencyId, GatewayDependencyKind.ArtifactStore, artifactStoreReady, artifactStoreReady ? null : "Store probe failed.", EmptyProfiles()),
            [GatewayDependencyHealthService.RuntimeSupervisorDependencyId] = new GatewayDependencyProbeResult(
                GatewayDependencyHealthService.RuntimeSupervisorDependencyId,
                GatewayDependencyKind.RuntimeSupervisor,
                true,
                null,
                new Dictionary<string, RuntimeProfileProbeIdentity>(StringComparer.Ordinal)
                {
                    [RuntimeId] = RuntimeIdentity(Find(catalog.Runtimes, RuntimeId))
                })
        };
        return new GatewayDependencySnapshot(catalog, dependencies, DateTimeOffset.UnixEpoch, artifactStoreReady);
    }

    private static ResolveSelectionResponse CreateResolution(IReadOnlyList<SelectionChange>? changes = null, IReadOnlyList<PipelineStageDescriptor>? stages = null) =>
        new(new ResolvedSelection(LanguageId, ToolchainId, ReferenceSetId, OutputId, null), changes ?? [], new EffectiveCapabilities([], [], [], []), "resolution-id", new PipelinePlanDescriptor("test-release", CompilerWorkerId, CompilerWorkerId, ReferenceSetId, stages ?? [BuildStage()], null, "test-policy", []), DateTimeOffset.MaxValue);

    private static PipelineStageDescriptor BuildStage() => new("build", PipelineStageKind.Build, CompilerWorkerId, null, "dotnet-managed-pe-v1");

    private static CatalogDocument CreateCatalog(bool includeNotBuiltComponents = false)
    {
        var toolchains = new List<ToolchainManifest>
        {
            new()
            {
                Id = ToolchainId,
                DisplayName = "Test compiler",
                WorkerId = CompilerWorkerId,
                ReleaseTrack = "stable",
                ResolvedVersion = "1.0.0",
                DefaultReferenceSetId = ReferenceSetId,
                SupportedLanguageIds = [LanguageId],
                AllowedReferenceSetIds = [ReferenceSetId],
                ProducesArtifactFormats = ["dotnet-managed-pe-v1"],
                Capabilities = ["compile-check", "managed-pe"],
                Availability = Healthy()
            }
        };
        var referenceSets = new List<ReferenceSetManifest>
        {
            new()
            {
                Id = ReferenceSetId,
                DisplayName = "Test references",
                TargetFramework = "net10.0",
                Digest = "sha256:test",
                RuntimeFamily = "dotnet",
                Availability = Healthy()
            }
        };
        var runtimes = new List<RuntimeManifest>
        {
            new()
            {
                Id = RuntimeId,
                DisplayName = ".NET test runtime",
                Family = "dotnet",
                ResolvedVersion = "10.0.0",
                Rid = "linux-x64",
                Architecture = "x64",
                AcceptedArtifactFormats = ["dotnet-managed-pe-v1"],
                Capabilities = ["run"],
                Availability = Healthy()
            }
        };
        var processors = new List<ArtifactProcessorManifest>
        {
            new()
            {
                Id = ProcessorId,
                DisplayName = "Test artifact processor",
                ResolvedVersion = "1.0.0",
                WorkerId = ArtifactWorkerId,
                AcceptsArtifactFormats = ["dotnet-managed-pe-v1"],
                ProducesArtifactFormats = ["text-v1"],
                Capabilities = ["il"],
                Availability = Healthy()
            }
        };
        var presets = new List<ProfilePreset>
        {
            new()
            {
                Id = PresetId,
                DisplayName = "Test preset",
                LanguageId = LanguageId,
                ToolchainId = ToolchainId,
                ReferenceSetId = ReferenceSetId,
                DefaultOutputId = OutputId,
                DefaultRuntimeId = RuntimeId,
                Availability = Healthy()
            }
        };

        if (includeNotBuiltComponents)
        {
            toolchains.Add(new ToolchainManifest { Id = NotBuiltToolchainId, DisplayName = "Not-built compiler", WorkerId = "compiler-not-built", ReleaseTrack = "candidate", ResolvedVersion = "0.0.0", DefaultReferenceSetId = NotBuiltReferenceSetId, SupportedLanguageIds = [LanguageId], AllowedReferenceSetIds = [NotBuiltReferenceSetId], ProducesArtifactFormats = ["dotnet-managed-pe-v1"], Capabilities = ["compile-check"], Availability = NotBuilt() });
            referenceSets.Add(new ReferenceSetManifest { Id = NotBuiltReferenceSetId, DisplayName = "Not-built references", TargetFramework = "net-next.0", Digest = "sha256:not-built", RuntimeFamily = "dotnet", Availability = NotBuilt() });
            runtimes.Add(new RuntimeManifest { Id = NotBuiltRuntimeId, DisplayName = "Not-built runtime", Family = "dotnet", ResolvedVersion = "0.0.0", Rid = "linux-x64", Architecture = "x64", AcceptedArtifactFormats = ["dotnet-managed-pe-v1"], Capabilities = ["run"], Availability = NotBuilt() });
            processors.Add(new ArtifactProcessorManifest { Id = NotBuiltProcessorId, DisplayName = "Not-built artifact processor", ResolvedVersion = "not-built-version", WorkerId = "artifact-worker-not-built", AcceptsArtifactFormats = ["dotnet-managed-pe-v1"], ProducesArtifactFormats = ["text-v1"], Capabilities = ["il"], Availability = NotBuilt() });
            presets.Add(new ProfilePreset { Id = NotBuiltPresetId, DisplayName = "Not-built preset", LanguageId = LanguageId, ToolchainId = NotBuiltToolchainId, ReferenceSetId = NotBuiltReferenceSetId, DefaultOutputId = OutputId, DefaultRuntimeId = NotBuiltRuntimeId, Availability = NotBuilt() });
        }

        return new CatalogDocument
        {
            SchemaVersion = 1,
            Revision = "test-revision",
            ReleaseId = "test-release",
            Languages =
            [
                new LanguageManifest { Id = LanguageId, DisplayName = "Test language", MonacoLanguageId = "plaintext", Extensions = [".test"], DefaultFileName = "Program.test", DefaultSource = "test", DefaultToolchainId = ToolchainId, Capabilities = ["diagnostics"] }
            ],
            Toolchains = toolchains,
            ReferenceSets = referenceSets,
            Runtimes = runtimes,
            ArtifactProcessors = processors,
            Outputs =
            [
                new OutputManifest { Id = OutputId, DisplayName = "Test output", Renderer = "text", RequiresRuntime = false, RequiredCapabilities = [] }
            ],
            Compatibility = [],
            Presets = presets
        };
    }

    private static ComponentAvailability Healthy() => new()
    {
        Installed = true,
        Health = "healthy"
    };

    private static ComponentAvailability NotBuilt() => new()
    {
        Installed = false,
        Health = "not-built",
        Reason = "This component was not included in the release."
    };

    private static ComponentAvailability Unavailable(string reason) => new()
    {
        Installed = true,
        Health = "unavailable",
        Reason = reason
    };

    private static void AssertUnavailable(ComponentAvailability availability, string reason)
    {
        Assert.True(availability.Installed);
        Assert.Equal("unavailable", availability.Health);
        Assert.Equal(reason, availability.Reason);
    }

    private static T Find<T>(IReadOnlyList<T> items, string id) where T : notnull =>
        Assert.Single(items, item => StringComparer.Ordinal.Equals(GetId(item), id));

    private static string GetId<T>(T item) => item switch
    {
        ToolchainManifest value => value.Id,
        ReferenceSetManifest value => value.Id,
        RuntimeManifest value => value.Id,
        ArtifactProcessorManifest value => value.Id,
        ProfilePreset value => value.Id,
        _ => throw new InvalidOperationException($"Unsupported catalog item type '{typeof(T).Name}'.")
    };

    private static Dictionary<string, RuntimeProfileProbeIdentity> EmptyProfiles() => new(StringComparer.Ordinal);

    private static HashSet<string> EmptyIds() => new(StringComparer.Ordinal);

    private static RuntimeProfileProbeIdentity RuntimeIdentity(RuntimeManifest runtime) => new(
        runtime.Id,
        runtime.Family,
        runtime.ResolvedVersion,
        runtime.Rid,
        runtime.Architecture,
        runtime.AcceptedArtifactFormats.ToHashSet(StringComparer.Ordinal),
        runtime.Capabilities.ToHashSet(StringComparer.Ordinal),
        runtime.ProvidedRuntimeFeatureTags.ToHashSet(StringComparer.Ordinal),
        runtime.ProvidedMetadataFeatureTags.ToHashSet(StringComparer.Ordinal),
        runtime.RuntimeCommit,
        runtime.JitVersion,
        runtime.JitCommit,
        runtime.RuntimeImageId,
        runtime.AcceptedRuntimeFamilies.ToHashSet(StringComparer.Ordinal),
            runtime.AcceptedFrameworks
                .Select(static framework => string.Join('|', framework.Name, framework.MinimumVersion ?? string.Empty, framework.MaximumVersion ?? string.Empty, framework.ExactVersion ?? string.Empty))
                .ToHashSet(StringComparer.Ordinal),
        runtime.ContainerIsolationKind,
        runtime.ContainerEnvironmentKind,
        runtime.JitSourceMappingKind);

    private static HttpResponseMessage JsonResponse(string json) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private sealed class FixedHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class FakeGatewayDependencyProbe(IReadOnlyDictionary<string, GatewayDependencyProbeResult>? configuredResults, IReadOnlyDictionary<string, RuntimeProfileProbeIdentity> runtimeProfiles) : IGatewayDependencyProbe
    {
        public Task<GatewayDependencyProbeResult> ProbeAsync(GatewayDependencyTarget target, TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (configuredResults?.TryGetValue(target.Id, out var configured) == true)
                return Task.FromResult(configured);

            IReadOnlyDictionary<string, RuntimeProfileProbeIdentity> profiles =
                target.Kind == GatewayDependencyKind.RuntimeSupervisor
                    ? runtimeProfiles : EmptyProfiles();
            return Task.FromResult(new GatewayDependencyProbeResult(target.Id, target.Kind, true, null, profiles));
        }
    }

    private const string LanguageId = "test-language";
    private const string ToolchainId = "test-toolchain";
    private const string CompilerWorkerId = "compiler-worker";
    private const string ReferenceSetId = "test-reference-set";
    private const string RuntimeId = "test-runtime";
    private const string ProcessorId = "test-processor";
    private const string ArtifactWorkerId = "artifact-worker";
    private const string OutputId = "test-output";
    private const string PresetId = "test-preset";
    private const string NotBuiltToolchainId = "not-built-toolchain";
    private const string NotBuiltReferenceSetId = "not-built-reference-set";
    private const string NotBuiltRuntimeId = "not-built-runtime";
    private const string NotBuiltProcessorId = "not-built-processor";
    private const string NotBuiltPresetId = "not-built-preset";
}
