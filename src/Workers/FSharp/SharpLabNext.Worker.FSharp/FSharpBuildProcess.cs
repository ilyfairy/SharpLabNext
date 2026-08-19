using System.Diagnostics;
using SharpLabNext.Contracts;
using SharpLabNext.Observability;
using SharpLabNext.Worker.FSharp.Compiler;
using SharpLabNext.WorkerHost;

namespace SharpLabNext.Worker.FSharp;

public interface IFSharpBuildExecutor
{
    Task<FSharpWorkerBuildExecution> ExecuteAsync(
        BuildRequest request,
        CancellationToken cancellationToken);
}

public sealed class FSharpBuildProcessExecutor(
    FSharpBuildService inProcessBuildService,
    ICompilerProcessRunner processRunner,
    FSharpWorkerSettings settings) : IFSharpBuildExecutor
{
    public async Task<FSharpWorkerBuildExecution> ExecuteAsync(
        BuildRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var outcome = SharpLabNextTelemetryOutcome.Failed;
        try
        {
            var execution = await ExecuteCoreAsync(request, cancellationToken).ConfigureAwait(false);
            outcome = TelemetryOutcome(execution.Result);
            return execution;
        }
        catch (FSharpBuildDeadlineExceededException)
        {
            outcome = SharpLabNextTelemetryOutcome.TimedOut;
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = SharpLabNextTelemetryOutcome.Cancelled;
            throw;
        }
        catch (CompilerProcessMemoryLimitExceededException)
        {
            outcome = SharpLabNextTelemetryOutcome.OutOfMemory;
            throw;
        }
        catch (CompilerProcessCapacityExceededException)
        {
            outcome = SharpLabNextTelemetryOutcome.Overloaded;
            throw;
        }
        catch (FSharpBuildOutputLimitExceededException)
        {
            outcome = SharpLabNextTelemetryOutcome.Overloaded;
            throw;
        }
        catch (CompilerProcessCrashedException)
        {
            outcome = SharpLabNextTelemetryOutcome.Crashed;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            SharpLabNextTelemetry.Metrics.RecordBuild(
                request.Workspace.LanguageId,
                request.ToolchainId,
                stopwatch.Elapsed,
                outcome,
                cacheHit: false);
        }
    }

    private async Task<FSharpWorkerBuildExecution> ExecuteCoreAsync(
        BuildRequest request,
        CancellationToken cancellationToken)
    {
        if (!settings.BuildProcess.Enabled)
            return await inProcessBuildService.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);

        var timeout = EffectiveTimeout(request.DeadlineUtc, settings.CompilationLimits.MaxBuildMilliseconds);
        try
        {
            var execution = await processRunner.RunAsync<BuildRequest, FSharpWorkerBuildExecution>(
                FSharpBuildChild.ChildArgument,
                request,
                timeout,
                cancellationToken).ConfigureAwait(false);
            ValidateExecution(request, execution);
            return execution;
        }
        catch (CompilerProcessTimeoutException)
        {
            throw new FSharpBuildDeadlineExceededException(
                "The F# compiler process deadline elapsed.",
                cancellationToken);
        }
        catch (CompilerChildReportedException exception)
        {
            throw MapFailure(exception, cancellationToken);
        }
    }

    private static SharpLabNextTelemetryOutcome TelemetryOutcome(OperationResult result) => result switch
    {
        BuildResult { Outcome: BuildOutcome.Succeeded } => SharpLabNextTelemetryOutcome.Succeeded,
        CompilationCheckResult { CompilationSucceeded: true } => SharpLabNextTelemetryOutcome.Succeeded,
        BuildResult or CompilationCheckResult => SharpLabNextTelemetryOutcome.Failed,
        _ => SharpLabNextTelemetryOutcome.Succeeded
    };

    internal static void ValidateExecution(BuildRequest request, FSharpWorkerBuildExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        var identity = execution.Result switch
        {
            BuildResult result => result.Identity,
            CompilationCheckResult result => result.Identity,
            AstResult result => result.Identity,
            GeneratedSourceResult result => result.Identity,
            _ => throw new CompilerProcessProtocolException(
                "The F# compiler process returned an unexpected result type.")
        };
        if (identity is not null &&
            (!StringComparer.Ordinal.Equals(identity.ToolchainId, request.ToolchainId) ||
             !StringComparer.Ordinal.Equals(identity.ReferenceSetId, request.ReferenceSetId) ||
             !StringComparer.Ordinal.Equals(identity.LanguageId, request.Workspace.LanguageId)))
        {
            throw new CompilerProcessProtocolException(
                "The F# compiler process returned a mismatched build identity.");
        }

        if (execution.Artifact is null)
        {
            if (execution.Result is BuildResult { Outcome: BuildOutcome.Succeeded })
            {
                throw new CompilerProcessProtocolException(
                    "The F# compiler process omitted a successful build artifact.");
            }
            return;
        }

        FSharpArtifactPublisher.ValidateArtifact(execution.Artifact);
        if (execution.Result is not BuildResult
            {
                Outcome: BuildOutcome.Succeeded,
                ArtifactRef: { } artifactRef
            } buildResult ||
            artifactRef != execution.Artifact.ArtifactRef ||
            buildResult.Identity != execution.Artifact.Identity ||
            !StringComparer.Ordinal.Equals(execution.Artifact.ReferenceSetId, request.ReferenceSetId))
        {
            throw new CompilerProcessProtocolException(
                "The F# compiler process artifact did not match its typed result.");
        }
    }

    private static TimeSpan EffectiveTimeout(DateTimeOffset deadlineUtc, int maximumMilliseconds)
    {
        var remaining = deadlineUtc - DateTimeOffset.UtcNow;
        var maximum = TimeSpan.FromMilliseconds(maximumMilliseconds);
        return remaining < maximum ? remaining : maximum;
    }

    private static Exception MapFailure(
        CompilerChildReportedException exception,
        CancellationToken cancellationToken) => exception.Kind switch
        {
            CompilerChildFailureKind.InvalidRequest =>
                new FSharpBuildRequestValidationException(exception.PublicMessage),
            CompilerChildFailureKind.ReferenceSetUnavailable =>
                new FSharpReferenceSetUnavailableException(exception.PublicMessage),
            CompilerChildFailureKind.OutputLimitExceeded =>
                new FSharpBuildOutputLimitExceededException(exception.PublicMessage),
            CompilerChildFailureKind.DeadlineExceeded =>
                new FSharpBuildDeadlineExceededException(exception.PublicMessage, cancellationToken),
            CompilerChildFailureKind.CompilerFailure =>
                new FSharpCompilerFailureException(exception.PublicMessage),
            _ => new CompilerProcessProtocolException(
                "The F# compiler process reported an internal compiler failure.")
        };
}

