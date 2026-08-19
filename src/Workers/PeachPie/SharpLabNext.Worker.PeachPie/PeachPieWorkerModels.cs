using SharpLabNext.Contracts;

namespace SharpLabNext.Worker.PeachPie;

public sealed record PeachPieCompilerResponse(
    int CompilerProcessId,
    bool CompilationSucceeded,
    bool EmitSucceeded,
    byte[] PeImage,
    IReadOnlyList<Diagnostic> Diagnostics);

public class PeachPieWorkerException : Exception
{
    public PeachPieWorkerException(string message) : base(message) { }
    public PeachPieWorkerException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class PeachPieBuildRequestValidationException(string message) : PeachPieWorkerException(message);

public sealed class PeachPieReferenceSetUnavailableException : PeachPieWorkerException
{
    public PeachPieReferenceSetUnavailableException(string message) : base(message) { }
    public PeachPieReferenceSetUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class PeachPieCompilerFailureException(string message) : PeachPieWorkerException(message);
public sealed class PeachPieBuildOutputLimitExceededException(string message) : PeachPieWorkerException(message);
