using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Pchp.CodeAnalysis;
using SharpLabNext.LanguageWorker.Sdk;
using ContractDiagnostic = SharpLabNext.Contracts.Diagnostic;
using ContractDiagnosticSeverity = SharpLabNext.Contracts.DiagnosticSeverity;

namespace SharpLabNext.Worker.PeachPie;

public sealed class PeachPieCompiler(PeachPieReferenceSetProvider referenceSets, PeachPieWorkerSettings settings, LanguageWorkerCapabilityManifest manifest)
{
    internal const string BootstrapFileName = "__sharplabnext_bootstrap.php";

    public async Task<PeachPieCompilerResponse> CompileAsync(SharpLabNext.Contracts.BuildRequest request, CancellationToken cancellationToken)
    {
        var workspace = PeachPieWorkspaceValidator.Validate(request, manifest);
        var referenceSet = referenceSets.Get(request.ReferenceSetId);
        ValidateSupportAssembly(settings.RuntimeAssemblyPath, PeachPieToolchain.RuntimeAssemblyName);
        ValidateSupportAssembly(settings.LibraryAssemblyPath, PeachPieToolchain.LibraryAssemblyName);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(settings.WorkRoot);
        var parseOptions = PhpParseOptions.Default.WithLanguageVersion(new Version(8, 5));
        var syntaxTrees = CreateSyntaxTrees(workspace, settings.WorkRoot, parseOptions);
        var references = referenceSet.ReferenceAssemblyPaths.Append(settings.RuntimeAssemblyPath).Append(settings.LibraryAssemblyPath).Distinct(PathComparer()).Select(static path => MetadataReference.CreateFromFile(path)).ToImmutableArray();
        var compilationOptions = new PhpCompilationOptions(OutputKind.ConsoleApplication, baseDirectory: settings.WorkRoot, sdkDirectory: null, parseOptions: parseOptions);
        if (workspace.Options.Optimize)
            compilationOptions = compilationOptions.WithOptimizationLevel(OptimizationLevel.Release);
        else
            compilationOptions = compilationOptions.WithOptimizationLevel(OptimizationLevel.Debug);
        compilationOptions = compilationOptions.WithDeterministic(true);
        var compilation = PhpCompilation.Create(PeachPieToolchain.AssemblyName, syntaxTrees, references, options: compilationOptions);

        var diagnostics = compilation.GetParseDiagnostics(cancellationToken);
        if (!HasErrors(diagnostics))
        {
            cancellationToken.ThrowIfCancellationRequested();
            diagnostics = diagnostics.AddRange(await compilation.BindAndAnalyseTask().ConfigureAwait(false));
        }
        if (HasErrors(diagnostics))
        {
            return new PeachPieCompilerResponse(Environment.ProcessId, CompilationSucceeded: false, EmitSucceeded: false, PeImage: [], ConvertDiagnostics(diagnostics, workspace, settings.WorkRoot));
        }
        if (request.Target == SharpLabNext.Contracts.BuildTarget.CompileCheck)
        {
            return new PeachPieCompilerResponse(Environment.ProcessId, CompilationSucceeded: true, EmitSucceeded: false, PeImage: [], ConvertDiagnostics(diagnostics, workspace, settings.WorkRoot));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var pe = new MemoryStream();
        var emit = compilation.Emit(peStream: pe, cancellationToken: cancellationToken);
        diagnostics = diagnostics.AddRange(emit.Diagnostics);
        var converted = ConvertDiagnostics(diagnostics, workspace, settings.WorkRoot);
        if (!emit.Success)
        {
            return new PeachPieCompilerResponse(Environment.ProcessId, CompilationSucceeded: true, EmitSucceeded: false, PeImage: [], converted);
        }
        if (pe.Length > manifest.Limits.MaximumArtifactBytes)
            throw new PeachPieBuildOutputLimitExceededException("The PeachPie compiler output exceeds the artifact limit.");
        return new PeachPieCompilerResponse(Environment.ProcessId, CompilationSucceeded: true, EmitSucceeded: true, PeImage: pe.ToArray(), converted);
    }

    private static ImmutableArray<PhpSyntaxTree> CreateSyntaxTrees(ValidatedPeachPieWorkspace workspace, string root, PhpParseOptions parseOptions)
    {
        var builder = ImmutableArray.CreateBuilder<PhpSyntaxTree>(workspace.OrderedFiles.Count + 1);
        var escapedEntryPath = workspace.ActiveFile.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
        var bootstrap = $"<?php\nrequire '{escapedEntryPath}';\nreturn 0;\n";
        var bootstrapPath = Path.Combine(root, BootstrapFileName);
        builder.Add(PhpSyntaxTree.ParseCode(SourceText.From(bootstrap, Encoding.UTF8), parseOptions, parseOptions, bootstrapPath));
        foreach (var file in workspace.OrderedFiles)
        {
            var path = Path.GetFullPath(Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            builder.Add(PhpSyntaxTree.ParseCode(SourceText.From(file.Text, Encoding.UTF8), parseOptions, parseOptions, path));
        }
        return builder.MoveToImmutable();
    }

    private static List<ContractDiagnostic> ConvertDiagnostics(IEnumerable<Microsoft.CodeAnalysis.Diagnostic> diagnostics, ValidatedPeachPieWorkspace workspace, string root)
    {
        var converted = new List<ContractDiagnostic>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var workspacePaths = workspace.OrderedFiles.Select(static file => file.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var diagnostic in diagnostics)
        {
            if (converted.Count >= 1_000)
                break;
            var location = diagnostic.Location;
            string? path = null;
            SharpLabNext.Contracts.TextRange? range = null;
            if (location is not null && location.IsInSource)
            {
                var span = location.GetLineSpan();
                var absolutePath = Path.GetFullPath(span.Path);
                var relativePath = Path.GetRelativePath(root, absolutePath).Replace('\\', '/');
                if (relativePath.StartsWith("../", StringComparison.Ordinal) || relativePath == ".." || Path.IsPathRooted(relativePath))
                {
                    throw new PeachPieCompilerFailureException("The PeachPie compiler returned a diagnostic outside the managed workspace.");
                }
                if (StringComparer.OrdinalIgnoreCase.Equals(relativePath, BootstrapFileName))
                    continue;
                path = workspacePaths.FirstOrDefault(candidate => StringComparer.OrdinalIgnoreCase.Equals(candidate, relativePath));
                if (path is null)
                {
                    throw new PeachPieCompilerFailureException("The PeachPie compiler returned a diagnostic for an unknown source file.");
                }
                range = new SharpLabNext.Contracts.TextRange(span.StartLinePosition.Line, span.StartLinePosition.Character, span.EndLinePosition.Line, span.EndLinePosition.Character);
            }
            var message = Sanitize(diagnostic.GetMessage(CultureInfo.InvariantCulture));
            var key = string.Create(CultureInfo.InvariantCulture, $"{diagnostic.Id}|{diagnostic.Severity}|{path}|{range}|{message}");
            if (!seen.Add(key))
                continue;
            converted.Add(new ContractDiagnostic(
                "peachpie",
                diagnostic.Id,
                diagnostic.Severity switch
                {
                    Microsoft.CodeAnalysis.DiagnosticSeverity.Hidden => ContractDiagnosticSeverity.Hidden,
                    Microsoft.CodeAnalysis.DiagnosticSeverity.Info => ContractDiagnosticSeverity.Information,
                    Microsoft.CodeAnalysis.DiagnosticSeverity.Warning => ContractDiagnosticSeverity.Warning,
                    Microsoft.CodeAnalysis.DiagnosticSeverity.Error => ContractDiagnosticSeverity.Error,
                    _ => ContractDiagnosticSeverity.Information
                },
                message,
                path,
                range,
                [],
                [],
                workspace.Snapshot.Revision,
                workspace.Snapshot.SelectionRevision));
        }
        return converted;
    }

    private static bool HasErrors(IEnumerable<Microsoft.CodeAnalysis.Diagnostic> diagnostics) => diagnostics.Any(static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);

    private static void ValidateSupportAssembly(string path, string expectedFileName)
    {
        if (!File.Exists(path) || !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(path), expectedFileName))
            throw new PeachPieCompilerFailureException($"The pinned support assembly '{expectedFileName}' is unavailable.");
    }

    private static StringComparer PathComparer() => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string Sanitize(string message)
    {
        var value = message.Replace('\r', ' ').Replace('\n', ' ');
        return value.Length <= 2_048 ? value : value[..2_048];
    }
}
