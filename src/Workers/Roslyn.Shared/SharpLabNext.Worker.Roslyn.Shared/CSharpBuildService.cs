using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.Contracts;
using ContractOutputKind = SharpLabNext.Contracts.BuildOutputKind;
using RoslynOutputKind = Microsoft.CodeAnalysis.OutputKind;

namespace SharpLabNext.Worker.Roslyn;

public sealed class CSharpBuildService(
    ReferenceSetProvider referenceSets,
    RoslynWorkerIdentity identity,
    CompilationLimits compilationLimits,
    AstLimits astLimits)
{
    private const string AssemblyName = "SharpLabNext.User";

    public async Task<WorkerBuildExecution> ExecuteAsync(
        BuildRequest request,
        CancellationToken cancellationToken)
    {
        RoslynCompilerIdentity.Ensure(
            identity,
            "C# compiler",
            GetLoadedCompilerVersion(),
            GetLoadedCompilerCommit());
        cancellationToken.ThrowIfCancellationRequested();
        var remaining = request.DeadlineUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            throw new BuildDeadlineExceededException("The build deadline has already elapsed.", cancellationToken);

        var workerLimit = TimeSpan.FromMilliseconds(compilationLimits.MaxBuildMilliseconds);
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
            throw new BuildDeadlineExceededException("The build deadline elapsed.", deadlineCancellation.Token);
        }
    }

    public static string GetLoadedCompilerVersion()
    {
        var version = typeof(CSharpCompilation).Assembly.GetName().Version;
        return version is null
            ? "unknown"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    public static string? GetLoadedCompilerCommit() =>
        RoslynCompilerIdentity.GetCommit(typeof(CSharpCompilation).Assembly);

    private async Task<WorkerBuildExecution> ExecuteCoreAsync(
        BuildRequest request,
        CancellationToken cancellationToken)
    {
        var workspace = WorkspaceValidator.Validate(request, compilationLimits, identity);
        if (!StringComparer.Ordinal.Equals(workspace.Snapshot.LanguageId, "csharp"))
            throw new BuildRequestValidationException("CSharpBuildService only accepts languageId 'csharp'.");
        var referenceSet = await referenceSets
            .GetAsync(request.ReferenceSetId, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var buildIdentity = CreateBuildIdentity(referenceSet.Definition.Id);

        var parseOptions = CreateParseOptions(workspace.Options);
        var syntaxTrees = workspace.OrderedFiles
            .Select(file => (SyntaxTree)CSharpSyntaxTree.ParseText(
                SourceText.From(file.Text, Encoding.UTF8, SourceHashAlgorithm.Sha256),
                parseOptions,
                file.Path,
                cancellationToken))
            .ToArray();

        if (request.Target == BuildTarget.Ast)
        {
            var document = RoslynAstConverter.Convert(
                workspace,
                syntaxTrees,
                identity.ToolchainId,
                astLimits,
                cancellationToken);
            return new WorkerBuildExecution(new AstResult(document, buildIdentity), null);
        }

        var outputKind = ResolveOutputKind(
            workspace.Options.OutputKind,
            syntaxTrees,
            cancellationToken);
        var compilation = CSharpCompilation.Create(
            AssemblyName,
            syntaxTrees,
            referenceSet.References,
            CreateCompilationOptions(workspace.Options, outputKind));

        var preEmitDiagnostics = compilation.GetDiagnostics(cancellationToken);
        using var peStream = new LimitedMemoryStream(compilationLimits.MaxPeBytes);
        using var pdbStream = workspace.Options.EmitPortablePdb
            ? new LimitedMemoryStream(compilationLimits.MaxPdbBytes)
            : null;
        var emitOptions = workspace.Options.EmitPortablePdb
            ? new EmitOptions(
                debugInformationFormat: DebugInformationFormat.PortablePdb,
                pdbFilePath: $"{AssemblyName}.pdb")
            : null;

        var emitResult = compilation.Emit(
            peStream,
            pdbStream,
            options: emitOptions,
            cancellationToken: cancellationToken);
        var diagnostics = RoslynDiagnosticConverter.Convert(
            preEmitDiagnostics.Concat(emitResult.Diagnostics),
            workspace.Snapshot.Revision,
            workspace.Snapshot.SelectionRevision,
            compilationLimits.MaxDiagnostics);
        if (request.Target == BuildTarget.CompileCheck)
        {
            return new WorkerBuildExecution(
                new CompilationCheckResult(
                    emitResult.Success,
                    diagnostics,
                    buildIdentity,
                    workspace.Snapshot.Revision,
                    workspace.Snapshot.SelectionRevision),
                null);
        }

        if (!emitResult.Success)
        {
            var compilationHadErrors = preEmitDiagnostics.Any(static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
            var outcome = compilationHadErrors
                ? BuildOutcome.CompilationFailed
                : BuildOutcome.EmitFailed;
            return new WorkerBuildExecution(
                new BuildResult(
                    outcome,
                    null,
                    diagnostics,
                    buildIdentity,
                    workspace.Snapshot.Revision,
                    workspace.Snapshot.SelectionRevision),
                null);
        }

        var peImage = peStream.ToArray();
        var portablePdb = pdbStream?.ToArray() ?? [];
        var artifact = CreateArtifact(
            peImage,
            portablePdb,
            referenceSet.Definition,
            buildIdentity,
            outputKind,
            compilation.GetEntryPoint(cancellationToken)?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
        return new WorkerBuildExecution(
            new BuildResult(
                BuildOutcome.Succeeded,
                artifact.ArtifactRef,
                diagnostics,
                buildIdentity,
                workspace.Snapshot.Revision,
                workspace.Snapshot.SelectionRevision),
            artifact);
    }

    internal static CSharpParseOptions CreateParseOptions(BuildOptions options)
    {
        var languageVersion = LanguageVersion.Preview;
        if (!string.IsNullOrWhiteSpace(options.LanguageVersion) &&
            !LanguageVersionFacts.TryParse(options.LanguageVersion, out languageVersion))
        {
            throw new BuildRequestValidationException(
                $"C# language version '{options.LanguageVersion}' is not supported by this Roslyn worker.");
        }

        return new CSharpParseOptions(
            languageVersion,
            DocumentationMode.Parse,
            SourceCodeKind.Regular,
            options.PreprocessorSymbols ?? []);
    }

    internal static ContractOutputKind ResolveOutputKind(
        ContractOutputKind requestedOutputKind,
        IEnumerable<SyntaxTree> syntaxTrees,
        CancellationToken cancellationToken)
    {
        if (requestedOutputKind != ContractOutputKind.Auto)
            return requestedOutputKind;

        foreach (var syntaxTree in syntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (syntaxTree.GetRoot(cancellationToken) is CompilationUnitSyntax compilationUnit &&
                compilationUnit.Members.Any(static member => member is GlobalStatementSyntax))
            {
                return ContractOutputKind.Console;
            }
        }

        return ContractOutputKind.Library;
    }

    internal static CSharpCompilationOptions CreateCompilationOptions(
        BuildOptions options,
        ContractOutputKind resolvedOutputKind) =>
        new(
            ToRoslynOutputKind(resolvedOutputKind),
            optimizationLevel: options.Optimize ? OptimizationLevel.Release : OptimizationLevel.Debug,
            checkOverflow: options.CheckOverflow,
            allowUnsafe: options.AllowUnsafe,
            nullableContextOptions: options.NullableContext switch
            {
                NullableContextMode.ProjectDefault => NullableContextOptions.Disable,
                NullableContextMode.Disable => NullableContextOptions.Disable,
                NullableContextMode.Enable => NullableContextOptions.Enable,
                NullableContextMode.Warnings => NullableContextOptions.Warnings,
                NullableContextMode.Annotations => NullableContextOptions.Annotations,
                _ => throw new BuildRequestValidationException($"Nullable mode '{options.NullableContext}' is not supported.")
            },
            deterministic: true,
            concurrentBuild: true,
            metadataImportOptions: MetadataImportOptions.Public,
            assemblyIdentityComparer: DesktopAssemblyIdentityComparer.Default,
            reportSuppressedDiagnostics: false);

    internal static RoslynOutputKind ToRoslynOutputKind(ContractOutputKind resolvedOutputKind) =>
        resolvedOutputKind switch
        {
            ContractOutputKind.Console => RoslynOutputKind.ConsoleApplication,
            ContractOutputKind.Library => RoslynOutputKind.DynamicallyLinkedLibrary,
            ContractOutputKind.WindowsApplication => RoslynOutputKind.WindowsApplication,
            _ => throw new BuildRequestValidationException(
                $"Resolved output kind '{resolvedOutputKind}' is not supported.")
        };

    private BuildIdentity CreateBuildIdentity(string referenceSetId) =>
        new(
            identity.ReleaseId,
            "csharp",
            identity.ToolchainId,
            identity.CompilerVersion,
            GetLoadedCompilerCommit(),
            referenceSetId,
            identity.WorkerImageId);

    internal static CompiledArtifact CreateArtifact(
        byte[] peImage,
        byte[] portablePdb,
        ReferenceSetDefinition referenceSet,
        BuildIdentity buildIdentity,
        ContractOutputKind outputKind,
        string? entryPoint)
    {
        _ = ToRoslynOutputKind(outputKind);
        var entryFileExtension = outputKind == ContractOutputKind.Library
            ? referenceSet.LibraryFileExtension
            : referenceSet.ExecutableFileExtension;
        var entryAssembly = $"{AssemblyName}{entryFileExtension}";
        var peDigest = ContentIdentity.Compute(peImage).Value;
        var files = new List<ArtifactFileDescriptor>
        {
            new("primary-assembly", entryAssembly, peImage.LongLength, peDigest)
        };
        if (portablePdb.Length > 0)
        {
            files.Add(new ArtifactFileDescriptor(
                "portable-pdb",
                $"{AssemblyName}.pdb",
                portablePdb.LongLength,
                ContentIdentity.Compute(portablePdb).Value));
        }

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
            referenceSet.ArtifactFormat,
            new ArtifactRuntimeRequirement(
                referenceSet.RuntimeFamily,
                [new FrameworkRequirement(
                    referenceSet.FrameworkName,
                    referenceSet.GetRuntimeFrameworkVersion())],
                referenceSet.Architecture,
                referenceSet.RequiredRuntimeFeatureTags),
            referenceSet.MetadataFeatureTags,
            outputKind,
            entryAssembly,
            entryPoint,
            files,
            Metadata: referenceSet.CompatibilityGroup is null
                ? null
                : new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["compatibilityGroup"] = referenceSet.CompatibilityGroup
                }));
        var artifactRef = manifest.ArtifactId;

        return new CompiledArtifact(
            artifactRef,
            referenceSet.ArtifactFormat,
            AssemblyName,
            referenceSet.Id,
            referenceSet.TargetFramework,
            peImage,
            portablePdb,
            manifest,
            files,
            buildIdentity);
    }

}
