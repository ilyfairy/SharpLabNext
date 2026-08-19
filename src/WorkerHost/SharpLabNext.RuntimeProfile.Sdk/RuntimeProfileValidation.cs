namespace SharpLabNext.RuntimeProfile.Sdk;

public static class RuntimeProfileValidation
{
    private const int MaximumAcceptedFrameworks = 32;
    private const int MaximumFrameworkNameLength = 160;
    private const int MaximumFrameworkVersionLength = 128;
    private const int MaximumOperationArgumentTokens = 64;
    private const int MaximumOperationTokenLength = 4096;
    private const string RunnerAssemblyPath = "/opt/sharplabnext/SharpLabNext.Runner.dll";
    private const string JitInspectorAssemblyPath = "/opt/sharplabnext/SharpLabNext.JitInspector.dll";
    private const string LegacyJitInspectorAssemblyPath = "/opt/sharplabnext/SharpLabNext.LegacyJitInspector.dll";
    private const string LegacyRuntimeVersionSwitch = "--runtime-version";
    private const string CheckedJitBridgeAssemblyPath = "/opt/sharplabnext/SharpLabNext.CheckedJitBridge.dll";
    private const string MonoJitInspectorAssemblyPath = "/opt/sharplabnext/SharpLabNext.MonoJitInspector.dll";
    private const string WineLegacyJitInspectorAssemblyPath = @"Z:\opt\sharplabnext\SharpLabNext.LegacyJitInspector.dll";
    private const string WineRunnerAssemblyPath = "/opt/sharplabnext/SharpLabNext.WineRunner.dll";
    private const string TargetRuntimeRunnerAssemblyPath = "/opt/sharplabnext/SharpLabNext.TargetRuntimeRunner.exe";
    private const string WineTargetRuntimeRunnerAssemblyPath = @"Z:\opt\sharplabnext\SharpLabNext.TargetRuntimeRunner.exe";
    private const string FrameworkRuntimeName = ".NETFramework";
    private const string NotApplicableRuntimeIdentity = "not-applicable";
    private const string JitProfilerPath = "/opt/sharplabnext/SharpLabNext.JitProfiler.so";
    private static readonly HashSet<string> ShellExecutableNames = new(
        [
            "sh",
            "bash",
            "dash",
            "ash",
            "zsh",
            "ksh",
            "csh",
            "tcsh",
            "fish",
            "cmd",
            "cmd.exe",
            "powershell",
            "powershell.exe",
            "pwsh",
            "pwsh.exe",
            "wscript",
            "wscript.exe",
            "cscript",
            "cscript.exe"
        ],
        StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> ValidatePackage(
        RuntimeProfileDefinition profile,
        bool requireDigestPinnedImage)
    {
        var failures = Validate(profile, requireDigestPinnedImage).ToList();
        var policyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var policy in profile.SecurityPolicies)
        {
            failures.AddRange(Validate(policy));
            if (!policyIds.Add(policy.Id))
                failures.Add($"Duplicate security policy ID '{policy.Id}'.");
        }
        if (profile.SecurityPolicies.Count > 0)
        {
            failures.AddRange(profile.AllowedSecurityPolicyIds
                .Where(id => !policyIds.Contains(id))
                .Select(id => $"Runtime profile '{profile.Id}' allows missing security policy '{id}'."));
        }
        return failures;
    }

    public static IReadOnlyList<string> Validate(
        RuntimeProfileDefinition profile,
        bool requireDigestPinnedImage)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var failures = new List<string>();
        if (profile.SchemaVersion != 1)
            failures.Add($"Runtime profile '{profile.Id}' uses unsupported schema version {profile.SchemaVersion}.");
        RequireStableId(profile.Id, "runtime profile ID", failures);
        Require(profile.Image, $"image for runtime profile '{profile.Id}'", failures);
        if (profile.PromotionReceipt is { } promotionReceipt)
        {
            var expectedPath = $"profiles/runtime-promotion-receipts/{profile.Id}.json";
            if (!string.Equals(promotionReceipt.Path, expectedPath, StringComparison.Ordinal))
            {
                failures.Add(
                    $"Runtime profile '{profile.Id}' promotion receipt path must be '{expectedPath}'.");
            }
            if (!IsSha256(promotionReceipt.Sha256))
            {
                failures.Add(
                    $"Runtime profile '{profile.Id}' promotion receipt must have a canonical SHA-256 digest.");
            }
            if (profile.Image.LastIndexOf("@sha256:", StringComparison.Ordinal) <= 0)
            {
                failures.Add(
                    $"Runtime profile '{profile.Id}' with a promotion receipt must use a registry digest reference.");
            }
        }
        RequireStableId(profile.Family, $"family for runtime profile '{profile.Id}'", failures);
        if (profile.AcceptedRuntimeFamilies is null)
        {
            failures.Add($"Runtime profile '{profile.Id}' must declare accepted runtime families as an array.");
        }
        else
        {
            RequireDistinct(profile.AcceptedRuntimeFamilies, "accepted runtime family", failures);
            if (profile.AcceptedRuntimeFamilies.Count > 0 &&
                !profile.AcceptedRuntimeFamilies.Contains(profile.Family, StringComparer.Ordinal))
            {
                failures.Add(
                    $"Runtime profile '{profile.Id}' must include its own family '{profile.Family}' when accepted runtime families are explicit.");
            }
        }
        ValidateAcceptedFrameworks(profile, failures);
        Require(profile.RuntimeVersion, $"runtime version for runtime profile '{profile.Id}'", failures);
        Require(profile.RuntimeImageId, $"runtime image ID for runtime profile '{profile.Id}'", failures);
        RequireStableId(profile.Rid, $"RID for runtime profile '{profile.Id}'", failures);
        RequireStableId(profile.Architecture, $"architecture for runtime profile '{profile.Id}'", failures);
        RequireStableId(profile.CpuFeatureProfile, $"CPU feature profile for runtime profile '{profile.Id}'", failures);
        RequireNonEmptyDistinct(profile.AcceptedArtifactFormats, "accepted artifact format", failures);
        RequireNonEmptyDistinct(profile.Capabilities, "capability", failures);
        ValidateCapabilities(profile, failures);
        RequireNonEmptyDistinct(profile.AllowedSecurityPolicyIds, "allowed security policy", failures);
        RequireDistinct(profile.ProvidedRuntimeFeatureTags, "runtime feature tag", failures);
        RequireDistinct(profile.ProvidedMetadataFeatureTags, "metadata feature tag", failures);
        if (profile.Container is null)
            failures.Add($"Runtime profile '{profile.Id}' must declare a container definition.");
        else
            failures.AddRange(Validate(profile.Container));
        if (profile.Operations is { } operations)
            ValidateOperations(profile, operations, failures);
        else
            ValidateLegacyLayout(profile, failures);

