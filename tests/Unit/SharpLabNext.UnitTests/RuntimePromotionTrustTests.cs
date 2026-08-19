using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.BundleBuilder;
using SharpLabNext.Catalog;
using SharpLabNext.RuntimeProfile.Sdk;

namespace SharpLabNext.UnitTests;

public sealed class RuntimePromotionTrustTests
{
    [Fact]
    public async Task PromotionPlanFinalizerProducesReceiptAcceptedByReleaseTrust()
    {
        using var fixture = new PromotionFixture();
        var inputs = fixture.CreatePromotionWorkflowInputs();

        var context = RuntimePromotionPlanWorkflow.CreateContext(
            inputs.ProfileBytes,
            inputs.PreflightProfileBytes,
            inputs.PlanBytes,
            inputs.PerformancePolicyBytes);
        var result = RuntimePromotionPlanWorkflow.Finalize(
            inputs.ProfileBytes,
            inputs.PreflightProfileBytes,
            inputs.PlanBytes,
            inputs.PerformancePolicyBytes,
            inputs.CapabilityEvidence,
            inputs.PerformanceEvidenceBytes,
            fixture.RequestBinding);

        Assert.Equal(PromotionFixture.ProfileId, context.ProfileId);
        Assert.Equal(inputs.PlanSha256, context.PlanSha256);
        Assert.Equal(PromotionFixture.ProfileId, result.ProfileId);
        Assert.Equal(PromotionFixture.Digest(result.ReceiptBytes), result.ReceiptSha256);
        Assert.Equal(context.Capabilities.Count, result.CapabilityEvidenceSha256.Count);

        fixture.InstallFinalizedReceipt(result.ReceiptBytes);
        var snapshots = await fixture.CaptureAsync();
        Assert.Single(snapshots);
    }

