using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using SharpLabNext.Contracts;
using ContractOutputKind = SharpLabNext.Contracts.BuildOutputKind;
using RoslynOutputKind = Microsoft.CodeAnalysis.OutputKind;

namespace SharpLabNext.Worker.Roslyn;

public sealed class VisualBasicBuildService(ReferenceSetProvider referenceSets, RoslynWorkerIdentity identity, CompilationLimits compilationLimits, AstLimits astLimits)
{
    private const string AssemblyName = "SharpLabNext.User";

    public async Task<WorkerBuildExecution> ExecuteAsync(BuildRequest request, CancellationToken cancellationToken)
    {
        RoslynCompilerIdentity.Ensure(identity, "Visual Basic compiler", GetLoadedCompilerVersion(), GetLoadedCompilerCommit());
        cancellationToken.ThrowIfCancellationRequested();
        var remaining = request.DeadlineUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            throw new BuildDeadlineExceededException("The build deadline has already elapsed.", cancellationToken);
        var workerLimit = TimeSpan.FromMilliseconds(compilationLimits.MaxBuildMilliseconds);
        if (remaining > workerLimit)
            remaining = workerLimit;

        using var deadlineCancellation = new CancellationTokenSource(remaining);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineCancellation.Token);
        try
        {
            return await ExecuteCoreAsync(request, linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadlineCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new BuildDeadlineExceededException("The build deadline elapsed.", deadlineCancellation.Token);
        }
    }

    public static string GetLoadedCompilerVersion()
    {
        var version = typeof(VisualBasicCompilation).Assembly.GetName().Version;
        return version is null
            ? "unknown" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    public static string? GetLoadedCompilerCommit() =>
        RoslynCompilerIdentity.GetCommit(typeof(VisualBasicCompilation).Assembly);

    internal static VisualBasicParseOptions CreateParseOptions(BuildOptions options)
    {
        var languageVersion = LanguageVersion.Default;
        if (!string.IsNullOrWhiteSpace(options.LanguageVersion) && !LanguageVersionFacts.TryParse(options.LanguageVersion, ref languageVersion))
        {
            throw new BuildRequestValidationException($"Visual Basic language version '{options.LanguageVersion}' is not supported by this Roslyn worker.");
        }

        var symbols = (options.PreprocessorSymbols ?? []).Select(static symbol => new KeyValuePair<string, object>(symbol, true));
        return new VisualBasicParseOptions(languageVersion, DocumentationMode.Parse, SourceCodeKind.Regular, symbols);
    }

    internal static ContractOutputKind ResolveOutputKind(ContractOutputKind requestedOutputKind) =>
        requestedOutputKind == ContractOutputKind.Auto
            ? ContractOutputKind.Library : requestedOutputKind;

    internal static VisualBasicCompilationOptions CreateCompilationOptions(BuildOptions options) =>
        new VisualBasicCompilationOptions(
            ResolveOutputKind(options.OutputKind) switch
            {
                ContractOutputKind.Console => RoslynOutputKind.ConsoleApplication,
                ContractOutputKind.Library => RoslynOutputKind.DynamicallyLinkedLibrary,
                ContractOutputKind.WindowsApplication => RoslynOutputKind.WindowsApplication,
                _ => throw new BuildRequestValidationException($"Output kind '{options.OutputKind}' is not supported.")
            }).WithOptimizationLevel(options.Optimize ? OptimizationLevel.Release : OptimizationLevel.Debug).WithOptionStrict(OptionStrict.On).WithOptionInfer(true).WithOptionExplicit(true).WithOptionCompareText(false).WithOverflowChecks(options.CheckOverflow).WithDeterministic(true).WithConcurrentBuild(true).WithMetadataImportOptions(MetadataImportOptions.Public).WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default).WithReportSuppressedDiagnostics(false);