        ValidateRunnerSemantics(profile, failures);
        if (requireDigestPinnedImage && !IsImmutableImageReference(profile.Image))
            failures.Add($"Runtime profile '{profile.Id}' must use a digest-pinned image or immutable local image ID.");
        if (requireDigestPinnedImage && !IsSha256(profile.RuntimeImageId))
            failures.Add($"Runtime profile '{profile.Id}' must record an immutable runtime image ID.");
        if (profile.Image.EndsWith(":latest", StringComparison.OrdinalIgnoreCase))
            failures.Add($"Runtime profile '{profile.Id}' cannot use a floating latest tag.");
        return failures;
    }

    public static IReadOnlyList<string> Validate(RuntimeFrameworkCompatibilityDefinition framework)
    {
        ArgumentNullException.ThrowIfNull(framework);
        var failures = new List<string>();
        ValidateFrameworkName(framework.Name, failures);

        var exactVersionIsDeclared = framework.ExactVersion is not null;
        var minimumVersionIsDeclared = framework.MinimumVersion is not null;
        var maximumVersionIsDeclared = framework.MaximumVersion is not null;
        if (exactVersionIsDeclared)
        {
            if (minimumVersionIsDeclared || maximumVersionIsDeclared)
            {
                failures.Add(
                    $"Accepted framework '{framework.Name}' must declare either ExactVersion or a minimum/maximum range, not both.");
            }
            ValidateFrameworkVersion(framework.ExactVersion, framework.Name, "exact", failures, out _);
            return failures;
        }

        if (!minimumVersionIsDeclared || !maximumVersionIsDeclared)
        {
            failures.Add(
                $"Accepted framework '{framework.Name}' must declare both MinimumVersion and MaximumVersion, or ExactVersion.");
            return failures;
        }

        var minimumIsValid = ValidateFrameworkVersion(
            framework.MinimumVersion,
            framework.Name,
            "minimum",
            failures,
            out var minimum);
        var maximumIsValid = ValidateFrameworkVersion(
            framework.MaximumVersion,
            framework.Name,
            "maximum",
            failures,
            out var maximum);
        if (minimumIsValid && maximumIsValid && CompareFrameworkVersions(minimum!, maximum!) > 0)
        {
            failures.Add(
                $"Accepted framework '{framework.Name}' has MinimumVersion greater than MaximumVersion.");
        }
        return failures;
    }

    public static bool AcceptsFramework(
        RuntimeFrameworkCompatibilityDefinition accepted,
        string frameworkName,
        string minimumVersion)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        ArgumentException.ThrowIfNullOrWhiteSpace(frameworkName);
        ArgumentException.ThrowIfNullOrWhiteSpace(minimumVersion);
        if (!StringComparer.Ordinal.Equals(accepted.Name, frameworkName) ||
            Validate(accepted).Count > 0)
        {
            return false;
        }

        if (accepted.ExactVersion is { } exactVersion)
            return StringComparer.Ordinal.Equals(exactVersion, minimumVersion);
        if (!TryParseFrameworkVersion(minimumVersion, out var requested) ||
            !TryParseFrameworkVersion(accepted.MinimumVersion, out var minimum) ||
            !TryParseFrameworkVersion(accepted.MaximumVersion, out var maximum))
        {
            return false;
        }
        return CompareFrameworkVersions(requested!, minimum!) >= 0 &&
               CompareFrameworkVersions(requested!, maximum!) <= 0;
    }

    public static IReadOnlyList<string> Validate(RuntimeContainerDefinition container)
    {
        ArgumentNullException.ThrowIfNull(container);
        var failures = new List<string>();
        var environmentKindIsKnown = container.EnvironmentKind is
            RuntimeContainerEnvironmentKinds.CoreClr or
            RuntimeContainerEnvironmentKinds.Mono or
            RuntimeContainerEnvironmentKinds.Wine;
        if (!environmentKindIsKnown)
        {
            failures.Add(
                $"Runtime container environment kind '{container.EnvironmentKind}' is not supported.");
        }

        switch (container.IsolationKind)
        {
            case RuntimeContainerIsolationKinds.Standard:
                if (!StringComparer.Ordinal.Equals(
                        container.ExecutionUser,
                        RuntimeContainerExecutionUsers.NonRoot))
                {
                    failures.Add(
                        $"Standard container isolation requires execution user '{RuntimeContainerExecutionUsers.NonRoot}'.");
                }
                if (environmentKindIsKnown &&
                    container.EnvironmentKind is not (
                        RuntimeContainerEnvironmentKinds.CoreClr or
                        RuntimeContainerEnvironmentKinds.Mono))
                {
                    failures.Add(
                        "Standard container isolation supports only 'coreclr' or 'mono' environments.");
                }
                if (container.WinePrefixPath is not null)
                    failures.Add("A standard container cannot declare a Wine prefix path.");
                break;
            case RuntimeContainerIsolationKinds.Wine:
                if (container.ExecutionUser is not (
                        RuntimeContainerExecutionUsers.Root or
                        RuntimeContainerExecutionUsers.NonRoot))
                {
                    failures.Add(
                        $"Wine container isolation requires execution user '{RuntimeContainerExecutionUsers.Root}' or '{RuntimeContainerExecutionUsers.NonRoot}'.");
                }
                if (environmentKindIsKnown &&
                    !StringComparer.Ordinal.Equals(
                        container.EnvironmentKind,
                        RuntimeContainerEnvironmentKinds.Wine))
                {
                    failures.Add("Wine container isolation requires the 'wine' environment.");
                }
                ValidateCommand(
                    container.WinePrefixPath ?? string.Empty,
                    "Wine container prefix",
                    allowCommandName: false,
                    failures);
                if (container.WinePrefixPath is { } winePrefixPath &&
                    (!winePrefixPath.StartsWith("/opt/", StringComparison.Ordinal) ||
                     winePrefixPath.EndsWith('/')))
                {
                    failures.Add("The Wine container prefix must be a directory below /opt.");
                }
                break;
            default:
                failures.Add(
                    $"Runtime container isolation kind '{container.IsolationKind}' is not supported.");
                break;
        }
        return failures;
    }

    public static IReadOnlyList<string> Validate(RuntimeRunOperationDefinition operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var failures = new List<string>();
        ValidateOperation(
            operation,
            "Run",
            [RuntimeOperationPlaceholders.EntryAssembly, RuntimeOperationPlaceholders.Arguments],
            failures);
        RequirePlaceholderExactlyOnce(
            operation.Command,
            RuntimeOperationPlaceholders.EntryAssembly,
            "Run",
            failures);
        RequirePlaceholderAtMostOnce(
            operation.Command,
            RuntimeOperationPlaceholders.Arguments,
            "Run",
            failures);
        RequireDynamicPlaceholderLastAndAfterEntry(
            operation.Command,
            RuntimeOperationPlaceholders.Arguments,
            "Run",
            failures);
        ValidateRunImplementation(operation, failures);
        return failures;
    }

    public static IReadOnlyList<string> Validate(RuntimeJitOperationDefinition operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var failures = new List<string>();
        ValidateOperation(
            operation,
            "JIT",
            [RuntimeOperationPlaceholders.EntryAssembly, RuntimeOperationPlaceholders.MethodFilter],
            failures);
        RequirePlaceholderExactlyOnce(
            operation.Command,
            RuntimeOperationPlaceholders.EntryAssembly,
            "JIT",
            failures);
        RequirePlaceholderAtMostOnce(
            operation.Command,
            RuntimeOperationPlaceholders.MethodFilter,
            "JIT",
            failures);
        RequireDynamicPlaceholderLastAndAfterEntry(
            operation.Command,
            RuntimeOperationPlaceholders.MethodFilter,
            "JIT",
            failures);

        switch (operation.SourceMappingKind)
        {
            case RuntimeJitSourceMappingKinds.None:
                if (operation.ProfilerPath is not null)
                    failures.Add("A JIT operation with source mapping kind 'none' cannot declare a profiler path.");
                break;
            case RuntimeJitSourceMappingKinds.LinuxProfiler:
                if (!StringComparer.Ordinal.Equals(operation.PathStyle, RuntimeOperationPathStyles.Unix))
                {
                    failures.Add(
                        "A JIT operation with source mapping kind 'linux-profiler' must use Unix paths.");
                }
                ValidateCommand(
                    operation.ProfilerPath ?? string.Empty,
                    "JIT Linux profiler",
                    allowCommandName: false,
                    failures);
                break;
            case RuntimeJitSourceMappingKinds.CheckedJitDebugInfo:
                if (!StringComparer.Ordinal.Equals(operation.PathStyle, RuntimeOperationPathStyles.Unix))
                {
                    failures.Add(
                        "A JIT operation with source mapping kind 'checked-jit-debug-info' must use Unix paths.");
                }
                if (operation.ProfilerPath is not null)
                {
                    failures.Add(
                        "A JIT operation with source mapping kind 'checked-jit-debug-info' cannot declare a profiler path.");
                }
                break;
            default:
                failures.Add(
                    $"JIT source mapping kind '{operation.SourceMappingKind}' is not supported.");
                break;
        }
        ValidateJitImplementation(operation, failures);
        return failures;
    }

    private static void ValidateAcceptedFrameworks(
        RuntimeProfileDefinition profile,
        List<string> failures)
    {
        if (profile.AcceptedFrameworks is null)
        {
            failures.Add($"Runtime profile '{profile.Id}' must declare accepted frameworks as an array.");
            return;
        }
        if (profile.AcceptedFrameworks.Count > MaximumAcceptedFrameworks)
        {
            failures.Add(
                $"Runtime profile '{profile.Id}' cannot declare more than {MaximumAcceptedFrameworks} accepted frameworks.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var framework in profile.AcceptedFrameworks)
        {
            if (framework is null)
            {
                failures.Add($"Runtime profile '{profile.Id}' cannot declare a null accepted framework.");
                continue;
            }
            failures.AddRange(Validate(framework));
            if (!string.IsNullOrWhiteSpace(framework.Name) && !names.Add(framework.Name))
            {
                failures.Add(
                    $"Runtime profile '{profile.Id}' declares duplicate accepted framework '{framework.Name}'.");
            }
        }
    }

    private static void ValidateFrameworkName(string? name, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Length > MaximumFrameworkNameLength ||
            !name.Any(static character => char.IsAsciiLetterOrDigit(character)) ||
            name.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
        {
            failures.Add(
                "Accepted framework names must use 1-160 ASCII letters, digits, periods, underscores, or hyphens.");
        }
    }

    private static bool ValidateFrameworkVersion(
        string? version,
        string frameworkName,
        string boundName,
        List<string> failures,
        out ParsedFrameworkVersion? parsed)
    {
        if (TryParseFrameworkVersion(version, out parsed))
            return true;
        failures.Add(
            $"Accepted framework '{frameworkName}' has an invalid {boundName} version '{version}'.");
        return false;
    }

    private static bool TryParseFrameworkVersion(
        string? value,
        out ParsedFrameworkVersion? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumFrameworkVersionLength)
            return false;

        var buildSeparator = value.IndexOf('+');
        if (buildSeparator >= 0)
        {
            if (buildSeparator == value.Length - 1 ||
                value.LastIndexOf('+') != buildSeparator ||
                !IsVersionIdentifierSequence(value[(buildSeparator + 1)..]))
            {
                return false;
            }
        }
        var withoutBuild = buildSeparator >= 0 ? value[..buildSeparator] : value;
        var prereleaseSeparator = withoutBuild.IndexOf('-');
        var releaseText = prereleaseSeparator >= 0
            ? withoutBuild[..prereleaseSeparator]
            : withoutBuild;
        var prereleaseText = prereleaseSeparator >= 0
            ? withoutBuild[(prereleaseSeparator + 1)..]
            : null;
        var release = releaseText.Split('.');
        if (release.Length is 0 or > 4 || release.Any(static identifier =>
                identifier.Length == 0 || identifier.Any(static character => !char.IsAsciiDigit(character))))
        {
            return false;
        }

        string[] prerelease;
        if (prereleaseText is null)
        {
            prerelease = [];
        }
        else
        {
            if (!IsVersionIdentifierSequence(prereleaseText))
                return false;
            prerelease = prereleaseText.Split('.');
        }

        parsed = new ParsedFrameworkVersion(release, prerelease);
        return true;
    }

    private static bool IsVersionIdentifierSequence(string value) =>
        value.Split('.').All(static identifier =>
            identifier.Length > 0 && identifier.All(static character =>
                char.IsAsciiLetterOrDigit(character) || character == '-'));

    private static int CompareFrameworkVersions(
        ParsedFrameworkVersion left,
        ParsedFrameworkVersion right)
    {
        var releaseCount = Math.Max(left.Release.Length, right.Release.Length);
        for (var index = 0; index < releaseCount; index++)
        {
            var comparison = CompareNumericIdentifier(
                index < left.Release.Length ? left.Release[index] : "0",
                index < right.Release.Length ? right.Release[index] : "0");
            if (comparison != 0)
                return comparison;
        }

        if (left.Prerelease.Length == 0)
            return right.Prerelease.Length == 0 ? 0 : 1;
        if (right.Prerelease.Length == 0)
            return -1;
        var prereleaseCount = Math.Min(left.Prerelease.Length, right.Prerelease.Length);
        for (var index = 0; index < prereleaseCount; index++)
        {
            var leftIdentifier = left.Prerelease[index];
            var rightIdentifier = right.Prerelease[index];
            var leftIsNumeric = leftIdentifier.All(static character => char.IsAsciiDigit(character));
            var rightIsNumeric = rightIdentifier.All(static character => char.IsAsciiDigit(character));
            int comparison;
            if (leftIsNumeric && rightIsNumeric)
                comparison = CompareNumericIdentifier(leftIdentifier, rightIdentifier);
            else if (leftIsNumeric)
                comparison = -1;
            else if (rightIsNumeric)
                comparison = 1;
            else
                comparison = StringComparer.Ordinal.Compare(leftIdentifier, rightIdentifier);
            if (comparison != 0)
                return comparison;
        }
        return left.Prerelease.Length.CompareTo(right.Prerelease.Length);
    }

    private static int CompareNumericIdentifier(string left, string right)
    {
        var normalizedLeft = left.TrimStart('0');
        var normalizedRight = right.TrimStart('0');
        if (normalizedLeft.Length == 0)
            normalizedLeft = "0";
        if (normalizedRight.Length == 0)
            normalizedRight = "0";
        var lengthComparison = normalizedLeft.Length.CompareTo(normalizedRight.Length);
        return lengthComparison != 0
            ? lengthComparison
            : StringComparer.Ordinal.Compare(normalizedLeft, normalizedRight);
    }

    private static void ValidateOperations(
        RuntimeProfileDefinition profile,
        RuntimeProfileOperations operations,
        List<string> failures)
    {
        var hasRunCapability = profile.Capabilities.Contains("run", StringComparer.Ordinal);
        var hasJitCapability = profile.Capabilities.Contains("jit-asm", StringComparer.Ordinal);

        if (hasRunCapability && operations.Run is null)
            failures.Add($"Runtime profile '{profile.Id}' declares capability 'run' without a Run operation.");
        else if (!hasRunCapability && operations.Run is not null)
            failures.Add($"Runtime profile '{profile.Id}' declares a Run operation without capability 'run'.");

        if (hasJitCapability && operations.Jit is null)
            failures.Add($"Runtime profile '{profile.Id}' declares capability 'jit-asm' without a JIT operation.");
        else if (!hasJitCapability && operations.Jit is not null)
            failures.Add($"Runtime profile '{profile.Id}' declares a JIT operation without capability 'jit-asm'.");

        if (operations.Run is null && operations.Jit is null)
            failures.Add($"Runtime profile '{profile.Id}' must declare at least one operation.");
        if (operations.Run is not null)
        {
            failures.AddRange(Validate(operations.Run));
            ValidateFixedRuntimeVersion(profile, operations.Run, "Run", failures);
        }
        if (operations.Jit is not null)
        {
            failures.AddRange(Validate(operations.Jit));
            ValidateFixedRuntimeVersion(profile, operations.Jit, "JIT", failures);
        }
    }

    private static void ValidateCapabilities(
        RuntimeProfileDefinition profile,
        List<string> failures)
    {
        var known = new HashSet<string>(
            ["run", "jit-asm", "inspection", "execution-flow"],
            StringComparer.Ordinal);
        foreach (var capability in profile.Capabilities)
        {
            if (!known.Contains(capability))
            {
                failures.Add(
                    $"Runtime profile '{profile.Id}' declares unsupported capability '{capability}'.");
            }
        }

        var instrumentation = profile.Capabilities
            .Where(static capability => capability is "inspection" or "execution-flow")
            .ToArray();
        if (instrumentation.Length == 0)
            return;

        // Instrumentation is a Run-time sink, not a standalone operation. It
        // is intentionally restricted to the operation-based standard CoreCLR
        // runner until another runtime family supplies equivalent evidence.
        if (profile.Operations is null)
        {
            failures.Add(
                $"Runtime profile '{profile.Id}' cannot declare instrumentation capabilities without operation-based Run support.");
        }
        var isSupportedCoreClrFamily = profile.Family is "coreclr" or "coreclr-const-generics";
        if (!isSupportedCoreClrFamily ||
            profile.Container is null ||
            !StringComparer.Ordinal.Equals(profile.Container.IsolationKind, RuntimeContainerIsolationKinds.Standard) ||
            !StringComparer.Ordinal.Equals(profile.Container.EnvironmentKind, RuntimeContainerEnvironmentKinds.CoreClr) ||
            !StringComparer.Ordinal.Equals(profile.Layout.RunnerKind, RuntimeRunnerKinds.DotNet))
        {
            failures.Add(
                $"Runtime profile '{profile.Id}' instrumentation capabilities are supported only by the standard CoreCLR runner.");
        }
        if (profile.Operations?.Run?.ImplementationId is not RuntimeOperationImplementationIds.Runner)
        {
            failures.Add(
                $"Runtime profile '{profile.Id}' instrumentation capabilities require Run implementation '{RuntimeOperationImplementationIds.Runner}'.");
        }
    }

    private static void ValidateLegacyLayout(
        RuntimeProfileDefinition profile,
        List<string> failures)
    {
        if (profile.Layout is null)
        {
            failures.Add($"Runtime profile '{profile.Id}' must declare operations or a legacy image layout.");
            return;
        }
        if (profile.Layout.RunnerKind is not (
                RuntimeRunnerKinds.DotNet or
                RuntimeRunnerKinds.WineCoreClr or
                RuntimeRunnerKinds.WineNetFx or
                RuntimeRunnerKinds.WineJSharp20))
            failures.Add($"Runtime runner kind '{profile.Layout.RunnerKind}' is not supported.");
        ValidateCommand(profile.Layout.DotNetHostPath, "dotnet host", allowCommandName: true, failures);
        ValidateCommand(profile.Layout.RunnerAssemblyPath, "Runner assembly", allowCommandName: false, failures);
        var requiresJitInspector = profile.Capabilities.Contains("jit-asm", StringComparer.Ordinal);
        if (requiresJitInspector)
        {
            ValidateCommand(
                profile.Layout.JitInspectorAssemblyPath ?? string.Empty,
                "JIT inspector assembly",
                allowCommandName: false,
                failures);
        }
        else if (!string.IsNullOrWhiteSpace(profile.Layout.JitInspectorAssemblyPath))
        {
            failures.Add("A run-only legacy profile cannot declare a JIT inspector assembly.");
        }
        if (profile.Layout.RunnerKind is RuntimeRunnerKinds.WineCoreClr or
            RuntimeRunnerKinds.WineNetFx or
            RuntimeRunnerKinds.WineJSharp20)
        {
            ValidateCommand(profile.Layout.WineHostPath, "Wine host", allowCommandName: true, failures);
            ValidateCommand(
                profile.Layout.WinePrefixPath ?? string.Empty,
                "Wine prefix",
                allowCommandName: false,
                failures);
            if (StringComparer.Ordinal.Equals(profile.Layout.RunnerKind, RuntimeRunnerKinds.WineCoreClr))
                ValidateWineCoreClrProfile(profile, failures);
            else if (StringComparer.Ordinal.Equals(profile.Layout.RunnerKind, RuntimeRunnerKinds.WineNetFx))
                ValidateWineNetFxProfile(profile, failures);
            else
                ValidateWineJSharp20Profile(profile, failures);
        }
    }

    private static void ValidateRunnerSemantics(
        RuntimeProfileDefinition profile,
        List<string> failures)
    {
        // Mono is a managed compatibility runner, not a CoreCLR host.  Keep
        // this invariant in the profile SDK so a hand-authored profile cannot
        // advertise CoreCLR-only JIT/instrumentation and fail later inside the
        // Supervisor after a job has already been scheduled.
        if (profile.Container is { EnvironmentKind: RuntimeContainerEnvironmentKinds.Mono } ||
            StringComparer.Ordinal.Equals(profile.Family, "mono"))
        {
            ValidateMonoProfile(profile, failures);
        }

        // Operation-based profiles still carry a layout for discovery and backwards
        // compatibility. Apply the same product-specific invariants to that layout
        // so a syntactically valid profile cannot select the wrong runtime family.
        // Infer the product boundary from the family/container as well as the
        // runner kind.  J# deliberately shares the netfx family string, but
        // has a different artifact contract, prefix, and feature tag; applying
        // the ordinary Framework rules to it would reject the built-in
        // operation-based profile.  Dispatch by the concrete runner kind so
        // every Wine profile is checked exactly once.
        if (profile.Container is { EnvironmentKind: RuntimeContainerEnvironmentKinds.Wine })
        {
            var runnerKind = profile.Layout?.RunnerKind;
            switch (runnerKind)
            {
                case RuntimeRunnerKinds.WineCoreClr:
                    if (profile.Operations is not null)
                        ValidateWineCoreClrProfile(profile, failures);
                    break;
                case RuntimeRunnerKinds.WineNetFx:
                    if (profile.Operations is not null)
                        ValidateWineNetFxProfile(profile, failures);
                    break;
                case RuntimeRunnerKinds.WineJSharp20:
                    if (profile.Operations is not null)
                        ValidateWineJSharp20Profile(profile, failures);
                    break;
                default:
                    // An operation-based profile must still identify its
                    // family with a supported runner kind.  Dispatch by the
                    // declared family here as well, so changing the runner
                    // kind cannot bypass the product-specific checks.
                    if (StringComparer.Ordinal.Equals(profile.Family, "coreclr-wine"))
                        ValidateWineCoreClrProfile(profile, failures);
                    else if (StringComparer.Ordinal.Equals(profile.Family, "netfx-clr-wine"))
                        ValidateWineNetFxProfile(profile, failures);
                    else
                        failures.Add(
                            $"Wine runtime profile '{profile.Id}' must use a supported Wine runner kind.");
                    break;
            }
        }
    }

    private static void ValidateMonoProfile(
        RuntimeProfileDefinition profile,
        List<string> failures)
    {
        if (!StringComparer.Ordinal.Equals(profile.Family, "mono"))
            failures.Add("The Mono environment requires runtime family 'mono'.");
        if (profile.Container is null ||
            !StringComparer.Ordinal.Equals(profile.Container.IsolationKind, RuntimeContainerIsolationKinds.Standard) ||
            !StringComparer.Ordinal.Equals(profile.Container.EnvironmentKind, RuntimeContainerEnvironmentKinds.Mono) ||
            profile.Container.WinePrefixPath is not null)
        {
            failures.Add("The Mono runner requires standard container isolation with no Wine prefix.");
        }
        if (!StringComparer.Ordinal.Equals(profile.Rid, "linux-x64") ||
            !StringComparer.Ordinal.Equals(profile.Architecture, "x64"))
        {
            failures.Add("The Mono runner currently supports only linux-x64/x64.");
        }

        var acceptedFormats = new HashSet<string>(profile.AcceptedArtifactFormats, StringComparer.Ordinal);
        if (acceptedFormats.Count != 1 ||
            !acceptedFormats.Contains("dotnet-framework-managed-pe-v1"))
        {
            failures.Add("The Mono runner accepts only managed .NET Framework PE artifacts.");
        }

        var capabilities = new HashSet<string>(profile.Capabilities, StringComparer.Ordinal);
        if (!capabilities.Contains("run") ||
            capabilities.Any(static capability => capability is not ("run" or "jit-asm")))
        {
            failures.Add("The Mono runner exposes Run and optional bounded Mono JIT ASM only.");
        }
        if (profile.Operations?.Jit is { } monoJit &&
            (!StringComparer.Ordinal.Equals(
                monoJit.ImplementationId,
                RuntimeOperationImplementationIds.MonoJitInspector) ||
             !StringComparer.Ordinal.Equals(
                monoJit.SourceMappingKind,
                RuntimeJitSourceMappingKinds.None)))
        {
            failures.Add(
                $"The Mono runner requires JIT implementation '{RuntimeOperationImplementationIds.MonoJitInspector}' with source mapping kind '{RuntimeJitSourceMappingKinds.None}'.");
        }
        if (profile.Operations?.Run is { ImplementationId: not RuntimeOperationImplementationIds.TargetRuntimeRunner })
        {
            failures.Add(
                $"The Mono runner requires Run implementation '{RuntimeOperationImplementationIds.TargetRuntimeRunner}'.");
        }
        if (!StringComparer.Ordinal.Equals(profile.Layout.RunnerKind, RuntimeRunnerKinds.DotNet))
            failures.Add("The Mono runner must use the operation-based managed-runner layout.");
        if (!StringComparer.Ordinal.Equals(profile.Layout.DotNetHostPath, "/usr/bin/mono") ||
            !StringComparer.Ordinal.Equals(
                profile.Layout.RunnerAssemblyPath,
                TargetRuntimeRunnerAssemblyPath))
        {
            failures.Add(
                $"The Mono runner layout must invoke '{TargetRuntimeRunnerAssemblyPath}' through '/usr/bin/mono'.");
        }
        if (capabilities.Contains("jit-asm") &&
            !StringComparer.Ordinal.Equals(
                profile.Layout.JitInspectorAssemblyPath,
                MonoJitInspectorAssemblyPath))
        {
            failures.Add(
                $"The Mono runner JIT layout requires helper '{MonoJitInspectorAssemblyPath}'.");
        }
    }

    private static void ValidateRunImplementation(
        RuntimeRunOperationDefinition operation,
        List<string> failures)
    {
        if (operation.Command is null)
            return;
        switch (operation.ImplementationId)
        {
            case RuntimeOperationImplementationIds.Runner:
                RequireUnixDotNetImplementation(operation, "Run", failures);
                RequireExactInvocation(
                    operation.Command,
                    [
                        RunnerAssemblyPath,
                        RuntimeOperationPlaceholders.EntryAssembly,
                        "--",
                        RuntimeOperationPlaceholders.Arguments
                    ],
                    "Run",
                    RunnerAssemblyPath,
                    failures);
                break;
            case RuntimeOperationImplementationIds.LegacyJitInspector:
                ValidateLegacyJitInspectorImplementation(operation, "run", "Run", failures);
                break;
            case RuntimeOperationImplementationIds.WineRunner:
                RequireDotNetExecutable(operation.Command, "Run", failures);
                if (operation.Command.Argv is not { } wineRunnerArgv ||
                    wineRunnerArgv.Count != 6 ||
                    !TokenEquals(operation.Command, 0, WineRunnerAssemblyPath) ||
                    !TokenEquals(operation.Command, 1, "bridge") ||
                    !IsFixedToken(wineRunnerArgv[2]) ||
                    !TokenEquals(operation.Command, 3, RuntimeOperationPlaceholders.EntryAssembly) ||
                    !TokenEquals(operation.Command, 4, "--") ||
                    !TokenEquals(operation.Command, 5, RuntimeOperationPlaceholders.Arguments))
                {
                    failures.Add(
                        $"Run implementation '{RuntimeOperationImplementationIds.WineRunner}' must invoke '{WineRunnerAssemblyPath}' exactly as '<runner> bridge <fixed-host> {{entryAssembly}} -- {{arguments}}'.");
                }
                break;
            case RuntimeOperationImplementationIds.TargetRuntimeRunner:
                ValidateTargetRuntimeRunnerImplementation(operation, failures);
                break;
            case RuntimeOperationImplementationIds.DirectRuntime:
                if (operation.Command.Argv is not { } directArgv ||
                    !directArgv.SequenceEqual(
                        [
                            RuntimeOperationPlaceholders.EntryAssembly,
                            "--",
                            RuntimeOperationPlaceholders.Arguments
                        ],
                        StringComparer.Ordinal))
                {
                    failures.Add(
                        $"Run implementation '{RuntimeOperationImplementationIds.DirectRuntime}' must invoke exactly as '{{entryAssembly}} -- {{arguments}}'.");
                }
                break;
            default:
                failures.Add($"Run operation implementation '{operation.ImplementationId}' is not supported.");
                break;
        }
    }

    private static void ValidateTargetRuntimeRunnerImplementation(
        RuntimeRunOperationDefinition operation,
        List<string> failures)
    {
        var helperPath = operation.PathStyle switch
        {
            RuntimeOperationPathStyles.Unix => TargetRuntimeRunnerAssemblyPath,
            RuntimeOperationPathStyles.WineZ => WineTargetRuntimeRunnerAssemblyPath,
            _ => TargetRuntimeRunnerAssemblyPath
        };
        var expectedExecutable = operation.PathStyle switch
        {
            RuntimeOperationPathStyles.Unix => "/usr/bin/mono",
            RuntimeOperationPathStyles.WineZ => "/usr/lib/wine/wine64",
            _ => string.Empty
        };
        if (!StringComparer.Ordinal.Equals(operation.Command?.Executable, expectedExecutable) ||
            operation.Command?.Argv is not { } argv ||
            !argv.SequenceEqual(
                [
                    helperPath,
                    "run",
                    RuntimeOperationPlaceholders.EntryAssembly,
                    "--",
                    RuntimeOperationPlaceholders.Arguments
                ],
                StringComparer.Ordinal))
        {
            failures.Add(
                $"Run implementation '{RuntimeOperationImplementationIds.TargetRuntimeRunner}' must invoke the fixed target CLR host and '{TargetRuntimeRunnerAssemblyPath}' exactly as '<helper> run {{entryAssembly}} -- {{arguments}}'.");
        }
    }

    private static void ValidateJitImplementation(
        RuntimeJitOperationDefinition operation,
        List<string> failures)
    {
        if (operation.Command is null)
            return;
        switch (operation.ImplementationId)
        {
            case RuntimeOperationImplementationIds.JitInspector:
                RequireUnixDotNetImplementation(operation, "JIT", failures);
                RequireExactInvocation(
                    operation.Command,
                    [
                        JitInspectorAssemblyPath,
                        RuntimeOperationPlaceholders.EntryAssembly,
                        RuntimeOperationPlaceholders.MethodFilter
                    ],
                    "JIT",
                    JitInspectorAssemblyPath,
                    failures);
                if (StringComparer.Ordinal.Equals(
                        operation.SourceMappingKind,
                        RuntimeJitSourceMappingKinds.LinuxProfiler) &&
                    !StringComparer.Ordinal.Equals(operation.ProfilerPath, JitProfilerPath))
                {
                    failures.Add(
                        $"JIT implementation '{RuntimeOperationImplementationIds.JitInspector}' with Linux profiler mapping requires profiler '{JitProfilerPath}'.");
                }
                break;
            case RuntimeOperationImplementationIds.LegacyJitInspector:
                ValidateLegacyJitInspectorImplementation(operation, "jit", "JIT", failures);
                if (!StringComparer.Ordinal.Equals(
                        operation.SourceMappingKind,
                        RuntimeJitSourceMappingKinds.None))
                {
                    failures.Add(
                        $"JIT implementation '{RuntimeOperationImplementationIds.LegacyJitInspector}' supports only source mapping kind '{RuntimeJitSourceMappingKinds.None}'.");
                }
                break;
            case RuntimeOperationImplementationIds.CheckedJitBridge:
                RequireUnixDotNetImplementation(operation, "JIT", failures);
                RequireExactInvocation(
                    operation.Command,
                    [
                        CheckedJitBridgeAssemblyPath,
                        "jit",
                        RuntimeOperationPlaceholders.EntryAssembly,
                        RuntimeOperationPlaceholders.MethodFilter
                    ],
                    "JIT",
                    CheckedJitBridgeAssemblyPath,
                    failures);
                if (operation.SourceMappingKind is not (
                    RuntimeJitSourceMappingKinds.None or
                    RuntimeJitSourceMappingKinds.CheckedJitDebugInfo))
                {
                    failures.Add(
                        $"JIT implementation '{RuntimeOperationImplementationIds.CheckedJitBridge}' supports only source mapping kinds '{RuntimeJitSourceMappingKinds.None}' and '{RuntimeJitSourceMappingKinds.CheckedJitDebugInfo}'.");
                }
                break;
            case RuntimeOperationImplementationIds.MonoJitInspector:
                RequireUnixDotNetImplementation(operation, "JIT", failures);
                RequireExactInvocation(
                    operation.Command,
                    [
                        MonoJitInspectorAssemblyPath,
                        RuntimeOperationPlaceholders.EntryAssembly,
                        RuntimeOperationPlaceholders.MethodFilter
                    ],
                    "JIT",
                    MonoJitInspectorAssemblyPath,
                    failures);
                if (!StringComparer.Ordinal.Equals(
                        operation.SourceMappingKind,
                        RuntimeJitSourceMappingKinds.None) ||
                    operation.ProfilerPath is not null)
                {
                    failures.Add(
                        $"JIT implementation '{RuntimeOperationImplementationIds.MonoJitInspector}' supports only source mapping kind '{RuntimeJitSourceMappingKinds.None}' and cannot declare a profiler.");
                }
                break;
            default:
                failures.Add($"JIT operation implementation '{operation.ImplementationId}' is not supported.");
                break;
        }
    }

    private static void ValidateLegacyJitInspectorImplementation(
        RuntimeOperationDefinition operation,
        string verb,
        string operationName,
        List<string> failures)
    {
        var helperPath = operation.PathStyle switch
        {
            RuntimeOperationPathStyles.Unix => LegacyJitInspectorAssemblyPath,
            RuntimeOperationPathStyles.WineZ => WineLegacyJitInspectorAssemblyPath,
            _ => LegacyJitInspectorAssemblyPath
        };
        if (StringComparer.Ordinal.Equals(operation.PathStyle, RuntimeOperationPathStyles.Unix))
            RequireDotNetExecutable(operation.Command, operationName, failures);
        else if (!StringComparer.Ordinal.Equals(operation.Command.Executable, "/usr/lib/wine/wine64"))
            failures.Add($"{operationName} legacy Wine implementation requires executable '/usr/lib/wine/wine64'.");

        var invocation = verb == "run"
            ? new List<string>
            {
                helperPath,
                verb,
                RuntimeOperationPlaceholders.EntryAssembly,
                "--",
                RuntimeOperationPlaceholders.Arguments
            }
            :
            [
                helperPath,
                verb,
                RuntimeOperationPlaceholders.EntryAssembly,
                RuntimeOperationPlaceholders.MethodFilter
            ];
        var fixedRuntimeVersion = operation.Command.Argv is { } commandArgv
            ? ReadFixedFxVersion(commandArgv)
            : null;
        var guardedInvocation = fixedRuntimeVersion is null
            ? null
            : invocation
                .Take(1)
                .Concat([
                    LegacyRuntimeVersionSwitch,
                    fixedRuntimeVersion
                ])
                .Concat(invocation.Skip(1))
                .ToList();
        var hasValidShape = operation.Command.Argv is { } argv &&
            operation.PathStyle switch
            {
                RuntimeOperationPathStyles.Unix =>
                    HasExactSuffix(argv, 0, invocation) ||
                    guardedInvocation is not null && HasExactSuffix(argv, 0, guardedInvocation) ||
                    (guardedInvocation is not null &&
                     argv.Count == guardedInvocation.Count + 3 &&
                     StringComparer.Ordinal.Equals(argv[0], "exec") &&
                     StringComparer.Ordinal.Equals(argv[1], "--fx-version") &&
                     IsFixedToken(argv[2]) &&
                     HasExactSuffix(argv, 3, guardedInvocation)),
                RuntimeOperationPathStyles.WineZ =>
                    (argv.Count == invocation.Count + 1 &&
                     IsFixedWindowsDotNetHost(argv[0]) &&
                     HasExactSuffix(argv, 1, invocation)) ||
                    (guardedInvocation is not null &&
                     argv.Count == guardedInvocation.Count + 1 &&
                     IsFixedWindowsDotNetHost(argv[0]) &&
                     HasExactSuffix(argv, 1, guardedInvocation)) ||
                    (guardedInvocation is not null &&
                     argv.Count == guardedInvocation.Count + 4 &&
                     IsFixedWindowsDotNetHost(argv[0]) &&
                     StringComparer.Ordinal.Equals(argv[1], "exec") &&
                     StringComparer.Ordinal.Equals(argv[2], "--fx-version") &&
                     IsFixedToken(argv[3]) &&
                     HasExactSuffix(argv, 4, guardedInvocation)),
                _ => false
            };
        if (!hasValidShape)
        {
            failures.Add(
                $"{operationName} implementation must invoke '{helperPath}' using its exact fixed operation contract.");
        }
    }

    private static string? ReadFixedFxVersion(List<string> argv)
    {
        var index = -1;
        for (var candidate = 0; candidate < argv.Count; candidate++)
        {
            if (StringComparer.Ordinal.Equals(argv[candidate], "--fx-version"))
            {
                if (index >= 0 || candidate + 1 >= argv.Count || !IsFixedToken(argv[candidate + 1]))
                    return null;
                index = candidate;
            }
        }
        return index < 0 ? null : argv[index + 1];
    }

    private static void RequireUnixDotNetImplementation(
        RuntimeOperationDefinition operation,
        string operationName,
        List<string> failures)
    {
        if (!StringComparer.Ordinal.Equals(operation.PathStyle, RuntimeOperationPathStyles.Unix))
            failures.Add($"{operationName} modern helper implementation requires Unix paths.");
        RequireDotNetExecutable(operation.Command, operationName, failures);
    }

    private static void RequireDotNetExecutable(
        RuntimeOperationCommandDefinition command,
        string operationName,
        List<string> failures)
    {
        var executable = command.Executable.Replace('\\', '/');
        var fileName = executable[(executable.LastIndexOf('/') + 1)..];
        if (!StringComparer.Ordinal.Equals(fileName, "dotnet"))
            failures.Add($"{operationName} helper implementation requires a fixed dotnet executable.");
    }

    private static void RequireExactInvocation(
        RuntimeOperationCommandDefinition command,
        List<string> sequence,
        string operationName,
        string helperPath,
        List<string> failures)
    {
        if (command.Argv is null || !command.Argv.SequenceEqual(sequence, StringComparer.Ordinal))
        {
            failures.Add(
                $"{operationName} implementation must invoke '{helperPath}' using its fixed operation contract.");
        }
    }

    private static bool HasExactSuffix(
        List<string> values,
        int start,
        List<string> sequence)
    {
        if (values.Count != start + sequence.Count)
            return false;
        for (var index = 0; index < sequence.Count; index++)
        {
            if (!StringComparer.Ordinal.Equals(values[start + index], sequence[index]))
                return false;
        }
        return true;
    }

    private static bool TokenEquals(RuntimeOperationCommandDefinition command, int index, string value) =>
        command.Argv is { } argv &&
        index >= 0 && index < argv.Count &&
        StringComparer.Ordinal.Equals(argv[index], value);

    private static bool IsFixedToken(string token) =>
        !string.IsNullOrWhiteSpace(token) &&
        !IsKnownPlaceholder(token) &&
        !token.Contains('{') &&
        !token.Contains('}');

    private static bool IsFixedWindowsDotNetHost(string token)
    {
        if (!IsFixedToken(token) ||
            token.Length < 4 ||
            !char.IsAsciiLetter(token[0]) ||
            token[1] != ':' ||
            token[2] != '\\' ||
            !token.EndsWith(@"\dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return token.Split('\\').All(static segment => segment is not ("" or "." or ".."));
    }

    private static void ValidateFixedRuntimeVersion(
        RuntimeProfileDefinition profile,
        RuntimeOperationDefinition operation,
        string operationName,
        List<string> failures)
    {
        if (operation.Command?.Argv is not { } argv)
            return;
        var versionSwitchIndex = argv.FindIndex(static token =>
            StringComparer.Ordinal.Equals(token, "--fx-version"));
        if (versionSwitchIndex < 0)
            return;
        if (argv.Count(token => StringComparer.Ordinal.Equals(token, "--fx-version")) != 1 ||
            versionSwitchIndex + 1 >= argv.Count ||
            !StringComparer.Ordinal.Equals(argv[versionSwitchIndex + 1], profile.RuntimeVersion))
        {
            failures.Add(
                $"The {operationName} operation '--fx-version' must match runtime profile version '{profile.RuntimeVersion}'.");
        }
    }

    private static void ValidateOperation(
        RuntimeOperationDefinition operation,
        string operationName,
        IReadOnlyCollection<string> allowedPlaceholders,
        List<string> failures)
    {
        if (operation.PathStyle is not (
                RuntimeOperationPathStyles.Unix or
                RuntimeOperationPathStyles.WineZ))
        {
            failures.Add(
                $"{operationName} operation path style '{operation.PathStyle}' is not supported.");
        }

        if (operation.Command is null)
        {
            failures.Add($"The {operationName} operation must declare a command.");
            return;
        }

        ValidateOperationExecutable(operation.Command.Executable, operationName, failures);
        if (operation.Command.Argv is null)
        {
            failures.Add($"The {operationName} operation command must declare argv tokens.");
            return;
        }
        if (operation.Command.Argv.Count is 0 or > MaximumOperationArgumentTokens)
        {
            failures.Add(
                $"The {operationName} operation command must declare between 1 and {MaximumOperationArgumentTokens} argv tokens.");
        }

        foreach (var token in operation.Command.Argv)
        {
            if (string.IsNullOrEmpty(token) ||
                token.Length > MaximumOperationTokenLength ||
                token.Any(static character => char.IsControl(character)))
            {
                failures.Add(
                    $"The {operationName} operation contains an empty, oversized, or control-character argv token.");
                continue;
            }

            if (IsKnownPlaceholder(token))
            {
                if (!allowedPlaceholders.Contains(token, StringComparer.Ordinal))
                {
                    failures.Add(
                        $"Placeholder '{token}' is not allowed in the {operationName} operation.");
                }
                continue;
            }

            if (token.Contains('{') || token.Contains('}'))
            {
                failures.Add(
                    $"The {operationName} operation argv token '{token}' contains an unknown or embedded placeholder.");
                continue;
            }

            if (IsShellExecutable(token))
            {
                failures.Add(
                    $"The {operationName} operation cannot invoke shell token '{token}'.");
            }
        }
    }

    private static void ValidateOperationExecutable(
        string? executable,
        string operationName,
        List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(executable) ||
            executable.Length > 512 ||
            executable.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character)) ||
            executable.Contains('{') ||
            executable.Contains('}') ||
            executable.Contains('\\'))
        {
            failures.Add(
                $"The {operationName} operation executable must be a fixed Linux command name or absolute path.");
            return;
        }

        if (executable.Contains('/'))
        {
            if (!executable.StartsWith('/') ||
                executable.Split('/').Skip(1).Any(static segment => segment is "" or "." or ".."))
            {
                failures.Add(
                    $"The {operationName} operation executable must be a normalized absolute Linux path.");
            }
        }
        else if (executable.Any(static character =>
                     !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
        {
            failures.Add(
                $"The {operationName} operation executable command name is invalid.");
        }

        if (IsShellExecutable(executable))
            failures.Add($"The {operationName} operation cannot invoke shell executable '{executable}'.");
    }

    private static void RequirePlaceholderExactlyOnce(
        RuntimeOperationCommandDefinition? command,
        string placeholder,
        string operationName,
        List<string> failures)
    {
        if (command?.Argv is null)
            return;
        var count = command.Argv.Count(token => StringComparer.Ordinal.Equals(token, placeholder));
        if (count != 1)
        {
            failures.Add(
                $"The {operationName} operation must contain '{placeholder}' exactly once.");
        }
    }

    private static void RequirePlaceholderAtMostOnce(
        RuntimeOperationCommandDefinition? command,
        string placeholder,
        string operationName,
        List<string> failures)
    {
        if (command?.Argv is null)
            return;
        if (command.Argv.Count(token => StringComparer.Ordinal.Equals(token, placeholder)) > 1)
        {
            failures.Add(
                $"The {operationName} operation can contain '{placeholder}' at most once.");
        }
    }

    private static void RequireDynamicPlaceholderLastAndAfterEntry(
        RuntimeOperationCommandDefinition? command,
        string placeholder,
        string operationName,
        List<string> failures)
    {
        if (command?.Argv is null)
            return;
        var placeholderIndex = command.Argv.FindIndex(token =>
            StringComparer.Ordinal.Equals(token, placeholder));
        if (placeholderIndex < 0)
            return;
        var entryAssemblyIndex = command.Argv.FindIndex(token =>
            StringComparer.Ordinal.Equals(token, RuntimeOperationPlaceholders.EntryAssembly));
        if (placeholderIndex <= entryAssemblyIndex || placeholderIndex != command.Argv.Count - 1)
        {
            failures.Add(
                $"The {operationName} operation placeholder '{placeholder}' must follow '{RuntimeOperationPlaceholders.EntryAssembly}' and be the final argv token.");
        }
    }

    private static bool IsKnownPlaceholder(string token) =>
        token is RuntimeOperationPlaceholders.EntryAssembly or
            RuntimeOperationPlaceholders.Arguments or
            RuntimeOperationPlaceholders.MethodFilter;

    private static bool IsShellExecutable(string value)
    {
        var normalized = value.Replace('\\', '/');
        var separator = normalized.LastIndexOf('/');
        var fileName = separator >= 0 ? normalized[(separator + 1)..] : normalized;
        return ShellExecutableNames.Contains(fileName);
    }

    private static void ValidateWineNetFxProfile(
        RuntimeProfileDefinition profile,
        List<string> failures)
    {
        if (!StringComparer.Ordinal.Equals(profile.Family, "netfx-clr-wine"))
            failures.Add("The wine-netfx runner requires runtime family 'netfx-clr-wine'.");
        if (!StringComparer.Ordinal.Equals(profile.Rid, "linux-x64") ||
            !StringComparer.Ordinal.Equals(profile.Architecture, "x64"))
        {
            failures.Add("The wine-netfx runner currently supports only linux-x64/x64.");
        }
        var acceptedFormats = new HashSet<string>(profile.AcceptedArtifactFormats, StringComparer.Ordinal);
        var allowsManaged = acceptedFormats.Contains("dotnet-framework-managed-pe-v1");
        var allowsMixed = acceptedFormats.Contains("dotnet-framework-mixed-pe-v1");
        if (!allowsManaged ||
            acceptedFormats.Any(static format => format is not (
                "dotnet-framework-managed-pe-v1" or
                "dotnet-framework-mixed-pe-v1")) ||
            (allowsMixed && !StringComparer.Ordinal.Equals(profile.Id, "wine-netfx48-linux-x64")))
        {
            failures.Add(
                "The wine-netfx runner requires managed .NET Framework PE; mixed PE is restricted to the audited wine-netfx48 profile.");
        }
        if (profile.Capabilities.Count != 1 ||
            !StringComparer.Ordinal.Equals(profile.Capabilities[0], "run"))
        {
            failures.Add("The wine-netfx runner exposes only the run capability.");
        }
        if (profile.Operations?.Run is { ImplementationId: not (
                RuntimeOperationImplementationIds.TargetRuntimeRunner or
                RuntimeOperationImplementationIds.WineRunner) })
        {
            failures.Add(
                $"The wine-netfx runner requires Run implementation '{RuntimeOperationImplementationIds.TargetRuntimeRunner}' or the active legacy implementation '{RuntimeOperationImplementationIds.WineRunner}'.");
        }
        var prefix = profile.Layout.WinePrefixPath;
        if (prefix is not (
                "/opt/wine-dotnet" or
                "/opt/wine-netfx-clr2" or
                "/opt/wine-netfx-clr4"))
        {
            failures.Add(
                "The wine-netfx runner requires one of the dedicated '/opt/wine-dotnet', '/opt/wine-netfx-clr2', or '/opt/wine-netfx-clr4' prefixes.");
        }
        if (!StringComparer.Ordinal.Equals(profile.Layout.WineHostPath, "/usr/lib/wine/wine64"))
            failures.Add("The wine-netfx runner requires the explicit x64 Wine host '/usr/lib/wine/wine64'.");
        if (StringComparer.Ordinal.Equals(
                profile.Operations?.Run?.ImplementationId,
                RuntimeOperationImplementationIds.TargetRuntimeRunner) &&
            !StringComparer.Ordinal.Equals(
                profile.Layout.RunnerAssemblyPath,
                TargetRuntimeRunnerAssemblyPath))
        {
            failures.Add(
                $"The wine-netfx target-runtime layout requires helper '{TargetRuntimeRunnerAssemblyPath}'.");
        }

        // TargetRuntimeRunner profiles are the data-driven Framework matrix
        // rows.  Unlike the legacy WineRunner/J# profile, their runtime
        // identity is the exact Framework version in RuntimeVersion and the
        // Desktop CLR does not provide a product/runtime commit or a JIT
        // inspector.  Keep this contract fail-closed so a hand-authored
        // profile cannot route a different Framework artifact into the same
        // Wine prefix or advertise unverifiable JIT metadata.
        if (StringComparer.Ordinal.Equals(
                profile.Operations?.Run?.ImplementationId,
                RuntimeOperationImplementationIds.TargetRuntimeRunner))
        {
            ValidateTargetRuntimeFrameworkIdentity(profile, failures);
        }

        if (profile.Container is null ||
            !StringComparer.Ordinal.Equals(profile.Container.IsolationKind, RuntimeContainerIsolationKinds.Wine) ||
            !StringComparer.Ordinal.Equals(profile.Container.EnvironmentKind, RuntimeContainerEnvironmentKinds.Wine) ||
            !StringComparer.Ordinal.Equals(profile.Container.WinePrefixPath, prefix))
        {
            failures.Add("The wine-netfx runner layout and container must use the same dedicated Wine prefix.");
        }
    }

    private static void ValidateTargetRuntimeFrameworkIdentity(
        RuntimeProfileDefinition profile,
        List<string> failures)
    {
        if (!StringComparer.Ordinal.Equals(profile.RuntimeCommit, NotApplicableRuntimeIdentity))
        {
            failures.Add(
                $"The wine-netfx target-runtime profile must set RuntimeCommit to '{NotApplicableRuntimeIdentity}'.");
        }
        if (!StringComparer.Ordinal.Equals(profile.JitVersion, NotApplicableRuntimeIdentity))
        {
            failures.Add(
                $"The wine-netfx target-runtime profile must set JitVersion to '{NotApplicableRuntimeIdentity}'.");
        }
        if (!StringComparer.Ordinal.Equals(profile.JitCommit, NotApplicableRuntimeIdentity))
        {
            failures.Add(
                $"The wine-netfx target-runtime profile must set JitCommit to '{NotApplicableRuntimeIdentity}'.");
        }
        if (!StringComparer.Ordinal.Equals(profile.Layout.DotNetHostPath, "/usr/lib/wine/wine64"))
        {
            failures.Add(
                "The wine-netfx target-runtime layout requires the fixed x64 Wine host '/usr/lib/wine/wine64' as DotNetHostPath.");
        }

        var acceptedFrameworks = profile.AcceptedFrameworks is null
            ? []
            : profile.AcceptedFrameworks
                .Where(static framework =>
                    framework is not null &&
                    StringComparer.Ordinal.Equals(framework.Name, FrameworkRuntimeName))
                .ToArray();
        if (acceptedFrameworks.Length != 1 ||
            !StringComparer.Ordinal.Equals(acceptedFrameworks[0]?.ExactVersion, profile.RuntimeVersion) ||
            acceptedFrameworks[0]?.MinimumVersion is not null ||
            acceptedFrameworks[0]?.MaximumVersion is not null)
        {
            failures.Add(
                $"The wine-netfx target-runtime profile must accept exactly '{FrameworkRuntimeName}' version '{profile.RuntimeVersion}'.");
        }
    }

    private static void ValidateWineCoreClrProfile(
        RuntimeProfileDefinition profile,
        List<string> failures)
    {
        if (!StringComparer.Ordinal.Equals(profile.Family, "coreclr-wine"))
            failures.Add("The wine-coreclr runner requires runtime family 'coreclr-wine'.");
        if (!StringComparer.Ordinal.Equals(profile.Rid, "linux-x64") ||
            !StringComparer.Ordinal.Equals(profile.Architecture, "x64"))
        {
            failures.Add("The wine-coreclr runner currently supports only linux-x64/x64.");
        }
        var acceptedFormats = new HashSet<string>(profile.AcceptedArtifactFormats, StringComparer.Ordinal);
        if (acceptedFormats.Count != 1 ||
            !acceptedFormats.Contains("dotnet-managed-pe-v1"))
        {
            failures.Add("The wine-coreclr runner accepts only dotnet-managed-pe-v1 artifacts.");
        }
        var capabilities = new HashSet<string>(profile.Capabilities, StringComparer.Ordinal);
        if (capabilities.Count == 0 ||
            capabilities.Any(capability => capability is not ("run" or "jit-asm")))
        {
            failures.Add("The wine-coreclr runner exposes only 'run' and optional 'jit-asm' capabilities.");
        }
        if (!StringComparer.Ordinal.Equals(profile.Layout.WinePrefixPath, "/opt/wine-dotnet"))
            failures.Add("The wine-coreclr runner requires the dedicated '/opt/wine-dotnet' prefix.");
        if (!StringComparer.Ordinal.Equals(profile.Layout.WineHostPath, "/usr/lib/wine/wine64"))
            failures.Add("The wine-coreclr runner requires the explicit x64 Wine host '/usr/lib/wine/wine64'.");
        if (profile.Container is null ||
            !StringComparer.Ordinal.Equals(profile.Container.IsolationKind, RuntimeContainerIsolationKinds.Wine) ||
            !StringComparer.Ordinal.Equals(profile.Container.EnvironmentKind, RuntimeContainerEnvironmentKinds.Wine) ||
            !StringComparer.Ordinal.Equals(profile.Container.WinePrefixPath, "/opt/wine-dotnet"))
        {
            failures.Add("The wine-coreclr runner requires a Wine container using '/opt/wine-dotnet'.");
        }
        if (profile.Operations?.Jit is { SourceMappingKind: not RuntimeJitSourceMappingKinds.None })
            failures.Add("The wine-coreclr runner does not support source-mapped JIT operations.");
        if (profile.Operations?.Run is { ImplementationId: not RuntimeOperationImplementationIds.LegacyJitInspector })
        {
            failures.Add(
                $"The wine-coreclr runner requires Run implementation '{RuntimeOperationImplementationIds.LegacyJitInspector}'.");
        }
        if (profile.Operations?.Jit is { ImplementationId: not RuntimeOperationImplementationIds.LegacyJitInspector })
        {
            failures.Add(
                $"The wine-coreclr runner requires JIT implementation '{RuntimeOperationImplementationIds.LegacyJitInspector}'.");
        }
    }

    private static void ValidateWineJSharp20Profile(
        RuntimeProfileDefinition profile,
        List<string> failures)
    {
        if (!StringComparer.Ordinal.Equals(profile.Id, "wine-jsharp20-linux-x64"))
            failures.Add("The wine-jsharp20 runner requires profile ID 'wine-jsharp20-linux-x64'.");
        if (!StringComparer.Ordinal.Equals(profile.Family, "netfx-clr-wine"))
            failures.Add("The wine-jsharp20 runner requires runtime family 'netfx-clr-wine'.");
        if (!StringComparer.Ordinal.Equals(profile.Rid, "linux-x64") ||
            !StringComparer.Ordinal.Equals(profile.Architecture, "x64"))
        {
            failures.Add("The wine-jsharp20 runner supports only linux-x64/x64.");
        }
        if (profile.AcceptedArtifactFormats.Count != 1 ||
            !StringComparer.Ordinal.Equals(
                profile.AcceptedArtifactFormats[0],
                "dotnet-framework-managed-pe-v1"))
        {
            failures.Add("The wine-jsharp20 runner accepts only managed .NET Framework PE artifacts.");
        }
        if (profile.Capabilities.Count != 1 ||
            !StringComparer.Ordinal.Equals(profile.Capabilities[0], "run"))
        {
            failures.Add("The wine-jsharp20 runner exposes only the run capability.");
        }
        if (profile.ProvidedRuntimeFeatureTags.Count != 1 ||
            !StringComparer.Ordinal.Equals(
                profile.ProvidedRuntimeFeatureTags[0],
                "runtime.jsharp20-wine"))
        {
            failures.Add("The wine-jsharp20 runner must provide only 'runtime.jsharp20-wine'.");
        }
        if (profile.ProvidedMetadataFeatureTags.Count != 0)
            failures.Add("The wine-jsharp20 runner cannot provide metadata feature tags.");
        if (!StringComparer.Ordinal.Equals(profile.Layout.WinePrefixPath, "/opt/wine-jsharp20"))
            failures.Add("The wine-jsharp20 runner requires the dedicated '/opt/wine-jsharp20' prefix.");
        if (!StringComparer.Ordinal.Equals(profile.Layout.WineHostPath, "/usr/lib/wine/wine64"))
            failures.Add("The wine-jsharp20 runner requires the explicit x64 Wine host '/usr/lib/wine/wine64'.");
        if (profile.AllowedSecurityPolicyIds.Count != 1 ||
            !StringComparer.Ordinal.Equals(
                profile.AllowedSecurityPolicyIds[0],
                "runtime-job-wine-jsharp20"))
        {
            failures.Add("The wine-jsharp20 runner requires only security policy 'runtime-job-wine-jsharp20'.");
        }
        if (profile.Operations?.Run is { ImplementationId: not RuntimeOperationImplementationIds.WineRunner })
            failures.Add($"The wine-jsharp20 runner requires Run implementation '{RuntimeOperationImplementationIds.WineRunner}'.");
    }

    public static IReadOnlyList<string> Validate(RuntimeSecurityPolicyDefinition policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var failures = new List<string>();
        RequireStableId(policy.Id, "security policy ID", failures);
        Positive(policy.MemoryBytes, $"{policy.Id}.MemoryBytes", failures);
        Positive(policy.NanoCpus, $"{policy.Id}.NanoCpus", failures);
        Positive(policy.PidsLimit, $"{policy.Id}.PidsLimit", failures);
        Positive(policy.MaximumDurationSeconds, $"{policy.Id}.MaximumDurationSeconds", failures);
        Positive(policy.MaximumArtifactBytes, $"{policy.Id}.MaximumArtifactBytes", failures);
        Positive(policy.MaximumOutputBytes, $"{policy.Id}.MaximumOutputBytes", failures);
        Positive(policy.TmpfsBytes, $"{policy.Id}.TmpfsBytes", failures);
        return failures;
    }

    public static bool IsImmutableImageReference(string? value) =>
        value is not null &&
        (IsSha256(value) ||
        value.LastIndexOf("@sha256:", StringComparison.Ordinal) is var separator &&
        separator > 0 &&
        IsSha256(value[(separator + 1)..]));

    public static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal))
            return false;
        foreach (var character in value.AsSpan(7))
        {
            if (!(character is >= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
        }
        return true;
    }

    private static void RequireNonEmptyDistinct(
        List<string> values,
        string description,
        List<string> failures)
    {
        if (values.Count == 0)
            failures.Add($"At least one {description} must be declared.");
        RequireDistinct(values, description, failures);
    }

    private static void RequireDistinct(
        IEnumerable<string> values,
        string description,
        List<string> failures)
    {
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            RequireStableId(value, description, failures);
            if (!string.IsNullOrWhiteSpace(value) && !observed.Add(value))
                failures.Add($"Duplicate {description} '{value}'.");
        }
    }

    private static void RequireStableId(string? value, string description, List<string> failures)
    {
        Require(value, description, failures);
        if (string.IsNullOrWhiteSpace(value))
            return;
        if (value.Length > 128 || value.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
        {
            failures.Add($"The {description} '{value}' is not a stable ID.");
        }
    }

    private static void Require(string? value, string description, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
            failures.Add($"The {description} must be non-empty and cannot contain NUL characters.");
    }

    private static void ValidateCommand(
        string value,
        string description,
        bool allowCommandName,
        List<string> failures)
    {
        Require(value, description, failures);
        if (string.IsNullOrWhiteSpace(value))
            return;
        if (value.Contains('\\') || value.Contains("..", StringComparison.Ordinal) ||
            (!allowCommandName && !value.StartsWith('/')) ||
            (allowCommandName && value.Contains('/') && !value.StartsWith('/')))
        {
            failures.Add($"The {description} path is invalid for a Linux runtime image.");
        }
    }

    private static void Positive(long value, string description, List<string> failures)
    {
        if (value <= 0)
            failures.Add($"{description} must be positive.");
    }

    private sealed record ParsedFrameworkVersion(string[] Release, string[] Prerelease);
}
