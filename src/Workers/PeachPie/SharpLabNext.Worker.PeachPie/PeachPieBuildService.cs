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
using SharpLabNext.WorkerHost;

namespace SharpLabNext.Worker.PeachPie;

public sealed class PeachPieBuildService(PeachPieReferenceSetProvider referenceSets, PeachPieCompiler compiler, ICompilerProcessRunner processRunner, PeachPieWorkerSettings settings, LanguageWorkerCapabilityManifest manifest) : ILanguageWorkerBuildService
{
    public async Task<LanguageWorkerBuildExecution> BuildAsync(BuildRequest request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var telemetryOutcome = SharpLabNextTelemetryOutcome.Failed;
        try
        {
            var workspace = PeachPieWorkspaceValidator.Validate(request, manifest);
            var referenceSet = referenceSets.Get(request.ReferenceSetId);
            var response = await CompileAsync(request, cancellationToken).ConfigureAwait(false);
            ValidateResponse(request, response);
            var identity = settings.Identity.CreateBuildIdentity(referenceSet.Definition.Id);
            if (request.Target == BuildTarget.CompileCheck)
            {
                telemetryOutcome = response.CompilationSucceeded
                    ? SharpLabNextTelemetryOutcome.Succeeded : SharpLabNextTelemetryOutcome.Failed;
                return new LanguageWorkerBuildExecution(new CompilationCheckResult(response.CompilationSucceeded, response.Diagnostics, identity, workspace.Snapshot.Revision, workspace.Snapshot.SelectionRevision));
            }
            if (!response.CompilationSucceeded || !response.EmitSucceeded)
            {
                return new LanguageWorkerBuildExecution(new BuildResult(response.CompilationSucceeded ? BuildOutcome.EmitFailed : BuildOutcome.CompilationFailed, null, response.Diagnostics, identity, workspace.Snapshot.Revision, workspace.Snapshot.SelectionRevision));
            }

            var entryPoint = InspectPe(response.PeImage);
            var remainingArtifactBytes = manifest.Limits.MaximumArtifactBytes - response.PeImage.Length;
            if (remainingArtifactBytes <= 0)
            {
                throw new PeachPieBuildOutputLimitExceededException("The PeachPie runtime support closure exceeds the artifact limit.");
            }
            var supportFiles = await PeachPieSupportAssemblyResolver.ResolveAsync(settings, referenceSet, remainingArtifactBytes, cancellationToken).ConfigureAwait(false);
            var files = new List<LanguageArtifactFile>(supportFiles.Count + 1)
            {
                new("primary-assembly", $"{PeachPieToolchain.AssemblyName}.dll", response.PeImage)
            };
            files.AddRange(supportFiles.Select(static file => new LanguageArtifactFile(file.Role, file.Path, file.Content)));
            var supportAssemblies = supportFiles.Where(static file => file.Role == "support-assembly").Select(static file => file.Path).ToArray();
            var nativeLibraries = supportFiles.Where(static file => file.Role == "native-library").Select(static file => file.Path).ToArray();
            var sourceOrder = string.Join('\n', workspace.OrderedFiles.Select(static file => file.Path));
            var definition = new LanguageArtifactDefinition(
                PeachPieToolchain.ArtifactFormat,
                PeachPieToolchain.AssemblyName,
                referenceSet.Definition.Id,
                referenceSet.Definition.TargetFramework,
                new ArtifactRuntimeRequirement("coreclr", [new FrameworkRequirement("Microsoft.NETCore.App", referenceSet.Definition.FrameworkVersion)], "x64", []),
                [],
                BuildOutputKind.Console,
                $"{PeachPieToolchain.AssemblyName}.dll",
                entryPoint,
                files,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["compiler"] = "PeachPie",
                    ["compilerVersion"] = settings.Identity.CompilerVersion,
                    ["compilerCommit"] = settings.Identity.CompilerCommit,
                    ["phpLanguageVersion"] = "8.5",
                    ["portablePdb"] = "false",
                    ["entryScript"] = workspace.ActiveFile,
                    ["bootstrap"] = "require-entry-then-return-zero-v1",
                    ["supportAssemblyClosure"] = string.Join(',', supportAssemblies),
                    ["nativeLibraryClosure"] = $"{PeachPieToolchain.NativeRuntimeIdentifier}:{string.Join(',', nativeLibraries)}",
                    ["nativeLibrarySourcePath"] = PeachPieToolchain.MonoUnixNativePackagePath,
                    ["sourceOrderSha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sourceOrder)))
                });
            var envelope = LanguageArtifactBuilder.CreateGenericEnvelope(definition, identity, manifest.Limits.MaximumArtifactBytes);
            telemetryOutcome = SharpLabNextTelemetryOutcome.Succeeded;
            return new LanguageWorkerBuildExecution(new BuildResult(BuildOutcome.Succeeded, envelope.ArtifactRef, response.Diagnostics, identity, workspace.Snapshot.Revision, workspace.Snapshot.SelectionRevision), envelope);
        }
        catch (CompilerProcessCapacityExceededException exception)
        {
            telemetryOutcome = SharpLabNextTelemetryOutcome.Overloaded;
            throw RequestException("compiler-capacity-exhausted", "PeachPie compiler process capacity is exhausted.", StatusCodes.Status429TooManyRequests, exception);
        }
        catch (CompilerProcessMemoryLimitExceededException exception)
        {
            telemetryOutcome = SharpLabNextTelemetryOutcome.OutOfMemory;
            throw RequestException("compiler-memory-limit", "The PeachPie compiler process exceeded its memory limit.", StatusCodes.Status429TooManyRequests, exception);
        }
        catch (CompilerProcessTimeoutException exception)
        {
            telemetryOutcome = SharpLabNextTelemetryOutcome.TimedOut;
            throw new OperationCanceledException("The PeachPie compiler process deadline elapsed.", exception, cancellationToken);
        }
        catch (CompilerChildReportedException exception)
        {
            throw MapFailure(exception, cancellationToken);
        }
        catch (CompilerProcessException exception)
        {
            telemetryOutcome = SharpLabNextTelemetryOutcome.Crashed;
            throw RequestException("compiler-process-unavailable", "The isolated PeachPie compiler process failed.", StatusCodes.Status503ServiceUnavailable, exception);
        }
        catch (PeachPieBuildOutputLimitExceededException exception)
        {
            throw RequestException("artifact-too-large", "The compiler output exceeds the configured artifact limit.", StatusCodes.Status413PayloadTooLarge, exception);
        }
        catch (PeachPieCompilerFailureException exception)
        {
            throw RequestException("compiler-failure", "The PeachPie compiler runtime support closure is unavailable.", StatusCodes.Status503ServiceUnavailable, exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            telemetryOutcome = SharpLabNextTelemetryOutcome.Cancelled;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            SharpLabNextTelemetry.Metrics.RecordBuild(PeachPieToolchain.LanguageId, PeachPieToolchain.ToolchainId, stopwatch.Elapsed, telemetryOutcome, cacheHit: false);
        }
    }

    private async Task<PeachPieCompilerResponse> CompileAsync(BuildRequest request, CancellationToken cancellationToken)
    {
        if (!settings.BuildProcess.Enabled)
            return await compiler.CompileAsync(request, cancellationToken).ConfigureAwait(false);
        var remaining = request.DeadlineUtc - DateTimeOffset.UtcNow;
        var maximum = TimeSpan.FromMilliseconds(manifest.Limits.MaximumBuildMilliseconds);
        var timeout = remaining < maximum ? remaining : maximum;
        return await processRunner.RunAsync<BuildRequest, PeachPieCompilerResponse>(PeachPieCompilerChild.ChildArgument, request, timeout, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateResponse(BuildRequest request, PeachPieCompilerResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (request.Target == BuildTarget.Artifact && response.CompilationSucceeded && response.EmitSucceeded && response.PeImage.Length == 0)
        {
            throw new CompilerProcessProtocolException("The PeachPie compiler process omitted a successful managed PE.");
        }
        if ((!response.CompilationSucceeded || !response.EmitSucceeded) && response.PeImage.Length != 0)
        {
            throw new CompilerProcessProtocolException("The PeachPie compiler process returned a PE for a failed build.");
        }
        if (response.Diagnostics.Any(diagnostic => diagnostic.WorkspaceRevision != request.Workspace.Revision || diagnostic.SelectionRevision != request.Workspace.SelectionRevision))
        {
            throw new CompilerProcessProtocolException("The PeachPie compiler process returned diagnostics for another workspace revision.");
        }
    }

    private static string InspectPe(byte[] image)
    {
        try
        {
            using var pe = new PEReader(new MemoryStream(image, writable: false));
            if (!pe.HasMetadata)
                throw new BadImageFormatException("The PeachPie output has no managed metadata.");
            var metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly)
                throw new BadImageFormatException("The PeachPie output is not an assembly.");
            var token = pe.PEHeaders.CorHeader?.EntryPointTokenOrRelativeVirtualAddress ?? 0;
            if (token == 0 || (pe.PEHeaders.CorHeader!.Flags & CorFlags.NativeEntryPoint) != 0)
                throw new BadImageFormatException("The PeachPie output has no managed entry point.");
            var handle = MetadataTokens.EntityHandle(token);
            if (handle.Kind != HandleKind.MethodDefinition)
                throw new BadImageFormatException("The PeachPie entry point is not a method definition.");
            var method = metadata.GetMethodDefinition((MethodDefinitionHandle)handle);
            var type = metadata.GetTypeDefinition(method.GetDeclaringType());
            var typeName = metadata.GetString(type.Name);
            var typeNamespace = metadata.GetString(type.Namespace);
            var methodName = metadata.GetString(method.Name);
            return string.IsNullOrEmpty(typeNamespace)
                ? $"{typeName}::{methodName}" : $"{typeNamespace}.{typeName}::{methodName}";
        }
        catch (BadImageFormatException exception)
        {
            throw RequestException("compiler-invalid-output", "The PeachPie compiler returned an invalid managed PE.", StatusCodes.Status503ServiceUnavailable, exception);
        }
    }

    private static Exception MapFailure(CompilerChildReportedException exception, CancellationToken cancellationToken) => exception.Kind switch
        {
            CompilerChildFailureKind.InvalidRequest => RequestException("invalid-workspace", exception.PublicMessage, StatusCodes.Status400BadRequest, exception),
            CompilerChildFailureKind.ReferenceSetUnavailable => RequestException("unsupported-reference-set", exception.PublicMessage, StatusCodes.Status503ServiceUnavailable, exception),
            CompilerChildFailureKind.OutputLimitExceeded => RequestException("artifact-too-large", exception.PublicMessage, StatusCodes.Status413PayloadTooLarge, exception),
            CompilerChildFailureKind.DeadlineExceeded =>
                new OperationCanceledException(exception.PublicMessage, exception, cancellationToken),
            CompilerChildFailureKind.CompilerFailure => RequestException("compiler-failure", exception.PublicMessage, StatusCodes.Status503ServiceUnavailable, exception),
            _ => RequestException("compiler-process-unavailable", "The isolated PeachPie compiler process failed.", StatusCodes.Status503ServiceUnavailable, exception)
        };

    private static LanguageWorkerRequestException RequestException(string code, string message, int statusCode, Exception exception) => new(code, message, statusCode, exception);
}