public static class FSharpBuildChild
{
    public const string ChildArgument = "--sharplabnext-fsharp-build-child";

    public static bool IsInvocation(string[] args) =>
        args.Length == 1 && StringComparer.Ordinal.Equals(args[0], ChildArgument);

    public static async Task RunAsync(WebApplicationBuilder builder)
    {
        var settings = FSharpWorkerSettings.FromConfiguration(builder.Configuration);
        using var referenceSets = new FSharpReferenceSetProvider(
            settings.ReferenceSets,
            builder.Environment.IsProduction() ||
            builder.Configuration.GetValue("ReferenceSetAttestation:Required", false));
        var buildService = new FSharpBuildService(
            referenceSets,
            new FSharpCompilerFacade(),
            settings);
        var output = Console.OpenStandardOutput();
        try
        {
            var request = await CompilerChildProtocol.ReadRequestAsync<BuildRequest>(
                Console.OpenStandardInput(),
                settings.BuildProcess.MaximumRequestBytes,
                CancellationToken.None).ConfigureAwait(false);
            var execution = await buildService.ExecuteAsync(request, CancellationToken.None).ConfigureAwait(false);
            await CompilerChildProtocol.WriteSuccessAsync(
                output,
                execution,
                settings.BuildProcess.MaximumResponseBytes,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (FSharpBuildRequestValidationException exception)
        {
            await WriteFailureAsync(CompilerChildFailureKind.InvalidRequest, exception.Message);
        }
        catch (FSharpReferenceSetUnavailableException exception)
        {
            await WriteFailureAsync(CompilerChildFailureKind.ReferenceSetUnavailable, exception.Message);
        }
        catch (FSharpBuildOutputLimitExceededException exception)
        {
            await WriteFailureAsync(CompilerChildFailureKind.OutputLimitExceeded, exception.Message);
        }
        catch (FSharpCompilerFailureException exception)
        {
            await WriteFailureAsync(CompilerChildFailureKind.CompilerFailure, exception.Message);
        }
        catch (FSharpBuildDeadlineExceededException exception)
        {
            await WriteFailureAsync(CompilerChildFailureKind.DeadlineExceeded, exception.Message);
        }
        catch (OperationCanceledException)
        {
            await WriteFailureAsync(
                CompilerChildFailureKind.DeadlineExceeded,
                "The F# compiler process deadline elapsed.");
        }
        catch (Exception)
        {
            await WriteFailureAsync(
                CompilerChildFailureKind.Internal,
                "The F# compiler process failed.");
        }

        async Task WriteFailureAsync(CompilerChildFailureKind kind, string message) =>
            await CompilerChildProtocol.WriteFailureAsync<FSharpWorkerBuildExecution>(
                output,
                kind,
                message,
                settings.BuildProcess.MaximumResponseBytes,
                CancellationToken.None).ConfigureAwait(false);
    }
}
