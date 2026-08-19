using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.Contracts;
using SharpLabNext.LanguageWorker.Sdk;
using SharpLabNext.Observability;

namespace SharpLabNext.Worker.GSharp;

public sealed class GSharpBuildService(
    GSharpReferenceSetProvider referenceSets,
    GSharpCompilerProcess compiler,
    GSharpWorkerSettings settings,
    LanguageWorkerCapabilityManifest manifest) : ILanguageWorkerBuildService
{
    public async Task<LanguageWorkerBuildExecution> BuildAsync(
        BuildRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var telemetryOutcome = SharpLabNextTelemetryOutcome.Failed;
        try
        {
            var remaining = request.DeadlineUtc - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new OperationCanceledException("The G# build deadline has elapsed.", cancellationToken);
            var maximum = TimeSpan.FromMilliseconds(manifest.Limits.MaximumBuildMilliseconds);
            using var deadline = new CancellationTokenSource(remaining < maximum ? remaining : maximum);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            try
            {
                var result = await BuildCoreAsync(request, linked.Token).ConfigureAwait(false);
                telemetryOutcome = TelemetryOutcome(result.Result);
                return result;
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
            SharpLabNextTelemetry.Metrics.RecordBuild(
                GSharpToolchain.LanguageId,
                request.ToolchainId,
                stopwatch.Elapsed,
                telemetryOutcome,
                cacheHit: false);
        }
    }

    private async Task<LanguageWorkerBuildExecution> BuildCoreAsync(
        BuildRequest request,
        CancellationToken cancellationToken)
    {
        var toolchain = settings.GetToolchain(request.ToolchainId);
        var workspace = GSharpWorkspaceValidator.Validate(
            request,
            manifest,
            toolchain);
        var referenceSet = referenceSets.Get(request.ReferenceSetId);
        var invocation = await compiler.CompileAsync(
            workspace,
            referenceSet,
            toolchain,
            cancellationToken).ConfigureAwait(false);
        var identity = toolchain.CreateBuildIdentity(settings.Identity, referenceSet.Definition.Id);
        if (!invocation.Succeeded)
        {
            if (request.Target == BuildTarget.CompileCheck)
            {
                return new LanguageWorkerBuildExecution(new CompilationCheckResult(
                    false,
                    invocation.Diagnostics,
                    identity,
                    workspace.Snapshot.Revision,
                    workspace.Snapshot.SelectionRevision));
            }
            var emitFailed = invocation.Diagnostics.Any(static diagnostic =>
                diagnostic.Code is "GS9998" or "GS9999");
            return new LanguageWorkerBuildExecution(new BuildResult(
                emitFailed ? BuildOutcome.EmitFailed : BuildOutcome.CompilationFailed,
                null,
                invocation.Diagnostics,
                identity,
                workspace.Snapshot.Revision,
                workspace.Snapshot.SelectionRevision));
        }

        ValidatePortablePdb(invocation.PortablePdb);
        var emittedEntryPoint = InspectPe(invocation.PeImage);
        if (workspace.Options.OutputKind == BuildOutputKind.Console && emittedEntryPoint is null)
        {
            var diagnostics = invocation.Diagnostics
                .Append(CreateMissingEntryPointDiagnostic(
                    workspace.Snapshot.Revision,
                    workspace.Snapshot.SelectionRevision))
                .ToArray();
            if (request.Target == BuildTarget.CompileCheck)
            {
                return new LanguageWorkerBuildExecution(new CompilationCheckResult(
                    false,
                    diagnostics,
                    identity,
                    workspace.Snapshot.Revision,
                    workspace.Snapshot.SelectionRevision));
            }

            return new LanguageWorkerBuildExecution(new BuildResult(
                BuildOutcome.CompilationFailed,
                null,
                diagnostics,
                identity,
                workspace.Snapshot.Revision,
                workspace.Snapshot.SelectionRevision));
        }

        var (outputKind, entryPoint) = ResolveOutputKind(
            workspace.Options.OutputKind,
            emittedEntryPoint);
        if (request.Target == BuildTarget.CompileCheck)
        {
            return new LanguageWorkerBuildExecution(new CompilationCheckResult(
                true,
                invocation.Diagnostics,
                identity,
                workspace.Snapshot.Revision,
                workspace.Snapshot.SelectionRevision));
        }

        var sourceOrder = string.Join('\n', workspace.OrderedFiles.Select(static file => file.Path));
        var definition = new LanguageArtifactDefinition(
            GSharpToolchain.ArtifactFormat,
            GSharpToolchain.AssemblyName,
            referenceSet.Definition.Id,
            referenceSet.Definition.TargetFramework,
            new ArtifactRuntimeRequirement(
                "coreclr",
                [new FrameworkRequirement("Microsoft.NETCore.App", referenceSet.Definition.FrameworkVersion)],
                "anycpu",
                []),
            [],
            outputKind,
            $"{GSharpToolchain.AssemblyName}.dll",
            entryPoint,
            [
                new LanguageArtifactFile(
                    "primary-assembly",
                    $"{GSharpToolchain.AssemblyName}.dll",
                    invocation.PeImage),
                new LanguageArtifactFile(
                    "portable-pdb",
                    $"{GSharpToolchain.AssemblyName}.pdb",
                    invocation.PortablePdb)
            ],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["compiler"] = "gsc",
                ["compilerVersion"] = toolchain.CompilerVersion,
                ["compilerCommit"] = toolchain.CompilerCommit,
                ["portablePdb"] = "true",
                ["optimizationControl"] = "compiler-default",
                ["requestedOptimize"] = workspace.Options.Optimize ? "true" : "false",
                ["sourceOrderSha256"] = Convert.ToHexStringLower(
                    SHA256.HashData(Encoding.UTF8.GetBytes(sourceOrder)))
            });
        var envelope = LanguageArtifactBuilder.CreateGenericEnvelope(
            definition,
            identity,
            manifest.Limits.MaximumArtifactBytes);
        return new LanguageWorkerBuildExecution(
            new BuildResult(
                BuildOutcome.Succeeded,
                envelope.ArtifactRef,
                invocation.Diagnostics,
                identity,
                workspace.Snapshot.Revision,
                workspace.Snapshot.SelectionRevision),
            envelope);
    }

    private static SharpLabNextTelemetryOutcome TelemetryOutcome(OperationResult result) => result switch
    {
        BuildResult { Outcome: BuildOutcome.Succeeded } => SharpLabNextTelemetryOutcome.Succeeded,
        CompilationCheckResult { CompilationSucceeded: true } => SharpLabNextTelemetryOutcome.Succeeded,
        BuildResult or CompilationCheckResult => SharpLabNextTelemetryOutcome.Failed,
        _ => SharpLabNextTelemetryOutcome.Succeeded
    };

    private static void ValidatePortablePdb(byte[] image)
    {
        try
        {
            using var provider = MetadataReaderProvider.FromPortablePdbStream(
                new MemoryStream(image, writable: false));
            _ = provider.GetMetadataReader().Documents.Count;
        }
        catch (BadImageFormatException exception)
        {
            throw new LanguageWorkerRequestException(
                "compiler-invalid-output",
                "The G# compiler returned an invalid Portable PDB.",
                StatusCodes.Status503ServiceUnavailable,
                exception);
        }
    }

    private static (BuildOutputKind OutputKind, string? EntryPoint) ResolveOutputKind(
        BuildOutputKind requestedOutputKind,
        string? emittedEntryPoint)
    {
        if (requestedOutputKind == BuildOutputKind.Auto)
        {
            return emittedEntryPoint is null
                ? (BuildOutputKind.Library, null)
                : (BuildOutputKind.Console, emittedEntryPoint);
        }

        return requestedOutputKind == BuildOutputKind.Library
            ? (BuildOutputKind.Library, null)
            : (BuildOutputKind.Console, emittedEntryPoint);
    }

    private static Diagnostic CreateMissingEntryPointDiagnostic(
        long workspaceRevision,
        long selectionRevision) => new(
        "gsc",
        "GS9999",
        DiagnosticSeverity.Error,
        "A console output requires a managed entry point. Add a func Main(...) or use a top-level statement.",
        null,
        null,
        [],
        [],
        workspaceRevision,
        selectionRevision);

    private static string? InspectPe(byte[] image)
    {
        try
        {
            using var peReader = new PEReader(new MemoryStream(image, writable: false));
            if (!peReader.HasMetadata)
                throw new BadImageFormatException("The G# output has no managed metadata.");
            var metadata = peReader.GetMetadataReader();
            if (!metadata.IsAssembly)
                throw new BadImageFormatException("The G# output is not an assembly.");
            var corHeader = peReader.PEHeaders.CorHeader
                ?? throw new BadImageFormatException("The G# output has no CLR header.");
            var token = corHeader.EntryPointTokenOrRelativeVirtualAddress;
            if (token == 0)
                return null;
            if ((corHeader.Flags & CorFlags.NativeEntryPoint) != 0)
                throw new BadImageFormatException("The G# output has a native entry point.");
            var handle = MetadataTokens.EntityHandle(token);
            if (handle.Kind != HandleKind.MethodDefinition)
                throw new BadImageFormatException("The G# entry point is not a method definition.");
            var method = metadata.GetMethodDefinition((MethodDefinitionHandle)handle);
            var type = metadata.GetTypeDefinition(method.GetDeclaringType());
            var typeName = metadata.GetString(type.Name);
            var typeNamespace = metadata.GetString(type.Namespace);
            var methodName = metadata.GetString(method.Name);
            return string.IsNullOrEmpty(typeNamespace)
                ? $"{typeName}::{methodName}"
                : $"{typeNamespace}.{typeName}::{methodName}";
        }
        catch (BadImageFormatException exception)
        {
            throw new LanguageWorkerRequestException(
                "compiler-invalid-output",
                "The G# compiler returned an invalid managed PE.",
                StatusCodes.Status503ServiceUnavailable,
                exception);
        }
    }
}
