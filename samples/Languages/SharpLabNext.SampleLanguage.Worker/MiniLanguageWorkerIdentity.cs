using SharpLabNext.Contracts;

namespace SharpLabNext.SampleLanguage.Worker;

public sealed record MiniLanguageWorkerIdentity(
    string ReleaseId,
    string ToolchainId,
    string CompilerVersion,
    string? CompilerCommit,
    string WorkerImageId)
{
    public BuildIdentity CreateBuildIdentity(string referenceSetId) => new(
        ReleaseId,
        MiniLanguageCompiler.LanguageId,
        ToolchainId,
        CompilerVersion,
        CompilerCommit,
        referenceSetId,
        WorkerImageId);
}
