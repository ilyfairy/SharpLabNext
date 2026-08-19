using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using SharpLabNext.Contracts;

namespace SharpLabNext.ArtifactProcessing.Protocol;

public static class ProcessorProtocol
{
    public const int Version = 1;
    public static string IlSpyVersion { get; } = RequiredAssemblyMetadata("SharpLabNext.ILSpyVersion");
    public static string IlVerificationVersion { get; } = RequiredAssemblyMetadata("SharpLabNext.ILVerificationVersion");
    public const string RuntimeInstrumentationVersion = "1.0.0";
    public const string RuntimeInstrumentationProfileId = "execution-flow-v1";

    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = ContractJson.CreateSerializerOptions();
        options.MaxDepth = 32;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }

    private static string RequiredAssemblyMetadata(string key) =>
        typeof(ProcessorProtocol).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))
            ?.Value is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException($"Assembly metadata '{key}' is missing.");
}

public sealed record ProcessorRequest(
    int ProtocolVersion,
    ProcessorOperation Operation,
    string AssemblyPath,
    string? PortablePdbPath,
    string OutputPath,
    IReadOnlyList<string> ReferenceRoots,
    string? SystemModuleName,
    bool IncludeSequencePoints,
    bool IncludeCompilerGeneratedMembers,
    bool IncludeMetadataTokens,
    int MaxCharacters,
    int MaxFindings,
    string? RewriterProfileId = null,
    string? PortablePdbOutputPath = null,
    string ArtifactFormat = "dotnet-managed-pe-v1");

public sealed record ProcessorResponse(
    int ProtocolVersion,
    ProcessorOutcome Outcome,
    string ProcessorId,
    string ProcessorVersion,
    string MediaType,
    long OutputCharacters,
    IReadOnlyList<ProcessorLinkedRange> LinkedRanges,
    IReadOnlyList<ProcessorFinding> Findings,
    bool Truncated,
    string? PublicMessage,
    bool? RewriteApplied = null,
    int? InstrumentationPointCount = null);

public sealed record ProcessorLinkedRange(
    string? SourceFilePath,
    ProcessorTextRange? SourceRange,
    ProcessorTextRange OutputRange);

public sealed record ProcessorFinding(
    string Code,
    string Message,
    string? TypeName,
    string? MethodName,
    int? MetadataToken,
    string? FilePath,
    ProcessorTextRange? Range);

public sealed record ProcessorTextRange(
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter);

public enum ProcessorOperation
{
    [JsonStringEnumMemberName("il")]
    Il,
    [JsonStringEnumMemberName("decompiled-csharp")]
    DecompiledCSharp,
    [JsonStringEnumMemberName("verify")]
    Verify,
    [JsonStringEnumMemberName("runtime-instrumentation-v1")]
    RuntimeInstrumentationV1
}

public enum ProcessorOutcome
{
    [JsonStringEnumMemberName("succeeded")]
    Succeeded,
    [JsonStringEnumMemberName("findings")]
    Findings,
    [JsonStringEnumMemberName("invalid-artifact")]
    InvalidArtifact,
    [JsonStringEnumMemberName("limit-exceeded")]
    LimitExceeded,
    [JsonStringEnumMemberName("failed")]
    Failed
}
