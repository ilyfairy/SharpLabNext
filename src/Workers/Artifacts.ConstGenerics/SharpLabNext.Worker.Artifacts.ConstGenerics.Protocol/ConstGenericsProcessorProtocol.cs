using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.Artifacts.ConstGenerics.Protocol;

public static class ConstGenericsProcessorProtocol
{
    public const int Version = 1;
    public static string IlSpyCommit { get; } =
        RequiredAssemblyMetadata("SharpLabNext.ConstGenericsIlSpyCommit");
    public static string RuntimeCommit { get; } =
        RequiredAssemblyMetadata("SharpLabNext.ConstGenericsRuntimeCommit");
    public const string MetadataFeatureTag = "metadata.const-generics.v1";
    public const string RuntimeFeatureTag = "runtime.const-generics.v1";
    public const string CompatibilityGroup = "const-generics-bcaed316";
    public static string IlSpyProcessorVersion { get; } =
        RequiredAssemblyMetadata("SharpLabNext.ConstGenericsIlSpyProcessorVersion");
    public static string VerificationProcessorVersion { get; } =
        RequiredAssemblyMetadata("SharpLabNext.ConstGenericsVerificationProcessorVersion");
    public const int MaximumRequestBytes = 256 * 1024;
    public const int MaximumResponseBytes = 4 * 1024 * 1024;
    public const int MaximumFindings = 5_000;
    public const int MaximumLinkedRanges = 20_000;

    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = ContractJson.CreateSerializerOptions();
        options.MaxDepth = 32;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }

    private static string RequiredAssemblyMetadata(string key) =>
        typeof(ConstGenericsProcessorProtocol).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))
            ?.Value is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException($"Assembly metadata '{key}' is missing.");
}

public sealed record ConstGenericsProcessorDescriptor(
    int ProtocolVersion,
    string IlSpyCommit,
    string RuntimeCommit,
    string MetadataFeatureTag,
    string CompatibilityGroup,
    IReadOnlyList<string> Operations);

public sealed record ConstGenericsProcessorRequest(
    int ProtocolVersion,
    ConstGenericsProcessorOperation Operation,
    string AssemblyPath,
    string? PortablePdbPath,
    string OutputPath,
    IReadOnlyList<string> ReferenceRoots,
    string? SystemModuleName,
    bool IncludeSequencePoints,
    bool IncludeCompilerGeneratedMembers,
    bool IncludeMetadataTokens,
    int MaxCharacters,
    int MaxFindings);

public sealed record ConstGenericsProcessorResponse(
    int ProtocolVersion,
    ConstGenericsProcessorOutcome Outcome,
    string ProcessorId,
    string ProcessorVersion,
    string MediaType,
    long OutputCharacters,
    IReadOnlyList<ConstGenericsProcessorLinkedRange> LinkedRanges,
    IReadOnlyList<ConstGenericsProcessorFinding> Findings,
    bool Truncated,
    string? PublicMessage);

public sealed record ConstGenericsProcessorLinkedRange(
    string? SourceFilePath,
    ConstGenericsProcessorTextRange? SourceRange,
    ConstGenericsProcessorTextRange OutputRange);

public sealed record ConstGenericsProcessorFinding(
    string Code,
    string Message,
    string? TypeName,
    string? MethodName,
    int? MetadataToken,
    string? FilePath,
    ConstGenericsProcessorTextRange? Range);

public sealed record ConstGenericsProcessorTextRange(
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter);

public enum ConstGenericsProcessorOperation
{
    Il,
    DecompiledCSharp,
    Verify
}

public enum ConstGenericsProcessorOutcome
{
    Succeeded,
    Findings,
    InvalidArtifact,
    LimitExceeded,
    Failed
}
