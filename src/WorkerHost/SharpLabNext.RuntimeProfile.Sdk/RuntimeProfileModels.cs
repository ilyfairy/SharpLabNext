using System.Text.Json.Serialization;

namespace SharpLabNext.RuntimeProfile.Sdk;

public class RuntimeProfileDefinition
{
    public int SchemaVersion { get; set; } = 1;

    public string Id { get; set; } = string.Empty;

    public string Image { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimePromotionReceiptReference? PromotionReceipt { get; set; }

    public string Family { get; set; } = "coreclr";

    /// <summary>When empty, only <see cref="Family"/> is accepted.</summary>
    public List<string> AcceptedRuntimeFamilies { get; set; } = [];

    /// <summary>Framework compatibility allowlist. Empty accepts only framework-free artifacts.</summary>
    public List<RuntimeFrameworkCompatibilityDefinition> AcceptedFrameworks { get; set; } = [];

    public string RuntimeVersion { get; set; } = string.Empty;

    public string RuntimeCommit { get; set; } = "unknown";

    public string JitVersion { get; set; } = string.Empty;

    public string JitCommit { get; set; } = "unknown";

    public string RuntimeImageId { get; set; } = string.Empty;

    public string Rid { get; set; } = "linux-x64";

    public string Architecture { get; set; } = "x64";

    public string CpuFeatureProfile { get; set; } = "x64-v2";

    public List<string> AcceptedArtifactFormats { get; set; } = [];

    public List<string> Capabilities { get; set; } = [];

    public List<string> ProvidedRuntimeFeatureTags { get; set; } = [];

    public List<string> ProvidedMetadataFeatureTags { get; set; } = [];

    public List<string> AllowedSecurityPolicyIds { get; set; } = [];

    public RuntimeContainerDefinition Container { get; set; } = new();

    public RuntimeImageLayout Layout { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeProfileOperations? Operations { get; set; }

    public List<RuntimeSecurityPolicyDefinition> SecurityPolicies { get; set; } = [];
}

public sealed class RuntimePromotionReceiptReference
{
    public string Path { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;
}

public sealed class RuntimeProfileOperations
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeRunOperationDefinition? Run { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeJitOperationDefinition? Jit { get; set; }
}

public class RuntimeOperationDefinition
{
    /// <summary>
    /// Stable identity of the trusted helper implementation invoked by this operation.
    /// Validation binds this identity to a fixed executable/argv shape.
    /// </summary>
    public string ImplementationId { get; set; } = string.Empty;

    public string PathStyle { get; set; } = RuntimeOperationPathStyles.Unix;

    public RuntimeOperationCommandDefinition Command { get; set; } = new();
}

public sealed class RuntimeRunOperationDefinition : RuntimeOperationDefinition
{
}

public sealed class RuntimeJitOperationDefinition : RuntimeOperationDefinition
{
    public string SourceMappingKind { get; set; } = RuntimeJitSourceMappingKinds.None;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProfilerPath { get; set; }

}

public sealed class RuntimeOperationCommandDefinition
{
    public string Executable { get; set; } = string.Empty;

    public List<string> Argv { get; set; } = [];
}

public static class RuntimeOperationPathStyles
{
    public const string Unix = "unix";
    public const string WineZ = "wine-z";
}

public static class RuntimeJitSourceMappingKinds
{
    public const string None = "none";
    public const string LinuxProfiler = "linux-profiler";
    public const string CheckedJitDebugInfo = "checked-jit-debug-info";
}

public static class RuntimeOperationPlaceholders
{
    public const string EntryAssembly = "{entryAssembly}";
    public const string Arguments = "{arguments}";
    public const string MethodFilter = "{methodFilter}";
}

public static class RuntimeOperationImplementationIds
{
    public const string Runner = "sharplabnext-runner-v1";
    public const string JitInspector = "sharplabnext-jit-inspector-v1";
    public const string LegacyJitInspector = "sharplabnext-legacy-jit-inspector-v1";
    public const string CheckedJitBridge = "sharplabnext-checked-jit-bridge-v1";
    public const string MonoJitInspector = "sharplabnext-mono-jit-inspector-v1";
    public const string DesktopClrJitInspector = "sharplabnext-desktop-clr-jit-inspector-v1";
    public const string WineRunner = "sharplabnext-wine-runner-v1";
    public const string TargetRuntimeRunner = "sharplabnext-target-runtime-runner-v1";
    public const string DirectRuntime = "sharplabnext-direct-runtime-v1";
}

public sealed class RuntimeContainerDefinition
{
    public string IsolationKind { get; set; } = RuntimeContainerIsolationKinds.Standard;

    public string EnvironmentKind { get; set; } = RuntimeContainerEnvironmentKinds.CoreClr;

    public string ExecutionUser { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WinePrefixPath { get; set; }
}

public static class RuntimeContainerIsolationKinds
{
    public const string Standard = "standard";
    public const string Wine = "wine";
}

public static class RuntimeContainerExecutionUsers
{
    public const string Root = "0:0";
    public const string NonRoot = "1654:1654";
}

public static class RuntimeContainerEnvironmentKinds
{
    public const string CoreClr = "coreclr";
    public const string Mono = "mono";
    public const string Wine = "wine";
}

public sealed class RuntimeFrameworkCompatibilityDefinition
{
    public string Name { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MinimumVersion { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MaximumVersion { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExactVersion { get; set; }
}

public sealed class RuntimeImageLayout
{
    public const string WorkspacePath = "/workspace";
    public const string TemporaryPath = "/tmp";

    public string DotNetHostPath { get; set; } = "dotnet";

    public string RunnerKind { get; set; } = RuntimeRunnerKinds.DotNet;

    public string RunnerAssemblyPath { get; set; } = "/opt/sharplabnext/SharpLabNext.Runner.dll";

    public string? JitInspectorAssemblyPath { get; set; }

    public string WineHostPath { get; set; } = "wine";

    public string? WinePrefixPath { get; set; }
}

public static class RuntimeRunnerKinds
{
    public const string DotNet = "dotnet";
    public const string WineCoreClr = "wine-coreclr";
    public const string WineNetFx = "wine-netfx";
    public const string WineJSharp20 = "wine-jsharp20";
}

public class RuntimeSecurityPolicyDefinition
{
    public string Id { get; set; } = "runtime-job-default";

    public long MemoryBytes { get; set; } = 256L * 1024 * 1024;

    public long NanoCpus { get; set; } = 1_000_000_000;

    public long PidsLimit { get; set; } = 64;

    public int MaximumDurationSeconds { get; set; } = 10;

    public long MaximumArtifactBytes { get; set; } = 64L * 1024 * 1024;

    public long MaximumOutputBytes { get; set; } = 1L * 1024 * 1024;

    public int TmpfsBytes { get; set; } = 32 * 1024 * 1024;
}
