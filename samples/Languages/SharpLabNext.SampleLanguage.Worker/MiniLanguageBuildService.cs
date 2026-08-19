using System.Diagnostics;
using System.Text;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Sdk;
using SharpLabNext.Observability;

namespace SharpLabNext.SampleLanguage.Worker;

public sealed class MiniLanguageBuildService(
    MiniLanguageWorkerIdentity workerIdentity,
    LanguageWorkerCapabilityManifest manifest) : ILanguageWorkerBuildService
{
    public Task<LanguageWorkerBuildExecution> BuildAsync(
        BuildRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var outcome = SharpLabNextTelemetryOutcome.Failed;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var execution = Build(request);
            outcome = TelemetryOutcome(execution.Result);
            return Task.FromResult(execution);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = SharpLabNextTelemetryOutcome.Cancelled;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            SharpLabNextTelemetry.Metrics.RecordBuild(
                MiniLanguageCompiler.LanguageId,
                workerIdentity.ToolchainId,
                stopwatch.Elapsed,
                outcome,
                cacheHit: false);
        }
    }

    private LanguageWorkerBuildExecution Build(BuildRequest request)
    {
        var options = request.EffectiveOptions;
        ValidateOutputKind(options.OutputKind);
        var compilation = MiniLanguageCompiler.Compile(request.Workspace with { BuildOptions = options });
        var identity = workerIdentity.CreateBuildIdentity(request.ReferenceSetId);
        if (request.Target == BuildTarget.CompileCheck)
        {
            OperationResult result = new CompilationCheckResult(
                compilation.Succeeded,
                compilation.Diagnostics,
                identity,
                request.Workspace.Revision,
                request.Workspace.SelectionRevision);
            return new LanguageWorkerBuildExecution(result);
        }

        if (!compilation.Succeeded)
        {
            OperationResult failed = new BuildResult(
                BuildOutcome.CompilationFailed,
                null,
                compilation.Diagnostics,
                identity,
                request.Workspace.Revision,
                request.Workspace.SelectionRevision);
            return new LanguageWorkerBuildExecution(failed);
        }

        var targetFramework = request.ReferenceSetId switch
        {
            "net10-ref" => "net10.0",
            "net11-preview-ref" => "net11.0",
            _ => throw new LanguageWorkerRequestException(
                "unsupported-reference-set",
                "MiniLang only supports the declared .NET reference sets.")
        };
        var definition = new LanguageArtifactDefinition(
            MiniLanguageCompiler.ArtifactFormat,
            "MiniLanguageProgram",
            request.ReferenceSetId,
            targetFramework,
            new ArtifactRuntimeRequirement(
                "none",
                [],
                "any",
                []),
            ["cil.ecma-335"],
            options.OutputKind,
            MiniLanguageCompiler.GeneratedFileName,
            options.OutputKind == BuildOutputKind.Console ? "Program::Main" : null,
            [new LanguageArtifactFile(
                "generated-il",
                MiniLanguageCompiler.GeneratedFileName,
                Encoding.UTF8.GetBytes(compilation.GeneratedCil!))],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sourceLanguage"] = MiniLanguageCompiler.LanguageId,
                ["intermediateKind"] = MiniLanguageCompiler.ArtifactFormat
            });
        var envelope = LanguageArtifactBuilder.CreateGenericEnvelope(
            definition,
            identity,
            manifest.Limits.MaximumArtifactBytes);
        OperationResult succeeded = new BuildResult(
            BuildOutcome.Succeeded,
            envelope.ArtifactRef,
            compilation.Diagnostics,
            identity,
            request.Workspace.Revision,
            request.Workspace.SelectionRevision);
        return new LanguageWorkerBuildExecution(succeeded, envelope);
    }

    internal static void ValidateOutputKind(BuildOutputKind outputKind)
    {
        if (outputKind is not (BuildOutputKind.Console or BuildOutputKind.Library))
        {
            throw new LanguageWorkerRequestException(
                "unsupported-output-kind",
                "MiniLang supports console and library outputs only.");
        }
    }

    private static SharpLabNextTelemetryOutcome TelemetryOutcome(OperationResult result) => result switch
    {
        BuildResult { Outcome: BuildOutcome.Succeeded } => SharpLabNextTelemetryOutcome.Succeeded,
        CompilationCheckResult { CompilationSucceeded: true } => SharpLabNextTelemetryOutcome.Succeeded,
        BuildResult or CompilationCheckResult => SharpLabNextTelemetryOutcome.Failed,
        _ => SharpLabNextTelemetryOutcome.Succeeded
    };
}
