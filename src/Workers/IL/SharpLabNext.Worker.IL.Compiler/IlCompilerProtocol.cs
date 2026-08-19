using System.Reflection;
using System.Text.Json;
using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.IL.Compiler;

public static class IlCompilerProtocol
{
    public const int Version = 1;
    public static string PackageVersion { get; } = typeof(IlCompilerProtocol).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => string.Equals(
            attribute.Key,
            "SharpLabNext.MobiusILAsmVersion",
            StringComparison.Ordinal))
        .Value!;
    public const int MaxRequestBytes = 4 * 1024 * 1024;
    public const int MaxResponseBytes = 1024 * 1024;
    public const int MaxSources = 64;
    public const int MaxDiagnostics = 1_000;
    public const int MaxPeBytes = 64 * 1024 * 1024;

    // The compiler is an isolated SharpLabNext child, but its request,
    // response, and health descriptor are still our own interaction protocol.
    // Keep one strict PascalCase configuration shared by both processes.
    public static JsonSerializerOptions JsonOptions { get; } =
        ContractJson.CreateSerializerOptions();
}

public sealed record IlCompilerRequest(
    int ProtocolVersion,
    string Target,
    int MaxPeBytes,
    IReadOnlyList<IlCompilerSource> Sources);

public sealed record IlCompilerSource(string Path, string Text);

public sealed record IlCompilerResponse(
    int ProtocolVersion,
    bool Succeeded,
    IReadOnlyList<IlCompilerDiagnostic> Diagnostics,
    string? FailureKind = null);

public sealed record IlCompilerDiagnostic(
    IlCompilerDiagnosticSeverity Severity,
    string Code,
    string Message,
    string? FilePath,
    int? StartLine,
    int? StartCharacter,
    int? EndLine,
    int? EndCharacter);

public enum IlCompilerDiagnosticSeverity
{
    Information,
    Warning,
    Error
}

public sealed record IlCompilerDescriptor(
    int ProtocolVersion,
    string Toolchain,
    string PackageVersion,
    string AssemblyVersion);
