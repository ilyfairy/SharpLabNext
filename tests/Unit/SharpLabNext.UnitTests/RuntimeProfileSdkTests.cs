using System.Text.Json;
using SharpLabNext.RuntimeProfile.Sdk;

namespace SharpLabNext.UnitTests;

public sealed class RuntimeProfileSdkTests
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void RuntimeProfileBuildsCommandsFromDeclaredImageLayout()
    {
        var profile = Profile();
        profile.Layout.DotNetHostPath = "/runtime/dotnet";
        profile.Layout.RunnerAssemblyPath = "/helpers/Runner.dll";
        profile.Layout.JitInspectorAssemblyPath = "/helpers/Jit.dll";

        var run = RuntimeProfileCommandBuilder.CreateRunCommand(profile, "app/Program.dll", ["first", "second"]);
        var jit = RuntimeProfileCommandBuilder.CreateJitCommand(profile, "app/Program.dll", "Program:Main");

        Assert.Equal(["/runtime/dotnet", "/helpers/Runner.dll", "/workspace/app/Program.dll", "--", "first", "second"], run);
        Assert.Equal(["/runtime/dotnet", "/helpers/Jit.dll", "/workspace/app/Program.dll", "Program:Main"], jit);
    }

    [Fact]
    public void RunOperationBuildsALinuxDirectCommandAndKeepsArgumentsLiteral()
    {
        var profile = Profile();
        profile.Operations = new RuntimeProfileOperations { Run = new RuntimeRunOperationDefinition { ImplementationId = RuntimeOperationImplementationIds.DirectRuntime, Command = new RuntimeOperationCommandDefinition { Executable = "/usr/share/dotnet/dotnet", Argv = [RuntimeOperationPlaceholders.EntryAssembly, "--", RuntimeOperationPlaceholders.Arguments] } } };

        var command = RuntimeProfileCommandBuilder.CreateRunCommand(profile, "app/Program.dll", ["$(touch /tmp/not-executed)", "; rm -rf /tmp/not-executed"]);

        Assert.Equal(
            [
                "/usr/share/dotnet/dotnet",
                "/workspace/app/Program.dll",
                "--",
                "$(touch /tmp/not-executed)",
                "; rm -rf /tmp/not-executed"
            ],
            command);
    }

    [Fact]
    public void RunOperationBuildsATargetRuntimeMonoCommand()
    {
        var profile = Profile();
        profile.Operations = new RuntimeProfileOperations
        {
            Run = new RuntimeRunOperationDefinition
            {
                ImplementationId = RuntimeOperationImplementationIds.TargetRuntimeRunner,
                Command = new RuntimeOperationCommandDefinition
                {
                    Executable = "/usr/bin/mono",
                    Argv =
                    [
                        "/opt/sharplabnext/SharpLabNext.TargetRuntimeRunner.exe",
                        "run",
                        RuntimeOperationPlaceholders.EntryAssembly,
                        "--",
                        RuntimeOperationPlaceholders.Arguments
                    ]
                }
            }
        };

        var command = RuntimeProfileCommandBuilder.CreateRunCommand(profile, "app/Program.exe", ["first"]);

        Assert.Equal(
            [
                "/usr/bin/mono",
                "/opt/sharplabnext/SharpLabNext.TargetRuntimeRunner.exe",
                "run",
                "/workspace/app/Program.exe",
                "--",
                "first"
            ],
            command);
    }

    [Fact]
    public void JitOperationBuildsAWineCoreClrCommandWithWineZPaths()
    {
        var profile = Profile();
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
                        RuntimeOperationPlaceholders.EntryAssembly,
                        RuntimeOperationPlaceholders.MethodFilter
                    ]
                }
            }
        };

        var command = RuntimeProfileCommandBuilder.CreateJitCommand(profile, "app/Program.dll", "Program:Main");

        Assert.Equal(
            [
                "/usr/lib/wine/wine64",
                @"C:\dotnet\dotnet.exe",
                @"Z:\opt\sharplabnext\SharpLabNext.LegacyJitInspector.dll",
                "jit",
                @"Z:\workspace\app\Program.dll",
                "Program:Main"
            ],
            command);
    }

    [Fact]
    public void OperationsReplaceTheLegacyRunnerEnumAndMustMatchCapabilities()
    {
        var profile = OperationProfile();
        profile.Layout.RunnerKind = "not-a-product-enum";

        var failures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);

        Assert.Empty(failures);

        profile.Operations!.Jit = null;
        failures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);

        Assert.Contains(failures, static failure => failure.Contains("'jit-asm' without a JIT operation", StringComparison.Ordinal));
    }

    [Fact]
    public void InstrumentationCapabilitiesRequireTheStandardCoreClrRunner()
    {
        var profile = OperationProfile();
        profile.Family = "coreclr";
        profile.Capabilities = ["run", "jit-asm", "inspection", "execution-flow"];

        Assert.Empty(RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false));

        profile.Family = "coreclr-wine";
        var failures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);

        Assert.Contains(failures, static failure => failure.Contains("instrumentation capabilities are supported only by the standard CoreCLR runner", StringComparison.Ordinal));
    }

    [Fact]
    public void InstrumentationCapabilitiesRequireTheModernRunnerImplementationAndInvocation()
    {
        var profile = OperationProfile();
        profile.Family = "coreclr";
        profile.Capabilities = ["run", "jit-asm", "inspection", "execution-flow"];
        profile.Operations!.Run!.ImplementationId = RuntimeOperationImplementationIds.LegacyJitInspector;
        profile.Operations.Run.Command.Argv =
        [
            "/opt/sharplabnext/SharpLabNext.LegacyJitInspector.dll",
            "run",
            RuntimeOperationPlaceholders.EntryAssembly,
            RuntimeOperationPlaceholders.Arguments
        ];

        var failures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);

        Assert.Contains(failures, static failure => failure.Contains("require Run implementation 'sharplabnext-runner-v1'", StringComparison.Ordinal));

        profile.Operations.Run.ImplementationId = RuntimeOperationImplementationIds.Runner;
        failures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);

        Assert.Contains(failures, static failure => failure.Contains("must invoke '/opt/sharplabnext/SharpLabNext.Runner.dll'", StringComparison.Ordinal));
    }

    [Fact]
    public void HelperImplementationContractsRejectExecutablePrefixBypasses()
    {
        var profile = OperationProfile();
        profile.Operations!.Run!.Command.Argv.Insert(0, "/opt/untrusted/Other.dll");
        profile.Operations.Jit!.Command.Argv.Insert(0, "/opt/untrusted/Other.dll");

        var runFailures = RuntimeProfileValidation.Validate(profile.Operations.Run);
        var jitFailures = RuntimeProfileValidation.Validate(profile.Operations.Jit);

        Assert.Contains(runFailures, static failure => failure.Contains("fixed operation contract", StringComparison.Ordinal));
        Assert.Contains(jitFailures, static failure => failure.Contains("fixed operation contract", StringComparison.Ordinal));

        var bridge = new RuntimeRunOperationDefinition
        {
            ImplementationId = RuntimeOperationImplementationIds.WineRunner,
            Command = new RuntimeOperationCommandDefinition
            {
                Executable = "dotnet",
                Argv =
                [
                    "/opt/untrusted/Other.dll",
                    "/opt/sharplabnext/SharpLabNext.WineRunner.dll",
                    "bridge",
                    "/usr/bin/mono",
                    RuntimeOperationPlaceholders.EntryAssembly,
                    "--",
                    RuntimeOperationPlaceholders.Arguments
                ]
            }
        };
        Assert.Contains(RuntimeProfileValidation.Validate(bridge), static failure => failure.Contains("must invoke 'sharplabnext-wine-runner-v1'", StringComparison.Ordinal) || failure.Contains("must invoke '/opt/sharplabnext/SharpLabNext.WineRunner.dll'", StringComparison.Ordinal));

        var targetRuntimeRunner = new RuntimeRunOperationDefinition
        {
            ImplementationId = RuntimeOperationImplementationIds.TargetRuntimeRunner,
            Command = new RuntimeOperationCommandDefinition
            {
                Executable = "/usr/bin/mono",
                Argv =
                [
                    "/opt/untrusted/Other.exe",
                    "run",
                    RuntimeOperationPlaceholders.EntryAssembly,
                    "--",
                    RuntimeOperationPlaceholders.Arguments
                ]
            }
        };
        Assert.Contains(RuntimeProfileValidation.Validate(targetRuntimeRunner), static failure => failure.Contains("fixed target CLR host", StringComparison.Ordinal));
    }

    [Fact]
    public void LegacyHelperContractRejectsPrefixBypassesAndRuntimeVersionDrift()
    {
        var profile = JsonSerializer.Deserialize<RuntimeProfileDefinition>(File.ReadAllText(Path.Combine(FindProfilesDirectory(), "candidates", "dotnet-7-linux-x64.json")), WebJsonOptions);
        Assert.NotNull(profile?.Operations?.Run);
        profile.Operations.Run.Command.Argv.Insert(0, "/opt/untrusted/Other.dll");

        var failures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);

        Assert.Contains(failures, static failure => failure.Contains("exact fixed operation contract", StringComparison.Ordinal));

        profile.Operations.Run.Command.Argv.RemoveAt(0);
        profile.Operations.Run.Command.Argv[2] = "8.0.0";
        failures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);

        Assert.Contains(failures, static failure => failure.Contains("'--fx-version' must match runtime profile version", StringComparison.Ordinal));
    }

    [Fact]
    public void InstrumentationCapabilitiesCannotBeDeclaredByLegacyProfiles()
    {
        var profile = Profile();
        profile.Family = "coreclr";
        profile.Capabilities = ["run", "inspection"];

        var failures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);

        Assert.Contains(failures, static failure => failure.Contains("without operation-based Run support", StringComparison.Ordinal));
    }

    [Fact]
    public void MonoProfilesAllowOnlyTheBoundedMonoJitInspector()
    {
        var profile = OperationProfile();
        profile.Family = "mono";
        profile.Capabilities = ["run"];
        profile.AcceptedArtifactFormats = ["dotnet-framework-managed-pe-v1"];
        profile.Container = new RuntimeContainerDefinition { IsolationKind = RuntimeContainerIsolationKinds.Standard, EnvironmentKind = RuntimeContainerEnvironmentKinds.Mono, ExecutionUser = RuntimeContainerExecutionUsers.NonRoot };
        profile.Operations!.Jit = null;
        profile.Layout.DotNetHostPath = "/usr/bin/mono";
        profile.Layout.RunnerAssemblyPath = "/opt/sharplabnext/SharpLabNext.TargetRuntimeRunner.exe";
        profile.Operations.Run!.ImplementationId = RuntimeOperationImplementationIds.TargetRuntimeRunner;
        profile.Operations.Run.Command = new RuntimeOperationCommandDefinition
        {
            Executable = "/usr/bin/mono",
            Argv =
            [
                "/opt/sharplabnext/SharpLabNext.TargetRuntimeRunner.exe",
                "run",
                RuntimeOperationPlaceholders.EntryAssembly,
                "--",
                RuntimeOperationPlaceholders.Arguments
            ]
        };

        Assert.Empty(RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false));

        profile.Capabilities = ["run", "jit-asm"];
        profile.Layout.JitInspectorAssemblyPath =
            "/opt/sharplabnext/SharpLabNext.MonoJitInspector.dll";
        profile.Operations.Jit = new RuntimeJitOperationDefinition
        {
            ImplementationId = RuntimeOperationImplementationIds.MonoJitInspector,
            SourceMappingKind = RuntimeJitSourceMappingKinds.None,
            Command = new RuntimeOperationCommandDefinition
            {
                Executable = "/usr/share/dotnet/dotnet",
                Argv = [
                    "/opt/sharplabnext/SharpLabNext.MonoJitInspector.dll",
                    RuntimeOperationPlaceholders.EntryAssembly,
                    RuntimeOperationPlaceholders.MethodFilter
                ]
            }
        };

        Assert.Empty(RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false));

        profile.Operations.Jit.ImplementationId = RuntimeOperationImplementationIds.JitInspector;
        profile.Operations.Jit.Command.Argv[0] =
            "/opt/sharplabnext/SharpLabNext.JitInspector.dll";
        var failures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);

        Assert.Contains(failures, static failure => failure.Contains("requires JIT implementation", StringComparison.Ordinal));

        profile.Operations.Jit.ImplementationId = RuntimeOperationImplementationIds.MonoJitInspector;
        profile.Operations.Jit.Command.Argv[0] =
            "/opt/sharplabnext/SharpLabNext.MonoJitInspector.dll";
        profile.Operations.Jit.SourceMappingKind = RuntimeJitSourceMappingKinds.LinuxProfiler;
        failures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);

        Assert.Contains(failures, static failure => failure.Contains("source mapping kind 'none'", StringComparison.Ordinal));
    }

    [Fact]
    public void WineOperationProfilesCannotBypassFamilyChecksWithDotNetRunnerKind()
    {
        var profile = OperationProfile();
        profile.Container = new RuntimeContainerDefinition { IsolationKind = RuntimeContainerIsolationKinds.Wine, EnvironmentKind = RuntimeContainerEnvironmentKinds.Wine, ExecutionUser = RuntimeContainerExecutionUsers.Root, WinePrefixPath = "/opt/not-the-coreclr-prefix" };
        profile.Layout.RunnerKind = RuntimeRunnerKinds.DotNet;

        var failures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);

        Assert.Contains(failures, static failure => failure.Contains("wine-coreclr runner", StringComparison.Ordinal));
        Assert.Contains(failures, static failure => failure.Contains("/opt/wine-dotnet", StringComparison.Ordinal));
    }

    [Fact]
    public void RuntimeProfileRejectsUnknownCapabilities()
    {
        var profile = OperationProfile();
        profile.Capabilities = ["run", "jit-asm", "not-a-capability"];

        var failures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);

        Assert.Contains(failures, static failure => failure.Contains("declares unsupported capability 'not-a-capability'", StringComparison.Ordinal));
    }

    [Fact]
    public void OperationValidationRejectsShellsAndPlaceholderInjection()
    {
        var profile = OperationProfile();
        profile.Capabilities = ["run"];
        profile.Operations!.Jit = null;
        profile.Operations.Run!.Command.Executable = "/bin/sh";
        profile.Operations.Run.Command.Argv =
        [
            "-c",
            $"{RuntimeOperationPlaceholders.EntryAssembly}; touch /tmp/pwned",
            "{unknown}"
        ];

        var failures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);

        Assert.Contains(failures, static failure => failure.Contains("shell executable", StringComparison.Ordinal));
        Assert.Contains(failures, static failure => failure.Contains("embedded placeholder", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => RuntimeProfileCommandBuilder.CreateRunCommand(profile, "Program.dll", []));
    }

    [Fact]
    public void OperationValidationRejectsDynamicArgumentsBeforeTheEntryAssembly()
    {
        var profile = OperationProfile();
        profile.Capabilities = ["run"];
        profile.Operations!.Jit = null;
        profile.Operations.Run!.Command.Argv =
        [
            RuntimeOperationPlaceholders.Arguments,
            RuntimeOperationPlaceholders.EntryAssembly
        ];

        var failures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);

        Assert.Contains(failures, static failure => failure.Contains("must follow '{entryAssembly}' and be the final argv token", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => RuntimeProfileCommandBuilder.CreateRunCommand(profile, "Program.dll", ["--additional-host-option"]));
    }

    [Fact]
    public void JitOperationValidationEnforcesTheSourceMappingContract()
    {
        var profile = OperationProfile();
        profile.Operations!.Jit!.PathStyle = RuntimeOperationPathStyles.WineZ;
        profile.Operations.Jit.SourceMappingKind = RuntimeJitSourceMappingKinds.LinuxProfiler;
        profile.Operations.Jit.ProfilerPath = null;

        var failures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);

        Assert.Contains(failures, static failure => failure.Contains("must use Unix paths", StringComparison.Ordinal));
        Assert.Contains(failures, static failure => failure.Contains("JIT Linux profiler", StringComparison.Ordinal));

        profile.Operations.Jit.PathStyle = RuntimeOperationPathStyles.Unix;
        profile.Operations.Jit.SourceMappingKind = RuntimeJitSourceMappingKinds.None;
        profile.Operations.Jit.ProfilerPath = "/opt/sharplabnext/libSharpLabNext.JitProfiler.so";
        failures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);

        Assert.Contains(failures, static failure => failure.Contains("cannot declare a profiler path", StringComparison.Ordinal));
    }

    [Fact]
    public void CheckedJitBridgeRequiresItsFixedInvocationAndMappingContract()
    {
        var profile = OperationProfile();
        profile.Family = "coreclr";
        profile.Operations!.Jit = new RuntimeJitOperationDefinition
        {
            ImplementationId = RuntimeOperationImplementationIds.CheckedJitBridge,
            PathStyle = RuntimeOperationPathStyles.Unix,
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
        };

        Assert.Empty(RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false));

        profile.Operations.Jit.ProfilerPath = "/opt/sharplabnext/SharpLabNext.JitProfiler.so";
        var failures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);
        Assert.Contains(failures, static failure => failure.Contains("cannot declare a profiler path", StringComparison.Ordinal));

        profile.Operations.Jit.ProfilerPath = null;
        profile.Operations.Jit.Command.Argv[1] = "--child";
        failures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);
        Assert.Contains(failures, static failure => failure.Contains("must invoke '/opt/sharplabnext/SharpLabNext.CheckedJitBridge.dll'", StringComparison.Ordinal));

        profile.Operations.Jit.Command.Argv[1] = "jit";
        profile.Operations.Jit.SourceMappingKind = RuntimeJitSourceMappingKinds.LinuxProfiler;
        failures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);
        Assert.Contains(failures, static failure => failure.Contains("supports only source mapping kinds", StringComparison.Ordinal));
    }

    [Fact]
    public void RuntimeProfileDefaultsToItsOwnFamilyAndAStandardCoreClrContainer()
    {
        var profile = OperationProfile();

        var failures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);

        Assert.Empty(failures);
        Assert.Empty(profile.AcceptedRuntimeFamilies);
        Assert.Empty(profile.AcceptedFrameworks);
        Assert.Equal(RuntimeContainerIsolationKinds.Standard, profile.Container.IsolationKind);
        Assert.Equal(RuntimeContainerEnvironmentKinds.CoreClr, profile.Container.EnvironmentKind);
        Assert.Null(profile.Container.WinePrefixPath);
    }

    [Fact]
    public void ExplicitAcceptedRuntimeFamiliesMustIncludeTheProfileFamily()
    {
        var profile = OperationProfile();
        profile.AcceptedRuntimeFamilies = ["coreclr-wine", "coreclr"];

        Assert.Empty(RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false));

        profile.AcceptedRuntimeFamilies = ["coreclr"];
        var failures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);

        Assert.Contains(failures, static failure => failure.Contains("must include its own family 'coreclr-wine'", StringComparison.Ordinal));
    }

    [Fact]
    public void AcceptedFrameworksAllowExactVersionsAndInclusivePrereleaseRanges()
    {
        var profile = OperationProfile();
        profile.AcceptedFrameworks =
        [
            new RuntimeFrameworkCompatibilityDefinition { Name = ".NETFramework", ExactVersion = "4.8" },
            new RuntimeFrameworkCompatibilityDefinition { Name = "Microsoft.NETCore.App", MinimumVersion = "11.0.0-preview.2", MaximumVersion = "11.0.0" },
            new RuntimeFrameworkCompatibilityDefinition { Name = "SharpLab.PrivateRuntime", ExactVersion = "9.0.0-constgenerics.1.23470.1" }
        ];

        Assert.Empty(RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false));
    }

    [Fact]
    public void AcceptedFrameworkMatchingUsesLiteralExactAndInclusiveSemanticRanges()
    {
        var exact = new RuntimeFrameworkCompatibilityDefinition { Name = ".NETFramework", ExactVersion = "4.8" };
        var range = new RuntimeFrameworkCompatibilityDefinition { Name = "Microsoft.NETCore.App", MinimumVersion = "11.0.0-preview.2", MaximumVersion = "11.0.0" };

        Assert.True(RuntimeProfileValidation.AcceptsFramework(exact, ".NETFramework", "4.8"));
        Assert.False(RuntimeProfileValidation.AcceptsFramework(exact, ".NETFramework", "4.8.0"));
        Assert.True(RuntimeProfileValidation.AcceptsFramework(range, "Microsoft.NETCore.App", "11.0.0-preview.5"));
        Assert.True(RuntimeProfileValidation.AcceptsFramework(range, "Microsoft.NETCore.App", "11.0.0"));
        Assert.False(RuntimeProfileValidation.AcceptsFramework(range, "Microsoft.NETCore.App", "11.0.1"));
        Assert.False(RuntimeProfileValidation.AcceptsFramework(range, "microsoft.netcore.app", "11.0.0"));
    }

    [Fact]
    public void AcceptedFrameworkValidationRejectsMixedAndIncompleteVersionModes()
    {
        var mixed = new RuntimeFrameworkCompatibilityDefinition { Name = "Microsoft.NETCore.App", ExactVersion = "10.0.9", MinimumVersion = "10.0.0", MaximumVersion = "10.0.99" };
        var incomplete = new RuntimeFrameworkCompatibilityDefinition { Name = ".NETFramework", MinimumVersion = "4.0" };

        Assert.Contains(RuntimeProfileValidation.Validate(mixed), static failure => failure.Contains("either ExactVersion or a minimum/maximum range, not both", StringComparison.Ordinal));
        Assert.Contains(RuntimeProfileValidation.Validate(incomplete), static failure => failure.Contains("both MinimumVersion and MaximumVersion", StringComparison.Ordinal));
    }

    [Fact]
    public void AcceptedFrameworkValidationRejectsInvalidRangesAndDuplicateNames()
    {
        var profile = OperationProfile();
        profile.AcceptedFrameworks =
        [
            new RuntimeFrameworkCompatibilityDefinition { Name = "Microsoft.NETCore.App", MinimumVersion = "11.0.0", MaximumVersion = "11.0.0-preview.2" },
            new RuntimeFrameworkCompatibilityDefinition { Name = "Microsoft.NETCore.App", ExactVersion = "latest" }
        ];

        var failures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);

        Assert.Contains(failures, static failure => failure.Contains("MinimumVersion greater than MaximumVersion", StringComparison.Ordinal));
        Assert.Contains(failures, static failure => failure.Contains("invalid exact version 'latest'", StringComparison.Ordinal));
        Assert.Contains(failures, static failure => failure.Contains("duplicate accepted framework 'Microsoft.NETCore.App'", StringComparison.Ordinal));
    }

    [Fact]
    public void ContainerValidationAllowsStandardMonoAndBothClosedWineUsers()
    {
        var standardMono = new RuntimeContainerDefinition { EnvironmentKind = RuntimeContainerEnvironmentKinds.Mono, ExecutionUser = RuntimeContainerExecutionUsers.NonRoot };
        var wine = new RuntimeContainerDefinition { IsolationKind = RuntimeContainerIsolationKinds.Wine, EnvironmentKind = RuntimeContainerEnvironmentKinds.Wine, ExecutionUser = RuntimeContainerExecutionUsers.Root, WinePrefixPath = "/opt/wine-dotnet" };

        Assert.Empty(RuntimeProfileValidation.Validate(standardMono));
        Assert.Empty(RuntimeProfileValidation.Validate(wine));
        wine.ExecutionUser = RuntimeContainerExecutionUsers.NonRoot;
        Assert.Empty(RuntimeProfileValidation.Validate(wine));
    }

    [Fact]
    public void ContainerValidationRejectsMissingArbitraryAndMismatchedExecutionUsers()
    {
        var standard = new RuntimeContainerDefinition();
        var standardRoot = new RuntimeContainerDefinition { ExecutionUser = RuntimeContainerExecutionUsers.Root };
        var wine = new RuntimeContainerDefinition { IsolationKind = RuntimeContainerIsolationKinds.Wine, EnvironmentKind = RuntimeContainerEnvironmentKinds.Wine, WinePrefixPath = "/opt/wine-dotnet" };

        Assert.Contains(RuntimeProfileValidation.Validate(standard), static failure => failure.Contains("requires execution user '1654:1654'", StringComparison.Ordinal));
        Assert.Contains(RuntimeProfileValidation.Validate(standardRoot), static failure => failure.Contains("requires execution user '1654:1654'", StringComparison.Ordinal));
        Assert.Contains(RuntimeProfileValidation.Validate(wine), static failure => failure.Contains("requires execution user '0:0' or '1654:1654'", StringComparison.Ordinal));

        wine.ExecutionUser = "1000:1000";
        Assert.Contains(RuntimeProfileValidation.Validate(wine), static failure => failure.Contains("requires execution user '0:0' or '1654:1654'", StringComparison.Ordinal));
    }

    [Fact]
    public void ContainerValidationRejectsMismatchedKindsAndWinePrefixPaths()
    {
        var standardWine = new RuntimeContainerDefinition { EnvironmentKind = RuntimeContainerEnvironmentKinds.Wine, WinePrefixPath = "/opt/wine-dotnet" };
        var wineWithoutPrefix = new RuntimeContainerDefinition { IsolationKind = RuntimeContainerIsolationKinds.Wine, EnvironmentKind = RuntimeContainerEnvironmentKinds.CoreClr };
        var wineWithUnsafePrefix = new RuntimeContainerDefinition { IsolationKind = RuntimeContainerIsolationKinds.Wine, EnvironmentKind = RuntimeContainerEnvironmentKinds.Wine, WinePrefixPath = "/opt/../shared-wine" };
        var wineOutsideOpt = new RuntimeContainerDefinition { IsolationKind = RuntimeContainerIsolationKinds.Wine, EnvironmentKind = RuntimeContainerEnvironmentKinds.Wine, WinePrefixPath = "/var/lib/wine-dotnet" };
        var unknownKinds = new RuntimeContainerDefinition { IsolationKind = "custom", EnvironmentKind = "native" };

        var standardFailures = RuntimeProfileValidation.Validate(standardWine);
        var wineFailures = RuntimeProfileValidation.Validate(wineWithoutPrefix);
        var unsafePrefixFailures = RuntimeProfileValidation.Validate(wineWithUnsafePrefix);
        var outsideOptFailures = RuntimeProfileValidation.Validate(wineOutsideOpt);
        var unknownKindFailures = RuntimeProfileValidation.Validate(unknownKinds);

        Assert.Contains(standardFailures, static failure => failure.Contains("supports only 'coreclr' or 'mono'", StringComparison.Ordinal));
        Assert.Contains(standardFailures, static failure => failure.Contains("cannot declare a Wine prefix", StringComparison.Ordinal));
        Assert.Contains(wineFailures, static failure => failure.Contains("requires the 'wine' environment", StringComparison.Ordinal));
        Assert.Contains(wineFailures, static failure => failure.Contains("Wine container prefix", StringComparison.Ordinal));
        Assert.Contains(unsafePrefixFailures, static failure => failure.Contains("path is invalid", StringComparison.Ordinal));
        Assert.Contains(outsideOptFailures, static failure => failure.Contains("below /opt", StringComparison.Ordinal));
        Assert.Contains(unknownKindFailures, static failure => failure.Contains("environment kind 'native' is not supported", StringComparison.Ordinal));
        Assert.Contains(unknownKindFailures, static failure => failure.Contains("isolation kind 'custom' is not supported", StringComparison.Ordinal));
    }

    [Fact]
    public void WineNetFxProfileBuildsDedicatedRunnerCommandAndRejectsJitWithoutTheDesktopClrProvider()
    {
        var profile = WineProfile();
        profile.Layout.DotNetHostPath = "/runtime/dotnet";
        profile.Layout.RunnerAssemblyPath = "/helpers/WineRunner.dll";
        profile.Layout.WineHostPath = "/opt/wine/bin/wine";

        var run = RuntimeProfileCommandBuilder.CreateRunCommand(profile, "app/Program.exe", ["first", "second"]);

        Assert.Equal(
            [
                "/runtime/dotnet",
                "/helpers/WineRunner.dll",
                "bridge",
                "/opt/wine/bin/wine",
                "/workspace/app/Program.exe",
                "--",
                "first",
                "second"
            ],
            run);
        Assert.Throws<NotSupportedException>(() => RuntimeProfileCommandBuilder.CreateJitCommand(profile, "app/Program.exe", null));
        Assert.Null(profile.Layout.JitInspectorAssemblyPath);
    }

    [Fact]
    public void WineNetFxProfileCannotExpandTheRootRunnerBoundary()
    {
        var profile = WineProfile();
        profile.Family = "coreclr";
        profile.Capabilities = ["run", "jit-asm"];
        profile.AcceptedArtifactFormats = ["dotnet-managed-pe-v1"];

        var failures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);

        Assert.Contains(failures, static failure => failure.Contains("netfx-clr-wine", StringComparison.Ordinal));
        Assert.Contains(failures, static failure => failure.Contains("only the run capability", StringComparison.Ordinal));
        Assert.Contains(failures, static failure => failure.Contains("requires managed .NET Framework PE", StringComparison.Ordinal));
    }

    [Fact]
    public void WineNetFxProfileAllowsOnlyTheBoundedDesktopClrJitProvider()
    {
        var profile = DesktopClrJitWineProfile();

        Assert.Empty(RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false));
        Assert.Equal(
            [
                "/usr/share/dotnet/dotnet",
                "/opt/sharplabnext/SharpLabNext.WineRunner.dll",
                "desktop-jit",
                "/workspace/app/Program.exe",
                "Example.Program:Main"
            ],
            RuntimeProfileCommandBuilder.CreateJitCommand(profile, "app/Program.exe", "Example.Program:Main"));

        profile.Operations!.Jit!.ImplementationId = RuntimeOperationImplementationIds.LegacyJitInspector;
        var oldProviderFailures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);
        Assert.Contains(oldProviderFailures, static failure => failure.Contains("Desktop CLR JIT provider", StringComparison.Ordinal));

        profile = DesktopClrJitWineProfile();
        profile.Operations!.Jit!.SourceMappingKind = RuntimeJitSourceMappingKinds.LinuxProfiler;
        var mappingFailures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);
        Assert.Contains(mappingFailures, static failure => failure.Contains("source mapping kind 'none'", StringComparison.Ordinal));

        profile = DesktopClrJitWineProfile();
        profile.Operations!.Jit = null;
        var missingOperationFailures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);
        Assert.Contains(missingOperationFailures, static failure => failure.Contains("capability 'jit-asm' without a JIT operation", StringComparison.Ordinal));
        Assert.Contains(missingOperationFailures, static failure => failure.Contains("only the run capability unless", StringComparison.Ordinal));
    }

    [Fact]
    public void WineNetFxProfileAcceptsTheDedicatedClr2AndClr4Prefixes()
    {
        foreach (var prefix in new[] { "/opt/wine-netfx-clr2", "/opt/wine-netfx-clr4" })
        {
            var profile = WineProfile();
            profile.Layout.WinePrefixPath = prefix;
            profile.Container = new RuntimeContainerDefinition { IsolationKind = RuntimeContainerIsolationKinds.Wine, EnvironmentKind = RuntimeContainerEnvironmentKinds.Wine, ExecutionUser = RuntimeContainerExecutionUsers.Root, WinePrefixPath = prefix };

            Assert.Empty(RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false));
        }
    }

    [Fact]
    public void WineCoreClrOperationProfileUsesWineZAndEnforcesTheProductBoundary()
    {
        var profile = new RuntimeProfileDefinition
        {
            Id = "wine-dotnet-10-linux-x64",
            Image = "example/wine-runtime@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            Family = "coreclr-wine",
            RuntimeVersion = "10.0.10",
            RuntimeImageId = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            JitVersion = "10.0.10",
            Rid = "linux-x64",
            Architecture = "x64",
            AcceptedArtifactFormats = ["dotnet-managed-pe-v1"],
            Capabilities = ["run", "jit-asm"],
            AllowedSecurityPolicyIds = ["runtime-job-default"],
            Container = new RuntimeContainerDefinition { IsolationKind = RuntimeContainerIsolationKinds.Wine, EnvironmentKind = RuntimeContainerEnvironmentKinds.Wine, ExecutionUser = RuntimeContainerExecutionUsers.NonRoot, WinePrefixPath = "/opt/wine-dotnet" },
            Layout = new RuntimeImageLayout { RunnerKind = RuntimeRunnerKinds.WineCoreClr, DotNetHostPath = "/opt/wine-dotnet/drive_c/dotnet/dotnet.exe", WineHostPath = "/usr/lib/wine/wine64", WinePrefixPath = "/opt/wine-dotnet", RunnerAssemblyPath = "/opt/sharplabnext/SharpLabNext.LegacyJitInspector.dll" },
            Operations = new RuntimeProfileOperations
            {
                Run = new RuntimeRunOperationDefinition
                {
                    ImplementationId = RuntimeOperationImplementationIds.LegacyJitInspector,
                    PathStyle = RuntimeOperationPathStyles.WineZ,
                    Command = new RuntimeOperationCommandDefinition
                    {
                        Executable = "/usr/lib/wine/wine64",
                        Argv =
                        [
                            @"Z:\opt\wine-dotnet\drive_c\dotnet\dotnet.exe",
                            "exec",
                            "--fx-version",
                            "10.0.10",
                            @"Z:\opt\sharplabnext\SharpLabNext.LegacyJitInspector.dll",
                            "--runtime-version",
                            "10.0.10",
                            "run",
                            RuntimeOperationPlaceholders.EntryAssembly,
                            "--",
                            RuntimeOperationPlaceholders.Arguments
                        ]
                    }
                },
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
                            @"Z:\opt\wine-dotnet\drive_c\dotnet\dotnet.exe",
                            "exec",
                            "--fx-version",
                            "10.0.10",
                            @"Z:\opt\sharplabnext\SharpLabNext.LegacyJitInspector.dll",
                            "--runtime-version",
                            "10.0.10",
                            "jit",
                            RuntimeOperationPlaceholders.EntryAssembly,
                            RuntimeOperationPlaceholders.MethodFilter
                        ]
                    }
                }
            }
        };

        Assert.Empty(RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false));
        Assert.Equal(
            [
                "/usr/lib/wine/wine64",
                @"Z:\opt\wine-dotnet\drive_c\dotnet\dotnet.exe",
                "exec",
                "--fx-version",
                "10.0.10",
                @"Z:\opt\sharplabnext\SharpLabNext.LegacyJitInspector.dll",
                "--runtime-version",
                "10.0.10",
                "run",
                @"Z:\workspace\Program.dll",
                "--"
            ],
            RuntimeProfileCommandBuilder.CreateRunCommand(profile, "Program.dll", []));
        Assert.Equal(
            [
                "/usr/lib/wine/wine64",
                @"Z:\opt\wine-dotnet\drive_c\dotnet\dotnet.exe",
                "exec",
                "--fx-version",
                "10.0.10",
                @"Z:\opt\sharplabnext\SharpLabNext.LegacyJitInspector.dll",
                "--runtime-version",
                "10.0.10",
                "jit",
                @"Z:\workspace\Program.dll",
                "Program:Main"
            ],
            RuntimeProfileCommandBuilder.CreateJitCommand(profile, "Program.dll", "Program:Main"));

        profile.Layout.WinePrefixPath = "/opt/wine-netfx-clr4";
        var failures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);
        Assert.Contains(failures, static failure => failure.Contains("/opt/wine-dotnet", StringComparison.Ordinal));
    }

    [Fact]
    public void GeneratedRuntimeProfilesPassSdkSemanticValidation()
    {
        var profilesDirectory = FindProfilesDirectory();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var profilePaths = Directory.EnumerateFiles(profilesDirectory, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();

        Assert.NotEmpty(profilePaths);
        foreach (var path in profilePaths)
        {
            var profile = JsonSerializer.Deserialize<RuntimeProfileDefinition>(File.ReadAllText(path), jsonOptions);
            Assert.NotNull(profile);
            var failures = RuntimeProfileValidation.Validate(profile!, requireDigestPinnedImage: false);
            Assert.True(failures.Count == 0, $"{Path.GetFileName(path)} failed SDK validation: {string.Join(" | ", failures)}");
        }
    }

    [Fact]
    public void GeneratedRuntimeMatrixProfilesCloseCompatibilityContracts()
    {
        var candidatesDirectory = Path.Combine(FindProfilesDirectory(), "candidates");
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        RuntimeProfileDefinition Load(string fileName) =>
            JsonSerializer.Deserialize<RuntimeProfileDefinition>(File.ReadAllText(Path.Combine(candidatesDirectory, fileName)), jsonOptions) ?? throw new InvalidDataException($"Candidate profile '{fileName}' is invalid.");

        var wineCoreClr = Load("wine-dotnet-10-linux-x64.json");
        Assert.Equal(["coreclr-wine", "coreclr"], wineCoreClr.AcceptedRuntimeFamilies);
        Assert.Equal(RuntimeOperationImplementationIds.LegacyJitInspector, wineCoreClr.Operations?.Run?.ImplementationId);
        Assert.Equal(RuntimeOperationImplementationIds.LegacyJitInspector, wineCoreClr.Operations?.Jit?.ImplementationId);

        var mono = Load("mono-6.12-linux-x64.json");
        Assert.Equal(["mono", "netfx-clr-wine"], mono.AcceptedRuntimeFamilies);
        Assert.Equal(RuntimeOperationImplementationIds.TargetRuntimeRunner, mono.Operations?.Run?.ImplementationId);
        Assert.Equal("/usr/bin/mono", mono.Operations?.Run?.Command.Executable);
        Assert.Equal("/opt/sharplabnext/SharpLabNext.TargetRuntimeRunner.exe", mono.Layout.RunnerAssemblyPath);
        Assert.Equal(RuntimeOperationImplementationIds.MonoJitInspector, mono.Operations?.Jit?.ImplementationId);
        Assert.Equal(RuntimeJitSourceMappingKinds.None, mono.Operations?.Jit?.SourceMappingKind);
        Assert.Equal("/usr/share/dotnet/dotnet", mono.Operations?.Jit?.Command.Executable);
        Assert.Equal("/opt/sharplabnext/SharpLabNext.MonoJitInspector.dll", mono.Layout.JitInspectorAssemblyPath);

        foreach (var fileName in new[]
                 {
                     "dotnet-10-linux-x64.json",
                     "dotnet-11-preview-linux-x64.json"
                 })
        {
            var linuxCoreClr = Load(fileName);
            Assert.Equal(RuntimeOperationImplementationIds.Runner, linuxCoreClr.Operations?.Run?.ImplementationId);
            Assert.Equal(RuntimeOperationImplementationIds.JitInspector, linuxCoreClr.Operations?.Jit?.ImplementationId);
            Assert.Equal(RuntimeJitSourceMappingKinds.LinuxProfiler, linuxCoreClr.Operations?.Jit?.SourceMappingKind);
            Assert.Equal("/opt/sharplabnext/SharpLabNext.Runner.dll", linuxCoreClr.Layout.RunnerAssemblyPath);
            Assert.Equal("/opt/sharplabnext/SharpLabNext.JitInspector.dll", linuxCoreClr.Layout.JitInspectorAssemblyPath);
        }

        foreach (var (fileName, minimum, maximum) in new[]
                 {
                     ("dotnet-core-3.0-linux-x64.json", "3.0.1", "3.0.3"),
                     ("dotnet-core-3.1-linux-x64.json", "3.1.0", "3.1.32"),
                     ("dotnet-5-linux-x64.json", "5.0.0", "5.0.17"),
                     ("wine-dotnet-core-3.0-linux-x64.json", "3.0.1", "3.0.3"),
                     ("wine-dotnet-core-3.1-linux-x64.json", "3.1.0", "3.1.32"),
                     ("wine-dotnet-5-linux-x64.json", "5.0.0", "5.0.17")
                 })
        {
            var framework = Assert.Single(Load(fileName).AcceptedFrameworks);
            Assert.Equal("Microsoft.NETCore.App", framework.Name);
            Assert.Equal(minimum, framework.MinimumVersion);
            Assert.Equal(maximum, framework.MaximumVersion);
            Assert.Null(framework.ExactVersion);
        }

        var genericNetFx = Load("wine-netfx20-linux-x64.json");
        Assert.Equal(["dotnet-framework-managed-pe-v1"], genericNetFx.AcceptedArtifactFormats);
        Assert.Equal(RuntimeOperationImplementationIds.TargetRuntimeRunner, genericNetFx.Operations?.Run?.ImplementationId);
        Assert.Equal(
            [
                @"Z:\opt\sharplabnext\SharpLabNext.TargetRuntimeRunner.exe",
                "run",
                RuntimeOperationPlaceholders.EntryAssembly,
                "--",
                RuntimeOperationPlaceholders.Arguments
            ],
            genericNetFx.Operations?.Run?.Command.Argv);

        var netFx48 = Load("wine-netfx48-linux-x64.json");
        Assert.Equal(["dotnet-framework-managed-pe-v1", "dotnet-framework-mixed-pe-v1"], netFx48.AcceptedArtifactFormats);
        Assert.Equal(["runtime.netfx48-wine"], netFx48.ProvidedRuntimeFeatureTags);

        foreach (var path in Directory.EnumerateFiles(candidatesDirectory, "*.json"))
        {
            var profile = JsonSerializer.Deserialize<RuntimeProfileDefinition>(File.ReadAllText(path), jsonOptions) ?? throw new InvalidDataException($"Candidate profile '{path}' is invalid.");
            var expectedWinePolicy = StringComparer.Ordinal.Equals(profile.Family, "netfx-clr-wine");
            var expectedPolicyId = expectedWinePolicy ? "runtime-job-wine-netfx" : "runtime-job-default";
            Assert.Equal([expectedPolicyId], profile.AllowedSecurityPolicyIds);
            var policy = Assert.Single(profile.SecurityPolicies);
            Assert.Equal(expectedPolicyId, policy.Id);
            Assert.Equal(expectedWinePolicy ? 1_073_741_824 : 268_435_456, policy.MemoryBytes);
            Assert.Equal(expectedWinePolicy ? 128 : 64, policy.PidsLimit);
            Assert.Equal(expectedWinePolicy ? 30 : 10, policy.MaximumDurationSeconds);
        }
    }

    [Fact]
    public void ActiveInstrumentationProfilesUseTheModernRunnerImplementation()
    {
        var profilesDirectory = FindProfilesDirectory();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var profiles = Directory.EnumerateFiles(profilesDirectory, "*.json", SearchOption.TopDirectoryOnly).Select(path => JsonSerializer.Deserialize<RuntimeProfileDefinition>(File.ReadAllText(path), jsonOptions) ?? throw new InvalidDataException($"Active profile '{path}' is invalid.")).Where(static profile => profile.Capabilities.Contains("inspection", StringComparer.Ordinal) || profile.Capabilities.Contains("execution-flow", StringComparer.Ordinal)).ToArray();

        Assert.NotEmpty(profiles);
        foreach (var profile in profiles)
            Assert.Equal(RuntimeOperationImplementationIds.Runner, profile.Operations?.Run?.ImplementationId);
    }

    [Fact]
    public void GeneratedLinuxCoreClrProfilesUseVersionClosedHelperCommands()
    {
        var candidatesDirectory = Path.Combine(FindProfilesDirectory(), "candidates");
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var profilePaths = Directory.EnumerateFiles(candidatesDirectory, "*.json").Where(static path => Path.GetFileName(path).StartsWith("dotnet-", StringComparison.Ordinal) && !Path.GetFileName(path).StartsWith("wine-", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();

        Assert.NotEmpty(profilePaths);
        foreach (var path in profilePaths)
        {
            var profile = JsonSerializer.Deserialize<RuntimeProfileDefinition>(File.ReadAllText(path), jsonOptions);
            Assert.NotNull(profile);
            Assert.NotNull(profile.Operations?.Run);
            if (string.Equals(profile.Operations.Run.ImplementationId, RuntimeOperationImplementationIds.Runner, StringComparison.Ordinal))
            {
                Assert.Equal(
                    [
                        "/opt/sharplabnext/SharpLabNext.Runner.dll",
                        "{entryAssembly}",
                        "--",
                        "{arguments}"
                    ],
                    profile.Operations.Run.Command.Argv);
            }
            else
            {
                Assert.Equal(RuntimeOperationImplementationIds.LegacyJitInspector, profile.Operations.Run.ImplementationId);
                Assert.Collection(profile.Operations.Run.Command.Argv.Take(4), value => Assert.Equal("exec", value), value => Assert.Equal("--fx-version", value), value => Assert.Equal(profile.RuntimeVersion, value), value => Assert.Equal("/opt/sharplabnext/SharpLabNext.LegacyJitInspector.dll", value));
            }
            if (profile.Operations.Jit is { } jit)
            {
                if (string.Equals(jit.ImplementationId, RuntimeOperationImplementationIds.CheckedJitBridge, StringComparison.Ordinal))
                {
                    Assert.Equal(
                        [
                            "/opt/sharplabnext/SharpLabNext.CheckedJitBridge.dll",
                            "jit",
                            "{entryAssembly}",
                            "{methodFilter}"
                        ],
                        jit.Command.Argv);
                }
                else if (string.Equals(jit.ImplementationId, RuntimeOperationImplementationIds.JitInspector, StringComparison.Ordinal))
                {
                    Assert.Equal(
                        [
                            "/opt/sharplabnext/SharpLabNext.JitInspector.dll",
                            "{entryAssembly}",
                            "{methodFilter}"
                        ],
                        jit.Command.Argv);
                    Assert.Equal(RuntimeJitSourceMappingKinds.LinuxProfiler, jit.SourceMappingKind);
                }
                else
                {
                    Assert.Equal(RuntimeOperationImplementationIds.LegacyJitInspector, jit.ImplementationId);
                    Assert.Equal(["exec", "--fx-version", profile.RuntimeVersion], jit.Command.Argv.Take(3));
                }
            }
        }
    }

    [Fact]
    public void LegacyCoreClrProfileRejectsFxVersionWithoutHelperRuntimeGuard()
    {
        var profile = LoadCandidateProfileForValidation("dotnet-5-linux-x64.json");
        var argv = profile.Operations!.Run!.Command.Argv;
        var guardIndex = argv.IndexOf("--runtime-version");
        Assert.True(guardIndex >= 0);
        argv.RemoveRange(guardIndex, 2);

        var failures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);

        Assert.Contains(failures, static failure => failure.Contains("exact fixed operation contract", StringComparison.Ordinal));
    }

    [Fact]
    public void WineNetFxRunOnlyProfileRejectsAFakeJitInspectorPath()
    {
        var profile = WineProfile();
        profile.Layout.JitInspectorAssemblyPath = "/helpers/Jit.dll";

        var failures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);

        Assert.Contains(failures, static failure => failure.Contains("cannot declare a JIT inspector", StringComparison.Ordinal));
    }

    [Fact]
    public void TargetRuntimeWineFrameworkProfilesBindExactRuntimeIdentity()
    {
        var profile = LoadCandidateProfileForValidation("wine-netfx48-linux-x64.json");

        Assert.Empty(RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false));

        profile.RuntimeCommit = "operator-payload";
        var runtimeCommitFailures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);
        Assert.Contains(runtimeCommitFailures, static failure => failure.Contains("RuntimeCommit", StringComparison.Ordinal));

        profile = LoadCandidateProfileForValidation("wine-netfx48-linux-x64.json");
        profile.JitVersion = "4.8";
        var jitVersionFailures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);
        Assert.Contains(jitVersionFailures, static failure => failure.Contains("JitVersion", StringComparison.Ordinal));

        profile = LoadCandidateProfileForValidation("wine-netfx48-linux-x64.json");
        profile.JitCommit = "operator-jit";
        var jitCommitFailures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);
        Assert.Contains(jitCommitFailures, static failure => failure.Contains("JitCommit", StringComparison.Ordinal));
    }

    [Fact]
    public void TargetRuntimeWineFrameworkProfilesRequireExactFrameworkAndFixedHost()
    {
        var profile = LoadCandidateProfileForValidation("wine-netfx48-linux-x64.json");
        profile.AcceptedFrameworks[0].ExactVersion = "4.7.2";
        var frameworkFailures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);

        Assert.Contains(frameworkFailures, static failure => failure.Contains("accept exactly '.NETFramework' version '4.8'", StringComparison.Ordinal));

        profile = LoadCandidateProfileForValidation("wine-netfx48-linux-x64.json");
        profile.Layout.DotNetHostPath = "/usr/bin/mono";
        var hostFailures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);

        Assert.Contains(hostFailures, static failure => failure.Contains("DotNetHostPath", StringComparison.Ordinal));
    }

    [Fact]
    public void WineJSharp20ProfileUsesDedicatedPrefixAndRunOnlyContract()
    {
        var profile = JSharpWineProfile();

        var failures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: true);
        var run = RuntimeProfileCommandBuilder.CreateRunCommand(profile, "SharpLabNext.User.exe", []);

        Assert.Empty(failures);
        Assert.Equal(
            [
                "dotnet",
                "/opt/sharplabnext/SharpLabNext.WineRunner.dll",
                "bridge",
                "/usr/lib/wine/wine64",
                "/workspace/SharpLabNext.User.exe",
                "--"
            ],
            run);
        Assert.Equal("/opt/wine-jsharp20", profile.Layout.WinePrefixPath);
        Assert.Throws<NotSupportedException>(() => RuntimeProfileCommandBuilder.CreateJitCommand(profile, "SharpLabNext.User.exe", null));
    }

    [Fact]
    public void WineJSharp20ProfileRejectsAnyCpuNet48AndSharedPrefixSubstitutions()
    {
        var profile = JSharpWineProfile();
        profile.Architecture = "anycpu";
        profile.AcceptedArtifactFormats =
        [
            "dotnet-framework-managed-pe-v1",
            "dotnet-framework-mixed-pe-v1"
        ];
        profile.ProvidedRuntimeFeatureTags = ["runtime.netfx48-wine"];
        profile.Layout.WinePrefixPath = "/opt/wine-dotnet";

        var failures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: false);

        Assert.Contains(failures, static failure => failure.Contains("linux-x64/x64", StringComparison.Ordinal));
        Assert.Contains(failures, static failure => failure.Contains("only managed", StringComparison.Ordinal));
        Assert.Contains(failures, static failure => failure.Contains("runtime.jsharp20-wine", StringComparison.Ordinal));
        Assert.Contains(failures, static failure => failure.Contains("/opt/wine-jsharp20", StringComparison.Ordinal));
    }

    [Fact]
    public void RuntimeProfileRejectsFloatingImageAndUnsafeHelperPath()
    {
        var profile = Profile();
        profile.Image = "example/runtime:latest";
        profile.Layout.RunnerAssemblyPath = "/opt/../unsafe.dll";

        var failures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: true);

        Assert.Contains(failures, static failure => failure.Contains("digest-pinned", StringComparison.Ordinal));
        Assert.Contains(failures, static failure => failure.Contains("floating latest", StringComparison.Ordinal));
        Assert.Contains(failures, static failure => failure.Contains("Runner assembly path", StringComparison.Ordinal));
    }

    [Fact]
    public void PromotionReceiptRequiresCanonicalProfilePathDigestAndRegistryImage()
    {
        var profile = Profile();
        profile.AcceptedArtifactFormats = ["dotnet-managed-pe-v1"];
        profile.Capabilities = ["run"];
        profile.AllowedSecurityPolicyIds = ["runtime-job-default"];
        profile.PromotionReceipt = new RuntimePromotionReceiptReference { Path = "profiles/runtime-promotion-receipts/example-runtime.json", Sha256 = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" };

        Assert.Empty(RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: true));

        profile.PromotionReceipt.Path = "profiles/runtime-promotion-receipts/other.json";
        profile.PromotionReceipt.Sha256 = "sha256:not-a-digest";
        profile.Image = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var failures = RuntimeProfileValidation.Validate(profile, requireDigestPinnedImage: true);

        Assert.Contains(failures, static failure => failure.Contains("receipt path", StringComparison.Ordinal));
        Assert.Contains(failures, static failure => failure.Contains("canonical SHA-256", StringComparison.Ordinal));
        Assert.Contains(failures, static failure => failure.Contains("registry digest", StringComparison.Ordinal));
    }

    [Fact]
    public void RuntimeProfilePackageRequiresEveryAllowedPolicy()
    {
        var profile = Profile();
        profile.AllowedSecurityPolicyIds = ["missing-policy"];
        profile.SecurityPolicies = [new RuntimeSecurityPolicyDefinition { Id = "runtime-job-default" }];

        var failures = RuntimeProfileValidation.ValidatePackage(profile, requireDigestPinnedImage: false);

        Assert.Contains(failures, static failure => failure.Contains("missing-policy", StringComparison.Ordinal));
    }

    private static RuntimeProfileDefinition Profile() => new()
    {
        Id = "example-runtime",
        Image = "example/runtime@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        RuntimeVersion = "11.0.0-preview.5",
        RuntimeImageId = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        JitVersion = "11.0.0-preview.5",
        Container = new RuntimeContainerDefinition { ExecutionUser = RuntimeContainerExecutionUsers.NonRoot }
    };

    private static RuntimeProfileDefinition OperationProfile() => new()
    {
        Id = "operation-runtime",
        Image = "example/runtime@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        Family = "coreclr-wine",
        RuntimeVersion = "11.0.0",
        RuntimeImageId = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        JitVersion = "11.0.0",
        AcceptedArtifactFormats = ["dotnet-managed-pe-v1"],
        Capabilities = ["run", "jit-asm"],
        AllowedSecurityPolicyIds = ["runtime-job-default"],
        Container = new RuntimeContainerDefinition { ExecutionUser = RuntimeContainerExecutionUsers.NonRoot },
        Operations = new RuntimeProfileOperations
        {
            Run = new RuntimeRunOperationDefinition
            {
                ImplementationId = RuntimeOperationImplementationIds.Runner,
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
                SourceMappingKind = RuntimeJitSourceMappingKinds.LinuxProfiler,
                ProfilerPath = "/opt/sharplabnext/SharpLabNext.JitProfiler.so",
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
        }
    };

    private static RuntimeProfileDefinition WineProfile() => new()
    {
        Id = "wine-netfx48-linux-x64",
        Image = "example/wine-runtime@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        Family = "netfx-clr-wine",
        RuntimeVersion = "wine-9.0+netfx48",
        RuntimeImageId = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        JitVersion = "not-supported",
        Rid = "linux-x64",
        Architecture = "x64",
        AcceptedArtifactFormats =
        [
            "dotnet-framework-managed-pe-v1",
            "dotnet-framework-mixed-pe-v1"
        ],
        Capabilities = ["run"],
        ProvidedRuntimeFeatureTags = ["runtime.netfx48-wine"],
        AllowedSecurityPolicyIds = ["runtime-job-wine-netfx"],
        Container = new RuntimeContainerDefinition { IsolationKind = RuntimeContainerIsolationKinds.Wine, EnvironmentKind = RuntimeContainerEnvironmentKinds.Wine, ExecutionUser = RuntimeContainerExecutionUsers.Root, WinePrefixPath = "/opt/wine-dotnet" },
        Layout = new RuntimeImageLayout { RunnerKind = RuntimeRunnerKinds.WineNetFx, RunnerAssemblyPath = "/opt/sharplabnext/SharpLabNext.WineRunner.dll", WineHostPath = "/usr/lib/wine/wine64", WinePrefixPath = "/opt/wine-dotnet" }
    };

    private static RuntimeProfileDefinition DesktopClrJitWineProfile()
    {
        var profile = WineProfile();
        profile.RuntimeVersion = "4.8";
        profile.RuntimeCommit = "not-applicable";
        profile.JitVersion = "not-applicable";
        profile.JitCommit = "not-applicable";
        profile.AcceptedFrameworks = [new RuntimeFrameworkCompatibilityDefinition
        {
            Name = ".NETFramework",
            ExactVersion = "4.8"
        }];
        profile.Capabilities = ["run", "jit-asm"];
        profile.Layout.DotNetHostPath = "/usr/lib/wine/wine64";
        profile.Layout.RunnerAssemblyPath = "/opt/sharplabnext/SharpLabNext.TargetRuntimeRunner.exe";
        profile.Layout.JitInspectorAssemblyPath = "/opt/sharplabnext/SharpLabNext.WineRunner.dll";
        profile.Operations = new RuntimeProfileOperations
        {
            Run = new RuntimeRunOperationDefinition
            {
                ImplementationId = RuntimeOperationImplementationIds.TargetRuntimeRunner,
                PathStyle = RuntimeOperationPathStyles.WineZ,
                Command = new RuntimeOperationCommandDefinition
                {
                    Executable = "/usr/lib/wine/wine64",
                    Argv =
                    [
                        @"Z:\opt\sharplabnext\SharpLabNext.TargetRuntimeRunner.exe",
                        "run",
                        RuntimeOperationPlaceholders.EntryAssembly,
                        "--",
                        RuntimeOperationPlaceholders.Arguments
                    ]
                }
            },
            Jit = new RuntimeJitOperationDefinition
            {
                ImplementationId = RuntimeOperationImplementationIds.DesktopClrJitInspector,
                PathStyle = RuntimeOperationPathStyles.Unix,
                SourceMappingKind = RuntimeJitSourceMappingKinds.None,
                Command = new RuntimeOperationCommandDefinition
                {
                    Executable = "/usr/share/dotnet/dotnet",
                    Argv =
                    [
                        "/opt/sharplabnext/SharpLabNext.WineRunner.dll",
                        "desktop-jit",
                        RuntimeOperationPlaceholders.EntryAssembly,
                        RuntimeOperationPlaceholders.MethodFilter
                    ]
                }
            }
        };
        return profile;
    }

    private static RuntimeProfileDefinition JSharpWineProfile() => new()
    {
        Id = "wine-jsharp20-linux-x64",
        Image = "example/jsharp-runtime@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        Family = "netfx-clr-wine",
        RuntimeVersion = "wine-9.0+clr2+jsharp-2.0.50727.937",
        RuntimeImageId = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        JitVersion = "not-supported",
        Rid = "linux-x64",
        Architecture = "x64",
        AcceptedArtifactFormats = ["dotnet-framework-managed-pe-v1"],
        Capabilities = ["run"],
        ProvidedRuntimeFeatureTags = ["runtime.jsharp20-wine"],
        AllowedSecurityPolicyIds = ["runtime-job-wine-jsharp20"],
        Container = new RuntimeContainerDefinition { IsolationKind = RuntimeContainerIsolationKinds.Wine, EnvironmentKind = RuntimeContainerEnvironmentKinds.Wine, ExecutionUser = RuntimeContainerExecutionUsers.Root, WinePrefixPath = "/opt/wine-jsharp20" },
        Layout = new RuntimeImageLayout { RunnerKind = RuntimeRunnerKinds.WineJSharp20, RunnerAssemblyPath = "/opt/sharplabnext/SharpLabNext.WineRunner.dll", WineHostPath = "/usr/lib/wine/wine64", WinePrefixPath = "/opt/wine-jsharp20" }
    };

    private static RuntimeProfileDefinition LoadCandidateProfileForValidation(string fileName)
    {
        var path = Path.Combine(FindProfilesDirectory(), "candidates", fileName);
        return JsonSerializer.Deserialize<RuntimeProfileDefinition>(File.ReadAllText(path), WebJsonOptions) ?? throw new InvalidDataException($"Candidate profile '{fileName}' is invalid.");
    }

    private static string FindProfilesDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "profiles", "runtimes");
            if (Directory.Exists(candidate))
                return candidate;
        }

        throw new DirectoryNotFoundException("Could not locate profiles/runtimes from the test output directory.");
    }
}
