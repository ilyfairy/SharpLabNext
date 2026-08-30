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

namespace SharpLabNext.Worker.JSharp;

public sealed class JSharpBuildService : ILanguageWorkerBuildService
{
    private readonly IJSharpCompilerProcess _compiler;
    private readonly JSharpWorkerSettings _settings;
    private readonly LanguageWorkerCapabilityManifest _manifest;

    public JSharpBuildService(JSharpCompilerProcess compiler, JSharpWorkerSettings settings, LanguageWorkerCapabilityManifest manifest) : this((IJSharpCompilerProcess)compiler, settings, manifest) { }

    internal JSharpBuildService(IJSharpCompilerProcess compiler, JSharpWorkerSettings settings, LanguageWorkerCapabilityManifest manifest)
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
            if (remaining <= TimeSpan.Zero)
                throw new OperationCanceledException("The J# build deadline has elapsed.", cancellationToken);
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
            SharpLabNextTelemetry.Metrics.RecordBuild(JSharpToolchain.LanguageId, JSharpToolchain.ToolchainId, stopwatch.Elapsed, telemetryOutcome, cacheHit: false);
        }
    }

    private async Task<LanguageWorkerBuildExecution> BuildCoreAsync(BuildRequest request, CancellationToken cancellationToken)
    {
        var workspace = JSharpWorkspaceValidator.Validate(request, _manifest, _settings.Identity.CompilerVersion);
        var invocation = await _compiler.CompileAsync(workspace, cancellationToken).ConfigureAwait(false);
        var identity = _settings.Identity.CreateBuildIdentity();
        if (!invocation.Succeeded)
        {
            if (request.Target == BuildTarget.CompileCheck)
            {
                return new LanguageWorkerBuildExecution(new CompilationCheckResult(false, invocation.Diagnostics, identity, workspace.Snapshot.Revision, workspace.Snapshot.SelectionRevision));
            }
            return new LanguageWorkerBuildExecution(new BuildResult(BuildOutcome.CompilationFailed, null, invocation.Diagnostics, identity, workspace.Snapshot.Revision, workspace.Snapshot.SelectionRevision));
        }

        var inspection = InspectManagedClr2Pe(invocation.PeImage);
        if (request.Target == BuildTarget.CompileCheck)
        {
            return new LanguageWorkerBuildExecution(new CompilationCheckResult(true, invocation.Diagnostics, identity, workspace.Snapshot.Revision, workspace.Snapshot.SelectionRevision));
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["compiler"] = "vjc",
            ["compilerVersion"] = _settings.Identity.CompilerVersion,
            ["deterministic"] = "false",
            ["portablePdb"] = "false",
            ["clrMetadataVersion"] = inspection.MetadataVersion,
            ["sourceSha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(workspace.SourceFile.Text)))
        };
        if (_settings.Identity.CompilerCommit is { } compilerCommit)
            metadata["compilerCommit"] = compilerCommit;

        var definition = new LanguageArtifactDefinition(
            JSharpToolchain.ArtifactFormat,
            JSharpToolchain.AssemblyName,
            JSharpToolchain.ReferenceSetId,
            JSharpToolchain.TargetFramework,
            new ArtifactRuntimeRequirement(JSharpToolchain.RuntimeFamily, [new FrameworkRequirement(JSharpToolchain.FrameworkName, JSharpToolchain.FrameworkVersion)], JSharpToolchain.Architecture, [JSharpToolchain.RuntimeFeatureTag]),
            [],
            workspace.Options.OutputKind,
            JSharpToolchain.OutputFileName,
            inspection.EntryPoint,
            [new LanguageArtifactFile("primary-assembly", JSharpToolchain.OutputFileName, invocation.PeImage)],
            metadata);
        var envelope = LanguageArtifactBuilder.CreateGenericEnvelope(definition, identity, _manifest.Limits.MaximumArtifactBytes);
        return new LanguageWorkerBuildExecution(new BuildResult(BuildOutcome.Succeeded, envelope.ArtifactRef, invocation.Diagnostics, identity, workspace.Snapshot.Revision, workspace.Snapshot.SelectionRevision), envelope);
    }

    internal static JSharpPeInspection InspectManagedClr2Pe(byte[] image)
    {
        try
        {
            using var peReader = new PEReader(new MemoryStream(image, writable: false));
            var headers = peReader.PEHeaders;
            if (headers.CoffHeader.Machine != Machine.Amd64 || headers.PEHeader?.Magic != PEMagic.PE32Plus || headers.CorHeader is null || (headers.CorHeader.Flags & CorFlags.ILOnly) == 0 || (headers.CorHeader.Flags & CorFlags.NativeEntryPoint) != 0 || (headers.CorHeader.Flags & CorFlags.Requires32Bit) != 0 || (headers.CorHeader.Flags & CorFlags.Prefers32Bit) != 0 || !peReader.HasMetadata)
            {
                throw new BadImageFormatException("The image is not an x64 IL-only CLR assembly.");
            }

            var metadata = peReader.GetMetadataReader();
            if (!metadata.IsAssembly || !StringComparer.Ordinal.Equals(metadata.MetadataVersion, "v2.0.50727"))
                throw new BadImageFormatException("The image does not target the CLR 2.0 metadata contract.");
            var entryPointToken = headers.CorHeader.EntryPointTokenOrRelativeVirtualAddress;
            if (entryPointToken == 0)
                throw new BadImageFormatException("The J# console executable has no managed entry point.");
            var handle = MetadataTokens.EntityHandle(entryPointToken);
            if (handle.Kind != HandleKind.MethodDefinition)
                throw new BadImageFormatException("The J# entry point is not a method definition.");
            var method = metadata.GetMethodDefinition((MethodDefinitionHandle)handle);
            var declaringType = metadata.GetTypeDefinition(method.GetDeclaringType());
            var typeName = metadata.GetString(declaringType.Name);
            var typeNamespace = metadata.GetString(declaringType.Namespace);
            var methodName = metadata.GetString(method.Name);
            var entryPoint = string.IsNullOrEmpty(typeNamespace)
                ? $"{typeName}::{methodName}" : $"{typeNamespace}.{typeName}::{methodName}";
            return new JSharpPeInspection(metadata.MetadataVersion, entryPoint);
        }
        catch (BadImageFormatException exception)
        {
            throw new LanguageWorkerRequestException("compiler-invalid-output", "The J# compiler returned an invalid x64 CLR 2.0 managed executable.", StatusCodes.Status503ServiceUnavailable, exception);
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

internal sealed record JSharpPeInspection(string MetadataVersion, string EntryPoint);