    [Fact]
    public void PromotionPlanFinalizerRejectsEvidenceBoundToAnotherPlan()
    {
        using var fixture = new PromotionFixture();
        var inputs = fixture.CreatePromotionWorkflowInputs();
        var evidence = inputs.CapabilityEvidence.ToDictionary(
            static item => item.Key,
            static item => item.Value,
            StringComparer.Ordinal);
        var run = JsonNode.Parse(evidence["run"])!.AsObject();
        run["producer"]!["planSha256"] = $"sha256:{new string('0', 64)}";
        evidence["run"] = PromotionFixture.JsonBytes(run);

        var exception = Assert.Throws<BundleValidationException>(() =>
            RuntimePromotionPlanWorkflow.Finalize(
                inputs.ProfileBytes,
                inputs.PreflightProfileBytes,
                inputs.PlanBytes,
                inputs.PerformancePolicyBytes,
                evidence,
                inputs.PerformanceEvidenceBytes,
                fixture.RequestBinding));

        Assert.Contains("exact promotion plan bytes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PromotionPlanFinalizerRejectsIncompleteCapabilitySet()
    {
        using var fixture = new PromotionFixture();
        var inputs = fixture.CreatePromotionWorkflowInputs();
        var evidence = inputs.CapabilityEvidence.ToDictionary(
            static item => item.Key,
            static item => item.Value,
            StringComparer.Ordinal);
        Assert.True(evidence.Remove("inspection"));

        var exception = Assert.Throws<BundleValidationException>(() =>
            RuntimePromotionPlanWorkflow.Finalize(
                inputs.ProfileBytes,
                inputs.PreflightProfileBytes,
                inputs.PlanBytes,
                inputs.PerformancePolicyBytes,
                evidence,
                inputs.PerformanceEvidenceBytes,
                fixture.RequestBinding));

        Assert.Contains("exactly one capability document", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PromotionPlanFinalizerRejectsProbeArtifactRequestDrift()
    {
        using var fixture = new PromotionFixture();
        var inputs = fixture.CreatePromotionWorkflowInputs();
        var evidence = inputs.CapabilityEvidence.ToDictionary(
            static item => item.Key,
            static item => item.Value,
            StringComparer.Ordinal);
        var run = JsonNode.Parse(evidence["run"])!.AsObject();
        run["probeArtifact"]!["sourceArtifactSha256"] =
            $"sha256:{new string('0', 64)}";
        evidence["run"] = PromotionFixture.JsonBytes(run);

        var exception = Assert.Throws<BundleValidationException>(() =>
            RuntimePromotionPlanWorkflow.Finalize(
                inputs.ProfileBytes,
                inputs.PreflightProfileBytes,
                inputs.PlanBytes,
                inputs.PerformancePolicyBytes,
                evidence,
                inputs.PerformanceEvidenceBytes,
                fixture.RequestBinding));

        Assert.Contains("canonical probe artifact reference", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PromotionPlanFinalizerRejectsJitMethodFilterRequestDrift()
    {
        using var fixture = new PromotionFixture();
        var inputs = fixture.CreatePromotionWorkflowInputs();
        var evidence = inputs.CapabilityEvidence.ToDictionary(
            static item => item.Key,
            static item => item.Value,
            StringComparer.Ordinal);
        var jit = JsonNode.Parse(evidence["jit-asm"])!.AsObject();
        jit["invocation"]!["methodFilter"] = "SharpLabNext.Preflight:DifferentMethod";
        evidence["jit-asm"] = PromotionFixture.JsonBytes(jit);

        var exception = Assert.Throws<BundleValidationException>(() =>
            RuntimePromotionPlanWorkflow.Finalize(
                inputs.ProfileBytes,
                inputs.PreflightProfileBytes,
                inputs.PlanBytes,
                inputs.PerformancePolicyBytes,
                evidence,
                inputs.PerformanceEvidenceBytes,
                fixture.RequestBinding));

        Assert.Contains("JIT method filter", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PromotionPlanFinalizerRejectsExecutionFlowArtifactRequestDrift()
    {
        using var fixture = new PromotionFixture();
        var inputs = fixture.CreatePromotionWorkflowInputs();
        var evidence = inputs.CapabilityEvidence.ToDictionary(
            static item => item.Key,
            static item => item.Value,
            StringComparer.Ordinal);
        var flow = JsonNode.Parse(evidence["execution-flow"])!.AsObject();
        flow["probeArtifact"]!["artifactSha256"] =
            $"sha256:{new string('0', 64)}";
        evidence["execution-flow"] = PromotionFixture.JsonBytes(flow);

        var exception = Assert.Throws<BundleValidationException>(() =>
            RuntimePromotionPlanWorkflow.Finalize(
                inputs.ProfileBytes,
                inputs.PreflightProfileBytes,
                inputs.PlanBytes,
                inputs.PerformancePolicyBytes,
                evidence,
                inputs.PerformanceEvidenceBytes,
                fixture.RequestBinding));

        Assert.Contains("Execution Flow artifact reference", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PromotionPlanExpandsInstrumentationOnlyInTheImmutablePreflightProfile()
    {
        using var fixture = new PromotionFixture();
        var inputs = fixture.CreatePromotionWorkflowInputs();
        var candidate = JsonNode.Parse(inputs.ProfileBytes)!.AsObject();
        var preflight = JsonNode.Parse(inputs.PreflightProfileBytes)!.AsObject();

        Assert.Equal(
            ["run", "jit-asm"],
            candidate["capabilities"]!.AsArray().Select(static item => item!.GetValue<string>()));
        Assert.Equal(
            ["execution-flow", "inspection", "jit-asm", "run"],
            preflight["capabilities"]!.AsArray().Select(static item => item!.GetValue<string>()));
        Assert.Null(candidate["promotionReceipt"]);
        Assert.Null(preflight["promotionReceipt"]);

        var context = RuntimePromotionPlanWorkflow.CreateContext(
            inputs.ProfileBytes,
            inputs.PreflightProfileBytes,
            inputs.PlanBytes,
            inputs.PerformancePolicyBytes);
        Assert.Equal(
            ["execution-flow", "inspection", "jit-asm", "run"],
            context.Capabilities);
    }

    [Fact]
    public void PromotionPlanAllowsImmutableFrameworkRangeNormalization()
    {
        using var fixture = new PromotionFixture();
        var inputs = fixture.CreatePromotionWorkflowInputs();
        var candidate = JsonNode.Parse(inputs.ProfileBytes)!.AsObject();
        candidate["acceptedFrameworks"] = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "Microsoft.NETCore.App",
                ["minimumVersion"] = "10.0.0",
                ["maximumVersion"] = "10.0.9"
            }
        };
        var preflight = JsonNode.Parse(inputs.PreflightProfileBytes)!.AsObject();
        preflight["acceptedFrameworks"] = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "Microsoft.NETCore.App",
                ["exactVersion"] = "10.0.9"
            }
        };
        var candidateBytes = PromotionFixture.JsonBytes(candidate);
        var preflightBytes = PromotionFixture.JsonBytes(preflight);
        var plan = JsonNode.Parse(inputs.PlanBytes)!.AsObject();
        plan["profileSha256"] = PromotionFixture.Digest(candidateBytes);
        plan["preflightProfile"]!["sha256"] = PromotionFixture.Digest(preflightBytes);

        var context = RuntimePromotionPlanWorkflow.CreateContext(
            candidateBytes,
            preflightBytes,
            PromotionFixture.JsonBytes(plan),
            inputs.PerformancePolicyBytes);

        Assert.Equal(PromotionFixture.ProfileId, context.ProfileId);
    }

    [Fact]
    public void PromotionPlanRejectsInstrumentationLeakingIntoBlockedCandidateProfile()
    {
        using var fixture = new PromotionFixture();
        var inputs = fixture.CreatePromotionWorkflowInputs();
        var candidate = JsonNode.Parse(inputs.ProfileBytes)!.AsObject();
        candidate["capabilities"] = JsonNode.Parse(inputs.PreflightProfileBytes)!["capabilities"]!.DeepClone();
        var candidateBytes = PromotionFixture.JsonBytes(candidate);
        var plan = JsonNode.Parse(inputs.PlanBytes)!.AsObject();
        plan["profileSha256"] = PromotionFixture.Digest(candidateBytes);

        var exception = Assert.Throws<BundleValidationException>(() =>
            RuntimePromotionPlanWorkflow.CreateContext(
                candidateBytes,
                inputs.PreflightProfileBytes,
                PromotionFixture.JsonBytes(plan),
                inputs.PerformancePolicyBytes));

        Assert.Contains("strict non-instrumentation subset", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PromotionBoundRuntimeImageMayBindImplementationRevisionBeforeReleaseRevision()
    {
        const string implementationRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string releaseRevision = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var definition = PromotionImageDefinition();
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RepositorySourceProvenanceResolver.ImageLabel] = implementationRevision,
            ["org.opencontainers.image.revision"] = implementationRevision
        };

        ReleaseBundleBuilder.ValidateInspectionSourceRevision(
            definition,
            labels,
            releaseRevision,
            promotionBoundRuntime: true);

        var ordinary = Assert.Throws<BundleValidationException>(() =>
            ReleaseBundleBuilder.ValidateInspectionSourceRevision(
                definition,
                labels,
                releaseRevision,
                promotionBoundRuntime: false));
        Assert.Contains("source revision label", ordinary.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PromotionBoundRuntimeImageRejectsDisagreeingImplementationRevisionLabels()
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RepositorySourceProvenanceResolver.ImageLabel] =
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            ["org.opencontainers.image.revision"] =
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        };

        var exception = Assert.Throws<BundleValidationException>(() =>
            ReleaseBundleBuilder.ValidateInspectionSourceRevision(
                PromotionImageDefinition(),
                labels,
                "cccccccccccccccccccccccccccccccccccccccc",
                promotionBoundRuntime: true));

        Assert.Contains("canonical, matching implementation revision", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceClosureAcceptsExactReceiptDerivedImplementationToReleaseTransaction()
    {
        using var fixture = new PromotionFixture();
        var inputs = fixture.CreatePromotionWorkflowInputs();
        var finalized = RuntimePromotionPlanWorkflow.Finalize(
            inputs.ProfileBytes,
            inputs.PreflightProfileBytes,
            inputs.PlanBytes,
            inputs.PerformancePolicyBytes,
            inputs.CapabilityEvidence,
            inputs.PerformanceEvidenceBytes,
            fixture.RequestBinding);
        fixture.InstallFinalizedReceipt(finalized.ReceiptBytes);
        fixture.WriteSourceClosureMaterial(inputs);
        var trust = await fixture.CaptureAsync();
        var inspector = new PromotionSourceInspector(
            isAncestor: true,
            fixture.SourceClosurePaths().Select(static path =>
                new RuntimePromotionSourceChange("M", path)).ToArray());
        var releaseSource = new RepositorySourceProvenance(
            "abababababababababababababababababababab",
            "abababababababababababababababababababab",
            IsDirty: false,
            IsVerified: true,
            DevelopmentOverrideUsed: false);

        var snapshot = await RuntimePromotionSourceClosure.CaptureAsync(
            fixture.Root,
            releaseSource,
            trust,
            inspector,
            TestContext.Current.CancellationToken);
        Assert.NotNull(snapshot);
        Assert.Equal(trust[0].BuildSourceRevision, snapshot.BuildSourceRevision);
        Assert.Equal(releaseSource.Revision, snapshot.ReleaseSourceRevision);
        Assert.Equal(fixture.SourceClosurePaths().Order(StringComparer.Ordinal),
            snapshot.Files.Select(static file => file.RelativePath));

        await RuntimePromotionSourceClosure.RevalidateAsync(
            fixture.Root,
            snapshot,
            inspector,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, inspector.AncestryChecks);
        Assert.Equal(2, inspector.DiffChecks);
    }

    [Fact]
    public async Task SourceClosureRejectsSourceChangeOutsideReceiptDerivedTransaction()
    {
        using var fixture = new PromotionFixture();
        var inputs = fixture.CreatePromotionWorkflowInputs();
        var finalized = RuntimePromotionPlanWorkflow.Finalize(
            inputs.ProfileBytes,
            inputs.PreflightProfileBytes,
            inputs.PlanBytes,
            inputs.PerformancePolicyBytes,
            inputs.CapabilityEvidence,
            inputs.PerformanceEvidenceBytes,
            fixture.RequestBinding);
        fixture.InstallFinalizedReceipt(finalized.ReceiptBytes);
        fixture.WriteSourceClosureMaterial(inputs);
        var trust = await fixture.CaptureAsync();
        var changes = fixture.SourceClosurePaths()
            .Select(static path => new RuntimePromotionSourceChange("M", path))
            .Append(new RuntimePromotionSourceChange("M", "src/RuntimeJobs/Unexpected.cs"))
            .ToArray();

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() =>
            RuntimePromotionSourceClosure.CaptureAsync(
                fixture.Root,
                new RepositorySourceProvenance(
                    "abababababababababababababababababababab",
                    "abababababababababababababababababababab",
                    IsDirty: false,
                    IsVerified: true,
                    DevelopmentOverrideUsed: false),
                trust,
                new PromotionSourceInspector(isAncestor: true, changes),
                TestContext.Current.CancellationToken));

        Assert.Contains("exact verified transaction union", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Unexpected.cs", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceClosureRejectsTransactionMissingCandidateProfile()
    {
        using var fixture = new PromotionFixture();
        var inputs = fixture.CreatePromotionWorkflowInputs();
        var finalized = RuntimePromotionPlanWorkflow.Finalize(
            inputs.ProfileBytes,
            inputs.PreflightProfileBytes,
            inputs.PlanBytes,
            inputs.PerformancePolicyBytes,
            inputs.CapabilityEvidence,
            inputs.PerformanceEvidenceBytes,
            fixture.RequestBinding);
        fixture.InstallFinalizedReceipt(finalized.ReceiptBytes);
        fixture.WriteSourceClosureMaterial(inputs);
        var trust = await fixture.CaptureAsync();
        var candidatePath = $"profiles/runtimes/candidates/{PromotionFixture.ProfileId}.json";
        var changes = fixture.SourceClosurePaths()
            .Where(path => !StringComparer.Ordinal.Equals(path, candidatePath))
            .Select(static path => new RuntimePromotionSourceChange("M", path))
            .ToArray();

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() =>
            RuntimePromotionSourceClosure.CaptureAsync(
                fixture.Root,
                ReleaseSource(),
                trust,
                new PromotionSourceInspector(isAncestor: true, changes),
                TestContext.Current.CancellationToken));

        Assert.Contains("exact verified transaction union", exception.Message, StringComparison.Ordinal);
        Assert.Contains(candidatePath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceClosureRejectsExtraCandidateProfile()
    {
        using var fixture = new PromotionFixture();
        var inputs = fixture.CreatePromotionWorkflowInputs();
        var finalized = RuntimePromotionPlanWorkflow.Finalize(
            inputs.ProfileBytes,
            inputs.PreflightProfileBytes,
            inputs.PlanBytes,
            inputs.PerformancePolicyBytes,
            inputs.CapabilityEvidence,
            inputs.PerformanceEvidenceBytes,
            fixture.RequestBinding);
        fixture.InstallFinalizedReceipt(finalized.ReceiptBytes);
        fixture.WriteSourceClosureMaterial(inputs);
        var trust = await fixture.CaptureAsync();
        const string extraCandidatePath = "profiles/runtimes/candidates/unbound.json";
        var changes = fixture.SourceClosurePaths()
            .Select(static path => new RuntimePromotionSourceChange("M", path))
            .Append(new RuntimePromotionSourceChange("A", extraCandidatePath))
            .ToArray();

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() =>
            RuntimePromotionSourceClosure.CaptureAsync(
                fixture.Root,
                ReleaseSource(),
                trust,
                new PromotionSourceInspector(isAncestor: true, changes),
                TestContext.Current.CancellationToken));

        Assert.Contains("exact verified transaction union", exception.Message, StringComparison.Ordinal);
        Assert.Contains(extraCandidatePath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceClosureRevalidationRejectsCandidateProfileByteMutation()
    {
        using var fixture = new PromotionFixture();
        var inputs = fixture.CreatePromotionWorkflowInputs();
        var finalized = RuntimePromotionPlanWorkflow.Finalize(
            inputs.ProfileBytes,
            inputs.PreflightProfileBytes,
            inputs.PlanBytes,
            inputs.PerformancePolicyBytes,
            inputs.CapabilityEvidence,
            inputs.PerformanceEvidenceBytes,
            fixture.RequestBinding);
        fixture.InstallFinalizedReceipt(finalized.ReceiptBytes);
        fixture.WriteSourceClosureMaterial(inputs);
        var trust = await fixture.CaptureAsync();
        var changes = fixture.SourceClosurePaths()
            .Select(static path => new RuntimePromotionSourceChange("M", path))
            .ToArray();
        var inspector = new PromotionSourceInspector(isAncestor: true, changes);
        var snapshot = await RuntimePromotionSourceClosure.CaptureAsync(
            fixture.Root,
            ReleaseSource(),
            trust,
            inspector,
            TestContext.Current.CancellationToken);
        Assert.NotNull(snapshot);

        File.AppendAllText(
            Path.Combine(
                fixture.Root,
                "profiles",
                "runtimes",
                "candidates",
                $"{PromotionFixture.ProfileId}.json"),
            " ");

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() =>
            RuntimePromotionSourceClosure.RevalidateAsync(
                fixture.Root,
                snapshot,
                inspector,
                TestContext.Current.CancellationToken));

        Assert.Contains("changed before release finalization", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"profiles/runtimes/candidates/{PromotionFixture.ProfileId}.json", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceClosureRejectsAllowedPlanPathWhoseBytesDoNotBindTheReceipt()
    {
        using var fixture = new PromotionFixture();
        var inputs = fixture.CreatePromotionWorkflowInputs();
        var finalized = RuntimePromotionPlanWorkflow.Finalize(
            inputs.ProfileBytes,
            inputs.PreflightProfileBytes,
            inputs.PlanBytes,
            inputs.PerformancePolicyBytes,
            inputs.CapabilityEvidence,
            inputs.PerformanceEvidenceBytes,
            fixture.RequestBinding);
        fixture.InstallFinalizedReceipt(finalized.ReceiptBytes);
        fixture.WriteSourceClosureMaterial(inputs);
        var trust = await fixture.CaptureAsync();
        File.AppendAllText(
            Path.Combine(
                fixture.Root,
                "profiles",
                "runtime-promotion-plans",
                $"{PromotionFixture.ProfileId}.json"),
            " ");

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() =>
            RuntimePromotionSourceClosure.CaptureAsync(
                fixture.Root,
                new RepositorySourceProvenance(
                    "abababababababababababababababababababab",
                    "abababababababababababababababababababab",
                    IsDirty: false,
                    IsVerified: true,
                    DevelopmentOverrideUsed: false),
                trust,
                new PromotionSourceInspector(
                    isAncestor: true,
                    fixture.SourceClosurePaths().Select(static path =>
                        new RuntimePromotionSourceChange("M", path)).ToArray()),
                TestContext.Current.CancellationToken));

        Assert.Contains("plan does not bind its receipt", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceClosureRejectsReleaseBuiltAtTheImplementationRevision()
    {
        using var fixture = new PromotionFixture();
        var trust = await fixture.CaptureAsync();

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() =>
            RuntimePromotionSourceClosure.CaptureAsync(
                fixture.Root,
                new RepositorySourceProvenance(
                    trust[0].BuildSourceRevision,
                    trust[0].BuildSourceRevision,
                    IsDirty: false,
                    IsVerified: true,
                    DevelopmentOverrideUsed: false),
                trust,
                new PromotionSourceInspector(isAncestor: true, []),
                TestContext.Current.CancellationToken));

        Assert.Contains("must be distinct commits", exception.Message, StringComparison.Ordinal);
    }

    private static DeploymentImageDefinition PromotionImageDefinition() => new()
    {
        Id = PromotionFixture.ProfileId,
        Repository = "registry.example/sharplabnext/runtime-dotnet-10",
        ImmutableReference = PromotionFixture.ImmutableReference,
        RuntimeId = PromotionFixture.ProfileId
    };

    private static RepositorySourceProvenance ReleaseSource() => new(
        "abababababababababababababababababababab",
        "abababababababababababababababababababab",
        IsDirty: false,
        IsVerified: true,
        DevelopmentOverrideUsed: false);

    [Theory]
    [InlineData("/opt/sharplabnext/target-dotnet/shared/Microsoft.NETCore.App/10.0.10/../libclrjit.so")]
    [InlineData("/opt/sharplabnext/target-dotnet//shared/Microsoft.NETCore.App/10.0.10/libclrjit.so")]
    [InlineData("/opt/sharplabnext/target-dotnet/./shared/Microsoft.NETCore.App/10.0.10/libclrjit.so")]
    [InlineData("/opt/sharplabnext/target-dotnet\\shared\\Microsoft.NETCore.App\\10.0.10\\libclrjit.so")]
    public void PromotionPlanRejectsNonCanonicalJitLibraryPath(string jitLibraryPath)
    {
        using var fixture = new PromotionFixture();
        var inputs = fixture.CreatePromotionWorkflowInputs();
        var plan = JsonNode.Parse(inputs.PlanBytes)!.AsObject();
        plan["jitLibraryPath"] = jitLibraryPath;

        var exception = Assert.Throws<BundleValidationException>(() =>
            RuntimePromotionPlanWorkflow.CreateContext(
                inputs.ProfileBytes,
                inputs.PreflightProfileBytes,
                PromotionFixture.JsonBytes(plan),
                inputs.PerformancePolicyBytes));

        Assert.Contains("JIT library path", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("image")]
    [InlineData("runtimeImageId")]
    public void PromotionPlanRejectsPreflightImageIdentityThatDiffersFromPlan(string field)
    {
        using var fixture = new PromotionFixture();
        var inputs = fixture.CreatePromotionWorkflowInputs();
        var preflight = JsonNode.Parse(inputs.PreflightProfileBytes)!.AsObject();
        preflight[field] = field == "image"
            ? $"registry.example/other@sha256:{new string('8', 64)}"
            : $"sha256:{new string('9', 64)}";
        var preflightBytes = PromotionFixture.JsonBytes(preflight);
        var plan = JsonNode.Parse(inputs.PlanBytes)!.AsObject();
        plan["preflightProfile"]!["sha256"] = PromotionFixture.Digest(preflightBytes);

        var exception = Assert.Throws<BundleValidationException>(() =>
            RuntimePromotionPlanWorkflow.CreateContext(
                inputs.ProfileBytes,
                preflightBytes,
                PromotionFixture.JsonBytes(plan),
                inputs.PerformancePolicyBytes));

        Assert.Contains("immutable preflight image", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RuntimeContainerExecutionUsers.Root)]
    [InlineData(RuntimeContainerExecutionUsers.NonRoot)]
    public void CapabilityEvidenceSandboxUserBindsExactProfileExecutionIdentity(string executionUser)
    {
        using var fixture = new PromotionFixture();

        fixture.ValidateRunEvidenceSandboxUser(
            profileExecutionUser: executionUser,
            receiptPlatform: "wine",
            evidenceUser: executionUser);
    }

    [Fact]
    public void CapabilityEvidenceRejectsSandboxUserThatDiffersFromProfileExecutionIdentity()
    {
        using var fixture = new PromotionFixture();

        var exception = Assert.Throws<BundleValidationException>(() =>
            fixture.ValidateRunEvidenceSandboxUser(
                profileExecutionUser: RuntimeContainerExecutionUsers.NonRoot,
                receiptPlatform: "wine",
                evidenceUser: RuntimeContainerExecutionUsers.Root));

        Assert.Contains("sandbox user", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CapabilityEvidenceRejectsUnapprovedSupervisorSandboxPolicy()
    {
        using var fixture = new PromotionFixture();

        var exception = Assert.Throws<BundleValidationException>(() =>
            fixture.ValidateRunEvidenceSandboxPolicy(
                supervisorPolicyId: "runtime-job-weak",
                seccompSha256: "sha256:" + new string('8', 64)));

        Assert.Contains("Supervisor sandbox policy", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mono", "mono-6.12-linux-x64")]
    [InlineData("netfx-clr-wine", "wine-netfx48-linux-x64")]
    public void TargetRuntimeRunnerExecutableIsAcceptedForOperatorFamilies(string family, string profileId)
    {
        const string helperPath = "/opt/sharplabnext/SharpLabNext.TargetRuntimeRunner.exe";
        var profile = new RuntimeProfileDefinition
        {
            Id = profileId,
            Family = family,
            Layout = new RuntimeImageLayout { RunnerAssemblyPath = helperPath },
            Operations = new RuntimeProfileOperations
            {
                Run = new RuntimeRunOperationDefinition
                {
                    ImplementationId = RuntimeOperationImplementationIds.TargetRuntimeRunner,
                    Command = new RuntimeOperationCommandDefinition
                    {
                        Executable = family == "mono" ? "/usr/bin/mono" : "/usr/bin/wine",
                        Argv = [helperPath, "run", RuntimeOperationPlaceholders.EntryAssembly]
                    }
                }
            }
        };
        var receipt = new RuntimePromotionReceiptDocument
        {
            SchemaVersion = 2,
            PlanSha256 = $"sha256:{new string('0', 64)}",
            ProfileId = profileId,
            MatrixTargetId = profileId,
            Platform = family == "mono" ? "mono" : "framework",
            Family = family,
            ResolvedVersion = "test",
            Image = new RuntimePromotionImageIdentity
            {
                Reference = $"registry.example/runtime@sha256:{new string('1', 64)}",
                ImageId = $"sha256:{new string('2', 64)}",
                SizeBytes = 1
            },
            ComponentIdentity = new RuntimePromotionComponentIdentity
            {
                SourceUri = $"docker://registry.example/operator@sha256:{new string('3', 64)}",
                SourceDigest = $"sha256:{new string('3', 64)}"
            },
            RuntimeIdentity = new RuntimePromotionRuntimeIdentity
            {
                RuntimeCommit = "not-applicable",
                JitVersion = "not-applicable",
                JitCommit = "not-applicable"
            },
            Operations = new RuntimePromotionOperations
            {
                Run = new RuntimePromotionOperationHelper
                {
                    Implementation = RuntimeOperationImplementationIds.TargetRuntimeRunner,
                    AssemblyPath = helperPath,
                    AssemblySha256 = $"sha256:{new string('4', 64)}"
                }
            },
            Performance = new RuntimePromotionPerformanceBinding
            {
                Result = "passed",
                PolicyId = "test",
                PolicyPath = "profiles/runtime-performance-policies/test.json",
                PolicySha256 = $"sha256:{new string('5', 64)}",
                EvidencePath = $"profiles/runtime-promotion-evidence/{profileId}/performance.json",
                EvidenceSha256 = $"sha256:{new string('6', 64)}"
            },
            SourceRevision = new string('7', 40),
            Checks = []
        };

        var file = Assert.Single(RuntimePromotionTrust.ValidateOperationBindings(profile, receipt));
        Assert.Equal(helperPath, file.Path);
    }

    [Fact]
    public async Task CapturesReceiptAndRevalidatesEvidenceImageAndHelpers()
    {
        using var fixture = new PromotionFixture();
        var snapshots = await fixture.CaptureAsync();

        await RuntimePromotionTrust.RevalidateAsync(
            fixture.Root,
            snapshots,
            fixture.Docker,
            TestContext.Current.CancellationToken);

        var snapshot = Assert.Single(snapshots);
        Assert.Equal(PromotionFixture.ProfileId, snapshot.RuntimeId);
        Assert.Equal(PromotionFixture.ImmutableReference, Assert.Single(fixture.Docker.InspectedReferences));
        Assert.Equal(6, fixture.Docker.InspectedFiles.Count);
        Assert.All(fixture.Docker.InspectedFiles, static item =>
            Assert.Equal(PromotionFixture.ImageId, item.ImageId));
        Assert.Contains(fixture.Docker.InspectedFiles, static item =>
            item.Path == "/opt/sharplabnext/SharpLabNext.Runner.dll");
        Assert.Contains(fixture.Docker.InspectedFiles, static item =>
            item.Path == "/opt/sharplabnext/SharpLabNext.JitInspector.dll");
        Assert.Contains(fixture.Docker.InspectedFiles, static item =>
            item.Path == "/opt/sharplabnext/SharpLabNext.JitProfiler.so");
        Assert.Contains(fixture.Docker.InspectedFiles, static item =>
            item.Path == "/opt/sharplabnext/target-dotnet/dotnet");
        Assert.Contains(fixture.Docker.InspectedFiles, static item =>
            item.Path == "/opt/sharplabnext/SharpLab.Runtime.dll");
        Assert.Contains(fixture.Docker.InspectedFiles, static item =>
            item.Path.EndsWith("/libclrjit.so", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RevalidationRejectsEvidenceChangedAfterCapture()
    {
        using var fixture = new PromotionFixture();
        var snapshots = await fixture.CaptureAsync();
        var evidence = Assert.Single(snapshots).Evidence[0];
        await File.AppendAllTextAsync(
            Path.Combine(fixture.Root, evidence.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
            "changed",
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() =>
            RuntimePromotionTrust.RevalidateAsync(
                fixture.Root,
                snapshots,
                fixture.Docker,
                TestContext.Current.CancellationToken));

        Assert.Contains("changed before release finalization", exception.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Docker.InspectedReferences);
    }

    [Fact]
    public async Task RevalidationRejectsImmutableReferenceResolvingToAnotherImage()
    {
        using var fixture = new PromotionFixture();
        var snapshots = await fixture.CaptureAsync();
        fixture.Docker.Inspection = fixture.Docker.Inspection with
        {
            ImageId = $"sha256:{new string('9', 64)}"
        };

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() =>
            RuntimePromotionTrust.RevalidateAsync(
                fixture.Root,
                snapshots,
                fixture.Docker,
                TestContext.Current.CancellationToken));

        Assert.Contains("no longer resolves", exception.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Docker.InspectedFiles);
    }

    [Fact]
    public async Task RevalidationRejectsHelperBytesThatDisagreeWithReceipt()
    {
        using var fixture = new PromotionFixture();
        var snapshots = await fixture.CaptureAsync();
        fixture.Docker.Files["/opt/sharplabnext/SharpLabNext.JitProfiler.so"] =
            new DockerImageFileInspection($"sha256:{new string('9', 64)}", 1);

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() =>
            RuntimePromotionTrust.RevalidateAsync(
                fixture.Root,
                snapshots,
                fixture.Docker,
                TestContext.Current.CancellationToken));

        Assert.Contains("changed before release finalization", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRejectsReceiptIdentityThatDisagreesWithReleaseLock()
    {
        using var fixture = new PromotionFixture();
        fixture.Component = fixture.Component with
        {
            SourceUri = "https://example.invalid/a-different-runtime.tar.gz"
        };

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() => fixture.CaptureAsync());

        Assert.Contains("component source URI", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRejectsUnknownReceiptFields()
    {
        using var fixture = new PromotionFixture();
        fixture.UpdateReceipt(static receipt => receipt["unreviewedField"] = true);

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() => fixture.CaptureAsync());

        Assert.Contains("invalid JSON", exception.Message, StringComparison.Ordinal);
        Assert.Contains("unreviewedField", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRejectsUnpairedProfilerPathAndDigest()
    {
        using var fixture = new PromotionFixture();
        fixture.UpdateReceipt(static receipt =>
        {
            var operations = receipt["operations"]!.AsObject();
            operations["jit"]!.AsObject().Remove("profilerSha256");
        });

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() => fixture.CaptureAsync());

        Assert.Contains("profiler path and digest", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRejectsDuplicateCapabilityChecks()
    {
        using var fixture = new PromotionFixture();
        fixture.UpdateReceipt(static receipt =>
        {
            var checks = receipt["checks"]!.AsArray();
            checks.RemoveAt(checks.Count - 1);
            checks.Add(checks[0]!.DeepClone());
        });

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() => fixture.CaptureAsync());

        Assert.Contains("do not exactly cover", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRejectsPerformanceEvidenceWithoutRequiredMappingScenario()
    {
        using var fixture = new PromotionFixture();
        fixture.UpdatePerformanceEvidence(static evidence =>
            evidence["scenarios"]!.AsObject().Remove("mapping"));

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() => fixture.CaptureAsync());

        Assert.Contains("invalid scenario set", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRejectsPerformanceEvidenceWithInsufficientSamples()
    {
        using var fixture = new PromotionFixture();
        fixture.UpdatePerformanceEvidence(static evidence =>
            evidence["scenarios"]!["run"]!["cold"]!.AsArray().RemoveAt(0));

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() => fixture.CaptureAsync());

        Assert.Contains("exactly 3 samples", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRejectsPerformanceEvidenceReusingOperationIdAcrossScenarios()
    {
        using var fixture = new PromotionFixture();
        fixture.UpdatePerformanceEvidence(static evidence =>
        {
            var runOperationId = evidence["scenarios"]!["run"]!["cold"]![0]!["operationId"]!.GetValue<string>();
            evidence["scenarios"]!["jit"]!["cold"]![0]!["operationId"] = runOperationId;
        });

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() => fixture.CaptureAsync());

        Assert.Contains("reuses an operation ID", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRecomputesAndRejectsOverBudgetPerformanceP95()
    {
        using var fixture = new PromotionFixture();
        fixture.UpdatePerformanceEvidence(static evidence =>
        {
            foreach (var sample in evidence["scenarios"]!["run"]!["cold"]!.AsArray())
                sample!["latencyMilliseconds"] = 40000;
        });

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() => fixture.CaptureAsync());

        Assert.Contains("exceeds its P95 budget", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRejectsReceiptImageSizeThatDisagreesWithDocker()
    {
        using var fixture = new PromotionFixture();
        fixture.UpdateReceipt(static receipt =>
            receipt["image"]!.AsObject()["sizeBytes"] = 536870913);

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() => fixture.CaptureAsync());

        Assert.Contains("image size does not match", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevalidationRejectsImmutableImageSizeDrift()
    {
        using var fixture = new PromotionFixture();
        var snapshots = await fixture.CaptureAsync();
        fixture.Docker.Inspection = fixture.Docker.Inspection with { SizeBytes = 536870913 };

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() =>
            RuntimePromotionTrust.RevalidateAsync(
                fixture.Root,
                snapshots,
                fixture.Docker,
                TestContext.Current.CancellationToken));

        Assert.Contains("image size changed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevalidationRejectsPerformancePolicyDrift()
    {
        using var fixture = new PromotionFixture();
        var snapshots = await fixture.CaptureAsync();
        await File.AppendAllTextAsync(
            fixture.PerformancePolicyPath,
            "changed",
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() =>
            RuntimePromotionTrust.RevalidateAsync(
                fixture.Root,
                snapshots,
                fixture.Docker,
                TestContext.Current.CancellationToken));

        Assert.Contains("performance policy changed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRejectsOversizedRetainedEvidence()
    {
        using var fixture = new PromotionFixture();
        fixture.ReplaceEvidence("run", new byte[1024 * 1024 + 1]);

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() => fixture.CaptureAsync());

        Assert.Contains("exceeds the 1048576-byte limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRejectsMinimalCapabilityEvidence()
    {
        using var fixture = new PromotionFixture();
        fixture.ReplaceEvidence(
            "run",
            Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"profileId\":\"dotnet-10-linux-x64\",\"capability\":\"run\",\"result\":\"passed\"}\n"));

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() => fixture.CaptureAsync());

        Assert.Contains("evidence is invalid JSON", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRejectsJitEvidenceWithoutJitLibrary()
    {
        using var fixture = new PromotionFixture();
        fixture.UpdateCapabilityEvidence("jit-asm", static evidence =>
        {
            var artifacts = evidence["artifacts"]!.AsArray();
            var jitLibrary = artifacts.Single(node =>
                node!["role"]!.GetValue<string>() == "jit-library");
            artifacts.Remove(jitLibrary);
        });

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() => fixture.CaptureAsync());

        Assert.Contains("has no jit-library artifact", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRejectsJitEvidenceWithSingleSourceRange()
    {
        using var fixture = new PromotionFixture();
        fixture.UpdateCapabilityEvidence("jit-asm", static evidence =>
        {
            var jit = evidence["jit"]!.AsObject();
            jit["methods"]![0]!["sourceRanges"]!.AsArray().RemoveAt(1);
            jit["mapping"]!["rangeCount"] = 1;
            jit["mapping"]!["distinctSourceRangeCount"] = 1;
        });

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() => fixture.CaptureAsync());

        Assert.Contains("lacks multiple PDB-matched source ranges", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRejectsCapabilityEvidenceWithoutCompleteProcessCleanup()
    {
        using var fixture = new PromotionFixture();
        fixture.UpdateCapabilityEvidence("run", static evidence =>
            evidence["lifecycle"]!["timeout"]!["processTreeRemoved"] = false);

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() => fixture.CaptureAsync());

        Assert.Contains("complete cleanup", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRejectsExplicitNullCapabilityFieldsWithoutEscapingValidation()
    {
        using var fixture = new PromotionFixture();
        fixture.UpdateCapabilityEvidence("run", static evidence =>
            evidence["run"]!["expectedStdoutMarker"] = null);

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() => fixture.CaptureAsync());

        Assert.Contains("run evidence cannot contain explicit JSON null values", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRejectsExplicitNullReceiptFieldsBeforeDeserialization()
    {
        using var fixture = new PromotionFixture();
        fixture.UpdateReceipt(static receipt =>
            receipt["componentIdentity"]!["sourceUri"] = null);

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() => fixture.CaptureAsync());

        Assert.Contains("promotion receipt cannot contain explicit JSON null values", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRejectsExplicitNullPerformanceEvidenceBeforeDeserialization()
    {
        using var fixture = new PromotionFixture();
        fixture.UpdatePerformanceEvidence(static evidence =>
            evidence["scenarios"]!["run"]!["cold"]![0]!["peakMemoryBytes"] = null);

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() => fixture.CaptureAsync());

        Assert.Contains("performance evidence cannot contain explicit JSON null values", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRejectsExplicitNullPerformancePolicyBeforeDeserialization()
    {
        using var fixture = new PromotionFixture();
        fixture.UpdatePerformancePolicy(static policy =>
            policy["scenarios"]!["run"]!["cold"]!["maximumP95LatencyMilliseconds"] = null);

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() => fixture.CaptureAsync());

        Assert.Contains("performance policy cannot contain explicit JSON null values", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/workspace/../SharpLabNext.Preflight.pdb")]
    [InlineData("Z:\\workspace\\..\\SharpLabNext.Preflight.pdb")]
    public async Task CaptureRejectsPdbPathsWithDotSegments(string pdbPath)
    {
        using var fixture = new PromotionFixture();
        fixture.UpdateCapabilityEvidence("jit-asm", evidence =>
            evidence["jit"]!["pdb"]!["path"] = pdbPath);

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() => fixture.CaptureAsync());

        Assert.Contains("PDB identity is invalid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRejectsSubstitutedExecutableHostArtifact()
    {
        using var fixture = new PromotionFixture();
        fixture.UpdateCapabilityEvidence("run", static evidence =>
        {
            var runtimeHost = evidence["artifacts"]!.AsArray().Single(node =>
                node!["role"]!.GetValue<string>() == "runtime-host");
            runtimeHost!["path"] = PromotionFixture.RuntimeHostPath + ".substituted";
        });

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() => fixture.CaptureAsync());

        Assert.Contains("runtime-host artifact does not match", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRejectsRepeatedImagePathMetadataDrift()
    {
        using var fixture = new PromotionFixture();
        fixture.UpdateCapabilityEvidence("inspection", static evidence =>
        {
            var runtimeHost = evidence["artifacts"]!.AsArray().Single(node =>
                node!["role"]!.GetValue<string>() == "runtime-host");
            runtimeHost!["sizeBytes"] = 2;
        });

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() => fixture.CaptureAsync());

        Assert.Contains("assigns conflicting path, byte, role, format, or architecture identities", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRejectsCommandAndSecurityPolicyDrift()
    {
        using var commandFixture = new PromotionFixture();
        commandFixture.UpdateCapabilityEvidence("run", static evidence =>
            evidence["invocation"]!["command"]![0] = "/opt/sharplabnext/substituted-dotnet");

        var commandException = await Assert.ThrowsAsync<BundleValidationException>(() =>
            commandFixture.CaptureAsync());
        Assert.Contains("command does not match", commandException.Message, StringComparison.Ordinal);

        using var policyFixture = new PromotionFixture();
        policyFixture.UpdateCapabilityEvidence("run", static evidence =>
            evidence["sandbox"]!["deadlineMilliseconds"] = 10_001);

        var policyException = await Assert.ThrowsAsync<BundleValidationException>(() =>
            policyFixture.CaptureAsync());
        Assert.Contains("resource limits do not match the selected security policy", policyException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevalidationRejectsCapabilityArtifactSizeDrift()
    {
        using var fixture = new PromotionFixture();
        var snapshots = await fixture.CaptureAsync();
        fixture.Docker.Files[PromotionFixture.RuntimeHostPath] =
            new DockerImageFileInspection(PromotionFixture.RuntimeHostDigest, 2);

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() =>
            RuntimePromotionTrust.RevalidateAsync(
                fixture.Root,
                snapshots,
                fixture.Docker,
                TestContext.Current.CancellationToken));

        Assert.Contains("size changed before release finalization", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAndJitOnlyCoreClrPromotionMayOmitIncompatibleSupportAssembly()
    {
        using var fixture = new PromotionFixture(
            includeInstrumentation: false,
            includeSupportAssembly: false);

        var snapshots = await fixture.CaptureAsync();
        await RuntimePromotionTrust.RevalidateAsync(
            fixture.Root,
            snapshots,
            fixture.Docker,
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(fixture.Docker.InspectedFiles, static item =>
            item.Path == "/opt/sharplabnext/SharpLab.Runtime.dll");
    }

    [Fact]
    public async Task InstrumentationPromotionRequiresSupportAssembly()
    {
        using var fixture = new PromotionFixture(includeSupportAssembly: false);

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() => fixture.CaptureAsync());

        Assert.Contains("for its instrumentation capabilities", exception.Message, StringComparison.Ordinal);
    }

    private sealed class PromotionFixture : IDisposable
    {
        private static readonly JsonSerializerOptions ProfileJsonOptions =
            new(JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
        private static int PerformanceSampleSequence;
        private readonly bool _includeSupportAssembly;
        public const string ProfileId = "dotnet-10-linux-x64";
        public const string ImmutableReference =
            "registry.example/sharplabnext/runtime-dotnet-10@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public const string ImageId =
            "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string SourceRevision = "cccccccccccccccccccccccccccccccccccccccc";
        private const string ReleaseRevision = "abababababababababababababababababababab";
        private const string RuntimeCommit = "dddddddddddddddddddddddddddddddddddddddd";
        private const string RuntimeVersion = "10.0.9";
        private const string ComponentSourceUri =
            "https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.9/dotnet-runtime-10.0.9-linux-x64.tar.gz";
        private const string ComponentSha512 =
            "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        private const string RunnerDigest =
            "sha256:1111111111111111111111111111111111111111111111111111111111111111";
        private const string JitInspectorDigest =
            "sha256:2222222222222222222222222222222222222222222222222222222222222222";
        private const string ProfilerDigest =
            "sha256:3333333333333333333333333333333333333333333333333333333333333333";
        public const string RuntimeHostDigest =
            "sha256:4444444444444444444444444444444444444444444444444444444444444444";
        private const string SupportAssemblyDigest =
            "sha256:5555555555555555555555555555555555555555555555555555555555555555";
        private const string JitLibraryDigest =
            "sha256:6666666666666666666666666666666666666666666666666666666666666666";
        public const string RuntimeHostPath = "/opt/sharplabnext/target-dotnet/dotnet";
        private const string SupportAssemblyPath = "/opt/sharplabnext/SharpLab.Runtime.dll";
        private const string JitLibraryPath =
            "/opt/sharplabnext/target-dotnet/shared/Microsoft.NETCore.App/10.0.9/libclrjit.so";
        private const string EntryAssemblyPath = "/workspace/SharpLabNext.Preflight.dll";
        private const string JitMethodFilter = "SharpLabNext.Preflight:MultipleSequencePoints";
        private const string PerformancePolicyId = "runtime-image-linux-x64-v1";
        private static readonly string PlanSha256 = $"sha256:{new string('f', 64)}";
        private static readonly string PreflightProfileSha256 = $"sha256:{new string('8', 64)}";

        public PromotionFixture(
            bool includeInstrumentation = true,
            bool includeSupportAssembly = true)
        {
            _includeSupportAssembly = includeSupportAssembly;
            Root = Path.Combine(Path.GetTempPath(), $"sharplabnext-runtime-promotion-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(Root, "profiles", "runtime-promotion-receipts"));
            Directory.CreateDirectory(Path.Combine(Root, "profiles", "runtime-promotion-evidence", ProfileId));
            Directory.CreateDirectory(Path.Combine(Root, "profiles", "runtime-performance-policies"));

            Profile = new RuntimeProfileDefinition
            {
                Id = ProfileId,
                Image = ImmutableReference,
                Family = "coreclr",
                AcceptedRuntimeFamilies = ["coreclr"],
                AcceptedArtifactFormats = ["dotnet-managed-pe-v1"],
                RuntimeVersion = RuntimeVersion,
                RuntimeCommit = RuntimeCommit,
                JitVersion = RuntimeVersion,
                JitCommit = RuntimeCommit,
                RuntimeImageId = ImageId,
                Capabilities = includeInstrumentation
                    ? ["run", "jit-asm", "inspection", "execution-flow"]
                    : ["run", "jit-asm"],
                Container = new RuntimeContainerDefinition
                {
                    IsolationKind = RuntimeContainerIsolationKinds.Standard,
                    EnvironmentKind = RuntimeContainerEnvironmentKinds.CoreClr,
                    ExecutionUser = RuntimeContainerExecutionUsers.NonRoot
                },
                Layout = new RuntimeImageLayout
                {
                    DotNetHostPath = RuntimeHostPath,
                    RunnerAssemblyPath = "/opt/sharplabnext/SharpLabNext.Runner.dll",
                    JitInspectorAssemblyPath = "/opt/sharplabnext/SharpLabNext.JitInspector.dll"
                },
                Operations = new RuntimeProfileOperations
                {
                    Run = new RuntimeRunOperationDefinition
                    {
                        ImplementationId = RuntimeOperationImplementationIds.Runner,
                        Command = new RuntimeOperationCommandDefinition
                        {
                            Executable = RuntimeHostPath,
                            Argv =
                            [
                                "/opt/sharplabnext/SharpLabNext.Runner.dll",
                                RuntimeOperationPlaceholders.EntryAssembly,
                                "--",
                                RuntimeOperationPlaceholders.Arguments
                            ]
                        }
                    },
                    Jit = new RuntimeJitOperationDefinition
                    {
                        ImplementationId = RuntimeOperationImplementationIds.JitInspector,
                        SourceMappingKind = RuntimeJitSourceMappingKinds.LinuxProfiler,
                        ProfilerPath = "/opt/sharplabnext/SharpLabNext.JitProfiler.so",
                        Command = new RuntimeOperationCommandDefinition
                        {
                            Executable = RuntimeHostPath,
                            Argv =
                            [
                                "/opt/sharplabnext/SharpLabNext.JitInspector.dll",
                                RuntimeOperationPlaceholders.EntryAssembly,
                                RuntimeOperationPlaceholders.MethodFilter
                            ]
                        }
                    }
                },
                AllowedSecurityPolicyIds = ["runtime-job-default"],
                SecurityPolicies = [new RuntimeSecurityPolicyDefinition()]
            };
            Component = new LockedComponent
            {
                Kind = "runtime",
                ResolvedVersion = RuntimeVersion,
                Commit = RuntimeCommit,
                JitCommit = RuntimeCommit,
                SourceUri = ComponentSourceUri,
                Sha512 = ComponentSha512
            };
            Docker = new PromotionDocker
            {
                Inspection = new DockerImageInspection(
                    ImageId,
                    "linux",
                    "amd64",
                    536870912,
                    [ImmutableReference],
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [RepositorySourceProvenanceResolver.ImageLabel] = SourceRevision,
                        ["org.opencontainers.image.revision"] = SourceRevision
                    })
            };
            Docker.Files[Profile.Layout.RunnerAssemblyPath] = new DockerImageFileInspection(RunnerDigest, 1);
            Docker.Files[Profile.Layout.JitInspectorAssemblyPath!] = new DockerImageFileInspection(JitInspectorDigest, 1);
            Docker.Files[Profile.Operations.Jit!.ProfilerPath!] = new DockerImageFileInspection(ProfilerDigest, 1);
            Docker.Files[RuntimeHostPath] = new DockerImageFileInspection(RuntimeHostDigest, 1);
            if (includeSupportAssembly)
                Docker.Files[SupportAssemblyPath] = new DockerImageFileInspection(SupportAssemblyDigest, 1);
            Docker.Files[JitLibraryPath] = new DockerImageFileInspection(JitLibraryDigest, 1);

            WritePromotionMaterial();
        }

        public string Root { get; }
        public string PerformancePolicyPath => Path.Combine(
            Root,
            "profiles",
            "runtime-performance-policies",
            $"{PerformancePolicyId}.json");
        public RuntimeProfileDefinition Profile { get; }
        public LockedComponent Component { get; set; }
        public PromotionDocker Docker { get; }
        public RuntimeCapabilityRequestBinding RequestBinding => new(
            $"sha256:{new string('d', 64)}",
            Profile.Capabilities.Contains("execution-flow", StringComparer.Ordinal)
                ? $"sha256:{new string('e', 64)}"
                : null,
            Profile.Capabilities.Contains("jit-asm", StringComparer.Ordinal)
                ? JitMethodFilter
                : null);

        public Task<IReadOnlyList<RuntimePromotionTrustSnapshot>> CaptureAsync()
        {
            var runtime = new RuntimeManifest
            {
                Id = ProfileId,
                DisplayName = ".NET 10",
                Family = "coreclr",
                ResolvedVersion = RuntimeVersion,
                Rid = "linux-x64",
                Architecture = "x64",
                AcceptedArtifactFormats = ["dotnet-managed-pe-v1"],
                Capabilities = Profile.Capabilities,
                Availability = new ComponentAvailability
                {
                    Installed = true,
                    Health = "healthy"
                }
            };
            var catalog = new CatalogDocument
            {
                SchemaVersion = 1,
                Revision = "test",
                ReleaseId = "test",
                Languages = [],
                Toolchains = [],
                ReferenceSets = [],
                Runtimes = [runtime],
                ArtifactProcessors = [],
                Outputs = [],
                Compatibility = [],
                Presets = []
            };
            var releaseLock = new ReleaseLockDocument
            {
                SchemaVersion = 1,
                ReleaseId = "test",
                ResolvedAt = DateTimeOffset.UtcNow,
                Components = new Dictionary<string, LockedComponent>(StringComparer.Ordinal)
                {
                    [ProfileId] = Component
                }
            };
            var deployment = new DeploymentImageManifest
            {
                SchemaVersion = 1,
                Images =
                [
                    new DeploymentImageDefinition
                    {
                        Id = ProfileId,
                        Repository = "registry.example/sharplabnext/runtime-dotnet-10",
                        ImmutableReference = ImmutableReference,
                        RuntimeId = ProfileId
                    }
                ]
            };
            var inspected = new InspectedImage(
                ProfileId,
                ImmutableReference,
                ImageId,
                "linux",
                "amd64",
                536870912,
                [ImmutableReference],
                Docker.Inspection.Labels,
                null,
                null,
                ProfileId,
                null,
                ProfileId,
                null,
                null);
            return RuntimePromotionTrust.CaptureAsync(
                Root,
                new RepositorySourceProvenance(
                    ReleaseRevision,
                    ReleaseRevision,
                    IsDirty: false,
                    IsVerified: true,
                    DevelopmentOverrideUsed: false),
                catalog,
                releaseLock,
                deployment,
                [Profile],
                [inspected],
                TestContext.Current.CancellationToken);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }

        public void UpdateReceipt(Action<JsonObject> update)
        {
            var receiptPath = Path.Combine(
                Root,
                Profile.PromotionReceipt!.Path.Replace('/', Path.DirectorySeparatorChar));
            var receipt = JsonNode.Parse(File.ReadAllText(receiptPath))!.AsObject();
            update(receipt);
            WriteReceipt(receiptPath, receipt);
        }

        public void ReplaceEvidence(string capability, byte[] bytes)
        {
            var evidencePath = Path.Combine(
                Root,
                "profiles",
                "runtime-promotion-evidence",
                ProfileId,
                capability + ".json");
            File.WriteAllBytes(evidencePath, bytes);
            UpdateReceipt(receipt =>
            {
                var check = receipt["checks"]!.AsArray()
                    .Select(static node => node!.AsObject())
                    .Single(item => string.Equals(
                        item["capability"]!.GetValue<string>(),
                        capability,
                        StringComparison.Ordinal));
                check["evidenceSha256"] = Digest(bytes);
            });
        }

        public void UpdateCapabilityEvidence(string capability, Action<JsonObject> update)
        {
            var evidencePath = Path.Combine(
                Root,
                "profiles",
                "runtime-promotion-evidence",
                ProfileId,
                capability + ".json");
            var evidence = JsonNode.Parse(File.ReadAllText(evidencePath))!.AsObject();
            update(evidence);
            ReplaceEvidence(capability, JsonBytes(evidence));
        }

        public void UpdatePerformanceEvidence(Action<JsonObject> update)
        {
            var path = Path.Combine(
                Root,
                "profiles",
                "runtime-promotion-evidence",
                ProfileId,
                "performance.json");
            var evidence = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            update(evidence);
            var bytes = JsonBytes(evidence);
            File.WriteAllBytes(path, bytes);
            UpdateReceipt(receipt =>
                receipt["performance"]!.AsObject()["evidenceSha256"] = Digest(bytes));
        }

        public void UpdatePerformancePolicy(Action<JsonObject> update)
        {
            var policy = JsonNode.Parse(File.ReadAllText(PerformancePolicyPath))!.AsObject();
            update(policy);
            var policyBytes = JsonBytes(policy);
            File.WriteAllBytes(PerformancePolicyPath, policyBytes);
            var policyDigest = Digest(policyBytes);

            var evidencePath = Path.Combine(
                Root,
                "profiles",
                "runtime-promotion-evidence",
                ProfileId,
                "performance.json");
            var evidence = JsonNode.Parse(File.ReadAllText(evidencePath))!.AsObject();
            evidence["policy"]!["sha256"] = policyDigest;
            var evidenceBytes = JsonBytes(evidence);
            File.WriteAllBytes(evidencePath, evidenceBytes);

            UpdateReceipt(receipt =>
            {
                var performance = receipt["performance"]!.AsObject();
                performance["policySha256"] = policyDigest;
                performance["evidenceSha256"] = Digest(evidenceBytes);
            });
        }

        public PromotionWorkflowInputs CreatePromotionWorkflowInputs()
        {
            var candidateProfile = JsonNode.Parse(JsonSerializer.SerializeToUtf8Bytes(
                Profile,
                ProfileJsonOptions))!.AsObject();
            candidateProfile.Remove("promotionReceipt");
            candidateProfile["capabilities"] = new JsonArray(
                Profile.Capabilities
                    .Where(static capability => capability is not ("inspection" or "execution-flow"))
                    .Select(static capability => (JsonNode?)JsonValue.Create(capability))
                    .ToArray());
            var profileBytes = JsonBytes(candidateProfile);
            var performancePolicyBytes = File.ReadAllBytes(PerformancePolicyPath);
            var receiptPath = Path.Combine(
                Root,
                Profile.PromotionReceipt!.Path.Replace('/', Path.DirectorySeparatorChar));
            var receipt = JsonNode.Parse(File.ReadAllBytes(receiptPath))!.AsObject();
            var preflightProfile = candidateProfile.DeepClone().AsObject();
            preflightProfile["image"] = receipt["image"]!["reference"]!.DeepClone();
            preflightProfile["runtimeImageId"] = receipt["image"]!["imageId"]!.DeepClone();
            preflightProfile["capabilities"] = new JsonArray(
                Profile.Capabilities.Order(StringComparer.Ordinal)
                    .Select(static capability => (JsonNode?)JsonValue.Create(capability))
                    .ToArray());
            var preflightProfileBytes = JsonBytes(preflightProfile);
            var plan = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["profileId"] = ProfileId,
                ["profileSha256"] = Digest(profileBytes),
                ["matrixTargetId"] = receipt["matrixTargetId"]!.DeepClone(),
                ["platform"] = receipt["platform"]!.DeepClone(),
                ["family"] = receipt["family"]!.DeepClone(),
                ["resolvedVersion"] = receipt["resolvedVersion"]!.DeepClone(),
                ["image"] = receipt["image"]!.DeepClone(),
                ["componentIdentity"] = receipt["componentIdentity"]!.DeepClone(),
                ["runtimeIdentity"] = receipt["runtimeIdentity"]!.DeepClone(),
                ["sourceRevision"] = SourceRevision,
                ["createdAtUtc"] = "2026-07-22T00:00:00Z",
                ["producer"] = new JsonObject
                {
                    ["id"] = RuntimePromotionPlanWorkflow.ProducerId,
                    ["sourceRevision"] = SourceRevision
                },
                ["securityPolicyId"] = "runtime-job-default",
                ["capabilities"] = new JsonArray(
                    Profile.Capabilities.Order(StringComparer.Ordinal)
                        .Select(static value => (JsonNode?)JsonValue.Create(value))
                        .ToArray()),
                ["sourceMappingKind"] = Profile.Operations?.Jit?.SourceMappingKind ?? "not-applicable",
                ["operations"] = receipt["operations"]!.DeepClone(),
                ["jitLibraryPath"] = JitLibraryPath,
                ["preflightProfile"] = new JsonObject
                {
                    ["path"] = $"profiles/runtime-promotion-plans/{ProfileId}.profile.json",
                    ["sha256"] = Digest(preflightProfileBytes)
                },
                ["performance"] = new JsonObject
                {
                    ["policyId"] = PerformancePolicyId,
                    ["policyPath"] =
                        $"profiles/runtime-performance-policies/{PerformancePolicyId}.json",
                    ["policySha256"] = Digest(performancePolicyBytes),
                    ["evidencePath"] =
                        $"profiles/runtime-promotion-evidence/{ProfileId}/performance.json"
                }
            };
            var planBytes = JsonBytes(plan);
            var planSha256 = Digest(planBytes);
            var preflightProfileSha256 = Digest(preflightProfileBytes);
            var capabilityEvidence = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var capability in Profile.Capabilities)
            {
                var path = Path.Combine(
                    Root,
                    "profiles",
                    "runtime-promotion-evidence",
                    ProfileId,
                    capability + ".json");
                var evidence = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
                evidence["producer"]!["planSha256"] = planSha256;
                evidence["probeArtifact"]!["planSha256"] = planSha256;
                evidence["probeArtifact"]!["preflightProfileSha256"] =
                    preflightProfileSha256;
                var bytes = JsonBytes(evidence);
                File.WriteAllBytes(path, bytes);
                capabilityEvidence.Add(capability, bytes);
            }

            var performanceEvidencePath = Path.Combine(
                Root,
                "profiles",
                "runtime-promotion-evidence",
                ProfileId,
                "performance.json");
            var performanceEvidence = JsonNode.Parse(
                File.ReadAllBytes(performanceEvidencePath))!.AsObject();
            performanceEvidence["planSha256"] = planSha256;
            var performanceEvidenceBytes = JsonBytes(performanceEvidence);
            File.WriteAllBytes(performanceEvidencePath, performanceEvidenceBytes);
            return new PromotionWorkflowInputs(
                profileBytes,
                preflightProfileBytes,
                planBytes,
                planSha256,
                performancePolicyBytes,
                capabilityEvidence,
                performanceEvidenceBytes);
        }

        public void InstallFinalizedReceipt(byte[] receiptBytes)
        {
            var path = Path.Combine(
                Root,
                Profile.PromotionReceipt!.Path.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllBytes(path, receiptBytes);
            Profile.PromotionReceipt.Sha256 = Digest(receiptBytes);
        }

        public void WriteSourceClosureMaterial(PromotionWorkflowInputs inputs)
        {
            WriteRepositoryFile(
                $"profiles/runtime-promotion-plans/{ProfileId}.json",
                inputs.PlanBytes);
            WriteRepositoryFile(
                $"profiles/runtime-promotion-plans/{ProfileId}.profile.json",
                inputs.PreflightProfileBytes);
            WriteRepositoryFile(
                $"profiles/runtimes/candidates/{ProfileId}.json",
                inputs.ProfileBytes);
            WriteRepositoryFile(
                $"profiles/runtimes/{ProfileId}.json",
                JsonSerializer.SerializeToUtf8Bytes(Profile, ProfileJsonOptions));
            foreach (var path in new[]
                     {
                         "deploy/images.json",
                         "profiles/catalog/catalog.json",
                         "profiles/lock.json",
                         "profiles/runtime-matrix.json"
                     })
            {
                WriteRepositoryFile(path, "{}\n"u8.ToArray());
            }
        }

        public IReadOnlyList<string> SourceClosurePaths() =>
        [
            Profile.PromotionReceipt!.Path,
            .. Profile.Capabilities.Select(capability =>
                $"profiles/runtime-promotion-evidence/{ProfileId}/{capability}.json"),
            $"profiles/runtime-promotion-evidence/{ProfileId}/performance.json",
            $"profiles/runtime-promotion-plans/{ProfileId}.json",
            $"profiles/runtime-promotion-plans/{ProfileId}.profile.json",
            $"profiles/runtimes/candidates/{ProfileId}.json",
            $"profiles/runtimes/{ProfileId}.json",
            "deploy/images.json",
            "profiles/catalog/catalog.json",
            "profiles/lock.json",
            "profiles/runtime-matrix.json"
        ];

        private void WriteRepositoryFile(string relativePath, byte[] bytes)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        public void ValidateRunEvidenceSandboxUser(
            string profileExecutionUser,
            string receiptPlatform,
            string evidenceUser)
        {
            Profile.Container.ExecutionUser = profileExecutionUser;
            var receiptPath = Path.Combine(
                Root,
                Profile.PromotionReceipt!.Path.Replace('/', Path.DirectorySeparatorChar));
            var receiptJson = JsonNode.Parse(File.ReadAllBytes(receiptPath))!.AsObject();
            receiptJson["platform"] = receiptPlatform;
            var receipt = JsonSerializer.Deserialize<RuntimePromotionReceiptDocument>(
                JsonBytes(receiptJson),
                ProfileJsonOptions)!;
            var check = receipt.Checks
                .Select(static item => item!)
                .Single(static item => item.Capability == "run");
            var evidencePath = Path.Combine(
                Root,
                "profiles",
                "runtime-promotion-evidence",
                ProfileId,
                "run.json");
            var evidence = JsonNode.Parse(File.ReadAllBytes(evidencePath))!.AsObject();
            evidence["sandbox"]!["user"] = evidenceUser;

            _ = RuntimeCapabilityEvidenceValidation.Validate(
                JsonBytes(evidence),
                Profile,
                receipt,
                check);
        }

        public void ValidateRunEvidenceSandboxPolicy(
            string supervisorPolicyId,
            string seccompSha256)
        {
            var receiptPath = Path.Combine(
                Root,
                Profile.PromotionReceipt!.Path.Replace('/', Path.DirectorySeparatorChar));
            var receipt = JsonSerializer.Deserialize<RuntimePromotionReceiptDocument>(
                File.ReadAllBytes(receiptPath),
                ProfileJsonOptions)!;
            var check = receipt.Checks
                .Select(static item => item!)
                .Single(static item => item.Capability == "run");
            var evidencePath = Path.Combine(
                Root,
                "profiles",
                "runtime-promotion-evidence",
                ProfileId,
                "run.json");
            var evidence = JsonNode.Parse(File.ReadAllBytes(evidencePath))!.AsObject();
            evidence["sandbox"]!["supervisorPolicyId"] = supervisorPolicyId;
            evidence["sandbox"]!["seccompSha256"] = seccompSha256;

            _ = RuntimeCapabilityEvidenceValidation.Validate(
                JsonBytes(evidence),
                Profile,
                receipt,
                check);
        }

        private void WritePromotionMaterial()
        {
            var checks = new JsonArray();
            foreach (var capability in Profile.Capabilities)
            {
                var evidencePath = $"profiles/runtime-promotion-evidence/{ProfileId}/{capability}.json";
                var sourceMappingKind = capability == "jit-asm" ? "linux-profiler" : "not-applicable";
                var mappingSource = capability == "jit-asm" ? "ordinary" : "not-applicable";
                var evidenceBytes = JsonBytes(CreateCapabilityEvidence(
                    capability,
                    sourceMappingKind,
                    mappingSource));
                File.WriteAllBytes(
                    Path.Combine(Root, evidencePath.Replace('/', Path.DirectorySeparatorChar)),
                    evidenceBytes);
                checks.Add(new JsonObject
                {
                    ["capability"] = capability,
                    ["result"] = "passed",
                    ["networkDisabled"] = true,
                    ["supervisorSandbox"] = true,
                    ["outputLimitValidated"] = true,
                    ["sourceMappingKind"] = sourceMappingKind,
                    ["mappingSource"] = mappingSource,
                    ["evidencePath"] = evidencePath,
                    ["evidenceSha256"] = Digest(evidenceBytes)
                });
            }

            var performancePolicy = CreatePerformancePolicy();
            var performancePolicyBytes = JsonBytes(performancePolicy);
            var performancePolicyPath = Path.Combine(
                Root,
                "profiles",
                "runtime-performance-policies",
                $"{PerformancePolicyId}.json");
            File.WriteAllBytes(performancePolicyPath, performancePolicyBytes);
            var performancePolicyDigest = Digest(performancePolicyBytes);

            var performanceScenarios = new JsonObject
            {
                ["run"] = CreatePerformanceScenario(),
                ["jit"] = CreatePerformanceScenario(),
                ["mapping"] = CreatePerformanceScenario()
            };
            var performanceEvidence = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["profileId"] = ProfileId,
                ["planSha256"] = PlanSha256,
                ["image"] = new JsonObject
                {
                    ["reference"] = ImmutableReference,
                    ["imageId"] = ImageId,
                    ["sizeBytes"] = 536870912
                },
                ["sourceRevision"] = SourceRevision,
                ["policy"] = new JsonObject
                {
                    ["id"] = PerformancePolicyId,
                    ["sha256"] = performancePolicyDigest
                },
                ["capabilities"] = new JsonArray(
                    Profile.Capabilities.Order(StringComparer.Ordinal)
                        .Select(static value => (JsonNode?)JsonValue.Create(value))
                        .ToArray()),
                ["sourceMappingKind"] = "linux-profiler",
                ["environment"] = new JsonObject
                {
                    ["runnerId"] = "runtime-preflight-linux-x64-v1",
                    ["operatingSystem"] = "linux",
                    ["architecture"] = "x64",
                    ["nanoCpus"] = 1000000000,
                    ["memoryLimitBytes"] = 268435456
                },
                ["completedAtUtc"] = "2026-07-22T00:00:00Z",
                ["result"] = "passed",
                ["scenarios"] = performanceScenarios
            };
            var performanceEvidenceBytes = JsonBytes(performanceEvidence);
            var performanceEvidencePath =
                $"profiles/runtime-promotion-evidence/{ProfileId}/performance.json";
            File.WriteAllBytes(
                Path.Combine(Root, performanceEvidencePath.Replace('/', Path.DirectorySeparatorChar)),
                performanceEvidenceBytes);

            var receipt = new JsonObject
            {
                ["schemaVersion"] = 2,
                ["planSha256"] = PlanSha256,
                ["profileId"] = ProfileId,
                ["matrixTargetId"] = "dotnet-10",
                ["platform"] = "linux",
                ["family"] = "coreclr",
                ["resolvedVersion"] = RuntimeVersion,
                ["image"] = new JsonObject
                {
                    ["reference"] = ImmutableReference,
                    ["imageId"] = ImageId,
                    ["sizeBytes"] = 536870912
                },
                ["componentIdentity"] = new JsonObject
                {
                    ["sourceUri"] = ComponentSourceUri,
                    ["sourceDigest"] = $"sha512:{ComponentSha512}"
                },
                ["runtimeIdentity"] = new JsonObject
                {
                    ["runtimeCommit"] = RuntimeCommit,
                    ["jitVersion"] = RuntimeVersion,
                    ["jitCommit"] = RuntimeCommit
                },
                ["operations"] = new JsonObject
                {
                    ["run"] = new JsonObject
                    {
                        ["implementation"] = RuntimeOperationImplementationIds.Runner,
                        ["assemblyPath"] = Profile.Layout.RunnerAssemblyPath,
                        ["assemblySha256"] = RunnerDigest
                    },
                    ["jit"] = new JsonObject
                    {
                        ["implementation"] = RuntimeOperationImplementationIds.JitInspector,
                        ["assemblyPath"] = Profile.Layout.JitInspectorAssemblyPath,
                        ["assemblySha256"] = JitInspectorDigest,
                        ["profilerPath"] = Profile.Operations!.Jit!.ProfilerPath,
                        ["profilerSha256"] = ProfilerDigest
                    }
                },
                ["performance"] = new JsonObject
                {
                    ["result"] = "passed",
                    ["policyId"] = PerformancePolicyId,
                    ["policyPath"] =
                        $"profiles/runtime-performance-policies/{PerformancePolicyId}.json",
                    ["policySha256"] = performancePolicyDigest,
                    ["evidencePath"] = performanceEvidencePath,
                    ["evidenceSha256"] = Digest(performanceEvidenceBytes)
                },
                ["sourceRevision"] = SourceRevision,
                ["checks"] = checks
            };
            var receiptPath = $"profiles/runtime-promotion-receipts/{ProfileId}.json";
            Profile.PromotionReceipt = new RuntimePromotionReceiptReference
            {
                Path = receiptPath,
                Sha256 = string.Empty
            };
            WriteReceipt(
                Path.Combine(Root, receiptPath.Replace('/', Path.DirectorySeparatorChar)),
                receipt);
        }

        private JsonObject CreateCapabilityEvidence(
            string capability,
            string sourceMappingKind,
            string mappingSource)
        {
            var isJit = capability == "jit-asm";
            var operationImplementation = isJit
                ? Profile.Operations!.Jit!.ImplementationId
                : Profile.Operations!.Run!.ImplementationId;
            var command = isJit
                ? RuntimeProfileCommandBuilder.CreateJitCommand(
                    Profile,
                    "SharpLabNext.Preflight.dll",
                    JitMethodFilter)
                : RuntimeProfileCommandBuilder.CreateRunCommand(
                    Profile,
                    "SharpLabNext.Preflight.dll",
                    capability switch
                    {
                        "run" => ["success-security"],
                        "inspection" => ["inspection"],
                        "execution-flow" => ["execution-flow"],
                        _ => throw new InvalidOperationException(
                            $"Unknown Run capability '{capability}'.")
                    });
            var artifacts = new JsonArray
            {
                CreateArtifact(
                    "helper",
                    isJit ? Profile.Layout.JitInspectorAssemblyPath! : Profile.Layout.RunnerAssemblyPath,
                    isJit ? JitInspectorDigest : RunnerDigest,
                    "managed-pe",
                    "anycpu"),
                CreateArtifact("runtime-host", RuntimeHostPath, RuntimeHostDigest, "elf", "x64")
            };
            if (_includeSupportAssembly)
            {
                artifacts.Add(CreateArtifact(
                    "support-assembly",
                    SupportAssemblyPath,
                    SupportAssemblyDigest,
                    "managed-pe",
                    "anycpu"));
            }
            if (isJit)
            {
                artifacts.Add(CreateArtifact(
                    "jit-library",
                    JitLibraryPath,
                    JitLibraryDigest,
                    "elf",
                    "x64"));
                artifacts.Add(CreateArtifact(
                    "profiler",
                    Profile.Operations!.Jit!.ProfilerPath!,
                    ProfilerDigest,
                    "elf",
                    "x64"));
            }

            var evidence = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["profileId"] = ProfileId,
                ["capability"] = capability,
                ["image"] = new JsonObject
                {
                    ["reference"] = ImmutableReference,
                    ["imageId"] = ImageId
                },
                ["sourceRevision"] = SourceRevision,
                ["completedAtUtc"] = "2026-07-22T00:00:00Z",
                ["result"] = "passed",
                ["producer"] = new JsonObject
                {
                    ["id"] = "sharplabnext-runtime-preflight-v1",
                    ["sourceRevision"] = SourceRevision,
                    ["planSha256"] = PlanSha256
                },
                ["artifacts"] = artifacts,
                ["invocation"] = new JsonObject
                {
                    ["implementation"] = operationImplementation,
                    ["command"] = new JsonArray(command
                        .Select(static value => (JsonNode?)JsonValue.Create(value))
                        .ToArray()),
                    ["entryAssembly"] = new JsonObject
                    {
                        ["path"] = EntryAssemblyPath,
                        ["sha256"] = $"sha256:{new string('7', 64)}"
                    },
                    ["outcome"] = "succeeded",
                    ["exitCode"] = 0,
                    ["runtimeFrameCount"] = 3,
                    ["terminalFrameKind"] = "Exit",
                    ["terminalStatus"] = "completed",
                    ["stdoutBytes"] = 32,
                    ["stderrBytes"] = 16
                },
                ["sandbox"] = new JsonObject
                {
                    ["supervisorPolicyId"] = "runtime-linux-v1",
                    ["securityPolicyId"] = "runtime-job-default",
                    ["seccompSha256"] =
                        "sha256:01536f1d1df938ae611eba20d6349e0de7a99b6ecdee1549427a0b01b8301e28",
                    ["containerId"] = new string('9', 64),
                    ["networkMode"] = "none",
                    ["networkProbeBlocked"] = true,
                    ["readOnlyRootFilesystem"] = true,
                    ["readOnlyProbeBlocked"] = true,
                    ["capDrop"] = new JsonArray("ALL"),
                    ["noNewPrivileges"] = true,
                    ["user"] = "1654:1654",
                    ["nanoCpus"] = 1_000_000_000,
                    ["memoryBytes"] = 268_435_456,
                    ["pidsLimit"] = 64,
                    ["deadlineMilliseconds"] = 10_000,
                    ["outputLimitBytes"] = 1_048_576,
                    ["tmpfsBytes"] = 33_554_432
                },
                ["lifecycle"] = new JsonObject
                {
                    ["outputOverflow"] = CreateLifecycleProbe("output-limit-exceeded"),
                    ["timeout"] = CreateLifecycleProbe("timeout"),
                    ["cancellation"] = CreateLifecycleProbe("cancelled"),
                    ["processTreeCleanup"] = CreateLifecycleProbe("completed")
                }
            };
            var sourceProbeArtifactSha256 = $"sha256:{new string('d', 64)}";
            var probeArtifactSha256 = capability == "execution-flow"
                ? $"sha256:{new string('e', 64)}"
                : sourceProbeArtifactSha256;
            var probeArtifact = new JsonObject
            {
                ["contract"] = RuntimeCapabilityProbeContract.ContractId,
                ["sourceArtifactSha256"] = sourceProbeArtifactSha256,
                ["artifactSha256"] = probeArtifactSha256,
                ["entryAssemblySha256"] = $"sha256:{new string('7', 64)}",
                ["planSha256"] = PlanSha256,
                ["preflightProfileSha256"] = PreflightProfileSha256
            };
            if (capability == "execution-flow")
            {
                probeArtifact["derivation"] = new JsonObject
                {
                    ["parentArtifactSha256"] = sourceProbeArtifactSha256,
                    ["processorId"] = RuntimeCapabilityProbeContract.ExecutionFlowProcessorId,
                    ["processorVersion"] = RuntimeCapabilityProbeContract.ExecutionFlowProcessorVersion,
                    ["optionsSha256"] = RuntimeCapabilityProbeContract.ExecutionFlowOptionsDigest,
                    ["transformId"] = RuntimeCapabilityProbeContract.ExecutionFlowTransformId,
                    ["profileId"] = RuntimeCapabilityProbeContract.ExecutionFlowProfileId,
                    ["applied"] = true
                };
            }
            evidence["probeArtifact"] = probeArtifact;
            if (isJit)
                evidence["invocation"]!["methodFilter"] = JitMethodFilter;

            switch (capability)
            {
                case "run":
                    evidence["run"] = new JsonObject
                    {
                        ["expectedStdoutMarker"] = "stdout-marker",
                        ["observedStdoutMarker"] = "stdout-marker",
                        ["expectedStderrMarker"] = "stderr-marker",
                        ["observedStderrMarker"] = "stderr-marker",
                        ["exceptionFrameValidated"] = true
                    };
                    break;
                case "jit-asm":
                {
                    var ranges = new JsonArray
                    {
                        CreateSourceRange(0, 0, 8, 10),
                        CreateSourceRange(4, 8, 16, 11)
                    };
                    evidence["jit"] = new JsonObject
                    {
                        ["runtimeVersion"] = RuntimeVersion,
                        ["jitVersion"] = RuntimeVersion,
                        ["pdb"] = new JsonObject
                        {
                            ["path"] = "/workspace/SharpLabNext.Preflight.pdb",
                            ["sha256"] = $"sha256:{new string('a', 64)}",
                            ["contentId"] = new string('b', 40),
                            ["sequencePointCount"] = 2
                        },
                        ["methods"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["metadataToken"] = "0x06000001",
                                ["displayName"] = "SharpLabNext.Preflight.MultipleSequencePoints",
                                ["nativeCodeBytes"] = 32,
                                ["instructionCount"] = 12,
                                ["sourceRanges"] = ranges
                            }
                        },
                        ["mapping"] = new JsonObject
                        {
                            ["kind"] = sourceMappingKind,
                            ["source"] = mappingSource,
                            ["rangeCount"] = 2,
                            ["distinctSourceRangeCount"] = 2,
                            ["allRangesMatchPdb"] = true
                        }
                    };
                    break;
                }
                case "inspection":
                    evidence["inspection"] = new JsonObject
                    {
                        ["recordCount"] = 2,
                        ["kinds"] = new JsonArray("Value", "MemoryGraph"),
                        ["valueProbePassed"] = true,
                        ["memoryGraphProbePassed"] = true
                    };
                    break;
                case "execution-flow":
                    evidence["executionFlow"] = new JsonObject
                    {
                        ["recordCount"] = 3,
                        ["sequencePointCount"] = 2,
                        ["branchCount"] = 1,
                        ["sourceRangeCount"] = 2,
                        ["derivedArtifactSha256"] = probeArtifactSha256,
                        ["parentArtifactSha256"] = sourceProbeArtifactSha256,
                        ["processorId"] = RuntimeCapabilityProbeContract.ExecutionFlowProcessorId,
                        ["processorVersion"] = RuntimeCapabilityProbeContract.ExecutionFlowProcessorVersion,
                        ["optionsSha256"] = RuntimeCapabilityProbeContract.ExecutionFlowOptionsDigest,
                        ["transformId"] = RuntimeCapabilityProbeContract.ExecutionFlowTransformId,
                        ["profileId"] = RuntimeCapabilityProbeContract.ExecutionFlowProfileId,
                        ["applied"] = true
                    };
                    break;
                default:
                    throw new InvalidOperationException($"Unknown capability '{capability}'.");
            }
            return evidence;
        }

        private static JsonObject CreateArtifact(
            string role,
            string path,
            string sha256,
            string format,
            string architecture) => new()
        {
            ["role"] = role,
            ["path"] = path,
            ["sha256"] = sha256,
            ["sizeBytes"] = 1,
            ["format"] = format,
            ["architecture"] = architecture
        };

        private static JsonObject CreateLifecycleProbe(string terminalStatus) => new()
        {
            ["result"] = "passed",
            ["terminalStatus"] = terminalStatus,
            ["containerRemoved"] = true,
            ["processTreeRemoved"] = true
        };

        private static JsonObject CreateSourceRange(
            int ilOffset,
            int nativeStartOffset,
            int nativeEndOffset,
            int startLine) => new()
        {
            ["ilOffset"] = ilOffset,
            ["nativeStartOffset"] = nativeStartOffset,
            ["nativeEndOffset"] = nativeEndOffset,
            ["document"] = "Program.cs",
            ["startLine"] = startLine,
            ["startColumn"] = 9,
            ["endLine"] = startLine,
            ["endColumn"] = 18
        };

        private void WriteReceipt(string path, JsonObject receipt)
        {
            var receiptBytes = JsonBytes(receipt);
            File.WriteAllBytes(path, receiptBytes);
            Profile.PromotionReceipt!.Sha256 = Digest(receiptBytes);
        }

        private static JsonObject CreatePerformancePolicy() => new()
        {
            ["schemaVersion"] = 1,
            ["id"] = PerformancePolicyId,
            ["sampleCounts"] = new JsonObject { ["cold"] = 3, ["warm"] = 10 },
            ["resourceLimits"] = new JsonObject
            {
                ["nanoCpus"] = 1000000000,
                ["allowedMemoryBytes"] = new JsonArray(268435456, 1073741824)
            },
            ["image"] = new JsonObject { ["maximumSizeBytes"] = 8589934592 },
            ["scenarios"] = new JsonObject
            {
                ["run"] = CreatePerformanceBudget(30000, 45000, 10000, 20000),
                ["jit"] = CreatePerformanceBudget(45000, 60000, 20000, 30000),
                ["mapping"] = CreatePerformanceBudget(60000, 90000, 30000, 45000)
            }
        };

        private static JsonObject CreatePerformanceBudget(
            double coldP95,
            double coldSample,
            double warmP95,
            double warmSample) => new()
        {
            ["cold"] = new JsonObject
            {
                ["maximumP95LatencyMilliseconds"] = coldP95,
                ["maximumSampleLatencyMilliseconds"] = coldSample,
                ["maximumPeakMemoryBytes"] = 1073741824
            },
            ["warm"] = new JsonObject
            {
                ["maximumP95LatencyMilliseconds"] = warmP95,
                ["maximumSampleLatencyMilliseconds"] = warmSample,
                ["maximumPeakMemoryBytes"] = 1073741824
            }
        };

        private static JsonObject CreatePerformanceScenario() => new()
        {
            ["cold"] = CreateSamples(3, 100),
            ["warm"] = CreateSamples(10, 50)
        };

        private static JsonArray CreateSamples(int count, double latency) => new(
            Enumerable.Range(0, count)
                .Select(_ => (JsonNode)new JsonObject
                {
                    ["latencyMilliseconds"] = latency,
                    ["peakMemoryBytes"] = 134217728,
                    ["operationId"] =
                        $"op_{System.Threading.Interlocked.Increment(ref PerformanceSampleSequence):x32}",
                    ["resourceSampleCount"] = 1,
                    ["completedAtUtc"] = "2026-07-22T00:00:00.0000000Z"
                })
                .ToArray());

        public static byte[] JsonBytes(JsonObject value) => Encoding.UTF8.GetBytes(
            value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

        public static string Digest(byte[] bytes) =>
            $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
    }

    private sealed record PromotionWorkflowInputs(
        byte[] ProfileBytes,
        byte[] PreflightProfileBytes,
        byte[] PlanBytes,
        string PlanSha256,
        byte[] PerformancePolicyBytes,
        IReadOnlyDictionary<string, byte[]> CapabilityEvidence,
        byte[] PerformanceEvidenceBytes);

    private sealed class PromotionSourceInspector(
        bool isAncestor,
        IReadOnlyList<RuntimePromotionSourceChange> changes) : IRuntimePromotionSourceInspector
    {
        public int AncestryChecks { get; private set; }
        public int DiffChecks { get; private set; }

        public Task<bool> IsAncestorAsync(
            string repositoryRoot,
            string ancestorRevision,
            string descendantRevision,
            CancellationToken cancellationToken = default)
        {
            AncestryChecks++;
            return Task.FromResult(isAncestor);
        }

        public Task<IReadOnlyList<RuntimePromotionSourceChange>> DiffAsync(
            string repositoryRoot,
            string ancestorRevision,
            string descendantRevision,
            CancellationToken cancellationToken = default)
        {
            DiffChecks++;
            return Task.FromResult(changes);
        }
    }

    private sealed class PromotionDocker : IDockerCli
    {
        public required DockerImageInspection Inspection { get; set; }
        public Dictionary<string, DockerImageFileInspection> Files { get; } = new(StringComparer.Ordinal);
        public List<string> InspectedReferences { get; } = [];
        public List<(string ImageId, string Path)> InspectedFiles { get; } = [];

        public Task<DockerImageInspection> InspectImageAsync(
            string reference,
            CancellationToken cancellationToken)
        {
            InspectedReferences.Add(reference);
            return Task.FromResult(Inspection);
        }

        public Task<DockerImageFileInspection> InspectImageFileAsync(
            string imageId,
            string absolutePath,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            InspectedFiles.Add((imageId, absolutePath));
            return Task.FromResult(Files[absolutePath]);
        }

        public Task SaveImagesAsync(
            IReadOnlyList<string> references,
            string outputPath,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
