using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.Contracts;
using SharpLabNext.Observability;
using SharpLabNext.Worker.IL.Compiler;

namespace SharpLabNext.Worker.IL;

public sealed class IlBuildService(
    IlReferenceSetProvider referenceSets,
    IlAssemblerProcess assembler,
    IlWorkerIdentity identity,
    IlCompilationLimits limits)
{
    private const string AssemblyName = "SharpLabNext.User";

    public async Task<IlWorkerBuildExecution> ExecuteAsync(
        BuildRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var outcome = SharpLabNextTelemetryOutcome.Failed;
        try
        {
            var execution = await ExecuteWithDeadlineAsync(request, cancellationToken).ConfigureAwait(false);
            outcome = TelemetryOutcome(execution.Result);
            return execution;
        }
        catch (IlBuildDeadlineExceededException)
        {
            outcome = SharpLabNextTelemetryOutcome.TimedOut;
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = SharpLabNextTelemetryOutcome.Cancelled;
            throw;
        }
        catch (IlBuildOutputLimitExceededException)
        {
            outcome = SharpLabNextTelemetryOutcome.Overloaded;
            throw;
        }
        catch (IlAssemblerUnavailableException)
        {
            outcome = SharpLabNextTelemetryOutcome.Crashed;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            SharpLabNextTelemetry.Metrics.RecordBuild(
                "il",
                identity.ToolchainId,
                stopwatch.Elapsed,
                outcome,
                cacheHit: false);
        }
    }

    private async Task<IlWorkerBuildExecution> ExecuteWithDeadlineAsync(
        BuildRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remaining = request.DeadlineUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            throw new IlBuildDeadlineExceededException("The IL build deadline has already elapsed.", cancellationToken);
        var workerLimit = TimeSpan.FromMilliseconds(limits.MaxBuildMilliseconds);
        if (remaining > workerLimit)
            remaining = workerLimit;
        using var deadlineCancellation = new CancellationTokenSource(remaining);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadlineCancellation.Token);
        try
        {
            return await ExecuteCoreAsync(request, linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            deadlineCancellation.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            throw new IlBuildDeadlineExceededException("The IL build deadline elapsed.", deadlineCancellation.Token);
        }
    }

    private static SharpLabNextTelemetryOutcome TelemetryOutcome(OperationResult result) => result switch
    {
        BuildResult { Outcome: BuildOutcome.Succeeded } => SharpLabNextTelemetryOutcome.Succeeded,
        CompilationCheckResult { CompilationSucceeded: true } => SharpLabNextTelemetryOutcome.Succeeded,
        BuildResult or CompilationCheckResult => SharpLabNextTelemetryOutcome.Failed,
        _ => SharpLabNextTelemetryOutcome.Succeeded
    };

    private async Task<IlWorkerBuildExecution> ExecuteCoreAsync(
        BuildRequest request,
        CancellationToken cancellationToken)
    {
        var workspace = IlWorkspaceValidator.Validate(request, limits);
        var referenceSet = referenceSets.Get(request.ReferenceSetId);
        var invocation = await assembler.AssembleAsync(workspace, cancellationToken).ConfigureAwait(false);
        var diagnostics = ConvertDiagnostics(invocation.Diagnostics, workspace);
        var buildIdentity = new BuildIdentity(
            identity.ReleaseId,
            "il",
            identity.ToolchainId,
            identity.CompilerVersion,
            identity.CompilerCommit,
            referenceSet.Id,
            identity.WorkerImageId);

        if (!invocation.Succeeded)
        {
            if (request.Target == BuildTarget.CompileCheck)
            {
                return new IlWorkerBuildExecution(
                    new CompilationCheckResult(
                        false,
                        diagnostics,
                        buildIdentity,
                        workspace.Snapshot.Revision,
                        workspace.Snapshot.SelectionRevision),
                    null);
            }
            return new IlWorkerBuildExecution(
                new BuildResult(
                    BuildOutcome.CompilationFailed,
                    null,
                    diagnostics,
                    buildIdentity,
                    workspace.Snapshot.Revision,
                    workspace.Snapshot.SelectionRevision),
                null);
        }

        PeInspection inspection;
        try
        {
            inspection = InspectPe(invocation.PeImage);
        }
        catch (BadImageFormatException exception)
        {
            diagnostics = diagnostics.Append(new Diagnostic(
                "mobius-ilasm",
                "ILASM998",
                DiagnosticSeverity.Error,
                $"The assembler returned an invalid managed PE image: {Limit(exception.Message, 2_048)}",
                null,
                null,
                [],
                [],
                workspace.Snapshot.Revision,
                workspace.Snapshot.SelectionRevision)).ToArray();
            if (request.Target == BuildTarget.CompileCheck)
            {
                return new IlWorkerBuildExecution(
                    new CompilationCheckResult(
                        false,
                        diagnostics,
                        buildIdentity,
                        workspace.Snapshot.Revision,
                        workspace.Snapshot.SelectionRevision),
                    null);
            }
            return new IlWorkerBuildExecution(
                new BuildResult(
                    BuildOutcome.EmitFailed,
                    null,
                    diagnostics,
                    buildIdentity,
                    workspace.Snapshot.Revision,
                    workspace.Snapshot.SelectionRevision),
                null);
        }

        if (request.Target == BuildTarget.CompileCheck)
        {
            return new IlWorkerBuildExecution(
                new CompilationCheckResult(
                    true,
                    diagnostics,
                    buildIdentity,
                    workspace.Snapshot.Revision,
                    workspace.Snapshot.SelectionRevision),
                null);
        }

        var artifact = CreateArtifact(
            invocation.PeImage,
            referenceSet,
            buildIdentity,
            workspace.Options.OutputKind,
            inspection.EntryPoint,
            inspection.AssemblyDefinitionName,
            workspace.OrderedFiles.Count);
        return new IlWorkerBuildExecution(
            new BuildResult(
                BuildOutcome.Succeeded,
                artifact.ArtifactRef,
                diagnostics,
                buildIdentity,
                workspace.Snapshot.Revision,
                workspace.Snapshot.SelectionRevision),
            artifact);
    }

    private Diagnostic[] ConvertDiagnostics(
        IReadOnlyList<IlCompilerDiagnostic> diagnostics,
        ValidatedIlWorkspace workspace)
    {
        var files = workspace.OrderedFiles.ToDictionary(static file => file.Path, StringComparer.Ordinal);
        return diagnostics.Take(limits.MaxDiagnostics).Select(item =>
        {
            var filePath = item.FilePath is not null && files.ContainsKey(item.FilePath) ? item.FilePath : null;
            TextRange? range = null;
            if (filePath is not null && item.StartLine is not null && item.StartCharacter is not null)
            {
                var lines = files[filePath].Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
                var startLine = Math.Clamp(item.StartLine.Value, 0, Math.Max(0, lines.Length - 1));
                var endLine = Math.Clamp(item.EndLine ?? startLine, startLine, Math.Max(startLine, lines.Length - 1));
                var startCharacter = Math.Clamp(item.StartCharacter.Value, 0, lines[startLine].Length);
                var endCharacter = Math.Clamp(item.EndCharacter ?? startCharacter + 1, 0, lines[endLine].Length);
                if (endLine == startLine && endCharacter < startCharacter)
                    endCharacter = startCharacter;
                range = new TextRange(startLine, startCharacter, endLine, endCharacter);
            }
            return new Diagnostic(
                "mobius-ilasm",
                Limit(item.Code, 64),
                item.Severity switch
                {
                    IlCompilerDiagnosticSeverity.Information => DiagnosticSeverity.Information,
                    IlCompilerDiagnosticSeverity.Warning => DiagnosticSeverity.Warning,
                    _ => DiagnosticSeverity.Error
                },
                Limit(item.Message, 8_192),
                filePath,
                range,
                [],
                [],
                workspace.Snapshot.Revision,
                workspace.Snapshot.SelectionRevision);
        }).ToArray();
    }

    private static PeInspection InspectPe(byte[] peImage)
    {
        using var stream = new MemoryStream(peImage, writable: false);
        using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
        if (!peReader.HasMetadata || peReader.PEHeaders.CorHeader is null)
            throw new BadImageFormatException("The image has no managed metadata header.");
        var metadata = peReader.GetMetadataReader();
        if (!metadata.IsAssembly)
            throw new BadImageFormatException("The image is a module rather than a managed assembly.");
        var assemblyName = metadata.GetString(metadata.GetAssemblyDefinition().Name);
        string? entryPoint = null;
        var entryPointToken = peReader.PEHeaders.CorHeader.EntryPointTokenOrRelativeVirtualAddress;
        if (entryPointToken != 0 &&
            (peReader.PEHeaders.CorHeader.Flags & CorFlags.NativeEntryPoint) == 0)
        {
            var handle = MetadataTokens.EntityHandle(entryPointToken);
            if (handle.Kind != HandleKind.MethodDefinition)
                throw new BadImageFormatException("The managed entry point is not a method definition.");
            var methodHandle = (MethodDefinitionHandle)handle;
            var method = metadata.GetMethodDefinition(methodHandle);
            var declaringType = metadata.GetTypeDefinition(method.GetDeclaringType());
            var typeName = metadata.GetString(declaringType.Name);
            var typeNamespace = metadata.GetString(declaringType.Namespace);
            var methodName = metadata.GetString(method.Name);
            entryPoint = string.IsNullOrEmpty(typeNamespace)
                ? $"{typeName}::{methodName}"
                : $"{typeNamespace}.{typeName}::{methodName}";
        }
        return new PeInspection(Limit(assemblyName, 256), LimitNullable(entryPoint, 512));
    }

    private static IlCompiledArtifact CreateArtifact(
        byte[] peImage,
        IlReferenceSetDefinition referenceSet,
        BuildIdentity buildIdentity,
        BuildOutputKind outputKind,
        string? entryPoint,
        string sourceAssemblyName,
        int sourceFileCount)
    {
        var file = new ArtifactFileDescriptor(
            "primary-assembly",
            $"{AssemblyName}.dll",
            peImage.LongLength,
            Digest(peImage));
        var placeholder = new ArtifactRef($"sha256:{new string('0', ArtifactStoreProtocol.Sha256HexLength)}");
        var manifest = ArtifactIdentity.WithComputedId(new ArtifactManifest(
            ContractSchemaVersions.ArtifactManifest,
            placeholder,
            new ArtifactProducer(
                buildIdentity.ReleaseId,
                buildIdentity.LanguageId,
                buildIdentity.ToolchainId,
                buildIdentity.CompilerVersion,
                buildIdentity.CompilerCommit,
                buildIdentity.WorkerImageId),
            referenceSet.Id,
            referenceSet.TargetFramework,
            "dotnet-managed-pe-v1",
            new ArtifactRuntimeRequirement(
                "coreclr",
                [new FrameworkRequirement("Microsoft.NETCore.App", referenceSet.FrameworkVersion)],
                "anycpu",
                []),
            [],
            outputKind,
            file.Path,
            entryPoint,
            [file],
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["assembler"] = "Mobius.ILasm",
                ["assemblerVersion"] = buildIdentity.CompilerVersion,
                ["portablePdb"] = "false",
                ["sourceAssemblyName"] = sourceAssemblyName,
                ["sourceFileCount"] = sourceFileCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }));
        return new IlCompiledArtifact(
            manifest.ArtifactId,
            "dotnet-managed-pe-v1",
            AssemblyName,
            referenceSet.Id,
            referenceSet.TargetFramework,
            peImage,
            manifest,
            [file],
            buildIdentity);
    }

    private static string Digest(byte[] bytes) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";

    private static string Limit(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

    private static string? LimitNullable(string? value, int maximum) =>
        value is null || value.Length <= maximum ? value : value[..maximum];

    private sealed record PeInspection(string AssemblyDefinitionName, string? EntryPoint);
}
