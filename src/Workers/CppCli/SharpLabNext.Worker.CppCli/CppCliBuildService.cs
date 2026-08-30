using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Sdk;
using SharpLabNext.Observability;

namespace SharpLabNext.Worker.CppCli;

public sealed class CppCliBuildService : ILanguageWorkerBuildService
{
    private readonly ICppCliCompilerProcess _compiler;
    private readonly CppCliWorkerSettings _settings;
    private readonly LanguageWorkerCapabilityManifest _manifest;

    public CppCliBuildService(CppCliCompilerProcess compiler, CppCliWorkerSettings settings, LanguageWorkerCapabilityManifest manifest) : this((ICppCliCompilerProcess)compiler, settings, manifest) { }

    internal CppCliBuildService(ICppCliCompilerProcess compiler, CppCliWorkerSettings settings, LanguageWorkerCapabilityManifest manifest)
    {
        _compiler = compiler;
        _settings = settings;
        _manifest = manifest;
    }

    public async Task<LanguageWorkerBuildExecution> BuildAsync(BuildRequest request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var telemetryOutcome = SharpLabNextTelemetryOutcome.Failed;
        try
        {
            var remaining = request.DeadlineUtc - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new OperationCanceledException("The C++/CLI build deadline has elapsed.", cancellationToken);
            var maximum = TimeSpan.FromMilliseconds(_manifest.Limits.MaximumBuildMilliseconds);
            using var deadline = new CancellationTokenSource(remaining < maximum ? remaining : maximum);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            try
            {
                var execution = await BuildCoreAsync(request, linked.Token).ConfigureAwait(false);
                telemetryOutcome = TelemetryOutcome(execution.Result);
                return execution;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                telemetryOutcome = SharpLabNextTelemetryOutcome.TimedOut;
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            telemetryOutcome = SharpLabNextTelemetryOutcome.Cancelled;
            throw;
        }
        catch (LanguageWorkerRequestException exception) when (exception.StatusCode == StatusCodes.Status429TooManyRequests)
        {
            telemetryOutcome = SharpLabNextTelemetryOutcome.Overloaded;
            throw;
        }
        catch (LanguageWorkerRequestException exception) when (exception.StatusCode == StatusCodes.Status503ServiceUnavailable)
        {
            telemetryOutcome = SharpLabNextTelemetryOutcome.Crashed;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            SharpLabNextTelemetry.Metrics.RecordBuild(CppCliToolchain.LanguageId, CppCliToolchain.ToolchainId, stopwatch.Elapsed, telemetryOutcome, cacheHit: false);
        }
    }

    private async Task<LanguageWorkerBuildExecution> BuildCoreAsync(BuildRequest request, CancellationToken cancellationToken)
    {
        var workspace = CppCliWorkspaceValidator.Validate(request, _manifest, _settings.Identity.CompilerVersion);
        var invocation = await _compiler.CompileAsync(workspace, cancellationToken).ConfigureAwait(false);
        var identity = _settings.Identity.CreateBuildIdentity();
        if (!invocation.Succeeded)
        {
            if (request.Target == BuildTarget.CompileCheck)
            {
                return new LanguageWorkerBuildExecution(new CompilationCheckResult(false, invocation.Diagnostics, identity, workspace.Snapshot.Revision, workspace.Snapshot.SelectionRevision));
            }
            var linkFailed = invocation.Diagnostics.Any(static diagnostic => diagnostic.Code.StartsWith("LNK", StringComparison.OrdinalIgnoreCase));
            return new LanguageWorkerBuildExecution(new BuildResult(linkFailed ? BuildOutcome.EmitFailed : BuildOutcome.CompilationFailed, null, invocation.Diagnostics, identity, workspace.Snapshot.Revision, workspace.Snapshot.SelectionRevision));
        }

        ValidateMixedModePe(invocation.PeImage);
        if (request.Target == BuildTarget.CompileCheck)
        {
            return new LanguageWorkerBuildExecution(new CompilationCheckResult(true, invocation.Diagnostics, identity, workspace.Snapshot.Revision, workspace.Snapshot.SelectionRevision));
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["compiler"] = "msvc-cl",
            ["compilerVersion"] = _settings.Identity.CompilerVersion,
            ["deterministic"] = "true",
            ["mixedMode"] = "true",
            ["portablePdb"] = "false",
            ["sourceSha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(workspace.SourceFile.Text)))
        };
        if (_settings.Identity.CompilerCommit is { } compilerCommit)
            metadata["compilerCommit"] = compilerCommit;

        var definition = new LanguageArtifactDefinition(
            CppCliToolchain.ArtifactFormat,
            CppCliToolchain.AssemblyName,
            CppCliToolchain.ReferenceSetId,
            CppCliToolchain.TargetFramework,
            new ArtifactRuntimeRequirement(CppCliToolchain.RuntimeFamily, [new FrameworkRequirement(CppCliToolchain.FrameworkName, CppCliToolchain.FrameworkVersion)], "x64", []),
            [],
            workspace.Options.OutputKind,
            CppCliToolchain.OutputFileName,
            null,
            [new LanguageArtifactFile("primary-assembly", CppCliToolchain.OutputFileName, invocation.PeImage)],
            metadata);
        var envelope = LanguageArtifactBuilder.CreateGenericEnvelope(definition, identity, _manifest.Limits.MaximumArtifactBytes);
        return new LanguageWorkerBuildExecution(new BuildResult(BuildOutcome.Succeeded, envelope.ArtifactRef, invocation.Diagnostics, identity, workspace.Snapshot.Revision, workspace.Snapshot.SelectionRevision), envelope);
    }

    internal static void ValidateMixedModePe(byte[] image)
    {
        try
        {
            using var peReader = new PEReader(new MemoryStream(image, writable: false));
            var headers = peReader.PEHeaders;
            if (headers.CoffHeader.Machine != Machine.Amd64 || headers.PEHeader?.Magic != PEMagic.PE32Plus || headers.CorHeader is null || !peReader.HasMetadata || !peReader.GetMetadataReader().IsAssembly || (headers.CorHeader.Flags & CorFlags.ILOnly) != 0)
            {
                throw new BadImageFormatException("The image is not an x64 mixed-mode CLR assembly.");
            }
        }
        catch (BadImageFormatException exception)
        {
            throw new LanguageWorkerRequestException("compiler-invalid-output", "The C++/CLI compiler returned an invalid x64 mixed-mode PE.", StatusCodes.Status503ServiceUnavailable, exception);
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
