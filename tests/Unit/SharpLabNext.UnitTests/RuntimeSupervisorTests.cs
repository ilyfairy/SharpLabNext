using System.Buffers.Binary;
using System.Diagnostics;
using System.Formats.Tar;
using System.Net;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.BundleBuilder;
using SharpLabNext.Contracts;
using SharpLabNext.Operations;
using SharpLabNext.RuntimeProfile.Sdk;
using SharpLabNext.RuntimeProtocol;
using SharpLabNext.RuntimeSupervisor;

namespace SharpLabNext.UnitTests;

public sealed class RuntimeSupervisorTests
{
    private static readonly JsonSerializerOptions RuntimeProfilePreflightJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = false
        };

    [Fact]
    public void PromotionPreflightLoadsOneDigestBoundLocalProfile()
    {
        using var fixture = CreatePromotionPreflightProfile();
        var preflight = RuntimePromotionPreflightOptions.Load(fixture.Configuration);
        var options = ValidOptions();

        preflight.ApplyTo(options);

        var profile = Assert.Single(options.Profiles);
        Assert.Equal(fixture.ProfileId, profile.Id);
        Assert.Equal(fixture.PlanSha256, options.PromotionPreflightPlanSha256);
        Assert.Equal(fixture.ProfileSha256, options.PromotionPreflightProfileSha256);
        Assert.Equal(fixture.SourceRevision, options.PromotionPreflightSourceRevision);
        Assert.True(options.RequireDigestPinnedImages);
        Assert.False(options.SessionReuseEnabled);
        Assert.Single(options.SecurityPolicies);
    }

    [Fact]
    public void PromotionPreflightRejectsProfileDigestMismatch()
    {
        using var fixture = CreatePromotionPreflightProfile(
            profileSha256: $"sha256:{new string('0', 64)}");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            RuntimePromotionPreflightOptions.Load(fixture.Configuration));

        Assert.Contains("digest does not match", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PromotionPreflightRejectsMutableProfileImage()
    {
        using var fixture = CreatePromotionPreflightProfile(profile =>
        {
            profile.Image = "registry.example/sharplabnext/runtime:mutable";
            profile.RuntimeImageId = profile.Image;
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            RuntimePromotionPreflightOptions.Load(fixture.Configuration));

        Assert.Contains("invalid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionsRequirePinnedProductionImages()
    {
        var options = ValidOptions();
        options.RequireDigestPinnedImages = true;

        var result = new RuntimeSupervisorOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("digest-pinned", StringComparison.Ordinal));
    }

    [Fact]
    public void OptionsAcceptACompleteDevelopmentProfile()
    {
        var result = new RuntimeSupervisorOptionsValidator().Validate(null, ValidOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ApplicationSettingsBindWithoutDuplicatingProfileCollections()
    {
        var root = FindRepositoryRoot();
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(
                root,
                "src",
                "Supervisor",
                "SharpLabNext.RuntimeSupervisor",
                "appsettings.json"))
            .Build();
        var options = new RuntimeSupervisorOptions();

        configuration.GetSection(RuntimeSupervisorOptions.SectionName).Bind(options);
        var result = new RuntimeSupervisorOptionsValidator().Validate(null, options);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Failures ?? []));
        Assert.False(options.RequireDigestPinnedImages);
        Assert.True(options.SessionReuseEnabled);
        var wine = Assert.Single(options.Profiles, static profile =>
            profile.Id == "wine-netfx48-linux-x64");
        Assert.Equal("netfx-clr-wine", wine.Family);
        Assert.Equal("wine-9.0+netfx48", wine.RuntimeVersion);
        Assert.Equal("not-applicable", wine.RuntimeCommit);
        Assert.Equal("not-applicable", wine.JitVersion);
        Assert.Equal("not-applicable", wine.JitCommit);
        Assert.Equal(
            ["dotnet-framework-managed-pe-v1", "dotnet-framework-mixed-pe-v1"],
            wine.AcceptedArtifactFormats);
        Assert.Equal(["run"], wine.Capabilities);
        Assert.Equal(["runtime.netfx48-wine"], wine.ProvidedRuntimeFeatureTags);
        Assert.Equal(["runtime-job-wine-netfx"], wine.AllowedSecurityPolicyIds);
        Assert.Equal(RuntimeRunnerKinds.WineNetFx, wine.Layout.RunnerKind);
        Assert.Equal("/usr/lib/wine/wine64", wine.Layout.WineHostPath);
        Assert.Equal("/opt/wine-dotnet", wine.Layout.WinePrefixPath);
        Assert.Null(wine.Layout.JitInspectorAssemblyPath);
        var winePolicy = Assert.Single(options.SecurityPolicies, static policy =>
            policy.Id == "runtime-job-wine-netfx");
        Assert.Equal(1024L * 1024 * 1024, winePolicy.MemoryBytes);
        Assert.Equal(1_000_000_000, winePolicy.NanoCpus);
        Assert.Equal(64, winePolicy.PidsLimit);
        Assert.Equal(30, winePolicy.MaximumDurationSeconds);
        Assert.Equal(64L * 1024 * 1024, winePolicy.MaximumArtifactBytes);
        Assert.Equal(1L * 1024 * 1024, winePolicy.MaximumOutputBytes);
        Assert.Equal(32 * 1024 * 1024, winePolicy.TmpfsBytes);
        var jsharp = Assert.Single(options.Profiles, static profile =>
            profile.Id == "wine-jsharp20-linux-x64");
        Assert.Equal("wine-9.0+clr2+jsharp-2.0.50727.937", jsharp.RuntimeVersion);
        Assert.Equal("x64", jsharp.Architecture);
        Assert.Equal(["dotnet-framework-managed-pe-v1"], jsharp.AcceptedArtifactFormats);
        Assert.Equal(["run"], jsharp.Capabilities);
        Assert.Equal(["runtime.jsharp20-wine"], jsharp.ProvidedRuntimeFeatureTags);
        Assert.Equal(["runtime-job-wine-jsharp20"], jsharp.AllowedSecurityPolicyIds);
        Assert.Equal(RuntimeRunnerKinds.WineJSharp20, jsharp.Layout.RunnerKind);
        Assert.Equal("/usr/lib/wine/wine64", jsharp.Layout.WineHostPath);
        Assert.Equal("/opt/wine-jsharp20", jsharp.Layout.WinePrefixPath);
        Assert.Null(jsharp.Layout.JitInspectorAssemblyPath);
        var jsharpPolicy = Assert.Single(options.SecurityPolicies, static policy =>
            policy.Id == "runtime-job-wine-jsharp20");
        Assert.Equal(1024L * 1024 * 1024, jsharpPolicy.MemoryBytes);
        Assert.Equal(1_000_000_000, jsharpPolicy.NanoCpus);
        Assert.Equal(64, jsharpPolicy.PidsLimit);
        Assert.Equal(30, jsharpPolicy.MaximumDurationSeconds);
        Assert.All(options.Profiles, static profile =>
        {
            Assert.Equal(profile.Image, profile.RuntimeImageId);
            Assert.Equal(profile.AcceptedArtifactFormats.Distinct(StringComparer.Ordinal), profile.AcceptedArtifactFormats);
            Assert.Equal(profile.Capabilities.Distinct(StringComparer.Ordinal), profile.Capabilities);
            Assert.Equal(profile.AllowedSecurityPolicyIds.Distinct(StringComparer.Ordinal), profile.AllowedSecurityPolicyIds);
        });
    }

    [Fact]
    public void OptionsAcceptAnImmutableOfflineImageId()
    {
        var options = ValidOptions();
        var imageId = $"sha256:{new string('a', 64)}";
        options.RequireDigestPinnedImages = true;
        options.Profiles[0].Image = imageId;
        options.Profiles[0].RuntimeImageId = imageId;

        var result = new RuntimeSupervisorOptionsValidator().Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void OptionsRejectFloatingLatestEvenInDevelopment()
    {
        var options = ValidOptions();
        options.Profiles[0].Image = "sharplabnext/runtime:latest";

        var result = new RuntimeSupervisorOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("floating latest", StringComparison.Ordinal));
    }

    [Fact]
    public void ManagedDockerResourcesCarryAuditableLifecycleLabels()
    {
        const string traceParent = "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01";
        var labels = DockerEngineClient.CreateManagedLabels(
            "com.sharplabnext.runtime-job",
            "true",
            "operation-1",
            "release-1",
            "candidate-scope-1",
            materializer: true,
            traceParent: traceParent);

        Assert.Equal("true", labels["com.sharplabnext.runtime-job"]);
        Assert.Equal("operation-1", labels["com.sharplabnext.job-id"]);
        Assert.Equal("operation-1", labels["com.sharplabnext.operation-id"]);
        Assert.Equal("release-1", labels["com.sharplabnext.release-id"]);
        Assert.Equal("candidate-scope-1", labels["com.sharplabnext.resource-scope"]);
        Assert.Equal("true", labels["com.sharplabnext.materializer"]);
        Assert.Equal(traceParent, labels["com.sharplabnext.traceparent"]);
        Assert.True(DateTimeOffset.TryParse(
            labels["com.sharplabnext.created-at"],
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out _));
    }

    [Fact]
    public void RuntimeJobCapturesOnlyW3CTraceContext()
    {
        using var activity = new Activity("runtime-job")
            .SetIdFormat(ActivityIdFormat.W3C)
            .Start();

        Assert.Equal(activity.Id, RuntimeJobExecutor.CaptureTraceParent());
        Assert.Throws<ArgumentException>(() => DockerEngineClient.CreateManagedLabels(
            "com.sharplabnext.runtime-job",
            "true",
            "operation-1",
            "release-1",
            "candidate-scope-1",
            traceParent: "invalid"));
    }

    [Fact]
    public async Task RuntimeContainerUsesBoundedLocalLogsAndDockerLogsApiRemainsReadable()
    {
        using var handler = new RecordingDockerHandler();
        using var docker = CreateDockerClient(handler);
        var policy = new RuntimeSecurityPolicyOptions();
        var spec = new RuntimeContainerSpec(
            "runtime-test",
            "operation-1",
            "release-1",
            "runtime-image:test",
            ["/runtime/entrypoint"],
            new Dictionary<string, string>(StringComparer.Ordinal),
            policy,
            "com.sharplabnext.runtime-job",
            "candidate-scope-1",
            "workspace-1");

        var containerId = await docker.CreateContainerAsync(spec, TestContext.Current.CancellationToken);
        await using var logs = await docker.OpenContainerLogsAsync(
            containerId,
            TestContext.Current.CancellationToken);
        using var reader = new StreamReader(logs, Encoding.UTF8);

        Assert.Equal("runtime output", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
        var createRequest = Assert.Single(
            handler.Requests,
            static request => request.Method == HttpMethod.Post &&
                              request.Path.StartsWith("/v1.47/containers/create?", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(createRequest.Body!);
        var logConfig = body.RootElement.GetProperty("HostConfig").GetProperty("LogConfig");
        Assert.Equal("local", logConfig.GetProperty("Type").GetString());
        Assert.Equal("4m", logConfig.GetProperty("Config").GetProperty("max-size").GetString());
        Assert.Equal("1", logConfig.GetProperty("Config").GetProperty("max-file").GetString());
        Assert.Equal("false", logConfig.GetProperty("Config").GetProperty("compress").GetString());
        Assert.Equal("1654:1654", body.RootElement.GetProperty("User").GetString());
        Assert.Contains(
            "noexec",
            body.RootElement.GetProperty("HostConfig").GetProperty("Tmpfs").GetProperty("/tmp").GetString(),
            StringComparison.Ordinal);
        Assert.Contains(
            handler.Requests,
            request => request.Method == HttpMethod.Get &&
                       request.Path == $"/v1.47/containers/{containerId}/logs?stdout=true&stderr=true&follow=true&timestamps=false");
    }

    [Fact]
    public async Task WineRuntimeContainerUsesFixedRootReadOnlyNetworklessSandbox()
    {
        using var handler = new RecordingDockerHandler();
        using var docker = CreateDockerClient(handler);
        var spec = new RuntimeContainerSpec(
            "wine-runtime-test",
            "operation-1",
            "release-1",
            "wine-runtime-image:test",
            ["dotnet", "/opt/sharplabnext/SharpLabNext.WineRunner.dll", "wine", "/workspace/Program.exe"],
            new Dictionary<string, string>(StringComparer.Ordinal),
            new RuntimeSecurityPolicyOptions(),
            "com.sharplabnext.runtime-job",
            "candidate-scope-1",
            "workspace-1",
            IsolationKind: RuntimeContainerIsolationKind.WineRoot,
            WinePrefixPath: "/opt/wine-dotnet");

        await docker.CreateContainerAsync(spec, TestContext.Current.CancellationToken);

        var createRequest = Assert.Single(
            handler.Requests,
            static request => request.Method == HttpMethod.Post &&
                              request.Path.StartsWith("/v1.47/containers/create?", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(createRequest.Body!);
        var root = body.RootElement;
        var host = root.GetProperty("HostConfig");
        Assert.Equal("0:0", root.GetProperty("User").GetString());
        Assert.True(root.GetProperty("NetworkDisabled").GetBoolean());
        Assert.Equal("none", host.GetProperty("NetworkMode").GetString());
        Assert.True(host.GetProperty("ReadonlyRootfs").GetBoolean());
        Assert.False(host.GetProperty("Privileged").GetBoolean());
        Assert.Equal("ALL", Assert.Single(host.GetProperty("CapDrop").EnumerateArray()).GetString());
        Assert.Contains(
            host.GetProperty("SecurityOpt").EnumerateArray(),
            static value => value.GetString() == "no-new-privileges:true");
        var nofile = Assert.Single(
            host.GetProperty("Ulimits").EnumerateArray(),
            static value => value.GetProperty("Name").GetString() == "nofile");
        Assert.Equal(512, nofile.GetProperty("Soft").GetInt64());
        Assert.Equal(512, nofile.GetProperty("Hard").GetInt64());
        var tmpfs = host.GetProperty("Tmpfs");
        Assert.Equal(2, tmpfs.EnumerateObject().Count());
        Assert.Equal("rw,exec,nosuid,nodev,size=64m", tmpfs.GetProperty("/tmp").GetString());
        Assert.Equal(
            "rw,exec,nosuid,nodev,size=256m",
            tmpfs.GetProperty("/opt/wine-dotnet/drive_c/users/root/Temp").GetString());
        var workspace = Assert.Single(host.GetProperty("Mounts").EnumerateArray());
        Assert.Equal("/workspace", workspace.GetProperty("Target").GetString());
        Assert.True(workspace.GetProperty("ReadOnly").GetBoolean());
    }

    [Fact]
    public async Task WineCoreClrContainerUsesNonRootUserAndControlledSharedTemporaryDirectory()
    {
        using var handler = new RecordingDockerHandler();
        using var docker = CreateDockerClient(handler);
        var policy = new RuntimeSecurityPolicyOptions { TmpfsBytes = 48 * 1024 * 1024 };
        var spec = new RuntimeContainerSpec(
            "wine-coreclr-runtime-test",
            "operation-1",
            "release-1",
            "wine-runtime-image:test",
            ["/usr/lib/wine/wine64", @"Z:\opt\wine-dotnet\drive_c\dotnet\dotnet.exe"],
            new Dictionary<string, string>(StringComparer.Ordinal),
            policy,
            "com.sharplabnext.runtime-job",
            "candidate-scope-1",
            "workspace-1",
            IsolationKind: RuntimeContainerIsolationKind.WineNonRoot,
            WinePrefixPath: "/opt/wine-dotnet");

        await docker.CreateContainerAsync(spec, TestContext.Current.CancellationToken);

        var createRequest = Assert.Single(
            handler.Requests,
            static request => request.Method == HttpMethod.Post &&
                              request.Path.StartsWith("/v1.47/containers/create?", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(createRequest.Body!);
        var root = body.RootElement;
        Assert.Equal("1654:1654", root.GetProperty("User").GetString());
        var host = root.GetProperty("HostConfig");
        var tmpfs = host.GetProperty("Tmpfs");
        Assert.Single(tmpfs.EnumerateObject());
        Assert.Equal(
            "rw,exec,nosuid,nodev,size=50331648,uid=0,gid=0,mode=1777",
            tmpfs.GetProperty("/tmp").GetString());
        Assert.False(tmpfs.TryGetProperty(
            "/opt/wine-dotnet/drive_c/users/root/Temp",
            out _));
        var nofile = Assert.Single(
            host.GetProperty("Ulimits").EnumerateArray(),
            static value => value.GetProperty("Name").GetString() == "nofile");
        Assert.Equal(512, nofile.GetProperty("Soft").GetInt64());
        Assert.Equal(512, nofile.GetProperty("Hard").GetInt64());
    }

    [Fact]
    public void ProfileIsolationResolutionUsesOnlyExplicitSupportedCombinations()
    {
        var profile = Assert.Single(ValidOptions().Profiles);
        Assert.Equal(
            RuntimeContainerIsolationKind.Standard,
            RuntimeJobExecutor.ResolveIsolationKind(profile));

        profile.Container = new RuntimeContainerDefinition
        {
            IsolationKind = RuntimeContainerIsolationKinds.Wine,
            EnvironmentKind = RuntimeContainerEnvironmentKinds.Wine,
            ExecutionUser = RuntimeContainerExecutionUsers.NonRoot,
            WinePrefixPath = "/opt/wine-dotnet"
        };
        Assert.Equal(
            RuntimeContainerIsolationKind.WineNonRoot,
            RuntimeJobExecutor.ResolveIsolationKind(profile));

        profile.Container.ExecutionUser = RuntimeContainerExecutionUsers.Root;
        Assert.Equal(
            RuntimeContainerIsolationKind.WineRoot,
            RuntimeJobExecutor.ResolveIsolationKind(profile));

        profile.Container.IsolationKind = RuntimeContainerIsolationKinds.Standard;
        Assert.Throws<InvalidOperationException>(() =>
            RuntimeJobExecutor.ResolveIsolationKind(profile));
        profile.Container.ExecutionUser = "1000:1000";
        Assert.Throws<InvalidOperationException>(() =>
            RuntimeJobExecutor.ResolveIsolationKind(profile));
    }

    [Fact]
    public void WineRuntimeProfileResolvesRootIsolationAndRejectsJitCapability()
    {
        var profile = WineRuntimeProfile();

        Assert.Equal(
            RuntimeContainerIsolationKind.WineRoot,
            RuntimeJobExecutor.ResolveIsolationKind(profile));
        var exception = Assert.Throws<RuntimeJobFailureException>(() =>
            RuntimeJobExecutor.ValidateCapability(profile, "jit-asm"));
        Assert.Equal("runtime-capability-not-supported", exception.Code);
        Assert.False(exception.Retryable);
    }

    [Fact]
    public async Task JSharpRuntimeContainerUsesDedicatedPrefixTmpfsAndWineOpenFileLimit()
    {
        using var handler = new RecordingDockerHandler();
        using var docker = CreateDockerClient(handler);
        var spec = new RuntimeContainerSpec(
            "jsharp-runtime-test",
            "operation-jsharp",
            "release-1",
            "jsharp-runtime-image:test",
            ["dotnet", "/opt/sharplabnext/SharpLabNext.WineRunner.dll", "wine", "/workspace/Program.exe"],
            new Dictionary<string, string>(StringComparer.Ordinal),
            new RuntimeSecurityPolicyOptions(),
            "com.sharplabnext.runtime-job",
            "candidate-scope-1",
            "workspace-1",
            IsolationKind: RuntimeContainerIsolationKind.WineRoot,
            WinePrefixPath: "/opt/wine-jsharp20");

        await docker.CreateContainerAsync(spec, TestContext.Current.CancellationToken);

        var createRequest = Assert.Single(
            handler.Requests,
            static request => request.Method == HttpMethod.Post &&
                              request.Path.StartsWith("/v1.47/containers/create?", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(createRequest.Body!);
        var root = body.RootElement;
        var host = root.GetProperty("HostConfig");
        Assert.Equal("0:0", root.GetProperty("User").GetString());
        var nofile = Assert.Single(
            host.GetProperty("Ulimits").EnumerateArray(),
            static value => value.GetProperty("Name").GetString() == "nofile");
        Assert.Equal(512, nofile.GetProperty("Soft").GetInt64());
        Assert.Equal(512, nofile.GetProperty("Hard").GetInt64());
        var tmpfs = host.GetProperty("Tmpfs");
        Assert.Equal(2, tmpfs.EnumerateObject().Count());
        Assert.Equal(
            "rw,exec,nosuid,nodev,size=256m",
            tmpfs.GetProperty("/opt/wine-jsharp20/drive_c/users/root/Temp").GetString());
        Assert.False(tmpfs.TryGetProperty("/opt/wine-dotnet/drive_c/users/root/Temp", out _));
    }

    [Fact]
    public void JSharpRuntimeProfileUsesDedicatedIsolationPrefixAndRunEnvironment()
    {
        var profile = JSharpWineRuntimeProfile();

        Assert.Equal(
            RuntimeContainerIsolationKind.WineRoot,
            RuntimeJobExecutor.ResolveIsolationKind(profile));
        var environment = RuntimeJobExecutor.CreateRunEnvironment(profile, RunInstrumentation.None);
        Assert.Equal("/opt/wine-jsharp20", environment["WINEPREFIX"]);
        Assert.Equal("win64", environment["WINEARCH"]);
        Assert.Equal("-all", environment["WINEDEBUG"]);
        Assert.Equal(@"Z:\tmp", environment["SHARPLABNEXT_CAPTURE_DIRECTORY"]);
        var exception = Assert.Throws<RuntimeJobFailureException>(() =>
            RuntimeJobExecutor.ValidateCapability(profile, "jit-asm"));
        Assert.Equal("runtime-capability-not-supported", exception.Code);
    }

    [Fact]
    public void JSharpRuntimeAcceptsOnlyTheExactX64Clr2ArtifactContract()
    {
        var profile = JSharpWineRuntimeProfile();
        var manifest = JSharpManifest();

        RuntimeJobExecutor.ValidateCompatibility(manifest, profile);

        foreach (var architecture in new[] { "anycpu", "x86" })
        {
            var substituted = manifest with
            {
                RuntimeRequirement = manifest.RuntimeRequirement with { Architecture = architecture }
            };
            var exception = Assert.Throws<RuntimeJobFailureException>(() =>
                RuntimeJobExecutor.ValidateCompatibility(substituted, profile));
            Assert.Equal("incompatible-jsharp20-contract", exception.Code);
        }

        var net48Substitution = manifest with
        {
            ReferenceSetId = "netfx48-managed-ref",
            TargetFramework = "net48",
            RuntimeRequirement = manifest.RuntimeRequirement with
            {
                Frameworks = [new FrameworkRequirement(".NETFramework", "4.8")],
                RequiredRuntimeFeatureTags = ["runtime.netfx48-wine"]
            }
        };
        var net48Exception = Assert.Throws<RuntimeJobFailureException>(() =>
            RuntimeJobExecutor.ValidateCompatibility(net48Substitution, profile));
        Assert.Equal("incompatible-jsharp20-contract", net48Exception.Code);
    }

    [Fact]
    public void JSharpAndNetFx48RuntimeProfilesCannotSubstituteForEachOther()
    {
        var jsharpManifest = JSharpManifest();
        var jsharpOnNet48 = Assert.Throws<RuntimeJobFailureException>(() =>
            RuntimeJobExecutor.ValidateCompatibility(jsharpManifest, WineRuntimeProfile()));
        Assert.Equal("incompatible-jsharp20-contract", jsharpOnNet48.Code);

        var net48Manifest = jsharpManifest with
        {
            Producer = jsharpManifest.Producer with
            {
                LanguageId = "csharp",
                ToolchainId = "roslyn-stable-netfx48"
            },
            ReferenceSetId = "netfx48-managed-ref",
            TargetFramework = "net48",
            RuntimeRequirement = new ArtifactRuntimeRequirement(
                "netfx-clr-wine",
                [new FrameworkRequirement(".NETFramework", "4.8")],
                "anycpu",
                ["runtime.netfx48-wine"]),
            EntryPoint = "Program.Main()"
        };
        var net48OnJSharp = Assert.Throws<RuntimeJobFailureException>(() =>
            RuntimeJobExecutor.ValidateCompatibility(net48Manifest, JSharpWineRuntimeProfile()));
        Assert.Equal("incompatible-jsharp20-contract", net48OnJSharp.Code);
    }

    [Fact]
    public void OrdinaryClr2ManagedArtifactIsNotMisclassifiedAsJSharp()
    {
        var profile = WineNetFx20RuntimeProfile();
        var jsharp = JSharpManifest();
        var manifest = jsharp with
        {
            Producer = jsharp.Producer with
            {
                LanguageId = "csharp",
                ToolchainId = "roslyn-stable-netfx20"
            },
            ReferenceSetId = "netfx20-managed-ref",
            RuntimeRequirement = jsharp.RuntimeRequirement with
            {
                RequiredRuntimeFeatureTags = []
            },
            EntryPoint = "SharpLabNext.RuntimeCapabilityProbe.Program.Main"
        };

        RuntimeJobExecutor.ValidateCompatibility(manifest, profile);
    }

    [Theory]
    [InlineData(BuildOutputKind.Auto)]
    [InlineData((BuildOutputKind)999)]
    public void RuntimeCompatibilityRejectsNonConcreteArtifactOutputKinds(BuildOutputKind outputKind)
    {
        var manifest = Manifest(metadata: null, derived: false) with { OutputKind = outputKind };

        var exception = Assert.Throws<RuntimeJobFailureException>(() =>
            RuntimeJobExecutor.ValidateCompatibility(manifest, Assert.Single(ValidOptions().Profiles)));

        Assert.Equal("incompatible-artifact", exception.Code);
        Assert.Equal(WorkerErrorCategory.IncompatibleArtifact, exception.Category);
    }

    [Fact]
    public void RuntimeCompatibilityUsesExplicitFamilyAndFrameworkAllowlists()
    {
        var profile = Assert.Single(ValidOptions().Profiles);
        profile.AcceptedRuntimeFamilies = ["coreclr", "coreclr-compatible"];
        profile.AcceptedFrameworks =
        [
            new RuntimeFrameworkCompatibilityDefinition
            {
                Name = "Microsoft.NETCore.App",
                MinimumVersion = "9.0.0",
                MaximumVersion = "10.0.9"
            }
        ];
        var manifest = Manifest(metadata: null, derived: false) with
        {
            RuntimeRequirement = new ArtifactRuntimeRequirement(
                "coreclr-compatible",
                [new FrameworkRequirement("Microsoft.NETCore.App", "10.0.0")],
                "anycpu",
                [])
        };

        RuntimeJobExecutor.ValidateCompatibility(manifest, profile);

        var wrongVersion = manifest with
        {
            RuntimeRequirement = manifest.RuntimeRequirement with
            {
                Frameworks = [new FrameworkRequirement("Microsoft.NETCore.App", "11.0.0")]
            }
        };
        var frameworkException = Assert.Throws<RuntimeJobFailureException>(() =>
            RuntimeJobExecutor.ValidateCompatibility(wrongVersion, profile));
        Assert.Equal("incompatible-framework", frameworkException.Code);

        var wrongFamily = manifest with
        {
            RuntimeRequirement = manifest.RuntimeRequirement with { Family = "netfx-clr-wine" }
        };
        var familyException = Assert.Throws<RuntimeJobFailureException>(() =>
            RuntimeJobExecutor.ValidateCompatibility(wrongFamily, profile));
        Assert.Equal("incompatible-artifact", familyException.Code);
    }

    [Fact]
    public async Task RuntimeContainerLogsUseDockerUnixTimestampSessionRestartCursor()
    {
        using var handler = new RecordingDockerHandler();
        using var docker = CreateDockerClient(handler);
        var sinceUtc = new DateTimeOffset(2026, 7, 13, 6, 56, 8, TimeSpan.Zero).AddTicks(1006209);

        await using var logs = await docker.OpenContainerLogsSinceAsync(
            RecordingDockerHandler.ContainerId,
            sinceUtc,
            TestContext.Current.CancellationToken);

        Assert.Contains(
            handler.Requests,
            request => request.Path.Contains(
                "since=1783925768.100620900",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task RuntimeContainerResourceMonitorRetainsTheLargestCgroupMemoryObservation()
    {
        using var handler = new RecordingDockerHandler
        {
            StatsPayload = """
                {"memory_stats":{"usage":4096,"max_usage":8192}}
                {"memory_stats":{"usage":16384,"max_usage":12288}}
                {"memory_stats":{"usage":8192,"max_usage":24576}}
                """
        };
        using var docker = CreateDockerClient(handler);

        await using var monitor = await docker.StartContainerResourceMonitorAsync(
            RecordingDockerHandler.ContainerId,
            TestContext.Current.CancellationToken);
        var usage = await monitor.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, usage.SampleCount);
        Assert.Equal(24576, usage.PeakMemoryBytes);
        Assert.Contains(
            handler.Requests,
            request => request.Method == HttpMethod.Get &&
                       request.Path ==
                       $"/v1.47/containers/{RecordingDockerHandler.ContainerId}/stats?stream=true&one-shot=false");
    }

    [Fact]
    public async Task RuntimeContainerResourceMonitorUsesOneShotSampleWhenStreamEndsBeforeFirstObservation()
    {
        using var handler = new RecordingDockerHandler
        {
            StatsPayload = string.Empty,
            StatsOneShotPayload = "{\"memory_stats\":{\"usage\":32768,\"max_usage\":65536}}"
        };
        using var docker = CreateDockerClient(handler);

        await using var monitor = await docker.StartContainerResourceMonitorAsync(
            RecordingDockerHandler.ContainerId,
            TestContext.Current.CancellationToken);
        var usage = await monitor.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, usage.SampleCount);
        Assert.Equal(65536, usage.PeakMemoryBytes);
        Assert.Contains(
            handler.Requests,
            request => request.Method == HttpMethod.Get &&
                       request.Path ==
                       $"/v1.47/containers/{RecordingDockerHandler.ContainerId}/stats?stream=false&one-shot=true");
    }

    [Fact]
    public async Task RuntimeImageInspectionClosesTheImmutableReferenceAgainstDockerIdentityAndSize()
    {
        var digest = new string('b', 64);
        var reference = $"registry.example/sharplabnext/runtime@sha256:{digest}";
        using var handler = new RecordingDockerHandler
        {
            ImageInspectionPayload = $$"""
                {
                  "Id":"sha256:{{new string('c', 64)}}",
                  "RepoDigests":["{{reference}}"],
                  "Size":123456789,
                  "Os":"linux",
                  "Architecture":"amd64"
                }
                """
        };
        using var docker = CreateDockerClient(handler);

        var inspection = await docker.InspectImageAsync(
            reference,
            TestContext.Current.CancellationToken);

        Assert.Equal(reference, inspection.ImmutableReference);
        Assert.Equal($"sha256:{new string('c', 64)}", inspection.ImageId);
        Assert.Equal(123456789, inspection.SizeBytes);
        Assert.Equal("linux", inspection.OperatingSystem);
        Assert.Equal("amd64", inspection.Architecture);
        Assert.Contains(reference, inspection.RepoDigests);
        await Assert.ThrowsAsync<ArgumentException>(() => docker.InspectImageAsync(
            "sharplabnext/runtime:mutable",
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{not-json}")]
    [InlineData("{\"memory_stats\":{\"usage\":0,\"max_usage\":0}}")]
    public async Task RuntimeContainerResourceMonitorRejectsMissingOrMalformedMemoryEvidence(
        string statsPayload)
    {
        using var handler = new RecordingDockerHandler { StatsPayload = statsPayload };
        using var docker = CreateDockerClient(handler);
        await using var monitor = await docker.StartContainerResourceMonitorAsync(
            RecordingDockerHandler.ContainerId,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DockerEngineException>(() =>
            monitor.StopAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RuntimeImageInspectionRejectsARepositoryDigestThatDockerDidNotRetain()
    {
        var reference = $"registry.example/sharplabnext/runtime@sha256:{new string('b', 64)}";
        using var handler = new RecordingDockerHandler
        {
            ImageInspectionPayload = $$"""
                {
                  "Id":"sha256:{{new string('c', 64)}}",
                  "RepoDigests":["registry.example/sharplabnext/other@sha256:{{new string('d', 64)}}"],
                  "Size":123456789,
                  "Os":"linux",
                  "Architecture":"amd64"
                }
                """
        };
        using var docker = CreateDockerClient(handler);

        await Assert.ThrowsAsync<DockerEngineException>(() => docker.InspectImageAsync(
            reference,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RuntimeWorkspaceArchiveOwnsParentDirectoriesForSandboxCleanup()
    {
        using var archive = new MemoryStream();
        using (var writer = new TarWriter(archive, leaveOpen: true))
        {
            var directories = new HashSet<string>(StringComparer.Ordinal);
            RuntimeJobExecutor.WriteArchiveEntry(
                writer,
                directories,
                ".sharplabnext/stdin.txt",
                new MemoryStream("first"u8.ToArray(), writable: false));
            RuntimeJobExecutor.WriteArchiveEntry(
                writer,
                directories,
                ".sharplabnext/ready",
                new MemoryStream("ready\n"u8.ToArray(), writable: false));
        }

        archive.Position = 0;
        using var reader = new TarReader(archive, leaveOpen: true);
        var entries = new List<TarEntry>();
        while (reader.GetNextEntry(copyData: false) is { } entry)
            entries.Add(entry);

        Assert.Collection(
            entries,
            directory =>
            {
                Assert.Equal(".sharplabnext", directory.Name);
                Assert.Equal(TarEntryType.Directory, directory.EntryType);
                Assert.Equal(1654, directory.Uid);
                Assert.Equal(1654, directory.Gid);
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    directory.Mode);
            },
            stdin => Assert.Equal(".sharplabnext/stdin.txt", stdin.Name),
            ready => Assert.Equal(".sharplabnext/ready", ready.Name));
    }

    [Fact]
    public void WineRuntimeWorkspaceArchiveIsOwnedByRootWithoutCapabilities()
    {
        using var archive = new MemoryStream();
        using (var writer = new TarWriter(archive, leaveOpen: true))
        {
            RuntimeJobExecutor.WriteArchiveEntry(
                writer,
                new HashSet<string>(StringComparer.Ordinal),
                ".sharplabnext/ready",
                new MemoryStream("ready\n"u8.ToArray(), writable: false),
                uid: 0,
                gid: 0);
        }

        archive.Position = 0;
        using var reader = new TarReader(archive, leaveOpen: true);
        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            Assert.Equal(0, entry.Uid);
            Assert.Equal(0, entry.Gid);
        }
    }

    [Fact]
    public async Task WorkspaceMaterializerStartsTmpfsKeeperBeforeUploadingArchive()
    {
        using var handler = new RecordingDockerHandler();
        using var docker = CreateDockerClient(handler);
        await using var archive = new MemoryStream("tar payload"u8.ToArray());

        var materialization = await docker.MaterializeWorkspaceAsync(
            "operation-1",
            "release-1",
            "runtime-image:test",
            archive,
            new RuntimeSecurityPolicyOptions(),
            RuntimeContainerIsolationKind.Standard,
            "com.sharplabnext.runtime-job",
            "candidate-scope-1",
            TestContext.Current.CancellationToken);

        Assert.Equal(RecordingDockerHandler.ContainerId, materialization.MaterializerContainerId);
        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/v1.47/volumes/create", request.Path);
                using var body = JsonDocument.Parse(request.Body!);
                Assert.Equal("local", body.RootElement.GetProperty("Driver").GetString());
                var driverOptions = body.RootElement.GetProperty("DriverOpts");
                Assert.Equal("tmpfs", driverOptions.GetProperty("type").GetString());
                Assert.Equal("tmpfs", driverOptions.GetProperty("device").GetString());
                Assert.Contains("uid=1654,gid=1654", driverOptions.GetProperty("o").GetString(), StringComparison.Ordinal);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.StartsWith("/v1.47/containers/create?name=sln-materialize-", request.Path, StringComparison.Ordinal);
                using var body = JsonDocument.Parse(request.Body!);
                Assert.Equal("/bin/sh", body.RootElement.GetProperty("Entrypoint")[0].GetString());
                Assert.Equal("1654:1654", body.RootElement.GetProperty("User").GetString());
                Assert.Contains(
                    "trap 'rm -rf -- /workspace/* /workspace/.[!.]* /workspace/..?*; exit $?' TERM INT",
                    body.RootElement.GetProperty("Cmd")[1].GetString(),
                    StringComparison.Ordinal);
                Assert.Contains(
                    "while :; do sleep 2147483647 & wait $!; done",
                    body.RootElement.GetProperty("Cmd")[1].GetString(),
                    StringComparison.Ordinal);
                var hostConfig = body.RootElement.GetProperty("HostConfig");
                Assert.False(hostConfig.TryGetProperty("Init", out _));
                Assert.Equal("none", hostConfig.GetProperty("LogConfig").GetProperty("Type").GetString());
            },
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(
                    $"/v1.47/containers/{RecordingDockerHandler.ContainerId}/start",
                    request.Path);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.Equal(
                    $"/v1.47/containers/{RecordingDockerHandler.ContainerId}/archive?path=%2Fworkspace",
                    request.Path);
                Assert.Equal("application/x-tar", request.ContentType);
                Assert.Equal("tar payload", request.Body);
            });
    }

    [Fact]
    public async Task WineWorkspaceMaterializerAndVolumeAreRootOwned()
    {
        using var handler = new RecordingDockerHandler();
        using var docker = CreateDockerClient(handler);
        await using var archive = new MemoryStream("tar payload"u8.ToArray());

        await docker.MaterializeWorkspaceAsync(
            "operation-1",
            "release-1",
            "runtime-image:test",
            archive,
            new RuntimeSecurityPolicyOptions(),
            RuntimeContainerIsolationKind.WineRoot,
            "com.sharplabnext.runtime-job",
            "candidate-scope-1",
            TestContext.Current.CancellationToken);

        using var volume = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Contains(
            "uid=0,gid=0,mode=0700",
            volume.RootElement.GetProperty("DriverOpts").GetProperty("o").GetString(),
            StringComparison.Ordinal);
        using var materializer = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.Equal("0:0", materializer.RootElement.GetProperty("User").GetString());
        var host = materializer.RootElement.GetProperty("HostConfig");
        Assert.True(host.GetProperty("ReadonlyRootfs").GetBoolean());
        Assert.Equal("ALL", Assert.Single(host.GetProperty("CapDrop").EnumerateArray()).GetString());
        Assert.Contains(
            host.GetProperty("SecurityOpt").EnumerateArray(),
            static value => value.GetString() == "no-new-privileges:true");
    }

    [Fact]
    public async Task WineCoreClrWorkspaceMaterializerAndVolumeAreOwnedByExecutionUser()
    {
        using var handler = new RecordingDockerHandler();
        using var docker = CreateDockerClient(handler);
        await using var archive = new MemoryStream("tar payload"u8.ToArray());

        await docker.MaterializeWorkspaceAsync(
            "operation-1",
            "release-1",
            "runtime-image:test",
            archive,
            new RuntimeSecurityPolicyOptions(),
            RuntimeContainerIsolationKind.WineNonRoot,
            "com.sharplabnext.runtime-job",
            "candidate-scope-1",
            TestContext.Current.CancellationToken);

        using var volume = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Contains(
            "uid=1654,gid=1654,mode=0700",
            volume.RootElement.GetProperty("DriverOpts").GetProperty("o").GetString(),
            StringComparison.Ordinal);
        using var materializer = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.Equal("1654:1654", materializer.RootElement.GetProperty("User").GetString());
    }

    [Fact]
    public async Task WorkspaceMaterializerStopUsesBoundedGracefulDockerApi()
    {
        using var handler = new RecordingDockerHandler();
        using var docker = CreateDockerClient(handler);

        await docker.StopContainerAsync(
            RecordingDockerHandler.ContainerId,
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            $"/v1.47/containers/{RecordingDockerHandler.ContainerId}/stop?t=2",
            request.Path);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            docker.StopContainerAsync(
                RecordingDockerHandler.ContainerId,
                TimeSpan.FromSeconds(11),
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.NotModified)]
    public async Task WorkspaceMaterializerStopFailsClosedWhenKeeperIsMissingOrAlreadyStopped(
        HttpStatusCode statusCode)
    {
        using var handler = new RecordingDockerHandler
        {
            StopStatusCode = statusCode
        };
        using var docker = CreateDockerClient(handler);

        await Assert.ThrowsAsync<DockerEngineException>(() =>
            docker.StopContainerAsync(
                RecordingDockerHandler.ContainerId,
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ArchiveUploadFailureLeavesCallerOwnedStreamOpenForRetry()
    {
        using var handler = new RecordingDockerHandler
        {
            ArchiveStatusCode = HttpStatusCode.BadGateway
        };
        using var docker = CreateDockerClient(handler);
        await using var archive = new MemoryStream("retryable tar payload"u8.ToArray());

        await Assert.ThrowsAsync<DockerEngineException>(() => docker.UploadArchiveAsync(
            RecordingDockerHandler.ContainerId,
            archive,
            TestContext.Current.CancellationToken));

        Assert.True(archive.CanRead);
        Assert.True(archive.CanSeek);
        archive.Position = 0;
        handler.ArchiveStatusCode = HttpStatusCode.OK;
        await docker.UploadArchiveAsync(
            RecordingDockerHandler.ContainerId,
            archive,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["retryable tar payload", "retryable tar payload"],
            handler.Requests.Select(static request => request.Body));
    }

    [Fact]
    public async Task RuntimeSessionReusesStoppedContainerAndCyclesTmpfsKeeperAfterEveryJob()
    {
        var root = FindRepositoryRoot();
        var options = ValidOptions();
        var fakeDocker = new SessionDockerClient();
        var sandbox = RuntimeSandboxPolicy.Load(
            new RuntimeSandboxOptions
            {
                SeccompProfilePath = Path.Combine(
                    root,
                    "src",
                    "Supervisor",
                    "SharpLabNext.RuntimeSupervisor",
                    "security",
                    "runtime-job-seccomp.v1.json")
            },
            root);
        var sessions = new RuntimeSessionRegistry(
            fakeDocker,
            Options.Create(options),
            sandbox,
            NullLogger<RuntimeSessionRegistry>.Instance);
        var request = new RuntimeSessionRequest(
            "rs_0123456789abcdef",
            "release-1",
            "runtime-image:test",
            ["dotnet", "/opt/sharplabnext/SharpLabNext.Runner.dll", "app.dll"],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["MODE"] = "run" },
            options.SecurityPolicies[0],
            RuntimeContainerIsolationKind.Standard,
            WinePrefixPath: null,
            options.ContainerLabel,
            options.ResourceScope);

        await using var firstArchive = new MemoryStream("first"u8.ToArray());
        var first = await sessions.AcquireAsync(request, firstArchive, TestContext.Current.CancellationToken);
        Assert.False(first.Reused);
        Assert.Equal("runtime-1", first.ContainerId);
        await first.CompleteAsync(reusable: true);

        await using var secondArchive = new MemoryStream("second"u8.ToArray());
        var second = await sessions.AcquireAsync(request, secondArchive, TestContext.Current.CancellationToken);
        Assert.True(second.Reused);
        Assert.Equal(first.ContainerId, second.ContainerId);
        await second.CompleteAsync(reusable: true);
        await sessions.ReleaseAsync(request.SessionId, TestContext.Current.CancellationToken);

        Assert.Equal(1, fakeDocker.RuntimeContainerCreates);
        Assert.Equal(["first", "second"], fakeDocker.UploadedArchives);
        Assert.Equal(["materializer-1", "materializer-1"], fakeDocker.StartedContainers);
        Assert.Equal(["materializer-1", "materializer-1"], fakeDocker.StoppedContainers);
        Assert.Equal(
            [
                "start:materializer-1",
                "upload:first",
                "stop:materializer-1",
                "start:materializer-1",
                "upload:second",
                "stop:materializer-1"
            ],
            fakeDocker.WorkspaceLifecycle);
        Assert.Contains("runtime-1", fakeDocker.RemovedContainers);
        Assert.Contains("materializer-1", fakeDocker.RemovedContainers);
        Assert.Contains("workspace-1", fakeDocker.RemovedVolumes);
    }

    [Fact]
    public async Task RuntimeSessionReleasedBeforeAcquireRejectsWithoutCreatingDockerResources()
    {
        var root = FindRepositoryRoot();
        var options = ValidOptions();
        var fakeDocker = new SessionDockerClient();
        var sandbox = RuntimeSandboxPolicy.Load(
            new RuntimeSandboxOptions
            {
                SeccompProfilePath = Path.Combine(
                    root,
                    "src",
                    "Supervisor",
                    "SharpLabNext.RuntimeSupervisor",
                    "security",
                    "runtime-job-seccomp.v1.json")
            },
            root);
        var sessions = new RuntimeSessionRegistry(
            fakeDocker,
            Options.Create(options),
            sandbox,
            NullLogger<RuntimeSessionRegistry>.Instance);
        var request = new RuntimeSessionRequest(
            "rs_released_before_acquire",
            "release-1",
            "runtime-image:test",
            ["dotnet", "/opt/sharplabnext/SharpLabNext.Runner.dll", "app.dll"],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["MODE"] = "run" },
            options.SecurityPolicies[0],
            RuntimeContainerIsolationKind.Standard,
            WinePrefixPath: null,
            options.ContainerLabel,
            options.ResourceScope);

        await sessions.ReleaseAsync(request.SessionId, TestContext.Current.CancellationToken);
        await using var archive = new MemoryStream("must-not-upload"u8.ToArray());

        await Assert.ThrowsAsync<RuntimeSessionClosingException>(() =>
            sessions.AcquireAsync(request, archive, TestContext.Current.CancellationToken));
        Assert.Equal(0, fakeDocker.RuntimeContainerCreates);
        Assert.Empty(fakeDocker.UploadedArchives);
        Assert.Empty(fakeDocker.StartedContainers);
        Assert.Empty(fakeDocker.RemovedContainers);
        Assert.Empty(fakeDocker.RemovedVolumes);
    }

    [Fact]
    public async Task RuntimeSessionReleasedBeforeOneShotAdmissionRejectsWhenReuseIsDisabled()
    {
        var fakeDocker = new SessionDockerClient();
        var (sessions, request) = CreateRuntimeSessionFixture(
            fakeDocker,
            "rs_released_before_one_shot",
            sessionReuseEnabled: false);
        Assert.False(sessions.Enabled);

        await sessions.ReleaseAsync(request.SessionId, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<RuntimeSessionClosingException>(() =>
            sessions.AcquireOneShotAdmissionAsync(
                request.SessionId,
                TestContext.Current.CancellationToken));
        Assert.Equal(0, fakeDocker.RuntimeContainerCreates);
        Assert.Empty(fakeDocker.UploadedArchives);
        Assert.Empty(fakeDocker.StartedContainers);
        Assert.Empty(fakeDocker.RemovedContainers);
        Assert.Empty(fakeDocker.RemovedVolumes);
    }

    [Fact]
    public async Task ReleasedRuntimeSessionCannotCreateOneShotDockerResourcesWhenReuseIsDisabled()
    {
        const string sessionId = "rs_released_executor_one_shot";
        var options = ValidOptions();
        options.SessionReuseEnabled = false;
        var fakeDocker = new SessionDockerClient();
        var root = FindRepositoryRoot();
        var sandbox = RuntimeSandboxPolicy.Load(
            new RuntimeSandboxOptions
            {
                SeccompProfilePath = Path.Combine(
                    root,
                    "src",
                    "Supervisor",
                    "SharpLabNext.RuntimeSupervisor",
                    "security",
                    "runtime-job-seccomp.v1.json")
            },
            root);
        var sessions = new RuntimeSessionRegistry(
            fakeDocker,
            Options.Create(options),
            sandbox,
            NullLogger<RuntimeSessionRegistry>.Instance);
        var artifactStore = new RuntimeArtifactStoreClient(new ArtifactBundleDescriptor(
            Manifest(metadata: null, derived: false),
            []));
        var operations = new OperationStore();
        await using var scheduler = new BoundedOperationScheduler(
            operations,
            new OperationExecutionOptions
            {
                QueueCapacity = 1,
                WorkerConcurrency = 1,
                ExecutorId = "runtime-session-test"
            });
        var executor = new RuntimeJobExecutor(
            operations,
            scheduler,
            artifactStore,
            fakeDocker,
            sessions,
            Options.Create(options),
            new ServiceIdentity(
                "runtime-supervisor",
                ServiceKind.RuntimeSupervisor,
                "release-1",
                ProtocolVersion.WorkerV1,
                [],
                "ready"),
            NullLogger<RuntimeJobExecutor>.Instance);
        var request = new RunRequest(
            "released-one-shot-run",
            "released-one-shot-run-key",
            "pr_released_one_shot",
            artifactStore.Descriptor.Manifest.ArtifactId,
            "dotnet-10-linux-x64",
            new RunOptions([], null, RunInstrumentation.None, "runtime-job-default"),
            DateTimeOffset.UtcNow.AddMinutes(1));
        var operation = operations.Start(
            request.RequestId,
            request.IdempotencyKey,
            OperationKind.Run,
            "runtime-session-test-trace",
            DateTimeOffset.UtcNow);

        await sessions.ReleaseAsync(sessionId, TestContext.Current.CancellationToken);
        executor.QueueRun(operation, request, sessionId);
        var state = await WaitForTerminalAsync(operations, operation.Handle.OperationId);

        Assert.Equal(OperationStatus.Failed, state.Status);
        Assert.Equal("runtime-job-failed", state.Error?.Code);
        Assert.Equal(0, fakeDocker.RuntimeContainerCreates);
        Assert.Empty(fakeDocker.UploadedArchives);
        Assert.Empty(fakeDocker.StartedContainers);
        Assert.Empty(fakeDocker.RemovedContainers);
        Assert.Empty(fakeDocker.RemovedVolumes);
    }

    [Fact]
    public async Task OneShotMaterializerRemovalFailureIsRetriedBeforeWorkspaceVolumeCleanup()
    {
        var options = ValidOptions();
        var fakeDocker = new SessionDockerClient
        {
            NextRemoveContainerException = new HttpRequestException("Transient materializer removal failure.")
        };
        var root = FindRepositoryRoot();
        var sandbox = RuntimeSandboxPolicy.Load(
            new RuntimeSandboxOptions
            {
                SeccompProfilePath = Path.Combine(
                    root,
                    "src",
                    "Supervisor",
                    "SharpLabNext.RuntimeSupervisor",
                    "security",
                    "runtime-job-seccomp.v1.json")
            },
            root);
        var sessions = new RuntimeSessionRegistry(
            fakeDocker,
            Options.Create(options),
            sandbox,
            NullLogger<RuntimeSessionRegistry>.Instance);
        var artifactStore = new RuntimeArtifactStoreClient(new ArtifactBundleDescriptor(
            Manifest(metadata: null, derived: false),
            []));
        var operations = new OperationStore();
        await using var scheduler = new BoundedOperationScheduler(
            operations,
            new OperationExecutionOptions
            {
                QueueCapacity = 1,
                WorkerConcurrency = 1,
                ExecutorId = "runtime-one-shot-cleanup-test"
            });
        var executor = new RuntimeJobExecutor(
            operations,
            scheduler,
            artifactStore,
            fakeDocker,
            sessions,
            Options.Create(options),
            new ServiceIdentity(
                "runtime-supervisor",
                ServiceKind.RuntimeSupervisor,
                "release-1",
                ProtocolVersion.WorkerV1,
                [],
                "ready"),
            NullLogger<RuntimeJobExecutor>.Instance);
        var request = new RunRequest(
            "one-shot-cleanup-retry",
            "one-shot-cleanup-retry-key",
            "pr_one_shot_cleanup_retry",
            artifactStore.Descriptor.Manifest.ArtifactId,
            "dotnet-10-linux-x64",
            new RunOptions([], null, RunInstrumentation.None, "runtime-job-default"),
            DateTimeOffset.UtcNow.AddMinutes(1));
        var operation = operations.Start(
            request.RequestId,
            request.IdempotencyKey,
            OperationKind.Run,
            "runtime-one-shot-cleanup-trace",
            DateTimeOffset.UtcNow);

        executor.QueueRun(operation, request);
        await WaitForTerminalAsync(operations, operation.Handle.OperationId);
        for (var attempt = 0; attempt < 200 && fakeDocker.RemovedVolumes.Count == 0; attempt++)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.Equal(
            ["materializer-1", "runtime-1", "materializer-1"],
            fakeDocker.RemoveContainerAttempts);
        Assert.Equal(
            [
                "remove-failed:materializer-1",
                "remove:runtime-1",
                "remove:materializer-1",
                "volume:workspace-1"
            ],
            fakeDocker.CleanupLifecycle);
        Assert.Equal(["workspace-1"], fakeDocker.RemovedVolumes);
    }

    [Fact]
    public async Task RuntimeCapabilityPreflightProducesRunInspectionAndExecutionFlowEvidence()
    {
        await using var fixture = await CreateRuntimeCapabilityFixtureAsync(
            ["run", "inspection", "execution-flow"],
            RuntimeJitSourceMappingKinds.None);

        var response = await fixture.Coordinator.ProduceAsync(
            fixture.CreateRequest(),
            TestContext.Current.CancellationToken);

        var documents = response.Documents.ToDictionary(
            static document => document["capability"]!.GetValue<string>(),
            StringComparer.Ordinal);
        Assert.Equal(["execution-flow", "inspection", "run"], documents.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("passed", documents["run"]["result"]!.GetValue<string>());
        Assert.True(documents["run"]["run"]!["exceptionFrameValidated"]!.GetValue<bool>());
        Assert.Equal(2, documents["inspection"]["inspection"]!["recordCount"]!.GetValue<int>());
        Assert.Equal(3, documents["execution-flow"]["executionFlow"]!["recordCount"]!.GetValue<int>());
        Assert.Equal(fixture.FlowArtifactRef.Value,
            documents["execution-flow"]["executionFlow"]!["derivedArtifactSha256"]!.GetValue<string>());
        Assert.All(response.Documents, document => Assert.Equal(
            fixture.PlanSha256,
            document["producer"]!["planSha256"]!.GetValue<string>()));
        Assert.Equal(8, fixture.Docker.RuntimeContainerCreates);
        Assert.Equal(8, fixture.Docker.RuntimeContainerCreates - fixture.Docker.ActiveContainers.Count);
        Assert.All(response.Documents, static document =>
        {
            Assert.NotNull(document["lifecycle"]?["outputOverflow"]);
            Assert.NotNull(document["lifecycle"]?["timeout"]);
            Assert.NotNull(document["lifecycle"]?["cancellation"]);
            Assert.NotNull(document["lifecycle"]?["processTreeCleanup"]);
        });
    }

    [Theory]
    [InlineData(RuntimeContainerExecutionUsers.Root, RuntimeContainerIsolationKind.WineRoot)]
    [InlineData(RuntimeContainerExecutionUsers.NonRoot, RuntimeContainerIsolationKind.WineNonRoot)]
    public async Task RuntimeCapabilityEvidenceReportsTheResolvedWineExecutionUser(
        string executionUser,
        RuntimeContainerIsolationKind expectedIsolation)
    {
        await using var fixture = await CreateRuntimeCapabilityFixtureAsync(
            ["run"],
            RuntimeJitSourceMappingKinds.None);
        fixture.Profile.Container = new RuntimeContainerDefinition
        {
            IsolationKind = RuntimeContainerIsolationKinds.Wine,
            EnvironmentKind = RuntimeContainerEnvironmentKinds.Wine,
            ExecutionUser = executionUser,
            WinePrefixPath = "/opt/wine-dotnet"
        };

        var response = await fixture.Coordinator.ProduceAsync(
            fixture.CreateRequest(),
            TestContext.Current.CancellationToken);

        var run = Assert.Single(response.Documents);
        Assert.Equal(executionUser, run["sandbox"]!["user"]!.GetValue<string>());
        Assert.All(
            fixture.Docker.CreatedSpecs,
            spec => Assert.Equal(expectedIsolation, spec.IsolationKind));
    }

    [Fact]
    public async Task RuntimeCapabilityPreflightProducesMethodOnlyJitEvidenceWithoutPdbClaims()
    {
        await using var fixture = await CreateRuntimeCapabilityFixtureAsync(
            ["run", "jit-asm"],
            RuntimeJitSourceMappingKinds.None);

        var response = await fixture.Coordinator.ProduceAsync(
            fixture.CreateRequest(),
            TestContext.Current.CancellationToken);

        var jit = Assert.Single(response.Documents, static document =>
            document["capability"]!.GetValue<string>() == "jit-asm")["jit"]!;
        Assert.Null(jit["pdb"]);
        Assert.Equal("none", jit["mapping"]!["kind"]!.GetValue<string>());
        Assert.Equal("method", jit["mapping"]!["source"]!.GetValue<string>());
        Assert.Equal(0, jit["mapping"]!["rangeCount"]!.GetValue<int>());
        Assert.False(jit["mapping"]!["allRangesMatchPdb"]!.GetValue<bool>());
        Assert.Empty(jit["methods"]![0]!["sourceRanges"]!.AsArray());
    }

    [Fact]
    public async Task RuntimeCapabilityPreflightValidatesMappedJitRangesAgainstRealPeAndPortablePdb()
    {
        await using var fixture = await CreateRuntimeCapabilityFixtureAsync(
            ["run", "jit-asm"],
            RuntimeJitSourceMappingKinds.LinuxProfiler);

        var response = await fixture.Coordinator.ProduceAsync(
            fixture.CreateRequest(),
            TestContext.Current.CancellationToken);

        var document = Assert.Single(response.Documents, static document =>
            document["capability"]!.GetValue<string>() == "jit-asm");
        var jit = document["jit"]!;
        Assert.Equal("linux-profiler", jit["mapping"]!["kind"]!.GetValue<string>());
        Assert.Equal("ordinary", jit["mapping"]!["source"]!.GetValue<string>());
        Assert.True(jit["mapping"]!["allRangesMatchPdb"]!.GetValue<bool>());
        Assert.InRange(jit["mapping"]!["rangeCount"]!.GetValue<int>(), 2, int.MaxValue);
        Assert.Equal(fixture.PdbIdentity.ContentId, jit["pdb"]!["contentId"]!.GetValue<string>());
        Assert.Equal(fixture.PdbIdentity.Digest, jit["pdb"]!["sha256"]!.GetValue<string>());
        Assert.Equal(
            "/workspace/SharpLabNext.RuntimeCapabilityProbe.pdb",
            jit["pdb"]!["path"]!.GetValue<string>());
        Assert.Contains(document["artifacts"]!.AsArray(), static artifact =>
            artifact!["role"]!.GetValue<string>() == "profiler");
    }

    [Fact]
    public async Task RuntimeCapabilityPreflightDocumentsPassSharedPromotionValidation()
    {
        await using var fixture = await CreateRuntimeCapabilityFixtureAsync(
            ["run", "jit-asm", "inspection", "execution-flow"],
            RuntimeJitSourceMappingKinds.LinuxProfiler);
        var response = await fixture.Coordinator.ProduceAsync(
            fixture.CreateRequest(),
            TestContext.Current.CancellationToken);
        var documents = response.Documents.ToDictionary(
            static document => document["capability"]!.GetValue<string>(),
            static document => Encoding.UTF8.GetBytes(document.ToJsonString() + "\n"),
            StringComparer.Ordinal);
        var profileBytes = JsonSerializer.SerializeToUtf8Bytes<RuntimeProfileDefinition>(
            fixture.Profile,
            RuntimeProfilePreflightJsonOptions);
        var receiptBytes = CreateCapabilityDraftReceipt(fixture, response.Documents, documents);

        var context = RuntimeCapabilityEvidencePreflightValidator.CreateContext(
            profileBytes,
            receiptBytes,
            fixture.Policy.Id);
        var retainedImageFiles = new Dictionary<string, RuntimeCapabilityEvidenceImageFile>(
            StringComparer.Ordinal);
        foreach (var capability in context.Capabilities)
        {
            var validated = context.ValidateDocument(documents[capability]);

            Assert.Equal(capability, validated.Capability);
            Assert.Equal(
                $"profiles/runtime-promotion-evidence/{fixture.Profile.Id}/{capability}.json",
                validated.EvidencePath);
            foreach (var file in validated.ImageFiles)
            {
                if (retainedImageFiles.TryGetValue(file.Path, out var existing))
                    Assert.Equal(existing, file);
                else
                    retainedImageFiles.Add(file.Path, file);
            }
        }

        Assert.Equal(["execution-flow", "inspection", "jit-asm", "run"], context.Capabilities);
        Assert.Contains(retainedImageFiles.Values, static file => file.Role == "runtime-host");
        Assert.Contains(retainedImageFiles.Values, static file => file.Role == "profiler");
        Assert.Contains(retainedImageFiles.Values, static file => file.Role == "jit-library");
    }

    [Fact]
    public async Task RuntimeCapabilityPreflightEndpointPreservesPublicErrorStatusAndPascalCaseBody()
    {
        await using var fixture = await CreateRuntimeCapabilityFixtureAsync(
            ["run"],
            RuntimeJitSourceMappingKinds.None);
        var request = fixture.CreateRequest() with { RuntimeProfileId = "not-installed" };

        var result = await RuntimeCapabilityPreflightEndpoint.HandleAsync(
            request,
            fixture.Coordinator,
            TestContext.Current.CancellationToken);
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Response.Body = new MemoryStream();

        await result.ExecuteAsync(http);

        Assert.Equal(StatusCodes.Status404NotFound, http.Response.StatusCode);
        http.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(
            http.Response.Body,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(
            "capability-profile-not-installed",
            document.RootElement.GetProperty("Error").GetString());
        Assert.Equal(
            "The selected Runtime Profile is not installed.",
            document.RootElement.GetProperty("Message").GetString());
        Assert.False(document.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void RuntimeSupervisorDescriptorDeclaresBothPreflightCapabilitiesExactlyOnce()
    {
        Assert.Equal(
            RuntimeSupervisorServiceCapabilities.All.Count,
            RuntimeSupervisorServiceCapabilities.All.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("runtime-capability-preflight", RuntimeSupervisorServiceCapabilities.All);
        Assert.Contains("runtime-performance-preflight", RuntimeSupervisorServiceCapabilities.All);
    }

    [Fact]
    public async Task RuntimeCapabilityPreflightRejectsImageArtifactMismatchBeforeQueueing()
    {
        await using var fixture = await CreateRuntimeCapabilityFixtureAsync(
            ["run"],
            RuntimeJitSourceMappingKinds.None);
        fixture.Docker.CorruptInspectedRole = "helper";

        var exception = await Assert.ThrowsAsync<RuntimeCapabilityPreflightException>(() =>
            fixture.Coordinator.ProduceAsync(
                fixture.CreateRequest(),
                TestContext.Current.CancellationToken));

        Assert.Equal("capability-helper-identity-mismatch", exception.Code);
        Assert.Equal(0, fixture.Docker.RuntimeContainerCreates);
        Assert.Empty(fixture.Docker.ActiveContainers);
    }

    [Fact]
    public async Task RuntimeCapabilityPreflightRejectsInvalidPlanDigestBeforeQueueing()
    {
        await using var fixture = await CreateRuntimeCapabilityFixtureAsync(
            ["run"],
            RuntimeJitSourceMappingKinds.None);

        var exception = await Assert.ThrowsAsync<RuntimeCapabilityPreflightException>(() =>
            fixture.Coordinator.ProduceAsync(
                fixture.CreateRequest() with { PlanSha256 = "sha256:invalid" },
                TestContext.Current.CancellationToken));

        Assert.Equal("invalid-capability-plan-digest", exception.Code);
        Assert.Equal(0, fixture.Docker.RuntimeContainerCreates);
        Assert.Empty(fixture.Docker.ActiveContainers);
    }

    [Fact]
    public async Task RuntimePerformanceSampleMeasuresQueueThroughCleanupWithDockerPeakMemory()
    {
        var fakeDocker = new SessionDockerClient
        {
            ContainerLogBytes = await CompletedRunFramesAsync(),
            ResourceUsage = new RuntimeContainerResourceUsage(73400320, 4)
        };
        await using var fixture = CreateRuntimePerformanceFixture(fakeDocker);

        var sample = await fixture.Coordinator.MeasureAsync(
            new RuntimePerformanceSampleRequest(
                fixture.Options.Profiles[0].Id,
                fixture.Options.PromotionPreflightPlanSha256!,
                fixture.ArtifactStore.Descriptor.Manifest.ArtifactId,
                fixture.Options.SecurityPolicies[0].Id,
                RuntimePerformanceScenarios.Run),
            TestContext.Current.CancellationToken);

        Assert.Equal(RuntimePerformanceScenarios.Run, sample.Scenario);
        Assert.Equal(73400320, sample.Sample.PeakMemoryBytes);
        Assert.Equal(4, sample.ResourceSampleCount);
        Assert.True(sample.Sample.LatencyMilliseconds > 0);
        Assert.Equal(fixture.Options.Profiles[0].Image, sample.Image.Reference);
        Assert.Equal(fixture.Options.Profiles[0].RuntimeImageId, sample.Image.ImageId);
        Assert.Equal(fixture.Options.SecurityPolicies[0].MemoryBytes, sample.Environment.MemoryLimitBytes);
        Assert.Equal(["runtime-1"], fakeDocker.ResourceMonitorStarts);
        Assert.Contains("runtime-1", fakeDocker.RemovedContainers);
        Assert.Contains("materializer-1", fakeDocker.RemovedContainers);
        Assert.Contains("workspace-1", fakeDocker.RemovedVolumes);
    }

    [Fact]
    public async Task RuntimePerformanceJitSamplePublishesOutputAndReturnsACompletedSample()
    {
        var fakeDocker = new SessionDockerClient
        {
            ContainerLogBytes = await CompletedJitFramesAsync(sequencePointRangeCount: 0),
            ResourceUsage = new RuntimeContainerResourceUsage(83886080, 3)
        };
        await using var fixture = CreateRuntimePerformanceFixture(fakeDocker);

        var sample = await fixture.Coordinator.MeasureAsync(
            new RuntimePerformanceSampleRequest(
                fixture.Options.Profiles[0].Id,
                fixture.Options.PromotionPreflightPlanSha256!,
                fixture.ArtifactStore.Descriptor.Manifest.ArtifactId,
                fixture.Options.SecurityPolicies[0].Id,
                RuntimePerformanceScenarios.Jit,
                "Program.Main"),
            TestContext.Current.CancellationToken);

        Assert.Equal(RuntimePerformanceScenarios.Jit, sample.Scenario);
        Assert.Equal(RuntimeJitSourceMappingKinds.None, sample.SourceMappingKind);
        Assert.Equal(0, sample.DistinctSequencePointRangeCount);
        Assert.Equal(2, fixture.ArtifactStore.PublishedContentRefs.Count);
        Assert.Contains("Program.Main", Assert.Single(fakeDocker.CreatedSpecs).Command);
        Assert.Contains("runtime-1", fakeDocker.RemovedContainers);
        Assert.Contains("workspace-1", fakeDocker.RemovedVolumes);
    }

    [Fact]
    public async Task RuntimePerformanceMappingSampleRequiresTwoDistinctSequencePointRanges()
    {
        var fakeDocker = new SessionDockerClient
        {
            ContainerLogBytes = await CompletedJitFramesAsync(sequencePointRangeCount: 1)
        };
        await using var fixture = CreateRuntimePerformanceFixture(
            fakeDocker,
            RuntimeJitSourceMappingKinds.LinuxProfiler);

        var exception = await Assert.ThrowsAsync<RuntimePerformancePreflightException>(() =>
            fixture.Coordinator.MeasureAsync(
                new RuntimePerformanceSampleRequest(
                    fixture.Options.Profiles[0].Id,
                    fixture.Options.PromotionPreflightPlanSha256!,
                    fixture.ArtifactStore.Descriptor.Manifest.ArtifactId,
                    fixture.Options.SecurityPolicies[0].Id,
                    RuntimePerformanceScenarios.Mapping,
                    "Program.Main"),
                TestContext.Current.CancellationToken));

        Assert.Equal("performance-mapping-unavailable", exception.Code);
        Assert.Contains("runtime-1", fakeDocker.RemovedContainers);
        Assert.Contains("workspace-1", fakeDocker.RemovedVolumes);
    }

    [Fact]
    public async Task RuntimePerformanceMappingSampleRetainsDistinctSequencePointCount()
    {
        var fakeDocker = new SessionDockerClient
        {
            ContainerLogBytes = await CompletedJitFramesAsync(sequencePointRangeCount: 2)
        };
        await using var fixture = CreateRuntimePerformanceFixture(
            fakeDocker,
            RuntimeJitSourceMappingKinds.LinuxProfiler);

        var sample = await fixture.Coordinator.MeasureAsync(
            new RuntimePerformanceSampleRequest(
                fixture.Options.Profiles[0].Id,
                fixture.Options.PromotionPreflightPlanSha256!,
                fixture.ArtifactStore.Descriptor.Manifest.ArtifactId,
                fixture.Options.SecurityPolicies[0].Id,
                RuntimePerformanceScenarios.Mapping,
                "Program.Main"),
            TestContext.Current.CancellationToken);

        Assert.Equal(RuntimePerformanceScenarios.Mapping, sample.Scenario);
        Assert.Equal(RuntimeJitSourceMappingKinds.LinuxProfiler, sample.SourceMappingKind);
        Assert.Equal(2, sample.DistinctSequencePointRangeCount);
    }

    [Fact]
    public async Task RuntimePerformanceSampleRejectsMissingDockerStatsEvidence()
    {
        var fakeDocker = new SessionDockerClient
        {
            ContainerLogBytes = await CompletedRunFramesAsync(),
            NextResourceMonitorStopException = new DockerEngineException("No positive stats sample.")
        };
        await using var fixture = CreateRuntimePerformanceFixture(fakeDocker);

        var exception = await Assert.ThrowsAsync<RuntimePerformancePreflightException>(() =>
            fixture.Coordinator.MeasureAsync(
                new RuntimePerformanceSampleRequest(
                    fixture.Options.Profiles[0].Id,
                    fixture.Options.PromotionPreflightPlanSha256!,
                    fixture.ArtifactStore.Descriptor.Manifest.ArtifactId,
                    fixture.Options.SecurityPolicies[0].Id,
                    RuntimePerformanceScenarios.Run),
                TestContext.Current.CancellationToken));

        Assert.Equal("resource-monitor-stop-failed", exception.Code);
        Assert.Contains("runtime-1", fakeDocker.RemovedContainers);
        Assert.Contains("workspace-1", fakeDocker.RemovedVolumes);
    }

    [Fact]
    public async Task RuntimePerformanceSampleRejectsIncompleteOneShotCleanup()
    {
        var fakeDocker = new SessionDockerClient
        {
            ContainerLogBytes = await CompletedRunFramesAsync(),
            FailRuntimeContainerRemoval = true
        };
        await using var fixture = CreateRuntimePerformanceFixture(fakeDocker);

        var exception = await Assert.ThrowsAsync<RuntimePerformancePreflightException>(() =>
            fixture.Coordinator.MeasureAsync(
                new RuntimePerformanceSampleRequest(
                    fixture.Options.Profiles[0].Id,
                    fixture.Options.PromotionPreflightPlanSha256!,
                    fixture.ArtifactStore.Descriptor.Manifest.ArtifactId,
                    fixture.Options.SecurityPolicies[0].Id,
                    RuntimePerformanceScenarios.Run),
                TestContext.Current.CancellationToken));

        Assert.Equal("performance-cleanup-failed", exception.Code);
        Assert.Contains("remove-failed:runtime-1", fakeDocker.CleanupLifecycle);
        Assert.Contains("workspace-1", fakeDocker.RemovedVolumes);
    }

    [Fact]
    public async Task RuntimePerformanceSampleReportsQueueRejectionWithoutStartingDockerResources()
    {
        var fakeDocker = new SessionDockerClient { ContainerLogBytes = await CompletedRunFramesAsync() };
        await using var fixture = CreateRuntimePerformanceFixture(fakeDocker);
        await fixture.Scheduler.DisposeAsync();

        var exception = await Assert.ThrowsAsync<RuntimePerformancePreflightException>(() =>
            fixture.Coordinator.MeasureAsync(
                new RuntimePerformanceSampleRequest(
                    fixture.Options.Profiles[0].Id,
                    fixture.Options.PromotionPreflightPlanSha256!,
                    fixture.ArtifactStore.Descriptor.Manifest.ArtifactId,
                    fixture.Options.SecurityPolicies[0].Id,
                    RuntimePerformanceScenarios.Run),
                TestContext.Current.CancellationToken));

        Assert.Equal("performance-queue-rejected", exception.Code);
        Assert.Empty(fakeDocker.StartedContainers);
        Assert.Empty(fakeDocker.ResourceMonitorStarts);
    }

    [Fact]
    public async Task RuntimePerformanceSampleRejectsAnInspectedImageIdentityMismatchBeforeQueueing()
    {
        var immutableReference = $"registry.example/sharplabnext/runtime@sha256:{new string('b', 64)}";
        var fakeDocker = new SessionDockerClient
        {
            ImageInspection = new RuntimeImageInspection(
                immutableReference,
                $"sha256:{new string('d', 64)}",
                536870912,
                "linux",
                "amd64",
                [immutableReference])
        };
        await using var fixture = CreateRuntimePerformanceFixture(fakeDocker);

        var exception = await Assert.ThrowsAsync<RuntimePerformancePreflightException>(() =>
            fixture.Coordinator.MeasureAsync(
                new RuntimePerformanceSampleRequest(
                    fixture.Options.Profiles[0].Id,
                    fixture.Options.PromotionPreflightPlanSha256!,
                    fixture.ArtifactStore.Descriptor.Manifest.ArtifactId,
                    fixture.Options.SecurityPolicies[0].Id,
                    RuntimePerformanceScenarios.Run),
                TestContext.Current.CancellationToken));

        Assert.Equal("performance-image-identity-mismatch", exception.Code);
        Assert.Empty(fakeDocker.StartedContainers);
        Assert.Empty(fakeDocker.ResourceMonitorStarts);
    }

    [Fact]
    public async Task RuntimePerformanceSampleRejectsATimedOutJobAfterCleanup()
    {
        var fakeDocker = new SessionDockerClient
        {
            ContainerLogBytes = await CompletedRunFramesAsync(),
            NextWaitContainerException = new OperationCanceledException("simulated deadline")
        };
        await using var fixture = CreateRuntimePerformanceFixture(fakeDocker);

        var exception = await Assert.ThrowsAsync<RuntimePerformancePreflightException>(() =>
            fixture.Coordinator.MeasureAsync(
                new RuntimePerformanceSampleRequest(
                    fixture.Options.Profiles[0].Id,
                    fixture.Options.PromotionPreflightPlanSha256!,
                    fixture.ArtifactStore.Descriptor.Manifest.ArtifactId,
                    fixture.Options.SecurityPolicies[0].Id,
                    RuntimePerformanceScenarios.Run),
                TestContext.Current.CancellationToken));

        Assert.Equal("operation-timeout", exception.Code);
        Assert.Contains("runtime-1", fakeDocker.KilledContainers);
        Assert.Contains("runtime-1", fakeDocker.RemovedContainers);
        Assert.Contains("workspace-1", fakeDocker.RemovedVolumes);
    }

    [Fact]
    public async Task RuntimePerformanceSampleRejectsArtifactLeaseCleanupFailure()
    {
        var fakeDocker = new SessionDockerClient
        {
            ContainerLogBytes = await CompletedRunFramesAsync()
        };
        await using var fixture = CreateRuntimePerformanceFixture(fakeDocker);
        fixture.ArtifactStore.FailLeaseRelease = true;

        var exception = await Assert.ThrowsAsync<RuntimePerformancePreflightException>(() =>
            fixture.Coordinator.MeasureAsync(
                new RuntimePerformanceSampleRequest(
                    fixture.Options.Profiles[0].Id,
                    fixture.Options.PromotionPreflightPlanSha256!,
                    fixture.ArtifactStore.Descriptor.Manifest.ArtifactId,
                    fixture.Options.SecurityPolicies[0].Id,
                    RuntimePerformanceScenarios.Run),
                TestContext.Current.CancellationToken));

        Assert.Equal("performance-cleanup-failed", exception.Code);
        Assert.Contains("runtime-1", fakeDocker.RemovedContainers);
        Assert.Contains("workspace-1", fakeDocker.RemovedVolumes);
    }

    [Fact]
    public async Task RuntimePerformanceMeasurementHandlesCancellationBeforeBinding()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var measurement = new RuntimeJobMeasurementRegistration();

        measurement.BindCancellation(cancellation.Token);
        var completion = await measurement.Completion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(completion.ExecutionStarted);
        Assert.Equal("operation-cancelled-before-execution", completion.FailureCode);
        Assert.False(completion.CleanupSucceeded);
    }

    [Fact]
    public async Task RuntimeSessionIsolationChangeDestroysThePreviousContainer()
    {
        var fakeDocker = new SessionDockerClient();
        var (sessions, request) = CreateRuntimeSessionFixture(
            fakeDocker,
            "rs_isolation_change");
        await using var firstArchive = new MemoryStream("standard"u8.ToArray());
        var first = await sessions.AcquireAsync(
            request,
            firstArchive,
            TestContext.Current.CancellationToken);
        await first.CompleteAsync(reusable: true);

        await using var secondArchive = new MemoryStream("wine"u8.ToArray());
        var second = await sessions.AcquireAsync(
            request with
            {
                IsolationKind = RuntimeContainerIsolationKind.WineRoot,
                WinePrefixPath = "/opt/wine-dotnet"
            },
            secondArchive,
            TestContext.Current.CancellationToken);

        Assert.False(second.Reused);
        Assert.Equal("runtime-2", second.ContainerId);
        Assert.Equal(
            [RuntimeContainerIsolationKind.Standard, RuntimeContainerIsolationKind.WineRoot],
            fakeDocker.CreatedSpecs.Select(static spec => spec.IsolationKind));
        Assert.Contains("runtime-1", fakeDocker.RemovedContainers);
        await second.CompleteAsync(reusable: false);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("upload")]
    public async Task RuntimeSessionKeeperStartOrUploadHttpFailureRemovesOldGenerationAndRebuildsOnce(
        string failurePoint)
    {
        var fakeDocker = new SessionDockerClient();
        var (sessions, request) = CreateRuntimeSessionFixture(
            fakeDocker,
            "rs_upload_http_failure");
        await using var firstArchive = new MemoryStream("first"u8.ToArray());
        var first = await sessions.AcquireAsync(
            request,
            firstArchive,
            TestContext.Current.CancellationToken);
        await first.CompleteAsync(reusable: true);
        if (failurePoint == "start")
            fakeDocker.NextStartContainerException = new HttpRequestException("Keeper start failed.");
        else
            fakeDocker.NextUploadArchiveException = new HttpRequestException("Docker archive upload failed.");

        await using var replacementArchive = new MemoryStream("replacement"u8.ToArray());
        var replacement = await sessions.AcquireAsync(
            request,
            replacementArchive,
            TestContext.Current.CancellationToken);

        Assert.False(replacement.Reused);
        Assert.Equal("runtime-2", replacement.ContainerId);
        Assert.Equal(2, fakeDocker.RuntimeContainerCreates);
        Assert.Equal(["first", "replacement"], fakeDocker.UploadedArchives);
        Assert.Contains("runtime-1", fakeDocker.RemovedContainers);
        Assert.Contains("materializer-1", fakeDocker.RemovedContainers);
        Assert.Contains("workspace-1", fakeDocker.RemovedVolumes);
        await replacement.CompleteAsync(reusable: false);
    }

    [Fact]
    public async Task RuntimeSessionUploadCancellationRemovesOldGenerationWithoutCreatingReplacement()
    {
        var fakeDocker = new SessionDockerClient();
        var (sessions, request) = CreateRuntimeSessionFixture(
            fakeDocker,
            "rs_upload_cancelled");
        await using var firstArchive = new MemoryStream("first"u8.ToArray());
        var first = await sessions.AcquireAsync(
            request,
            firstArchive,
            TestContext.Current.CancellationToken);
        await first.CompleteAsync(reusable: true);
        using var uploadCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        fakeDocker.BeforeNextUploadArchive = uploadCancellation.Cancel;
        fakeDocker.NextUploadArchiveException = new OperationCanceledException(
            "Docker archive upload was cancelled.",
            innerException: null,
            uploadCancellation.Token);

        await using var cancelledArchive = new MemoryStream("cancelled"u8.ToArray());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sessions.AcquireAsync(
                request,
                cancelledArchive,
                uploadCancellation.Token));

        Assert.Equal(1, fakeDocker.RuntimeContainerCreates);
        Assert.Equal(["first"], fakeDocker.UploadedArchives);
        Assert.Contains("runtime-1", fakeDocker.RemovedContainers);
        Assert.Contains("materializer-1", fakeDocker.RemovedContainers);
        Assert.Contains("workspace-1", fakeDocker.RemovedVolumes);

        await using var recoveryArchive = new MemoryStream("recovery"u8.ToArray());
        var recovery = await sessions.AcquireAsync(
            request,
            recoveryArchive,
            TestContext.Current.CancellationToken);
        Assert.False(recovery.Reused);
        Assert.Equal("runtime-2", recovery.ContainerId);
        Assert.Equal(2, fakeDocker.RuntimeContainerCreates);
        await recovery.CompleteAsync(reusable: false);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("wait")]
    [InlineData("exit")]
    [InlineData("oom")]
    [InlineData("error")]
    public async Task RuntimeSessionWorkspaceCleanupFailureDoesNotEscapeAndForcesNewGeneration(
        string failurePoint)
    {
        var fakeDocker = new SessionDockerClient();
        var (sessions, request) = CreateRuntimeSessionFixture(
            fakeDocker,
            $"rs_cleanup_http_failure_{failurePoint}");
        await using var firstArchive = new MemoryStream("first"u8.ToArray());
        var first = await sessions.AcquireAsync(
            request,
            firstArchive,
            TestContext.Current.CancellationToken);
        switch (failurePoint)
        {
            case "stop":
                fakeDocker.NextStopContainerException = new HttpRequestException("Keeper stop failed.");
                break;
            case "wait":
                fakeDocker.NextWaitContainerException = new HttpRequestException("Cleanup wait failed.");
                break;
            case "exit":
                fakeDocker.NextWaitContainerExit = new RuntimeContainerExit(1, false, null);
                break;
            case "oom":
                fakeDocker.NextWaitContainerExit = new RuntimeContainerExit(137, true, null);
                break;
            case "error":
                fakeDocker.NextWaitContainerExit = new RuntimeContainerExit(0, false, "cleanup failed");
                break;
        }

        var completionException = await Record.ExceptionAsync(async () =>
            await first.CompleteAsync(reusable: true));

        Assert.Null(completionException);
        Assert.Contains("runtime-1", fakeDocker.RemovedContainers);
        Assert.Contains("materializer-1", fakeDocker.RemovedContainers);
        Assert.Contains("workspace-1", fakeDocker.RemovedVolumes);

        await using var secondArchive = new MemoryStream("second"u8.ToArray());
        var second = await sessions.AcquireAsync(
            request,
            secondArchive,
            TestContext.Current.CancellationToken);
        Assert.False(second.Reused);
        Assert.Equal("runtime-2", second.ContainerId);
        Assert.Equal(2, fakeDocker.RuntimeContainerCreates);
        await second.CompleteAsync(reusable: false);
    }

    [Fact]
    public void VersionedSandboxProfileIsDigestVerifiedAndDenyByDefault()
    {
        var root = FindRepositoryRoot();
        var options = new RuntimeSandboxOptions
        {
            SeccompProfilePath = Path.Combine(
                root,
                "src",
                "Supervisor",
                "SharpLabNext.RuntimeSupervisor",
                "security",
                "runtime-job-seccomp.v1.json")
        };

        var policy = RuntimeSandboxPolicy.Load(options, root);

        Assert.Equal(options.SeccompProfileSha256, policy.SeccompProfileSha256);
        Assert.Contains("no-new-privileges:true", policy.SecurityOptions);
        Assert.Contains(policy.SecurityOptions, static value => value.StartsWith("seccomp={", StringComparison.Ordinal));
        Assert.DoesNotContain(policy.SecurityOptions, static value => value.Contains("unconfined", StringComparison.Ordinal));
        Assert.Contains(policy.CreateUlimits(), static value => Equals(value["Name"], "core") && Equals(value["Hard"], 0L));
    }

    [Fact]
    public void SandboxProfileRejectsDigestMismatch()
    {
        var root = FindRepositoryRoot();
        var options = new RuntimeSandboxOptions
        {
            SeccompProfilePath = Path.Combine(
                root,
                "src",
                "Supervisor",
                "SharpLabNext.RuntimeSupervisor",
                "security",
                "runtime-job-seccomp.v1.json"),
            SeccompProfileSha256 = $"sha256:{new string('0', 64)}"
        };

        var exception = Assert.Throws<InvalidOperationException>(() => RuntimeSandboxPolicy.Load(options, root));

        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RunRequestAcceptsExecutionFlowAsAProtocolMode()
    {
        var request = new RunRequest(
            "request-1",
            "run:req_00000000-0000-4000-8000-000000000001",
            "resolution-1",
            new ArtifactRef($"sha256:{new string('a', 64)}"),
            "dotnet-10-linux-x64",
            new RunOptions([], null, RunInstrumentation.ExecutionFlow, "runtime-job-default"),
            DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Null(RuntimeJobRequestValidator.Validate(request));
    }

    [Fact]
    public void RuntimeRequestRejectsControlCharactersInIdempotencyKey()
    {
        var request = new RunRequest(
            "request-1",
            "run:req-1\nforged-log-line",
            "resolution-1",
            new ArtifactRef($"sha256:{new string('a', 64)}"),
            "dotnet-10-linux-x64",
            new RunOptions([], null, RunInstrumentation.None, "runtime-job-default"),
            DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.NotNull(RuntimeJobRequestValidator.Validate(request));
    }

    [Fact]
    public void ExecutionFlowRequiresRuntimeInstrumentationDerivation()
    {
        var manifest = Manifest(metadata: null, derived: false);

        var exception = Assert.Throws<RuntimeJobFailureException>(() =>
            RuntimeJobExecutor.ValidateInstrumentation(manifest, RunInstrumentation.ExecutionFlow));

        Assert.Equal("execution-flow-artifact-required", exception.Code);
    }

    [Fact]
    public void ExecutionFlowAcceptsThePinnedInstrumentationProfile()
    {
        var manifest = Manifest(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sharplabnext.instrumentation.transform"] = "runtime-instrumentation-v1",
                ["sharplabnext.instrumentation.profile"] = "execution-flow-v1"
            },
            derived: true);

        RuntimeJobExecutor.ValidateInstrumentation(manifest, RunInstrumentation.ExecutionFlow);
    }

    [Fact]
    public void JitRequestOnlyAllowsTheConfiguredProviderAndPolicies()
    {
        var request = new JitRequest(
            "request-1",
            "idempotency-1",
            "resolution-1",
            new ArtifactRef($"sha256:{new string('a', 64)}"),
            "dotnet-10-linux-x64",
            new JitOptions(null, "tier0-diffable", "disabled", "arbitrary-command", "runtime-job-default"),
            DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.NotNull(RuntimeJobRequestValidator.Validate(request));
    }

    [Theory]
    [InlineData(null, "*")]
    [InlineData("", "*")]
    [InlineData("Main", "*Main*")]
    [InlineData("Program.*", "Program.*")]
    public void JitEnvironmentUsesSubstringSemanticsForPlainMethodFilters(
        string? methodFilter,
        string expectedDisasmFilter)
    {
        var request = new JitRequest(
            "request-1",
            "idempotency-1",
            "resolution-1",
            new ArtifactRef($"sha256:{new string('a', 64)}"),
            "dotnet-10-linux-x64",
            new JitOptions(
                methodFilter,
                "tier0-diffable",
                "disabled",
                "coreclr-jitdisasm",
                "runtime-job-default"),
            DateTimeOffset.UtcNow.AddMinutes(1));

        var environment = RuntimeJobExecutor.CreateJitEnvironment(
            Assert.Single(ValidOptions().Profiles),
            request,
            "SharpLabNext.User.dll");

        Assert.Equal(expectedDisasmFilter, environment["COMPlus_JitDisasm"]);
        Assert.Equal("SharpLabNext.User", environment["COMPlus_JitDisasmAssemblies"]);
        Assert.Equal("1", environment["COMPlus_JitDisasmWithCodeBytes"]);
        Assert.Equal("1", environment["DOTNET_JitDisasmWithCodeBytes"]);
        Assert.Equal("1", environment["COMPlus_RichDebugInfo"]);
        Assert.Equal("1", environment["DOTNET_RichDebugInfo"]);
        Assert.DoesNotContain("COMPlus_JitDisasmWithDebugInfo", environment);
        Assert.DoesNotContain("DOTNET_JitDisasmWithDebugInfo", environment);
        Assert.Equal("1", environment["DOTNET_EnableDiagnostics"]);
        Assert.Equal("0", environment["DOTNET_EnableDiagnostics_IPC"]);
        Assert.Equal("0", environment["DOTNET_EnableDiagnostics_Debugger"]);
        Assert.Equal("1", environment["DOTNET_EnableDiagnostics_Profiler"]);
        Assert.Equal("1", environment["CORECLR_ENABLE_PROFILING"]);
        Assert.Equal(
            "/opt/sharplabnext/SharpLabNext.JitProfiler.so",
            environment["CORECLR_PROFILER_PATH"]);
        Assert.Equal("1", environment["SHARPLABNEXT_JIT_RESET_OUTPUT"]);
        Assert.Equal("SharpLabNext.User.dll", environment["SHARPLABNEXT_JIT_MAP_MODULE"]);
        Assert.Equal("/tmp/sharplabnext-jit.map", environment["SHARPLABNEXT_JIT_MAP_PATH"]);
        Assert.Equal(
            "/tmp/sharplabnext-jit-rich.map",
            environment["SHARPLABNEXT_JIT_RICH_MAP_PATH"]);
    }

    [Fact]
    public void WineJitEnvironmentUsesWindowsPathsWithoutTheLinuxProfiler()
    {
        var profile = Assert.Single(ValidOptions().Profiles);
        profile.Container = new RuntimeContainerDefinition
        {
            IsolationKind = RuntimeContainerIsolationKinds.Wine,
            EnvironmentKind = RuntimeContainerEnvironmentKinds.Wine,
            ExecutionUser = RuntimeContainerExecutionUsers.NonRoot,
            WinePrefixPath = "/opt/wine-coreclr"
        };
        profile.Operations = new RuntimeProfileOperations
        {
            Jit = new RuntimeJitOperationDefinition
            {
                ImplementationId = RuntimeOperationImplementationIds.LegacyJitInspector,
                PathStyle = RuntimeOperationPathStyles.WineZ,
                SourceMappingKind = RuntimeJitSourceMappingKinds.None,
                Command = new RuntimeOperationCommandDefinition
                {
                    Executable = "/usr/lib/wine/wine64",
                    Argv =
                    [
                        @"C:\dotnet\dotnet.exe",
                        @"Z:\opt\sharplabnext\SharpLabNext.LegacyJitInspector.dll",
                        "jit",
                        RuntimeOperationPlaceholders.EntryAssembly
                    ]
                }
            }
        };
        var request = new JitRequest(
            "request-1",
            "idempotency-1",
            "resolution-1",
            new ArtifactRef($"sha256:{new string('a', 64)}"),
            profile.Id,
            new JitOptions(null, "tier0-diffable", "disabled", "coreclr-jitdisasm", "runtime-job-default"),
            DateTimeOffset.UtcNow.AddMinutes(1));

        var environment = RuntimeJobExecutor.CreateJitEnvironment(profile, request, "SharpLabNext.User.dll");

        Assert.Equal(@"Z:\tmp\sharplabnext-jit.asm", environment["COMPlus_JitStdOutFile"]);
        Assert.Equal(@"Z:\tmp\sharplabnext-jit.asm", environment["SHARPLABNEXT_JIT_OUTPUT_PATH"]);
        Assert.Equal("/opt/wine-coreclr", environment["WINEPREFIX"]);
        Assert.Equal("win64", environment["WINEARCH"]);
        Assert.Equal("-all", environment["WINEDEBUG"]);
        Assert.Equal("0", environment["DOTNET_EnableDiagnostics"]);
        Assert.DoesNotContain("CORECLR_ENABLE_PROFILING", environment);
        Assert.DoesNotContain("CORECLR_PROFILER_PATH", environment);
        Assert.DoesNotContain("SHARPLABNEXT_JIT_MAP_PATH", environment);
        Assert.DoesNotContain("SHARPLABNEXT_JIT_RICH_MAP_PATH", environment);
    }

    [Fact]
    public void CheckedJitEnvironmentKeepsTheBridgeParentFreeOfNativeJitOutput()
    {
        var profile = Assert.Single(ValidOptions().Profiles);
        profile.Operations = new RuntimeProfileOperations
        {
            Jit = new RuntimeJitOperationDefinition
            {
                ImplementationId = RuntimeOperationImplementationIds.CheckedJitBridge,
                SourceMappingKind = RuntimeJitSourceMappingKinds.CheckedJitDebugInfo,
                Command = new RuntimeOperationCommandDefinition
                {
                    Executable = "/opt/sharplabnext/target-dotnet/dotnet",
                    Argv =
                    [
                        "/opt/sharplabnext/SharpLabNext.CheckedJitBridge.dll",
                        "jit",
                        RuntimeOperationPlaceholders.EntryAssembly,
                        RuntimeOperationPlaceholders.MethodFilter
                    ]
                }
            }
        };
        var request = new JitRequest(
            "request-1",
            "idempotency-1",
            "resolution-1",
            new ArtifactRef($"sha256:{new string('a', 64)}"),
            profile.Id,
            new JitOptions(null, "tier0-diffable", "disabled", "coreclr-jitdisasm", "runtime-job-default"),
            DateTimeOffset.UtcNow.AddMinutes(1));

        var environment = RuntimeJobExecutor.CreateJitEnvironment(
            profile,
            request,
            "SharpLabNext.User.dll");

        Assert.Equal("0", environment["DOTNET_EnableDiagnostics"]);
        Assert.Equal("0", environment["COMPlus_EnableDiagnostics"]);
        Assert.DoesNotContain("COMPlus_JitDisasm", environment);
        Assert.DoesNotContain("DOTNET_JitDisasm", environment);
        Assert.DoesNotContain("COMPlus_JitDisasmWithDebugInfo", environment);
        Assert.DoesNotContain("DOTNET_JitDisasmWithDebugInfo", environment);
        Assert.DoesNotContain("COMPlus_JitStdOutFile", environment);
        Assert.DoesNotContain("SHARPLABNEXT_JIT_OUTPUT_PATH", environment);
        Assert.DoesNotContain("SHARPLABNEXT_JIT_RESET_OUTPUT", environment);
        Assert.DoesNotContain("CORECLR_ENABLE_PROFILING", environment);
        Assert.DoesNotContain("CORECLR_PROFILER_PATH", environment);
        Assert.DoesNotContain("SHARPLABNEXT_JIT_MAP_PATH", environment);
        Assert.DoesNotContain("SHARPLABNEXT_JIT_RICH_MAP_PATH", environment);
    }

    [Fact]
    public void MonoJitEnvironmentDoesNotInjectCoreClrDiagnostics()
    {
        var profile = Assert.Single(ValidOptions().Profiles);
        profile.Container = new RuntimeContainerDefinition
        {
            IsolationKind = RuntimeContainerIsolationKinds.Standard,
            EnvironmentKind = RuntimeContainerEnvironmentKinds.Mono,
            ExecutionUser = RuntimeContainerExecutionUsers.NonRoot
        };
        profile.Operations = new RuntimeProfileOperations
        {
            Jit = new RuntimeJitOperationDefinition
            {
                ImplementationId = RuntimeOperationImplementationIds.MonoJitInspector,
                PathStyle = RuntimeOperationPathStyles.Unix,
                SourceMappingKind = RuntimeJitSourceMappingKinds.None,
                Command = new RuntimeOperationCommandDefinition
                {
                    Executable = "/usr/share/dotnet/dotnet",
                    Argv =
                    [
                        "/opt/sharplabnext/SharpLabNext.MonoJitInspector.dll",
                        RuntimeOperationPlaceholders.EntryAssembly,
                        RuntimeOperationPlaceholders.MethodFilter
                    ]
                }
            }
        };
        var request = new JitRequest(
            "request-1",
            "idempotency-1",
            "resolution-1",
            new ArtifactRef($"sha256:{new string('a', 64)}"),
            profile.Id,
            new JitOptions(null, "tier0-diffable", "disabled", "mono-jit", "runtime-job-default"),
            DateTimeOffset.UtcNow.AddMinutes(1));

        var environment = RuntimeJobExecutor.CreateJitEnvironment(
            profile,
            request,
            "SharpLabNext.User.exe");

        Assert.Equal("0", environment["DOTNET_EnableDiagnostics"]);
        Assert.Equal("0", environment["COMPlus_EnableDiagnostics"]);
        Assert.Equal("error", environment["MONO_LOG_LEVEL"]);
        Assert.DoesNotContain("COMPlus_JitDisasm", environment);
        Assert.DoesNotContain("COMPlus_JitStdOutFile", environment);
        Assert.DoesNotContain("SHARPLABNEXT_JIT_OUTPUT_PATH", environment);
        Assert.DoesNotContain("SHARPLABNEXT_JIT_RESET_OUTPUT", environment);
        Assert.DoesNotContain("CORECLR_ENABLE_PROFILING", environment);
        Assert.DoesNotContain("CORECLR_PROFILER_PATH", environment);
    }

    [Fact]
    public void MonoRunEnvironmentDoesNotInjectCoreClrOrWineSettings()
    {
        var profile = Assert.Single(ValidOptions().Profiles);
        profile.Container.EnvironmentKind = RuntimeContainerEnvironmentKinds.Mono;

        var environment = RuntimeJobExecutor.CreateRunEnvironment(profile, RunInstrumentation.None);

        Assert.DoesNotContain("DOTNET_EnableDiagnostics", environment);
        Assert.DoesNotContain("COMPlus_EnableDiagnostics", environment);
        Assert.DoesNotContain("WINEPREFIX", environment);
        Assert.DoesNotContain("SHARPLABNEXT_CAPTURE_DIRECTORY", environment);
        Assert.Equal("none", environment["SHARPLABNEXT_INSTRUMENTATION"]);
    }

    [Fact]
    public void RuntimeEntrypointClearsReusableJitFilesBeforeExec()
    {
        var script = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "deploy", "docker", "runtime-entrypoint.sh"));

        Assert.Contains(
            "if [ \"${SHARPLABNEXT_JIT_RESET_OUTPUT:-0}\" = \"1\" ]; then",
            script,
            StringComparison.Ordinal);
        Assert.Contains("/tmp/sharplabnext-jit.asm", script, StringComparison.Ordinal);
        Assert.Contains("/tmp/sharplabnext-jit.map", script, StringComparison.Ordinal);
        Assert.Contains("/tmp/sharplabnext-jit-rich.map", script, StringComparison.Ordinal);
        var cleanupIndex = script.IndexOf("rm -f", StringComparison.Ordinal);
        var execIndex = script.IndexOf("exec \"$@\"", StringComparison.Ordinal);
        Assert.True(cleanupIndex >= 0 && cleanupIndex < execIndex);
    }

    [Fact]
    public void JitSummaryMapsValidatedSectionRelativeSourceRanges()
    {
        var summary = Encoding.UTF8.GetBytes(
            """
            {
              "RuntimeVersion": "10.0.9",
              "Assembly": "SharpLabNext.User",
              "MethodFilter": null,
              "Methods": [
                {
                  "Method": "0x06000001",
                  "DisplayName": "Program.Main",
                  "Status": "prepared",
                  "Address": "0x00007FF800001000",
                  "NativeCodeSize": 12,
                  "InstructionCount": 3,
                  "MappingSource": "sequence-points",
                  "LinkedRanges": [
                    {
                      "SourceFilePath": "C:\\repo\\src\\Program.cs",
                      "SourceRange": {
                        "StartLine": 4,
                        "StartCharacter": 8,
                        "EndLine": 4,
                        "EndCharacter": 20
                      },
                      "OutputRange": {
                        "StartLine": 7,
                        "StartCharacter": 0,
                        "EndLine": 7,
                        "EndCharacter": 18
                      },
                      "Precision": "sequence-point"
                    },
                    {
                      "SourceFilePath": "Program.cs",
                      "SourceRange": {
                        "StartLine": -1,
                        "StartCharacter": 0,
                        "EndLine": 0,
                        "EndCharacter": 1
                      },
                      "OutputRange": {
                        "StartLine": 0,
                        "StartCharacter": 0,
                        "EndLine": 0,
                        "EndCharacter": 1
                      }
                    }
                  ]
                },
                {
                  "Method": "0x06000002",
                  "Status": "failed",
                  "Error": "Method preparation failed.",
                  "NativeCodeSize": 0,
                  "InstructionCount": 0,
                  "MappingSource": "none",
                  "LinkedRanges": []
                }
              ]
            }
            """);

        var method = Assert.Single(RuntimeJobExecutor.ParseJitMethods(summary));

        Assert.Equal("Program.Main", method.DisplayName);
        var linkedRange = Assert.Single(method.LinkedRanges);
        Assert.EndsWith("src/Program.cs", linkedRange.SourceFilePath, StringComparison.Ordinal);
        Assert.DoesNotContain(":", linkedRange.SourceFilePath, StringComparison.Ordinal);
        Assert.Equal(new TextRange(4, 8, 4, 20), linkedRange.SourceRange);
        Assert.Equal(new TextRange(7, 0, 7, 18), linkedRange.OutputRange);
        Assert.Equal("sequence-point", linkedRange.Precision);
    }

    [Fact]
    public async Task DockerMultiplexingCanSplitOneBase64RuntimeFrameAroundStderr()
    {
        await using var encodedLog = new MemoryStream();
        await using (var writer = new RuntimeFrameWriter(encodedLog, RuntimeFrameTransport.Base64Line))
        {
            await writer.WriteAsync(
                RuntimeFrameKind.Stdout,
                new byte[] { 0, 255, 128, 10, 13, 42 },
                TestContext.Current.CancellationToken);
        }
        var logBytes = encodedLog.ToArray();
        await using var dockerLog = new MemoryStream();
        await WriteDockerFrameAsync(dockerLog, 1, logBytes.AsMemory(0, 1));
        await WriteDockerFrameAsync(dockerLog, 2, "ignored daemon stderr"u8.ToArray());
        await WriteDockerFrameAsync(dockerLog, 1, logBytes.AsMemory(1, 7));
        await WriteDockerFrameAsync(dockerLog, 1, logBytes.AsMemory(8));
        dockerLog.Position = 0;
        await using var multiplexed = new DockerEngineClient.DockerMultiplexedReadStream(dockerLog);
        var reader = new RuntimeFrameLogReader(multiplexed);

        var decoded = await reader.ReadAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(decoded);
        Assert.Equal(RuntimeFrameKind.Stdout, decoded.Kind);
        Assert.Equal(new byte[] { 0, 255, 128, 10, 13, 42 }, decoded.Payload.ToArray());
        Assert.Null(await reader.ReadAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("anycpu", "x64", true)]
    [InlineData("AnyCPU", "arm64", true)]
    [InlineData("x64", "x64", true)]
    [InlineData("arm64", "x64", false)]
    public void RuntimeArchitectureCompatibilityNormalizesPortableArtifacts(
        string artifactArchitecture,
        string runtimeArchitecture,
        bool expected)
    {
        Assert.Equal(
            expected,
            RuntimeArchitectureCompatibility.IsCompatible(artifactArchitecture, runtimeArchitecture));
    }

    [Theory]
    [InlineData("completed", 0, 0, false, RunTerminalStatus.Completed)]
    [InlineData("non-zero-exit", 7, 7, false, RunTerminalStatus.NonZeroExit)]
    [InlineData("user-exception", 1, 1, false, RunTerminalStatus.UserException)]
    [InlineData("process-crash", 134, 134, false, RunTerminalStatus.ProcessCrash)]
    [InlineData("out-of-memory", 137, 137, false, RunTerminalStatus.OutOfMemory)]
    [InlineData(null, null, 139, false, RunTerminalStatus.ProcessCrash)]
    [InlineData("process-crash", 137, 137, true, RunTerminalStatus.OutOfMemory)]
    public void RunTerminalStatusPreservesCrashAndOutOfMemoryIdentity(
        string? reportedStatus,
        int? reportedExitCode,
        long containerExitCode,
        bool oomKilled,
        RunTerminalStatus expected)
    {
        var actual = RuntimeJobExecutor.ClassifyRunStatus(
            reportedStatus,
            reportedExitCode,
            new RuntimeContainerExit(containerExitCode, oomKilled, null));

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("completed", 0, false, 1, false, JitTerminalStatus.Completed)]
    [InlineData("no-matching-methods", 2, false, 0, false, JitTerminalStatus.NoMatchingMethods)]
    [InlineData("inspection-failed", 1, false, 0, false, JitTerminalStatus.InspectionFailed)]
    [InlineData(null, 134, false, 0, false, JitTerminalStatus.ProcessCrash)]
    [InlineData("out-of-memory", 137, false, 0, false, JitTerminalStatus.OutOfMemory)]
    [InlineData(null, 137, true, 0, false, JitTerminalStatus.OutOfMemory)]
    [InlineData("completed", 0, false, 1, true, JitTerminalStatus.OutputLimitExceeded)]
    public void JitTerminalStatusPreservesCrashAndResourceIdentity(
        string? reportedStatus,
        long containerExitCode,
        bool oomKilled,
        int methodCount,
        bool outputTruncated,
        JitTerminalStatus expected)
    {
        var actual = RuntimeJobExecutor.ClassifyJitStatus(
            reportedStatus,
            new RuntimeContainerExit(containerExitCode, oomKilled, null),
            methodCount,
            outputTruncated);

        Assert.Equal(expected, actual);
    }

    private static PromotionPreflightProfileFixture CreatePromotionPreflightProfile(
        Action<RuntimeProfileOptions>? update = null,
        string? profileSha256 = null)
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourcePath = Path.Combine(
            repositoryRoot,
            "profiles",
            "runtimes",
            "candidates",
            "dotnet-10-linux-x64.json");
        var profile = JsonSerializer.Deserialize<RuntimeProfileOptions>(
            File.ReadAllBytes(sourcePath),
            RuntimeProfilePreflightJsonOptions)
            ?? throw new InvalidOperationException("The preflight test profile is empty.");
        profile.Image = $"registry.example/sharplabnext/runtime@sha256:{new string('a', 64)}";
        profile.RuntimeImageId = $"sha256:{new string('b', 64)}";
        profile.PromotionReceipt = null;
        update?.Invoke(profile);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(profile, RuntimeProfilePreflightJsonOptions);
        var root = Path.Combine(Path.GetTempPath(), $"sharplabnext-preflight-profile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "profile.json");
        File.WriteAllBytes(path, bytes);
        var observedProfileSha256 =
            $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
        var planSha256 = $"sha256:{new string('c', 64)}";
        var sourceRevision = new string('d', 40);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{RuntimePromotionPreflightOptions.SectionName}:Enabled"] = "true",
                [$"{RuntimePromotionPreflightOptions.SectionName}:PlanSha256"] = planSha256,
                [$"{RuntimePromotionPreflightOptions.SectionName}:SourceRevision"] = sourceRevision,
                [$"{RuntimePromotionPreflightOptions.SectionName}:ProfilePath"] = path,
                [$"{RuntimePromotionPreflightOptions.SectionName}:ProfileSha256"] =
                    profileSha256 ?? observedProfileSha256
            })
            .Build();
        return new PromotionPreflightProfileFixture(
            root,
            profile.Id,
            planSha256,
            observedProfileSha256,
            sourceRevision,
            configuration);
    }

    private static RuntimeSupervisorOptions ValidOptions() => new()
    {
        DockerSocketPath = "/var/run/docker.sock",
        DockerApiVersion = "v1.47",
        ArtifactStoreBaseAddress = "http://artifact-store:8080",
        RequireDigestPinnedImages = false,
        Profiles =
        [
            new RuntimeProfileOptions
            {
                Id = "dotnet-10-linux-x64",
                Image = "sharplabnext/runtime-dotnet10:dev",
                RuntimeVersion = "10.0.9",
                JitVersion = "10.0.9",
                RuntimeImageId = "development-image-id",
                AcceptedArtifactFormats = ["dotnet-managed-pe-v1"],
                AcceptedFrameworks =
                [
                    new RuntimeFrameworkCompatibilityDefinition
                    {
                        Name = "Microsoft.NETCore.App",
                        ExactVersion = "10.0.9"
                    }
                ],
                Capabilities = ["run", "jit-asm"],
                AllowedSecurityPolicyIds = ["runtime-job-default"],
                Container = new RuntimeContainerDefinition
                {
                    ExecutionUser = RuntimeContainerExecutionUsers.NonRoot
                },
                Layout = new RuntimeImageLayout
                {
                    JitInspectorAssemblyPath = "/opt/sharplabnext/SharpLabNext.JitInspector.dll"
                }
            }
        ],
        SecurityPolicies = [new RuntimeSecurityPolicyOptions()]
    };

    private static RuntimeProfileOptions WineRuntimeProfile() => new()
    {
        Id = "wine-netfx48-linux-x64",
        Image = "sharplabnext/runtime-wine-netfx48:dev",
        Family = "netfx-clr-wine",
        RuntimeVersion = "wine-9.0+netfx48",
        RuntimeImageId = "development-image-id",
        JitVersion = "not-supported",
        Rid = "linux-x64",
        Architecture = "x64",
        AcceptedArtifactFormats =
        [
            "dotnet-framework-managed-pe-v1",
            "dotnet-framework-mixed-pe-v1"
        ],
        AcceptedFrameworks =
        [
            new RuntimeFrameworkCompatibilityDefinition
            {
                Name = ".NETFramework",
                ExactVersion = "4.8"
            }
        ],
        Capabilities = ["run"],
        ProvidedRuntimeFeatureTags = ["runtime.netfx48-wine"],
        AllowedSecurityPolicyIds = ["runtime-job-wine-netfx"],
        Container = new RuntimeContainerDefinition
        {
            IsolationKind = RuntimeContainerIsolationKinds.Wine,
            EnvironmentKind = RuntimeContainerEnvironmentKinds.Wine,
            ExecutionUser = RuntimeContainerExecutionUsers.Root,
            WinePrefixPath = "/opt/wine-dotnet"
        },
        Layout = new RuntimeImageLayout
        {
            RunnerKind = RuntimeRunnerKinds.WineNetFx,
            RunnerAssemblyPath = "/opt/sharplabnext/SharpLabNext.WineRunner.dll",
            WinePrefixPath = "/opt/wine-dotnet"
        }
    };

    private static RuntimeProfileOptions JSharpWineRuntimeProfile() => new()
    {
        Id = "wine-jsharp20-linux-x64",
        Image = "sharplabnext/runtime-wine-jsharp20:development",
        Family = "netfx-clr-wine",
        RuntimeVersion = "wine-9.0+clr2+jsharp-2.0.50727.937",
        RuntimeImageId = "development-image-id",
        JitVersion = "not-supported",
        Rid = "linux-x64",
        Architecture = "x64",
        AcceptedArtifactFormats = ["dotnet-framework-managed-pe-v1"],
        AcceptedFrameworks =
        [
            new RuntimeFrameworkCompatibilityDefinition
            {
                Name = ".NETFramework",
                ExactVersion = "2.0"
            }
        ],
        Capabilities = ["run"],
        ProvidedRuntimeFeatureTags = ["runtime.jsharp20-wine"],
        AllowedSecurityPolicyIds = ["runtime-job-wine-jsharp20"],
        Container = new RuntimeContainerDefinition
        {
            IsolationKind = RuntimeContainerIsolationKinds.Wine,
            EnvironmentKind = RuntimeContainerEnvironmentKinds.Wine,
            ExecutionUser = RuntimeContainerExecutionUsers.Root,
            WinePrefixPath = "/opt/wine-jsharp20"
        },
        Layout = new RuntimeImageLayout
        {
            RunnerKind = RuntimeRunnerKinds.WineJSharp20,
            RunnerAssemblyPath = "/opt/sharplabnext/SharpLabNext.WineRunner.dll",
            WinePrefixPath = "/opt/wine-jsharp20"
        }
    };

    private static RuntimeProfileOptions WineNetFx20RuntimeProfile() => new()
    {
        Id = "wine-netfx20-linux-x64",
        Image = "sharplabnext/runtime-wine-netfx20:development",
        Family = "netfx-clr-wine",
        RuntimeVersion = "2.0",
        RuntimeImageId = "development-image-id",
        JitVersion = "not-supported",
        Rid = "linux-x64",
        Architecture = "x64",
        AcceptedArtifactFormats = ["dotnet-framework-managed-pe-v1"],
        AcceptedFrameworks =
        [
            new RuntimeFrameworkCompatibilityDefinition
            {
                Name = ".NETFramework",
                ExactVersion = "2.0"
            }
        ],
        Capabilities = ["run"],
        AllowedSecurityPolicyIds = ["runtime-job-wine-netfx"],
        Container = new RuntimeContainerDefinition
        {
            IsolationKind = RuntimeContainerIsolationKinds.Wine,
            EnvironmentKind = RuntimeContainerEnvironmentKinds.Wine,
            ExecutionUser = RuntimeContainerExecutionUsers.Root,
            WinePrefixPath = "/opt/wine-netfx-clr2"
        },
        Layout = new RuntimeImageLayout
        {
            RunnerKind = RuntimeRunnerKinds.WineNetFx,
            RunnerAssemblyPath = "/opt/sharplabnext/SharpLabNext.TargetRuntimeRunner.exe",
            WinePrefixPath = "/opt/wine-netfx-clr2"
        }
    };

    private static ArtifactManifest JSharpManifest()
    {
        var artifactRef = new ArtifactRef($"sha256:{new string('c', 64)}");
        return new ArtifactManifest(
            1,
            artifactRef,
            new ArtifactProducer(
                "release",
                "jsharp",
                "vjc-jsharp20",
                "2.0.50727.937",
                null,
                $"sha256:{new string('d', 64)}"),
            "jsharp20-ref",
            "net20",
            "dotnet-framework-managed-pe-v1",
            new ArtifactRuntimeRequirement(
                "netfx-clr-wine",
                [new FrameworkRequirement(".NETFramework", "2.0")],
                "x64",
                ["runtime.jsharp20-wine"]),
            [],
            BuildOutputKind.Console,
            "SharpLabNext.User.exe",
            "Program::main",
            []);
    }

    private static SharpLabNext.Artifacts.Contracts.ArtifactManifest Manifest(
        IReadOnlyDictionary<string, string>? metadata,
        bool derived)
    {
        var artifactRef = new ArtifactRef($"sha256:{new string('a', 64)}");
        return new SharpLabNext.Artifacts.Contracts.ArtifactManifest(
            1,
            artifactRef,
            new SharpLabNext.Artifacts.Contracts.ArtifactProducer(
                "release",
                "csharp",
                "roslyn-stable",
                "1.0.0",
                null,
                "image"),
            "net10-ref",
            "net10.0",
            "dotnet-managed-pe-v1",
            new SharpLabNext.Artifacts.Contracts.ArtifactRuntimeRequirement(
                "coreclr",
                [],
                "anycpu",
                []),
            [],
            BuildOutputKind.Console,
            "app.dll",
            "Program.Main",
            [],
            derived
                ? new SharpLabNext.Artifacts.Contracts.ArtifactDerivation(
                    artifactRef,
                    "artifacts-default",
                    "1.0.0",
                    $"sha256:{new string('b', 64)}")
                : null,
            metadata);
    }

    private static DockerEngineClient CreateDockerClient(HttpMessageHandler handler)
    {
        var root = FindRepositoryRoot();
        var sandbox = RuntimeSandboxPolicy.Load(
            new RuntimeSandboxOptions
            {
                SeccompProfilePath = Path.Combine(
                    root,
                    "src",
                    "Supervisor",
                    "SharpLabNext.RuntimeSupervisor",
                    "security",
                    "runtime-job-seccomp.v1.json")
            },
            root);
        return new DockerEngineClient(ValidOptions(), sandbox, handler);
    }

    private static int CapabilityPdbProbe(int value)
    {
        var incremented = value + 1;
        var doubled = incremented * 2;
        return doubled - 3;
    }

    private static async Task<RuntimeCapabilityFixture> CreateRuntimeCapabilityFixtureAsync(
        IReadOnlyList<string> capabilities,
        string sourceMappingKind)
    {
        var repositoryRoot = FindRepositoryRoot();
        var configuration = Directory.GetParent(
                Path.GetDirectoryName(typeof(RuntimeSupervisorTests).Assembly.Location)!)?.Name
            ?? throw new InvalidOperationException("The unit-test build configuration could not be resolved.");
        var probeOutput = Path.Combine(
            repositoryRoot,
            "tests",
            "Fixtures",
            "SharpLabNext.RuntimeCapabilityProbe",
            "bin",
            configuration,
            "netcoreapp2.0");
        var assemblyPath = Path.Combine(probeOutput, "SharpLabNext.RuntimeCapabilityProbe.dll");
        var pdbPath = Path.Combine(probeOutput, "SharpLabNext.RuntimeCapabilityProbe.pdb");
        var assemblyBytes = await File.ReadAllBytesAsync(
            assemblyPath,
            TestContext.Current.CancellationToken);
        var pdbBytes = await File.ReadAllBytesAsync(
            pdbPath,
            TestContext.Current.CancellationToken);
        var pdbIdentity = ReadCapabilityPdbIdentity(assemblyBytes, pdbBytes);

        var options = ValidOptions();
        options.SessionReuseEnabled = false;
        var planSha256 = $"sha256:{new string('2', 64)}";
        var preflightProfileSha256 = $"sha256:{new string('3', 64)}";
        options.PromotionPreflightPlanSha256 = planSha256;
        options.PromotionPreflightProfileSha256 = preflightProfileSha256;
        options.PromotionPreflightSourceRevision = new string('1', 40);
        var policy = Assert.Single(options.SecurityPolicies);
        policy.MaximumDurationSeconds = 2;
        policy.MaximumOutputBytes = 32 * 1024;
        var profile = Assert.Single(options.Profiles);
        profile.Image = $"registry.example/sharplabnext/runtime@sha256:{new string('b', 64)}";
        profile.RuntimeImageId = $"sha256:{new string('a', 64)}";
        profile.Capabilities = capabilities.ToList();
        profile.SecurityPolicies = [policy];
        profile.Layout = new RuntimeImageLayout
        {
            DotNetHostPath = "/usr/share/dotnet/dotnet",
            RunnerKind = RuntimeRunnerKinds.DotNet,
            RunnerAssemblyPath = "/opt/sharplabnext/SharpLabNext.Runner.dll",
            JitInspectorAssemblyPath = "/opt/sharplabnext/SharpLabNext.JitInspector.dll"
        };
        profile.Operations = new RuntimeProfileOperations
        {
            Run = new RuntimeRunOperationDefinition
            {
                ImplementationId = RuntimeOperationImplementationIds.Runner,
                PathStyle = RuntimeOperationPathStyles.Unix,
                Command = new RuntimeOperationCommandDefinition
                {
                    Executable = profile.Layout.DotNetHostPath,
                    Argv =
                    [
                        profile.Layout.RunnerAssemblyPath,
                        RuntimeOperationPlaceholders.EntryAssembly,
                        "--",
                        RuntimeOperationPlaceholders.Arguments
                    ]
                }
            },
            Jit = capabilities.Contains("jit-asm", StringComparer.Ordinal)
                ? new RuntimeJitOperationDefinition
                {
                    ImplementationId = RuntimeOperationImplementationIds.JitInspector,
                    PathStyle = RuntimeOperationPathStyles.Unix,
                    SourceMappingKind = sourceMappingKind,
                    ProfilerPath = sourceMappingKind == RuntimeJitSourceMappingKinds.LinuxProfiler
                        ? "/opt/sharplabnext/SharpLabNext.JitProfiler.so"
                        : null,
                    Command = new RuntimeOperationCommandDefinition
                    {
                        Executable = profile.Layout.DotNetHostPath,
                        Argv =
                        [
                            profile.Layout.JitInspectorAssemblyPath,
                            RuntimeOperationPlaceholders.EntryAssembly,
                            RuntimeOperationPlaceholders.MethodFilter
                        ]
                    }
                }
                : null
        };

        var sourceRevision = new string('1', 40);
        var probeDescriptor = CreateCapabilityDescriptor(
            assemblyBytes,
            pdbBytes,
            sourceRevision,
            planSha256,
            preflightProfileSha256,
            profile,
            parentArtifactRef: null);
        var probeArtifactRef = probeDescriptor.Manifest.ArtifactId;
        var flowDescriptor = CreateCapabilityDescriptor(
            assemblyBytes,
            pdbBytes,
            sourceRevision,
            planSha256,
            preflightProfileSha256,
            profile,
            probeArtifactRef);
        var flowArtifactRef = flowDescriptor.Manifest.ArtifactId;
        var artifactStore = new CapabilityArtifactStoreClient(
        [
            probeDescriptor,
            flowDescriptor
        ],
        new Dictionary<(ArtifactRef ArtifactRef, string Path), byte[]>
        {
            [(probeArtifactRef, "SharpLabNext.RuntimeCapabilityProbe.dll")] = assemblyBytes,
            [(probeArtifactRef, "SharpLabNext.RuntimeCapabilityProbe.pdb")] = pdbBytes,
            [(flowArtifactRef, "SharpLabNext.RuntimeCapabilityProbe.dll")] = assemblyBytes,
            [(flowArtifactRef, "SharpLabNext.RuntimeCapabilityProbe.pdb")] = pdbBytes
        });
        var frames = await CreateCapabilityFramesAsync(
            pdbIdentity,
            mappedJit: sourceMappingKind != RuntimeJitSourceMappingKinds.None,
            checked((int)policy.MaximumOutputBytes));
        var docker = new CapabilityDockerClient(profile, frames);
        var root = repositoryRoot;
        var sandbox = RuntimeSandboxPolicy.Load(
            new RuntimeSandboxOptions
            {
                SeccompProfilePath = Path.Combine(
                    root,
                    "src",
                    "Supervisor",
                    "SharpLabNext.RuntimeSupervisor",
                    "security",
                    "runtime-job-seccomp.v1.json")
            },
            root);
        var operations = new OperationStore();
        var scheduler = new BoundedOperationScheduler(
            operations,
            new OperationExecutionOptions
            {
                QueueCapacity = 16,
                WorkerConcurrency = 1,
                ExecutorId = "runtime-capability-test"
            });
        var sessions = new RuntimeSessionRegistry(
            docker,
            Options.Create(options),
            sandbox,
            NullLogger<RuntimeSessionRegistry>.Instance);
        var executor = new RuntimeJobExecutor(
            operations,
            scheduler,
            artifactStore,
            docker,
            sessions,
            Options.Create(options),
            new ServiceIdentity(
                "runtime-supervisor",
                ServiceKind.RuntimeSupervisor,
                "release-1",
                ProtocolVersion.WorkerV1,
                [],
                "ready"),
            NullLogger<RuntimeJobExecutor>.Instance);
        var coordinator = new RuntimeCapabilityPreflightCoordinator(
            operations,
            executor,
            docker,
            artifactStore,
            sandbox,
            Options.Create(options));
        return new RuntimeCapabilityFixture(
            coordinator,
            scheduler,
            docker,
            profile,
            policy,
            probeArtifactRef,
            flowArtifactRef,
            pdbIdentity);
    }

    private static byte[] CreateCapabilityDraftReceipt(
        RuntimeCapabilityFixture fixture,
        IReadOnlyList<JsonObject> documents,
        Dictionary<string, byte[]> documentBytes)
    {
        var byCapability = documents.ToDictionary(
            static document => document["capability"]!.GetValue<string>(),
            StringComparer.Ordinal);
        var runHelper = FindCapabilityArtifact(byCapability["run"], "helper");
        var operations = new JsonObject
        {
            ["run"] = new JsonObject
            {
                ["implementation"] = fixture.Profile.Operations!.Run!.ImplementationId,
                ["assemblyPath"] = runHelper["path"]!.GetValue<string>(),
                ["assemblySha256"] = runHelper["sha256"]!.GetValue<string>()
            }
        };
        if (byCapability.TryGetValue("jit-asm", out var jitDocument))
        {
            var jitHelper = FindCapabilityArtifact(jitDocument, "helper");
            var profiler = FindCapabilityArtifact(jitDocument, "profiler");
            operations["jit"] = new JsonObject
            {
                ["implementation"] = fixture.Profile.Operations.Jit!.ImplementationId,
                ["assemblyPath"] = jitHelper["path"]!.GetValue<string>(),
                ["assemblySha256"] = jitHelper["sha256"]!.GetValue<string>(),
                ["profilerPath"] = profiler["path"]!.GetValue<string>(),
                ["profilerSha256"] = profiler["sha256"]!.GetValue<string>()
            };
        }

        var checks = new JsonArray();
        foreach (var capability in fixture.Profile.Capabilities.Order(StringComparer.Ordinal))
        {
            var document = byCapability[capability];
            var isJit = capability == "jit-asm";
            checks.Add(new JsonObject
            {
                ["capability"] = capability,
                ["result"] = "passed",
                ["networkDisabled"] = true,
                ["supervisorSandbox"] = true,
                ["outputLimitValidated"] = true,
                ["sourceMappingKind"] = isJit
                    ? document["jit"]!["mapping"]!["kind"]!.GetValue<string>()
                    : "not-applicable",
                ["mappingSource"] = isJit
                    ? document["jit"]!["mapping"]!["source"]!.GetValue<string>()
                    : "not-applicable",
                ["evidencePath"] =
                    $"profiles/runtime-promotion-evidence/{fixture.Profile.Id}/{capability}.json",
                ["evidenceSha256"] = ContentIdentity.Compute(documentBytes[capability]).Value
            });
        }

        var image = byCapability["run"]["image"]!;
        var receipt = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["planSha256"] = fixture.PlanSha256,
            ["profileId"] = fixture.Profile.Id,
            ["matrixTargetId"] = "runtime-capability-unit-test",
            ["platform"] = "linux",
            ["family"] = fixture.Profile.Family,
            ["resolvedVersion"] = fixture.Profile.RuntimeVersion,
            ["image"] = new JsonObject
            {
                ["reference"] = image["reference"]!.GetValue<string>(),
                ["imageId"] = image["imageId"]!.GetValue<string>(),
                ["sizeBytes"] = 512L * 1024 * 1024
            },
            ["componentIdentity"] = new JsonObject
            {
                ["sourceUri"] = "https://example.invalid/runtime.tar.gz",
                ["sourceDigest"] = $"sha512:{new string('2', 128)}"
            },
            ["runtimeIdentity"] = new JsonObject
            {
                ["runtimeCommit"] = fixture.Profile.RuntimeCommit,
                ["jitVersion"] = fixture.Profile.JitVersion,
                ["jitCommit"] = fixture.Profile.JitCommit
            },
            ["operations"] = operations,
            ["performance"] = new JsonObject
            {
                ["result"] = "passed",
                ["policyId"] = "runtime-capability-unit-test",
                ["policyPath"] =
                    "profiles/runtime-performance-policies/runtime-capability-unit-test.json",
                ["policySha256"] = $"sha256:{new string('3', 64)}",
                ["evidencePath"] =
                    $"profiles/runtime-promotion-evidence/{fixture.Profile.Id}/performance.json",
                ["evidenceSha256"] = $"sha256:{new string('4', 64)}"
            },
            ["sourceRevision"] = fixture.CreateRequest().SourceRevision,
            ["checks"] = checks
        };
        return Encoding.UTF8.GetBytes(receipt.ToJsonString());
    }

    private static JsonObject FindCapabilityArtifact(JsonObject document, string role) =>
        document["artifacts"]!.AsArray()
            .Select(static artifact => artifact!.AsObject())
            .Single(artifact => StringComparer.Ordinal.Equals(
                artifact["role"]!.GetValue<string>(),
                role));

    private static ArtifactBundleDescriptor CreateCapabilityDescriptor(
        byte[] assemblyBytes,
        byte[] pdbBytes,
        string sourceRevision,
        string planSha256,
        string preflightProfileSha256,
        RuntimeProfileOptions profile,
        ArtifactRef? parentArtifactRef)
    {
        var assemblyDigest = ContentIdentity.Compute(assemblyBytes).Value;
        var pdbDigest = ContentIdentity.Compute(pdbBytes).Value;
        var files = new[]
        {
            new ArtifactFileDescriptor(
                "managed-pe",
                "SharpLabNext.RuntimeCapabilityProbe.dll",
                assemblyBytes.LongLength,
                assemblyDigest),
            new ArtifactFileDescriptor(
                "portable-pdb",
                "SharpLabNext.RuntimeCapabilityProbe.pdb",
                pdbBytes.LongLength,
                pdbDigest)
        };
        var placeholder = new ArtifactRef($"sha256:{new string('0', 64)}");
        var framework = Assert.Single(profile.AcceptedFrameworks);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RuntimeCapabilityProbeContract.MetadataContractKey] =
                RuntimeCapabilityProbeContract.MetadataContractValue,
            [RuntimeCapabilityProbeContract.MetadataSourceRevisionKey] = sourceRevision,
            [RuntimeCapabilityProbeContract.MetadataPromotionPlanSha256Key] = planSha256,
            [RuntimeCapabilityProbeContract.MetadataPreflightProfileSha256Key] =
                preflightProfileSha256
        };
        if (parentArtifactRef is not null)
        {
            metadata[RuntimeCapabilityProbeContract.InstrumentationTransformKey] =
                RuntimeCapabilityProbeContract.ExecutionFlowTransformId;
            metadata[RuntimeCapabilityProbeContract.InstrumentationProfileKey] =
                RuntimeCapabilityProbeContract.ExecutionFlowProfileId;
            metadata[RuntimeCapabilityProbeContract.InstrumentationAppliedKey] = "true";
            metadata[RuntimeCapabilityProbeContract.InstrumentationPointsKey] = "4";
        }
        var manifest = ArtifactIdentity.WithComputedId(new ArtifactManifest(
            ArtifactStoreProtocol.ArtifactManifestVersion,
            placeholder,
            new ArtifactProducer(
                RuntimeCapabilityProbeContract.ReleaseId,
                RuntimeCapabilityProbeContract.LanguageId,
                RuntimeCapabilityProbeContract.ToolchainId,
                RuntimeCapabilityProbeContract.CompilerVersion,
                sourceRevision,
                $"source-revision:{sourceRevision}"),
            "runtime-capability-probe-netcoreapp2.0-ref",
            "netcoreapp2.0",
            "dotnet-managed-pe-v1",
            new ArtifactRuntimeRequirement(
                profile.Family,
                [new FrameworkRequirement(framework.Name, framework.ExactVersion!)],
                "anycpu",
                []),
            [],
            BuildOutputKind.Console,
            "SharpLabNext.RuntimeCapabilityProbe.dll",
            RuntimeCapabilityProbeContract.EntryPoint,
            files,
            parentArtifactRef is not null
                ? new ArtifactDerivation(
                    parentArtifactRef.Value,
                    RuntimeCapabilityProbeContract.ExecutionFlowProcessorId,
                    RuntimeCapabilityProbeContract.ExecutionFlowProcessorVersion,
                    RuntimeCapabilityProbeContract.ExecutionFlowOptionsDigest)
                : null,
            metadata));
        return new ArtifactBundleDescriptor(
            manifest,
            files.Select(static file => new ArtifactBundleEntry(
                file.Path,
                file.Size,
                file.Digest,
                file.Role,
                new ContentRef(file.Digest))).ToArray());
    }

    private static CapabilityPdbIdentity ReadCapabilityPdbIdentity(byte[] assemblyBytes, byte[] pdbBytes)
    {
        using var peStream = new MemoryStream(assemblyBytes, writable: false);
        using var peReader = new PEReader(peStream, PEStreamOptions.PrefetchEntireImage);
        var peMetadata = peReader.GetMetadataReader(MetadataReaderOptions.ApplyWindowsRuntimeProjections);
        using var pdbStream = new MemoryStream(pdbBytes, writable: false);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(
            pdbStream,
            MetadataStreamOptions.PrefetchMetadata);
        var reader = provider.GetMetadataReader(MetadataReaderOptions.ApplyWindowsRuntimeProjections);
        var header = Assert.IsType<DebugMetadataHeader>(reader.DebugMetadataHeader);
        var type = peMetadata.TypeDefinitions
            .Select(handle => peMetadata.GetTypeDefinition(handle))
            .Single(type =>
                peMetadata.GetString(type.Namespace) == "SharpLabNext.RuntimeCapabilityProbe" &&
                peMetadata.GetString(type.Name) == "Program");
        var methodHandle = type.GetMethods()
            .Single(handle => peMetadata.GetString(peMetadata.GetMethodDefinition(handle).Name) ==
                "MultipleSequencePoints");
        var row = MetadataTokens.GetRowNumber(methodHandle);
        var information = reader.GetMethodDebugInformation(
            MetadataTokens.MethodDebugInformationHandle(row));
        var ranges = information.GetSequencePoints()
            .Where(static point => !point.IsHidden)
            .Select(point =>
            {
                var documentHandle = point.Document.IsNil ? information.Document : point.Document;
                var document = reader.GetString(reader.GetDocument(documentHandle).Name);
                return new CapabilityPdbRange(
                    point.Offset,
                    SanitizeCapabilityDocument(document),
                    point.StartLine,
                    point.StartColumn,
                    point.EndLine,
                    point.EndColumn);
            })
            .DistinctBy(static range => (
                range.Document,
                range.StartLine,
                range.StartColumn,
                range.EndLine,
                range.EndColumn))
            .Take(3)
            .ToArray();
        Assert.InRange(ranges.Length, 2, 3);
        return new CapabilityPdbIdentity(
            ContentIdentity.Compute(pdbBytes).Value,
            Convert.ToHexStringLower(header.Id.AsSpan()),
            $"0x{MetadataTokens.GetToken(methodHandle):x8}",
            ranges);
    }

    private static string SanitizeCapabilityDocument(string path)
    {
        var segments = path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(static segment => segment is not ("." or "..") && !segment.EndsWith(':'))
            .TakeLast(8)
            .ToArray();
        var sanitized = segments.Length == 0 ? "source" : string.Join('/', segments);
        return sanitized.Length <= 512 ? sanitized : sanitized[^512..];
    }

    private static async Task<IReadOnlyDictionary<string, byte[]>> CreateCapabilityFramesAsync(
        CapabilityPdbIdentity pdb,
        bool mappedJit,
        int maximumOutputBytes)
    {
        var graph = new RuntimeGraphDocument(
            [new RuntimeGraphRoot("value", 1)],
            [new RuntimeGraphNode(1, "System.Int32", "scalar", "42", [])],
            false,
            null);
        var evidenceRanges = mappedJit
            ? pdb.Ranges.Select((range, index) => new
            {
                range.IlOffset,
                NativeStartOffset = index * 8,
                NativeEndOffset = (index + 1) * 8,
                range.Document,
                range.StartLine,
                range.StartColumn,
                range.EndLine,
                range.EndColumn
            }).ToArray<object>()
            : [];
        var jitSummary = RuntimeStructuredPayloadCodec.Serialize(new
        {
            RuntimeVersion = "10.0.9",
            Assembly = "SharpLabNext.UnitTests",
            MethodFilter = "RuntimeSupervisorTests.CapabilityPdbProbe",
            Methods = new[]
            {
                new
                {
                    Method = pdb.MethodToken,
                    DisplayName = "RuntimeSupervisorTests.CapabilityPdbProbe",
                    Status = "prepared",
                    Address = "0x00007ff800001000",
                    Error = (string?)null,
                    NativeCodeSize = 48,
                    InstructionCount = 12,
                    LinkedRanges = Array.Empty<object>(),
                    MappingSource = mappedJit ? "ordinary" : "method",
                    EvidenceRanges = evidenceRanges
                }
            }
        });
        var completed = """{"Status":"completed","ExitCode":0,"ElapsedMilliseconds":1}"""u8.ToArray();
        var userException = RuntimeStructuredPayloadCodec.Serialize(new
        {
            TypeName = "System.InvalidOperationException",
            Message = "capability user exception",
            StackTrace = "at CapabilityProbe.Throw()",
            InnerException = (object?)null,
            ElapsedMilliseconds = 1
        });
        var userExceptionExit =
            """{"Status":"user-exception","ExitCode":1,"ElapsedMilliseconds":1}"""u8.ToArray();

        return new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["success-security"] = await CreateRuntimeFramesAsync(
                (RuntimeFrameKind.Stdout, Encoding.UTF8.GetBytes(
                    "SLN-CAPABILITY-STDOUT-V1\nSLN-CAPABILITY-NETWORK-BLOCKED-V1\n" +
                    "SLN-CAPABILITY-ROOTFS-READONLY-V1\n")),
                (RuntimeFrameKind.Stderr, "SLN-CAPABILITY-STDERR-V1\n"u8.ToArray()),
                (RuntimeFrameKind.Exit, completed)),
            ["user-exception"] = await CreateRuntimeFramesAsync(
                (RuntimeFrameKind.Exception, userException),
                (RuntimeFrameKind.Exit, userExceptionExit)),
            ["output-overflow"] = await CreateRuntimeFramesAsync(
                (RuntimeFrameKind.Stdout, Enumerable.Repeat((byte)'x', maximumOutputBytes + 1).ToArray())),
            ["process-tree"] = await CreateRuntimeFramesAsync((RuntimeFrameKind.Exit, completed)),
            ["inspection"] = await CreateRuntimeFramesAsync(
                (RuntimeFrameKind.Inspection, RuntimeStructuredPayloadCodec.Serialize(
                    new RuntimeInspectionPayload("Value", "value", graph))),
                (RuntimeFrameKind.MemoryGraph, RuntimeStructuredPayloadCodec.Serialize(
                    new RuntimeInspectionPayload("MemoryGraph", "memory", graph))),
                (RuntimeFrameKind.Exit, completed)),
            ["execution-flow"] = await CreateRuntimeFramesAsync(
                (RuntimeFrameKind.Flow, RuntimeStructuredPayloadCodec.Serialize(new RuntimeFlowPayload(
                    "sequence-point", "Program.cs", new RuntimeSourceRange(1, 1, 1, 8), 1, null, null, null, false))),
                (RuntimeFrameKind.Flow, RuntimeStructuredPayloadCodec.Serialize(new RuntimeFlowPayload(
                    "branch", "Program.cs", new RuntimeSourceRange(2, 1, 2, 8), 1, null, null, null, false))),
                (RuntimeFrameKind.Flow, RuntimeStructuredPayloadCodec.Serialize(new RuntimeFlowPayload(
                    "sequence-point", "Program.cs", new RuntimeSourceRange(3, 1, 3, 8), 1, null, null, null, false))),
                (RuntimeFrameKind.Exit, completed)),
            ["jit"] = await CreateRuntimeFramesAsync(
                (RuntimeFrameKind.JitAssembly, "CapabilityPdbProbe():\n    ret\n"u8.ToArray()),
                (RuntimeFrameKind.JitSummary, jitSummary),
                (RuntimeFrameKind.Exit, completed)),
            ["empty"] = []
        };
    }

    private static async Task<byte[]> CreateRuntimeFramesAsync(
        params (RuntimeFrameKind Kind, byte[] Payload)[] frames)
    {
        await using var stream = new MemoryStream();
        await using var writer = new RuntimeFrameWriter(stream, RuntimeFrameTransport.Base64Line);
        foreach (var (kind, payload) in frames)
        {
            await writer.WriteAsync(kind, payload, TestContext.Current.CancellationToken);
        }
        return stream.ToArray();
    }

    private static async Task<byte[]> CompletedRunFramesAsync()
    {
        await using var stream = new MemoryStream();
        await using var writer = new RuntimeFrameWriter(stream, RuntimeFrameTransport.Base64Line);
        await writer.WriteAsync(
            RuntimeFrameKind.Exit,
            """{"Status":"completed","ExitCode":0,"ElapsedMilliseconds":1}"""u8.ToArray(),
            TestContext.Current.CancellationToken);
        return stream.ToArray();
    }

    private static async Task<byte[]> CompletedJitFramesAsync(int sequencePointRangeCount)
    {
        var linkedRanges = Enumerable.Range(0, sequencePointRangeCount)
            .Select(static index => new
            {
                SourceFilePath = "Program.cs",
                SourceRange = new
                {
                    StartLine = index + 1,
                    StartCharacter = 4,
                    EndLine = index + 1,
                    EndCharacter = 12
                },
                OutputRange = new
                {
                    StartLine = index + 1,
                    StartCharacter = 0,
                    EndLine = index + 1,
                    EndCharacter = 8
                },
                Precision = "sequence-point"
            })
            .ToArray();
        var summary = RuntimeStructuredPayloadCodec.Serialize(new
        {
            RuntimeVersion = "10.0.9",
            Assembly = "SharpLabNext.User",
            MethodFilter = "Program.Main",
            Methods = new[]
            {
                new
                {
                    Method = "0x06000001",
                    DisplayName = "Program.Main",
                    Status = "prepared",
                    Address = "0x00007FF800001000",
                    Error = (string?)null,
                    NativeCodeSize = 16,
                    InstructionCount = 4,
                    LinkedRanges = linkedRanges,
                    MappingSource = sequencePointRangeCount > 0 ? "sequence-points" : "none"
                }
            }
        });

        await using var stream = new MemoryStream();
        await using var writer = new RuntimeFrameWriter(stream, RuntimeFrameTransport.Base64Line);
        await writer.WriteAsync(
            RuntimeFrameKind.JitAssembly,
            "Program.Main():\n    ret\n"u8.ToArray(),
            TestContext.Current.CancellationToken);
        await writer.WriteAsync(
            RuntimeFrameKind.JitSummary,
            summary,
            TestContext.Current.CancellationToken);
        await writer.WriteAsync(
            RuntimeFrameKind.Exit,
            """{"Status":"completed","ExitCode":0,"ElapsedMilliseconds":1}"""u8.ToArray(),
            TestContext.Current.CancellationToken);
        return stream.ToArray();
    }

    private static RuntimePerformanceFixture CreateRuntimePerformanceFixture(
        SessionDockerClient fakeDocker,
        string sourceMappingKind = RuntimeJitSourceMappingKinds.None)
    {
        var options = ValidOptions();
        options.PromotionPreflightPlanSha256 = $"sha256:{new string('f', 64)}";
        options.PromotionPreflightProfileSha256 = $"sha256:{new string('e', 64)}";
        options.PromotionPreflightSourceRevision = new string('d', 40);
        options.SessionReuseEnabled = false;
        var profile = Assert.Single(options.Profiles);
        profile.Image = $"registry.example/sharplabnext/runtime@sha256:{new string('b', 64)}";
        profile.RuntimeImageId = $"sha256:{new string('a', 64)}";
        profile.Operations = new RuntimeProfileOperations
        {
            Run = new RuntimeRunOperationDefinition
            {
                ImplementationId = RuntimeOperationImplementationIds.Runner,
                PathStyle = RuntimeOperationPathStyles.Unix,
                Command = new RuntimeOperationCommandDefinition
                {
                    Executable = "/usr/share/dotnet/dotnet",
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
                PathStyle = RuntimeOperationPathStyles.Unix,
                SourceMappingKind = sourceMappingKind,
                ProfilerPath = sourceMappingKind == RuntimeJitSourceMappingKinds.LinuxProfiler
                    ? "/opt/sharplabnext/SharpLabNext.JitProfiler.so"
                    : null,
                Command = new RuntimeOperationCommandDefinition
                {
                    Executable = "/usr/share/dotnet/dotnet",
                    Argv =
                    [
                        "/opt/sharplabnext/SharpLabNext.JitInspector.dll",
                        RuntimeOperationPlaceholders.EntryAssembly,
                        RuntimeOperationPlaceholders.MethodFilter
                    ]
                }
            }
        };
        var root = FindRepositoryRoot();
        var sandbox = RuntimeSandboxPolicy.Load(
            new RuntimeSandboxOptions
            {
                SeccompProfilePath = Path.Combine(
                    root,
                    "src",
                    "Supervisor",
                    "SharpLabNext.RuntimeSupervisor",
                    "security",
                    "runtime-job-seccomp.v1.json")
            },
            root);
        var sessions = new RuntimeSessionRegistry(
            fakeDocker,
            Options.Create(options),
            sandbox,
            NullLogger<RuntimeSessionRegistry>.Instance);
        var artifactStore = new RuntimeArtifactStoreClient(new ArtifactBundleDescriptor(
            Manifest(metadata: null, derived: false),
            []));
        var operations = new OperationStore();
        var scheduler = new BoundedOperationScheduler(
            operations,
            new OperationExecutionOptions
            {
                QueueCapacity = 4,
                WorkerConcurrency = 1,
                ExecutorId = "runtime-performance-test"
            });
        var executor = new RuntimeJobExecutor(
            operations,
            scheduler,
            artifactStore,
            fakeDocker,
            sessions,
            Options.Create(options),
            new ServiceIdentity(
                "runtime-supervisor",
                ServiceKind.RuntimeSupervisor,
                "release-1",
                ProtocolVersion.WorkerV1,
                [],
                "ready"),
            NullLogger<RuntimeJobExecutor>.Instance);
        var coordinator = new RuntimePerformancePreflightCoordinator(
            operations,
            executor,
            fakeDocker,
            Options.Create(options));
        return new RuntimePerformanceFixture(
            coordinator,
            scheduler,
            artifactStore,
            options);
    }

    private static (RuntimeSessionRegistry Sessions, RuntimeSessionRequest Request) CreateRuntimeSessionFixture(
        SessionDockerClient fakeDocker,
        string sessionId,
        bool sessionReuseEnabled = true)
    {
        var root = FindRepositoryRoot();
        var options = ValidOptions();
        options.SessionReuseEnabled = sessionReuseEnabled;
        var sandbox = RuntimeSandboxPolicy.Load(
            new RuntimeSandboxOptions
            {
                SeccompProfilePath = Path.Combine(
                    root,
                    "src",
                    "Supervisor",
                    "SharpLabNext.RuntimeSupervisor",
                    "security",
                    "runtime-job-seccomp.v1.json")
            },
            root);
        var sessions = new RuntimeSessionRegistry(
            fakeDocker,
            Options.Create(options),
            sandbox,
            NullLogger<RuntimeSessionRegistry>.Instance);
        var request = new RuntimeSessionRequest(
            sessionId,
            "release-1",
            "runtime-image:test",
            ["dotnet", "/opt/sharplabnext/SharpLabNext.Runner.dll", "app.dll"],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["MODE"] = "run" },
            options.SecurityPolicies[0],
            RuntimeContainerIsolationKind.Standard,
            WinePrefixPath: null,
            options.ContainerLabel,
            options.ResourceScope);
        return (sessions, request);
    }

    private static async Task WriteDockerFrameAsync(
        Stream destination,
        byte streamKind,
        ReadOnlyMemory<byte> payload)
    {
        var header = new byte[8];
        header[0] = streamKind;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), payload.Length);
        await destination.WriteAsync(header, TestContext.Current.CancellationToken);
        await destination.WriteAsync(payload, TestContext.Current.CancellationToken);
    }

    private static async Task<OperationState> WaitForTerminalAsync(
        OperationStore operations,
        string operationId)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var state = operations.Get(operationId)
                ?? throw new InvalidOperationException("Runtime operation was not found.");
            if (state.Status is OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled)
                return state;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("Runtime operation did not become terminal.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SharpLabNext.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed record RecordedDockerRequest(
        HttpMethod Method,
        string Path,
        string? Body,
        string? ContentType);

    private sealed class PromotionPreflightProfileFixture(
        string root,
        string profileId,
        string planSha256,
        string profileSha256,
        string sourceRevision,
        IConfiguration configuration) : IDisposable
    {
        public string ProfileId { get; } = profileId;
        public string PlanSha256 { get; } = planSha256;
        public string ProfileSha256 { get; } = profileSha256;
        public string SourceRevision { get; } = sourceRevision;
        public IConfiguration Configuration { get; } = configuration;

        public void Dispose()
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RuntimePerformanceFixture(
        RuntimePerformancePreflightCoordinator coordinator,
        BoundedOperationScheduler scheduler,
        RuntimeArtifactStoreClient artifactStore,
        RuntimeSupervisorOptions options) : IAsyncDisposable
    {
        public RuntimePerformancePreflightCoordinator Coordinator { get; } = coordinator;
        public BoundedOperationScheduler Scheduler { get; } = scheduler;
        public RuntimeArtifactStoreClient ArtifactStore { get; } = artifactStore;
        public RuntimeSupervisorOptions Options { get; } = options;

        public ValueTask DisposeAsync() => Scheduler.DisposeAsync();
    }

    private sealed record CapabilityPdbRange(
        int IlOffset,
        string Document,
        int StartLine,
        int StartColumn,
        int EndLine,
        int EndColumn);

    private sealed record CapabilityPdbIdentity(
        string Digest,
        string ContentId,
        string MethodToken,
        IReadOnlyList<CapabilityPdbRange> Ranges);

    private sealed class RuntimeCapabilityFixture(
        RuntimeCapabilityPreflightCoordinator coordinator,
        BoundedOperationScheduler scheduler,
        CapabilityDockerClient docker,
        RuntimeProfileOptions profile,
        RuntimeSecurityPolicyOptions policy,
        ArtifactRef probeArtifactRef,
        ArtifactRef flowArtifactRef,
        CapabilityPdbIdentity pdbIdentity) : IAsyncDisposable
    {
        public RuntimeCapabilityPreflightCoordinator Coordinator { get; } = coordinator;
        public CapabilityDockerClient Docker { get; } = docker;
        public RuntimeProfileOptions Profile { get; } = profile;
        public RuntimeSecurityPolicyOptions Policy { get; } = policy;
        public ArtifactRef ProbeArtifactRef { get; } = probeArtifactRef;
        public ArtifactRef FlowArtifactRef { get; } = flowArtifactRef;
        public CapabilityPdbIdentity PdbIdentity { get; } = pdbIdentity;
        public string PlanSha256 { get; } = $"sha256:{new string('2', 64)}";
        public string PreflightProfileSha256 { get; } = $"sha256:{new string('3', 64)}";

        public RuntimeCapabilityPreflightRequest CreateRequest() => new(
            Profile.Id,
            Policy.Id,
            new string('1', 40),
            PlanSha256,
            PreflightProfileSha256,
            ProbeArtifactRef,
            Profile.Capabilities.Contains("execution-flow", StringComparer.Ordinal)
                ? FlowArtifactRef
                : null,
            Profile.Capabilities.Contains("jit-asm", StringComparer.Ordinal)
                ? "RuntimeSupervisorTests.CapabilityPdbProbe"
                : null,
            Profile.Capabilities.Contains("jit-asm", StringComparer.Ordinal)
                ? "/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.9/libclrjit.so"
                : null);

        public ValueTask DisposeAsync() => scheduler.DisposeAsync();
    }

    private sealed class CapabilityArtifactStoreClient(
        IReadOnlyList<ArtifactBundleDescriptor> descriptors,
        IReadOnlyDictionary<(ArtifactRef ArtifactRef, string Path), byte[]> files) : IArtifactStoreClient
    {
        private readonly Dictionary<ArtifactRef, ArtifactBundleDescriptor> _descriptors =
            descriptors.ToDictionary(static descriptor => descriptor.Manifest.ArtifactId);

        public Task<ArtifactBundleDescriptor?> GetArtifactAsync(
            ArtifactRef artifactRef,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _descriptors.TryGetValue(artifactRef, out var descriptor);
            return Task.FromResult(descriptor);
        }

        public Task<ArtifactContentResponse> OpenArtifactFileReadAsync(
            ArtifactRef artifactRef,
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = files[(artifactRef, ArtifactPath.Normalize(path))];
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };
            return Task.FromResult(new ArtifactContentResponse(
                response,
                new MemoryStream(bytes, writable: false)));
        }

        public Task<ArtifactLeaseResponse> AcquireLeaseAsync(
            ArtifactRef artifactRef,
            string owner,
            TimeSpan duration,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ArtifactLeaseResponse(
                $"lease-{Guid.NewGuid():N}",
                artifactRef,
                owner,
                DateTimeOffset.UtcNow.Add(duration)));
        }

        public Task ReleaseLeaseAsync(
            string leaseToken,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public async Task<PutContentResponse> PutContentAsync(
            ContentRef contentRef,
            Stream content,
            long? declaredSize = null,
            TimeSpan? timeToLive = null,
            CancellationToken cancellationToken = default)
        {
            using var output = new MemoryStream();
            await content.CopyToAsync(output, cancellationToken);
            Assert.Equal(declaredSize, output.Length);
            Assert.Equal(contentRef, ContentIdentity.Compute(output.ToArray()));
            return new PutContentResponse(
                contentRef,
                output.Length,
                DateTimeOffset.UtcNow.Add(timeToLive ?? TimeSpan.FromMinutes(5)),
                AlreadyExisted: false);
        }

        public Task<ArtifactContentResponse> OpenContentReadAsync(
            ContentRef contentRef,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PutArtifactResponse> PutArtifactAsync(
            ArtifactManifest manifest,
            IReadOnlyList<ArtifactFileUpload> files,
            TimeSpan? timeToLive = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ArtifactLeaseResponse> RenewLeaseAsync(
            string leaseToken,
            TimeSpan duration,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GarbageCollectionResponse> CollectGarbageAsync(
            int maxArtifacts = 1000,
            int maxContents = 5000,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class CapabilityDockerClient(
        RuntimeProfileOptions profile,
        IReadOnlyDictionary<string, byte[]> frames) : IDockerEngineClient
    {
        private readonly Dictionary<string, RuntimeContainerSpec> _specs = new(StringComparer.Ordinal);
        private readonly HashSet<string> _activeContainers = new(StringComparer.Ordinal);
        private long _nextId;

        public int RuntimeContainerCreates { get; private set; }
        public string? CorruptInspectedRole { get; set; }
        public HashSet<string> ActiveContainers => _activeContainers;
        public List<IReadOnlyList<RuntimeImageFileRequest>> ImageFileRequests { get; } = [];
        public List<RuntimeContainerSpec> CreatedSpecs { get; } = [];

        public Task<bool> PingAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<RuntimeImageInspection> InspectImageAsync(
            string immutableReference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new RuntimeImageInspection(
                immutableReference,
                profile.RuntimeImageId,
                512L * 1024 * 1024,
                "linux",
                "amd64",
                [immutableReference]));

        public Task<IReadOnlyList<RuntimeImageFileInspection>> InspectImageFilesAsync(
            string imageId,
            IReadOnlyList<RuntimeImageFileRequest> requestedFiles,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(profile.RuntimeImageId, imageId);
            ImageFileRequests.Add(requestedFiles.ToArray());
            var result = requestedFiles.Select(file =>
            {
                var (format, architecture) = file.Role switch
                {
                    "helper" or "support-assembly" => ("managed-pe", "anycpu"),
                    _ => ("elf", "x64")
                };
                var path = file.Role == CorruptInspectedRole ? file.Path + ".wrong" : file.Path;
                return new RuntimeImageFileInspection(
                    file.Role,
                    path,
                    $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(file.Path)))}",
                    65_536,
                    format,
                    architecture);
            }).ToArray();
            return Task.FromResult<IReadOnlyList<RuntimeImageFileInspection>>(result);
        }

        public Task<RuntimeWorkspaceMaterialization> MaterializeWorkspaceAsync(
            string jobId,
            string releaseId,
            string image,
            Stream archive,
            RuntimeSecurityPolicyOptions securityPolicy,
            RuntimeContainerIsolationKind isolationKind,
            string managementLabel,
            string resourceScope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var materializerId = NextContainerId();
            _activeContainers.Add(materializerId);
            return Task.FromResult(new RuntimeWorkspaceMaterialization(
                $"workspace-{materializerId}",
                materializerId));
        }

        public Task<string> CreateContainerAsync(
            RuntimeContainerSpec spec,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = NextContainerId();
            RuntimeContainerCreates++;
            CreatedSpecs.Add(spec);
            _specs.Add(id, spec);
            _activeContainers.Add(id);
            return Task.FromResult(id);
        }

        public Task UploadArchiveAsync(
            string containerId,
            Stream archive,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StartContainerAsync(
            string containerId,
            CancellationToken cancellationToken = default)
        {
            Assert.Contains(containerId, _activeContainers);
            return Task.CompletedTask;
        }

        public Task<IRuntimeContainerResourceMonitor> StartContainerResourceMonitorAsync(
            string containerId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task StopContainerAsync(
            string containerId,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<Stream> OpenContainerLogsAsync(
            string containerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(SelectFrames(_specs[containerId]), writable: false));

        public Task<Stream> OpenContainerLogsSinceAsync(
            string containerId,
            DateTimeOffset sinceUtc,
            CancellationToken cancellationToken = default) =>
            OpenContainerLogsAsync(containerId, cancellationToken);

        public async Task<RuntimeContainerExit> WaitContainerAsync(
            string containerId,
            CancellationToken cancellationToken = default)
        {
            var spec = _specs[containerId];
            if (spec.Command.Contains("hang", StringComparer.Ordinal))
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return spec.Command.Contains("user-exception", StringComparer.Ordinal)
                ? new RuntimeContainerExit(1, false, null)
                : new RuntimeContainerExit(0, false, null);
        }

        public Task KillContainerAsync(
            string containerId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveContainerAsync(
            string containerId,
            CancellationToken cancellationToken = default)
        {
            _activeContainers.Remove(containerId);
            return Task.CompletedTask;
        }

        public Task RemoveWorkspaceVolumeAsync(
            string volumeName,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ManagedRuntimeContainer>> ListManagedContainersAsync(
            string managementLabel,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ManagedRuntimeContainer>>(_activeContainers
                .Select(static id => new ManagedRuntimeContainer(id, DateTimeOffset.UtcNow, "running"))
                .ToArray());

        public Task<IReadOnlyList<ManagedWorkspaceVolume>> ListManagedWorkspaceVolumesAsync(
            string managementLabel,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ManagedWorkspaceVolume>>([]);

        private string NextContainerId() => Interlocked.Increment(ref _nextId).ToString(
            "x64",
            System.Globalization.CultureInfo.InvariantCulture);

        private byte[] SelectFrames(RuntimeContainerSpec spec)
        {
            if (spec.Command.Contains(profile.Layout.JitInspectorAssemblyPath!, StringComparer.Ordinal))
                return frames["jit"];
            foreach (var name in new[]
                     {
                         "success-security",
                         "user-exception",
                         "output-overflow",
                         "process-tree",
                         "inspection",
                         "execution-flow"
                     })
            {
                if (spec.Command.Contains(name, StringComparer.Ordinal))
                    return frames[name];
            }
            return frames["empty"];
        }
    }

    private sealed class RecordingDockerHandler : HttpMessageHandler
    {
        public const string ContainerId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        public List<RecordedDockerRequest> Requests { get; } = [];

        public HttpStatusCode ArchiveStatusCode { get; set; } = HttpStatusCode.OK;

        public HttpStatusCode StopStatusCode { get; set; } = HttpStatusCode.NoContent;

        public string? StatsPayload { get; set; }

        public string? StatsOneShotPayload { get; set; }

        public string? ImageInspectionPayload { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedDockerRequest(
                request.Method,
                request.RequestUri!.PathAndQuery,
                body,
                request.Content?.Headers.ContentType?.MediaType));

            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/volumes/create", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}") };
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/containers/create", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent($"{{\"Id\":\"{ContainerId}\"}}", Encoding.UTF8, "application/json")
                };
            }
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/start", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/stop", StringComparison.Ordinal))
                return new HttpResponseMessage(StopStatusCode);
            if (request.Method == HttpMethod.Put && request.RequestUri.AbsolutePath.EndsWith("/archive", StringComparison.Ordinal))
                return new HttpResponseMessage(ArchiveStatusCode);
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.EndsWith("/logs", StringComparison.Ordinal))
            {
                var payload = "runtime output"u8.ToArray();
                var frame = new byte[8 + payload.Length];
                frame[0] = 1;
                BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4), payload.Length);
                payload.CopyTo(frame.AsSpan(8));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(frame)
                };
            }
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.EndsWith("/stats", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        request.RequestUri.Query.Contains("one-shot=true", StringComparison.Ordinal)
                            ? StatsOneShotPayload ?? StatsPayload ?? throw new InvalidOperationException("No Docker stats fixture was configured.")
                            : StatsPayload ?? throw new InvalidOperationException("No Docker stats fixture was configured."),
                        Encoding.UTF8,
                        "application/json")
                };
            }
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.Contains("/images/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        ImageInspectionPayload ?? throw new InvalidOperationException("No Docker image fixture was configured."),
                        Encoding.UTF8,
                        "application/json")
                };
            }

            throw new InvalidOperationException($"Unexpected Docker request: {request.Method} {request.RequestUri.PathAndQuery}");
        }
    }

    private sealed class RuntimeArtifactStoreClient(ArtifactBundleDescriptor descriptor) : IArtifactStoreClient
    {
        public ArtifactBundleDescriptor Descriptor { get; } = descriptor;

        public List<ContentRef> PublishedContentRefs { get; } = [];

        public bool FailLeaseRelease { get; set; }

        public Task<ArtifactBundleDescriptor?> GetArtifactAsync(
            ArtifactRef artifactRef,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(Descriptor.Manifest.ArtifactId, artifactRef);
            return Task.FromResult<ArtifactBundleDescriptor?>(Descriptor);
        }

        public Task<ArtifactLeaseResponse> AcquireLeaseAsync(
            ArtifactRef artifactRef,
            string owner,
            TimeSpan duration,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ArtifactLeaseResponse(
                "lease-runtime-session-test",
                artifactRef,
                owner,
                DateTimeOffset.UtcNow.Add(duration)));
        }

        public Task ReleaseLeaseAsync(
            string leaseToken,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("lease-runtime-session-test", leaseToken);
            return FailLeaseRelease
                ? Task.FromException(new HttpRequestException("Lease release failed."))
                : Task.CompletedTask;
        }

        public async Task<PutContentResponse> PutContentAsync(
            ContentRef contentRef,
            Stream content,
            long? declaredSize = null,
            TimeSpan? timeToLive = null,
            CancellationToken cancellationToken = default)
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, cancellationToken);
            Assert.Equal(declaredSize, copy.Length);
            Assert.Equal(contentRef, ContentIdentity.Compute(copy.ToArray()));
            PublishedContentRefs.Add(contentRef);
            return new PutContentResponse(
                contentRef,
                copy.Length,
                DateTimeOffset.UtcNow.Add(timeToLive ?? TimeSpan.FromHours(1)),
                AlreadyExisted: false);
        }
        public Task<ArtifactContentResponse> OpenContentReadAsync(ContentRef contentRef, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PutArtifactResponse> PutArtifactAsync(ArtifactManifest manifest, IReadOnlyList<ArtifactFileUpload> files, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ArtifactContentResponse> OpenArtifactFileReadAsync(ArtifactRef artifactRef, string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ArtifactLeaseResponse> RenewLeaseAsync(string leaseToken, TimeSpan duration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GarbageCollectionResponse> CollectGarbageAsync(int maxArtifacts = 1000, int maxContents = 5000, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SessionDockerClient : IDockerEngineClient
    {
        public int RuntimeContainerCreates { get; private set; }

        public List<RuntimeContainerSpec> CreatedSpecs { get; } = [];

        public List<string> UploadedArchives { get; } = [];

        public List<string> StartedContainers { get; } = [];

        public List<string> KilledContainers { get; } = [];

        public List<string> StoppedContainers { get; } = [];

        public List<string> WorkspaceLifecycle { get; } = [];

        public List<string> RemovedContainers { get; } = [];

        public List<string> RemovedVolumes { get; } = [];

        public List<string> RemoveContainerAttempts { get; } = [];

        public List<string> CleanupLifecycle { get; } = [];

        public List<DateTimeOffset> LogSinceCursors { get; } = [];

        public List<string> ResourceMonitorStarts { get; } = [];

        public Exception? NextUploadArchiveException { get; set; }

        public Action? BeforeNextUploadArchive { get; set; }

        public Exception? NextStartContainerException { get; set; }

        public Exception? NextKillContainerException { get; set; }

        public Exception? NextStopContainerException { get; set; }

        public Exception? NextWaitContainerException { get; set; }

        public RuntimeContainerExit? NextWaitContainerExit { get; set; }

        public Exception? NextRemoveContainerException { get; set; }

        public Exception? NextResourceMonitorStartException { get; set; }

        public Exception? NextResourceMonitorStopException { get; set; }

        public RuntimeContainerResourceUsage ResourceUsage { get; set; } = new(4096, 1);

        public byte[] ContainerLogBytes { get; set; } = [];

        public bool FailRuntimeContainerRemoval { get; set; }

        public RuntimeImageInspection? ImageInspection { get; set; }

        public Task<bool> PingAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<RuntimeImageInspection> InspectImageAsync(
            string immutableReference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ImageInspection ?? new RuntimeImageInspection(
                immutableReference,
                $"sha256:{new string('a', 64)}",
                536870912,
                "linux",
                "amd64",
                [immutableReference]));

        public Task<string> CreateContainerAsync(
            RuntimeContainerSpec spec,
            CancellationToken cancellationToken = default)
        {
            RuntimeContainerCreates++;
            CreatedSpecs.Add(spec);
            return Task.FromResult($"runtime-{RuntimeContainerCreates}");
        }

        public async Task<RuntimeWorkspaceMaterialization> MaterializeWorkspaceAsync(
            string jobId,
            string releaseId,
            string image,
            Stream archive,
            RuntimeSecurityPolicyOptions securityPolicy,
            RuntimeContainerIsolationKind isolationKind,
            string managementLabel,
            string resourceScope,
            CancellationToken cancellationToken = default)
        {
            StartedContainers.Add("materializer-1");
            WorkspaceLifecycle.Add("start:materializer-1");
            await RecordArchiveAsync(archive, cancellationToken);
            return new RuntimeWorkspaceMaterialization("workspace-1", "materializer-1");
        }

        public Task UploadArchiveAsync(
            string containerId,
            Stream archive,
            CancellationToken cancellationToken = default)
        {
            var beforeUpload = BeforeNextUploadArchive;
            BeforeNextUploadArchive = null;
            beforeUpload?.Invoke();
            var exception = NextUploadArchiveException;
            NextUploadArchiveException = null;
            return exception is null
                ? RecordArchiveAsync(archive, cancellationToken)
                : Task.FromException(exception);
        }

        public Task StartContainerAsync(string containerId, CancellationToken cancellationToken = default)
        {
            var exception = NextStartContainerException;
            NextStartContainerException = null;
            if (exception is not null)
                return Task.FromException(exception);
            StartedContainers.Add(containerId);
            WorkspaceLifecycle.Add($"start:{containerId}");
            return Task.CompletedTask;
        }

        public Task<IRuntimeContainerResourceMonitor> StartContainerResourceMonitorAsync(
            string containerId,
            CancellationToken cancellationToken = default)
        {
            var exception = NextResourceMonitorStartException;
            NextResourceMonitorStartException = null;
            if (exception is not null)
                return Task.FromException<IRuntimeContainerResourceMonitor>(exception);
            ResourceMonitorStarts.Add(containerId);
            var stopException = NextResourceMonitorStopException;
            NextResourceMonitorStopException = null;
            return Task.FromResult<IRuntimeContainerResourceMonitor>(
                new SessionResourceMonitor(ResourceUsage, stopException));
        }

        public Task StopContainerAsync(
            string containerId,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Assert.InRange(timeout, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(10));
            var exception = NextStopContainerException;
            NextStopContainerException = null;
            if (exception is not null)
                return Task.FromException(exception);
            StoppedContainers.Add(containerId);
            WorkspaceLifecycle.Add($"stop:{containerId}");
            return Task.CompletedTask;
        }

        public Task<Stream> OpenContainerLogsAsync(
            string containerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(ContainerLogBytes, writable: false));

        public Task<Stream> OpenContainerLogsSinceAsync(
            string containerId,
            DateTimeOffset sinceUtc,
            CancellationToken cancellationToken = default)
        {
            LogSinceCursors.Add(sinceUtc);
            return Task.FromResult<Stream>(new MemoryStream(ContainerLogBytes, writable: false));
        }

        public Task<RuntimeContainerExit> WaitContainerAsync(
            string containerId,
            CancellationToken cancellationToken = default)
        {
            var exception = NextWaitContainerException;
            NextWaitContainerException = null;
            if (exception is not null)
                return Task.FromException<RuntimeContainerExit>(exception);
            var exit = NextWaitContainerExit ?? new RuntimeContainerExit(0, false, null);
            NextWaitContainerExit = null;
            return Task.FromResult(exit);
        }

        public Task KillContainerAsync(string containerId, CancellationToken cancellationToken = default)
        {
            var exception = NextKillContainerException;
            NextKillContainerException = null;
            if (exception is not null)
                return Task.FromException(exception);
            KilledContainers.Add(containerId);
            WorkspaceLifecycle.Add($"kill:{containerId}");
            return Task.CompletedTask;
        }

        public Task RemoveContainerAsync(string containerId, CancellationToken cancellationToken = default)
        {
            RemoveContainerAttempts.Add(containerId);
            if (FailRuntimeContainerRemoval && containerId.StartsWith("runtime-", StringComparison.Ordinal))
            {
                CleanupLifecycle.Add($"remove-failed:{containerId}");
                return Task.FromException(new HttpRequestException("Runtime container removal failed."));
            }
            var exception = NextRemoveContainerException;
            NextRemoveContainerException = null;
            if (exception is not null)
            {
                CleanupLifecycle.Add($"remove-failed:{containerId}");
                return Task.FromException(exception);
            }
            RemovedContainers.Add(containerId);
            CleanupLifecycle.Add($"remove:{containerId}");
            return Task.CompletedTask;
        }

        public Task RemoveWorkspaceVolumeAsync(string volumeName, CancellationToken cancellationToken = default)
        {
            RemovedVolumes.Add(volumeName);
            CleanupLifecycle.Add($"volume:{volumeName}");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ManagedRuntimeContainer>> ListManagedContainersAsync(
            string managementLabel,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ManagedRuntimeContainer>>([]);

        public Task<IReadOnlyList<ManagedWorkspaceVolume>> ListManagedWorkspaceVolumesAsync(
            string managementLabel,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ManagedWorkspaceVolume>>([]);

        private async Task RecordArchiveAsync(Stream archive, CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(archive, Encoding.UTF8, leaveOpen: true);
            var content = await reader.ReadToEndAsync(cancellationToken);
            UploadedArchives.Add(content);
            WorkspaceLifecycle.Add($"upload:{content}");
        }

        private sealed class SessionResourceMonitor(
            RuntimeContainerResourceUsage usage,
            Exception? stopException) : IRuntimeContainerResourceMonitor
        {
            public Task<RuntimeContainerResourceUsage> StopAsync(
                CancellationToken cancellationToken = default) => stopException is null
                ? Task.FromResult(usage)
                : Task.FromException<RuntimeContainerResourceUsage>(stopException);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