    private async Task<WorkerBuildExecution> ExecuteCoreAsync(BuildRequest request, CancellationToken cancellationToken)
    {
        var workspace = WorkspaceValidator.Validate(request, compilationLimits, identity);
        if (!StringComparer.Ordinal.Equals(workspace.Snapshot.LanguageId, "visual-basic"))
            throw new BuildRequestValidationException("VisualBasicBuildService only accepts languageId 'visual-basic'.");
        var referenceSet = await referenceSets.GetAsync(request.ReferenceSetId, cancellationToken).ConfigureAwait(false);
        var buildIdentity = new BuildIdentity(identity.ReleaseId, "visual-basic", identity.ToolchainId, identity.CompilerVersion, GetLoadedCompilerCommit(), referenceSet.Definition.Id, identity.WorkerImageId);
        var parseOptions = CreateParseOptions(workspace.Options);
        var syntaxTrees = workspace.OrderedFiles.Select(file => (SyntaxTree)VisualBasicSyntaxTree.ParseText(SourceText.From(file.Text, Encoding.UTF8, SourceHashAlgorithm.Sha256), parseOptions, file.Path, cancellationToken)).ToArray();

        if (request.Target == BuildTarget.Ast)
        {
            var document = RoslynAstConverter.Convert(workspace, syntaxTrees, identity.ToolchainId, astLimits, cancellationToken);
            return new WorkerBuildExecution(new AstResult(document, buildIdentity), null);
        }

        var outputKind = ResolveOutputKind(workspace.Options.OutputKind);
        var compilation = VisualBasicCompilation.Create(AssemblyName, syntaxTrees, referenceSet.References, CreateCompilationOptions(workspace.Options));
        var preEmitDiagnostics = compilation.GetDiagnostics(cancellationToken);
        using var peStream = new LimitedMemoryStream(compilationLimits.MaxPeBytes);
        using var pdbStream = workspace.Options.EmitPortablePdb
            ? new LimitedMemoryStream(compilationLimits.MaxPdbBytes) : null;
        var emitOptions = workspace.Options.EmitPortablePdb
            ? new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb, pdbFilePath: $"{AssemblyName}.pdb") : null;
        var emitResult = compilation.Emit(peStream, pdbStream, options: emitOptions, cancellationToken: cancellationToken);
        var diagnostics = RoslynDiagnosticConverter.Convert(preEmitDiagnostics.Concat(emitResult.Diagnostics), workspace.Snapshot.Revision, workspace.Snapshot.SelectionRevision, compilationLimits.MaxDiagnostics);
        if (request.Target == BuildTarget.CompileCheck)
        {
            return new WorkerBuildExecution(new CompilationCheckResult(emitResult.Success, diagnostics, buildIdentity, workspace.Snapshot.Revision, workspace.Snapshot.SelectionRevision), null);
        }

        if (!emitResult.Success)
        {
            var compilationHadErrors = preEmitDiagnostics.Any(static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
            return new WorkerBuildExecution(new BuildResult(compilationHadErrors ? BuildOutcome.CompilationFailed : BuildOutcome.EmitFailed, null, diagnostics, buildIdentity, workspace.Snapshot.Revision, workspace.Snapshot.SelectionRevision), null);
        }

        var peImage = peStream.ToArray();
        var portablePdb = pdbStream?.ToArray() ?? [];
        var artifact = CSharpBuildService.CreateArtifact(peImage, portablePdb, referenceSet.Definition, buildIdentity, outputKind, compilation.GetEntryPoint(cancellationToken)?.ToDisplayString(SymbolDisplayFormat.VisualBasicErrorMessageFormat));
        return new WorkerBuildExecution(new BuildResult(BuildOutcome.Succeeded, artifact.ArtifactRef, diagnostics, buildIdentity, workspace.Snapshot.Revision, workspace.Snapshot.SelectionRevision), artifact);
    }
}

public sealed class RoslynBuildService(CSharpBuildService csharp, VisualBasicBuildService visualBasic, RoslynWorkerIdentity identity)
{
    public Task<WorkerBuildExecution> ExecuteAsync(BuildRequest request, CancellationToken cancellationToken)
    {
        if (!identity.SupportsLanguage(request.Workspace.LanguageId))
        {
            throw new BuildRequestValidationException($"This Roslyn worker does not support languageId '{request.Workspace.LanguageId}'.");
        }

        return request.Workspace.LanguageId switch
        {
            "csharp" => csharp.ExecuteAsync(request, cancellationToken),
            "visual-basic" => visualBasic.ExecuteAsync(request, cancellationToken),
            _ => throw new BuildRequestValidationException("The Roslyn worker only accepts C# or Visual Basic builds.")
        };
    }
}
