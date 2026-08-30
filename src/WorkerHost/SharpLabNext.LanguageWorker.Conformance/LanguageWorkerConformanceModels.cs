using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Sdk;

namespace SharpLabNext.LanguageWorker.Conformance;

public sealed record LanguageWorkerConformanceScenario(
    ServiceIdentity ExpectedIdentity,
    string ExpectedWorkerImageId,
    LanguageWorkerCapabilityManifest ExpectedManifest,
    BuildRequest CompileCheckRequest,
    BuildRequest ArtifactRequest,
    OpenLanguageSessionRequest LanguageSessionRequest,
    string DocumentUri,
    string OpenText,
    string ChangedText,
    LanguageWorkerCompletionPosition CompletionPosition,
    string ExpectedCompletionLabel,
    string ExpectedOpenDiagnosticCode);

public sealed record LanguageWorkerCompletionPosition(int Line, int Character);

public sealed record LanguageWorkerConformanceReport(IReadOnlyList<string> PassedChecks)
{
    public bool Succeeded => PassedChecks.Count == 6;
}

public sealed class LanguageWorkerConformanceException(string check, string message, Exception? innerException = null) : Exception($"Language worker conformance check '{check}' failed: {message}", innerException)
{
    public string Check { get; } = check;
}
