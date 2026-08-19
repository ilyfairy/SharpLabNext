using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SharpLabNext.CheckedJitBridge;

internal sealed class JitMethodResult
{
    public JitMethodResult(
        string method,
        int metadataToken,
        string displayName,
        string status,
        string? address,
        string? error,
        JitMethodSignatureIdentity signatureIdentity)
    {
        Method = method;
        MetadataToken = metadataToken;
        DisplayName = displayName;
        Status = status;
        Address = address;
        Error = error;
        SignatureIdentity = signatureIdentity;
        LinkedRanges = new List<JitLinkedRange>();
        MappingSource = "none";
    }

    public string Method { get; }

    [JsonIgnore]
    public int MetadataToken { get; }

    [JsonIgnore]
    public JitMethodSignatureIdentity SignatureIdentity { get; }

    public string DisplayName { get; }

    public string Status { get; }

    public string? Address { get; }

    public string? Error { get; }

    public int NativeCodeSize { get; set; }

    public int InstructionCount { get; set; }

    public List<JitLinkedRange> LinkedRanges { get; set; }

    public string MappingSource { get; set; }
}

internal sealed record JitLinkedRange(
    string SourceFilePath,
    JitTextRange SourceRange,
    JitTextRange OutputRange,
    string Precision);

internal sealed record JitTextRange(
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter);

internal sealed record CheckedSourcePoint(
    int IlOffset,
    string? DocumentPath,
    JitTextRange? SourceRange);

internal sealed record CheckedMethodSourceMap(
    int IlLength,
    IReadOnlyList<CheckedSourcePoint> Points);

internal sealed record CheckedJitMappingSelection(
    IReadOnlyList<JitLinkedRange> Ranges,
    string Source);
