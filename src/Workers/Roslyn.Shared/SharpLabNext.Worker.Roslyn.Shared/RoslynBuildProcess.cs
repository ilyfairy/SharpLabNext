using System.Diagnostics;
using SharpLabNext.Contracts;
using SharpLabNext.Observability;
using SharpLabNext.WorkerHost;

namespace SharpLabNext.Worker.Roslyn;

public interface IRoslynBuildExecutor
{
    Task<WorkerBuildExecution> ExecuteAsync(
        BuildRequest request,
        CancellationToken cancellationToken);
}

public sealed class RoslynBuildProcessExecutor(
    RoslynBuildService inProcessBuildService,
    ICompilerProcessRunner processRunner,
    RoslynWorkerSettings settings) : IRoslynBuildExecutor
{
    public async Task<WorkerBuildExecution> ExecuteAsync(
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
        catch (BuildDeadlineExceededException)
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
        catch (BuildOutputLimitExceededException)
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

    private async Task<WorkerBuildExecution> ExecuteCoreAsync(
        BuildRequest request,
        CancellationToken cancellationToken)
    {
        if (!settings.BuildProcess.Enabled)
            return await inProcessBuildService.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);

        var timeout = EffectiveTimeout(request.DeadlineUtc, settings.CompilationLimits.MaxBuildMilliseconds);
        try
        {
            var execution = await processRunner.RunAsync<BuildRequest, WorkerBuildExecution>(
                RoslynBuildChild.ChildArgument,
                request,
                timeout,
                cancellationToken).ConfigureAwait(false);
            ValidateExecution(request, execution);
            return execution;
        }
        catch (CompilerProcessTimeoutException)
        {
            throw new BuildDeadlineExceededException("The Roslyn compiler process deadline elapsed.", cancellationToken);
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

    internal static void ValidateExecution(BuildRequest request, WorkerBuildExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        var identity = execution.Result switch
        {
            BuildResult result => result.Identity,
            CompilationCheckResult result => result.Identity,
            AstResult result => result.Identity,
            GeneratedSourceResult result => result.Identity,
            _ => throw new CompilerProcessProtocolException(
                "The Roslyn compiler process returned an unexpected result type.")
        };
        if (identity is not null &&
            (!StringComparer.Ordinal.Equals(identity.ToolchainId, request.ToolchainId) ||
             !StringComparer.Ordinal.Equals(identity.ReferenceSetId, request.ReferenceSetId) ||
             !StringComparer.Ordinal.Equals(identity.LanguageId, request.Workspace.LanguageId)))
        {
            throw new CompilerProcessProtocolException(
                "The Roslyn compiler process returned a mismatched build identity.");
        }

        if (execution.Artifact is null)
        {
            if (execution.Result is BuildResult { Outcome: BuildOutcome.Succeeded })
            {
                throw new CompilerProcessProtocolException(
                    "The Roslyn compiler process omitted a successful build artifact.");
            }
            return;
        }

        RoslynArtifactPublisher.ValidateArtifact(execution.Artifact);
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
                "The Roslyn compiler process artifact did not match its typed result.");
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
                new BuildRequestValidationException(exception.PublicMessage),
            CompilerChildFailureKind.ReferenceSetUnavailable =>
                new ReferenceSetUnavailableException(exception.PublicMessage),
            CompilerChildFailureKind.CompilerIdentityMismatch =>
                new CompilerIdentityMismatchException(exception.PublicMessage),
            CompilerChildFailureKind.OutputLimitExceeded =>
                new BuildOutputLimitExceededException(exception.PublicMessage),
            CompilerChildFailureKind.DeadlineExceeded =>
                new BuildDeadlineExceededException(exception.PublicMessage, cancellationToken),
            _ => new CompilerProcessProtocolException(
                "The Roslyn compiler process reported an internal compiler failure.")
        };
}

public static class RoslynBuildChild
{
    public const string ChildArgument = "--sharplabnext-roslyn-build-child";

    public static bool IsInvocation(string[] args) =>
        args.Length == 1 && StringComparer.Ordinal.Equals(args[0], ChildArgument);

    public static async Task RunAsync(WebApplicationBuilder builder)
    {
        await using var app = RoslynWorkerHost.Build(builder, configureObservability: false);
        var settings = app.Services.GetRequiredService<RoslynWorkerSettings>();
        var output = Console.OpenStandardOutput();
        try
        {
            var request = await CompilerChildProtocol.ReadRequestAsync<BuildRequest>(
                Console.OpenStandardInput(),
                settings.BuildProcess.MaximumRequestBytes,
                CancellationToken.None).ConfigureAwait(false);
            var execution = await app.Services.GetRequiredService<RoslynBuildService>()
                .ExecuteAsync(request, CancellationToken.None)
                .ConfigureAwait(false);
            await CompilerChildProtocol.WriteSuccessAsync(
                output,
                execution,
                settings.BuildProcess.MaximumResponseBytes,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (BuildRequestValidationException exception)
        {
            await WriteFailureAsync(CompilerChildFailureKind.InvalidRequest, exception.Message);
        }
        catch (ReferenceSetUnavailableException exception)
        {
            await WriteFailureAsync(CompilerChildFailureKind.ReferenceSetUnavailable, exception.Message);
        }
        catch (CompilerIdentityMismatchException exception)
        {
            await WriteFailureAsync(CompilerChildFailureKind.CompilerIdentityMismatch, exception.Message);
        }
        catch (BuildOutputLimitExceededException exception)
        {
            await WriteFailureAsync(CompilerChildFailureKind.OutputLimitExceeded, exception.Message);
        }
        catch (BuildDeadlineExceededException exception)
        {
            await WriteFailureAsync(CompilerChildFailureKind.DeadlineExceeded, exception.Message);
        }
        catch (OperationCanceledException)
        {
            await WriteFailureAsync(
                CompilerChildFailureKind.DeadlineExceeded,
                "The Roslyn compiler process deadline elapsed.");
        }
        catch (Exception)
        {
            await WriteFailureAsync(
                CompilerChildFailureKind.Internal,
                "The Roslyn compiler process failed.");
        }

        async Task WriteFailureAsync(CompilerChildFailureKind kind, string message) =>
            await CompilerChildProtocol.WriteFailureAsync<WorkerBuildExecution>(
                output,
                kind,
                message,
                settings.BuildProcess.MaximumResponseBytes,
                CancellationToken.None).ConfigureAwait(false);
    }
}
