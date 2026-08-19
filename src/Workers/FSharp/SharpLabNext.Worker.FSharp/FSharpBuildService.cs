using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Contracts;
using SharpLabNext.Worker.FSharp.Compiler;

namespace SharpLabNext.Worker.FSharp;

public sealed class FSharpBuildService(
    FSharpReferenceSetProvider referenceSets,
    FSharpCompilerFacade compiler,
    FSharpWorkerSettings settings)
{
    private const string AssemblyName = "SharpLabNext.User";

    public async Task<FSharpWorkerBuildExecution> ExecuteAsync(
        BuildRequest request,
        CancellationToken cancellationToken)
    {
        var remaining = request.DeadlineUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            throw new FSharpBuildDeadlineExceededException("The build deadline has elapsed.", cancellationToken);
        remaining = TimeSpan.FromMilliseconds(Math.Min(remaining.TotalMilliseconds, settings.CompilationLimits.MaxBuildMilliseconds));
        using var deadline = new CancellationTokenSource(remaining);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await ExecuteCoreAsync(request, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new FSharpBuildDeadlineExceededException("The F# build deadline elapsed.", deadline.Token);
        }
    }

    private async Task<FSharpWorkerBuildExecution> ExecuteCoreAsync(
        BuildRequest request,
        CancellationToken cancellationToken)
    {
        var workspace = FSharpWorkspaceValidator.Validate(request, settings.CompilationLimits);
        var referenceSet = await referenceSets.GetAsync(request.ReferenceSetId, cancellationToken).ConfigureAwait(false);
        var identity = CreateIdentity(referenceSet.Definition.Id);
        await using var temporary = await TemporaryFSharpWorkspace.CreateAsync(
            settings.WorkRoot,
            workspace.OrderedFiles,
            cancellationToken);
        var orderedPaths = workspace.OrderedFiles.Select(file => temporary.Paths[file.Path]).ToArray();
        var projectInput = CreateProjectInput(temporary.Root, orderedPaths, referenceSet, workspace.Options, workspace.Snapshot.Revision);
        foreach (var file in workspace.OrderedFiles)
        {
            var rejected = await FSharpSourceSafety.FindRejectedDirectiveAsync(
                compiler,
                projectInput,
                temporary.Paths[file.Path],
                file.Text,
                cancellationToken).ConfigureAwait(false);
            if (rejected is not null)
                throw new FSharpBuildRequestValidationException($"F# directive '#{rejected}' is not allowed in managed workspaces.");
        }

        if (request.Target == BuildTarget.Ast)
            return await CreateAstAsync(workspace, projectInput, temporary, identity, cancellationToken).ConfigureAwait(false);

        var outputPath = Path.Combine(temporary.Root, $"{AssemblyName}.dll");
        var arguments = CreateCompilerArguments(outputPath, orderedPaths, referenceSet, workspace.Options);
        var response = await compiler.CompileAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(response.TerminatingException) &&
            !response.Diagnostics.Any(static diagnostic => diagnostic.Severity == CompilerDiagnosticSeverity.Error))
        {
            throw new FSharpCompilerFailureException("The pinned F# compiler terminated unexpectedly.");
        }
        var diagnostics = ConvertDiagnostics(response.Diagnostics, workspace, temporary);
        var succeeded = !response.Diagnostics.Any(static diagnostic => diagnostic.Severity == CompilerDiagnosticSeverity.Error) &&
            File.Exists(outputPath);
        if (request.Target == BuildTarget.CompileCheck)
        {
            return new FSharpWorkerBuildExecution(
                new CompilationCheckResult(
                    succeeded,
                    diagnostics,
                    identity,
                    workspace.Snapshot.Revision,
                    workspace.Snapshot.SelectionRevision),
                null);
        }

        if (!succeeded)
        {
            var outcome = response.Diagnostics.Any(static diagnostic => diagnostic.Severity == CompilerDiagnosticSeverity.Error)
                ? BuildOutcome.CompilationFailed
                : BuildOutcome.EmitFailed;
            return new FSharpWorkerBuildExecution(
                new BuildResult(
                    outcome,
                    null,
                    diagnostics,
                    identity,
                    workspace.Snapshot.Revision,
                    workspace.Snapshot.SelectionRevision),
                null);
        }

        var pe = await ReadBoundedAsync(outputPath, settings.CompilationLimits.MaxPeBytes, cancellationToken);
        var pdbPath = Path.ChangeExtension(outputPath, ".pdb");
        var pdb = workspace.Options.EmitPortablePdb && File.Exists(pdbPath)
            ? await ReadBoundedAsync(pdbPath, settings.CompilationLimits.MaxPdbBytes, cancellationToken)
            : [];
        var fsharpCore = await ReadBoundedAsync(
            referenceSet.FSharpCoreAssemblyPath,
            settings.CompilationLimits.MaxPeBytes,
            cancellationToken);
        var artifact = CreateArtifact(
            pe,
            pdb,
            fsharpCore,
            referenceSet,
            identity,
            workspace.Options.OutputKind,
            workspace.OrderedFiles);
        return new FSharpWorkerBuildExecution(
            new BuildResult(
                BuildOutcome.Succeeded,
                artifact.ArtifactRef,
                diagnostics,
                identity,
                workspace.Snapshot.Revision,
                workspace.Snapshot.SelectionRevision),
            artifact);
    }

    internal static FSharpProjectInput CreateProjectInput(
        string root,
        string[] sourcePaths,
        LoadedFSharpReferenceSet referenceSet,
        BuildOptions options,
        long revision) => new(
            Path.Combine(root, "SharpLabNext.User.fsproj"),
            sourcePaths,
            CreateProjectOptions(referenceSet, options),
            DateTime.UnixEpoch.AddTicks(Math.Max(0, revision)));

    internal static string[] CreateProjectOptions(LoadedFSharpReferenceSet referenceSet, BuildOptions options)
    {
        var result = new List<string>
        {
            options.OutputKind switch
            {
                BuildOutputKind.Console => "--target:exe",
                BuildOutputKind.Library => "--target:library",
                BuildOutputKind.WindowsApplication => "--target:winexe",
                _ => throw new FSharpBuildRequestValidationException("The output kind is not supported.")
            },
            "--targetprofile:netcore",
            "--noframework",
            "--nowin32manifest",
            "--fullpaths",
            "--flaterrors",
            options.Optimize ? "--optimize+" : "--optimize-",
            options.CheckOverflow ? "--checked+" : "--checked-"
        };
        if (!string.IsNullOrWhiteSpace(options.LanguageVersion) && options.LanguageVersion != "default")
            result.Add($"--langversion:{options.LanguageVersion}");
        foreach (var symbol in options.PreprocessorSymbols ?? [])
            result.Add($"--define:{symbol}");
        result.AddRange(referenceSet.ReferenceAssemblyPaths.Select(static path => $"-r:{path}"));
        result.Add($"-r:{referenceSet.FSharpCoreAssemblyPath}");
        return result.ToArray();
    }

    private async Task<FSharpWorkerBuildExecution> CreateAstAsync(
        ValidatedFSharpWorkspace workspace,
        FSharpProjectInput projectInput,
        TemporaryFSharpWorkspace temporary,
        BuildIdentity identity,
        CancellationToken cancellationToken)
    {
        var children = new List<AstNode>(workspace.OrderedFiles.Count);
        var truncated = false;
        var remainingNodes = settings.AstLimits.MaxNodes;
        foreach (var file in workspace.OrderedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await compiler.ParseAstAsync(
                projectInput,
                temporary.Paths[file.Path],
                file.Text,
                Math.Max(1, remainingNodes),
                settings.AstLimits.MaxDepth,
                Math.Max(1, settings.AstLimits.MaxUtf8Bytes / workspace.OrderedFiles.Count),
                settings.AstLimits.MaxTextPreviewCharacters,
                cancellationToken).ConfigureAwait(false);
            var root = ConvertAstNode(response.Root);
            var properties = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["path"] = file.Path,
                ["version"] = file.Version.ToString(CultureInfo.InvariantCulture),
                ["active"] = (file.Path == workspace.ActiveFile).ToString().ToLowerInvariant()
            };
            children.Add(new AstNode("Document", root.Range, root.FullRange, properties, [root]));
            remainingNodes -= CountNodes(root);
            truncated |= response.Truncated || remainingNodes <= 0;
            if (remainingNodes <= 0)
                break;
        }
        var rootNode = new AstNode(
            "Workspace",
            new TextRange(0, 0, 0, 0),
            null,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["sourceOrder"] = string.Join(";", workspace.OrderedFiles.Select(static file => file.Path))
            },
            children);
        return new FSharpWorkerBuildExecution(
            new AstResult(
                new AstDocument(
                    "fsharp",
                    settings.Identity.ToolchainId,
                    workspace.Snapshot.Revision,
                    rootNode,
                    truncated),
                identity),
            null);
    }

    private static string[] CreateCompilerArguments(
        string outputPath,
        string[] sourcePaths,
        LoadedFSharpReferenceSet referenceSet,
        BuildOptions options)
    {
        var arguments = new List<string> { "fsc.exe" };
        arguments.Add(options.OutputKind switch
        {
            BuildOutputKind.Console => "--target:exe",
            BuildOutputKind.Library => "--target:library",
            BuildOutputKind.WindowsApplication => "--target:winexe",
            _ => throw new FSharpBuildRequestValidationException("The output kind is not supported.")
        });
        arguments.AddRange(CreateProjectOptions(referenceSet, options).Skip(1));
        arguments.Add(options.EmitPortablePdb ? "--debug:portable" : "--debug-");
        arguments.Add("--deterministic+");
        arguments.Add("--utf8output");
        arguments.Add("--warn:3");
        arguments.Add($"--out:{outputPath}");
        arguments.AddRange(sourcePaths);
        return arguments.ToArray();
    }

    private Diagnostic[] ConvertDiagnostics(
        IReadOnlyList<FSharpCompilerDiagnostic> diagnostics,
        ValidatedFSharpWorkspace workspace,
        TemporaryFSharpWorkspace temporary)
    {
        var paths = temporary.Paths.ToDictionary(static pair => Path.GetFullPath(pair.Value), static pair => pair.Key, StringComparer.OrdinalIgnoreCase);
        return diagnostics
            .Take(settings.CompilationLimits.MaxDiagnostics)
            .Select(diagnostic => new Diagnostic(
                "fsharp",
                diagnostic.Code,
                diagnostic.Severity switch
                {
                    CompilerDiagnosticSeverity.Hidden => DiagnosticSeverity.Hidden,
                    CompilerDiagnosticSeverity.Information => DiagnosticSeverity.Information,
                    CompilerDiagnosticSeverity.Warning => DiagnosticSeverity.Warning,
                    CompilerDiagnosticSeverity.Error => DiagnosticSeverity.Error,
                    _ => DiagnosticSeverity.Information
                },
                Sanitize(diagnostic.Message),
                ResolvePath(diagnostic.FilePath, paths),
                new TextRange(
                    diagnostic.Range.StartLine,
                    diagnostic.Range.StartCharacter,
                    diagnostic.Range.EndLine,
                    diagnostic.Range.EndCharacter),
                [],
                [],
                workspace.Snapshot.Revision,
                workspace.Snapshot.SelectionRevision))
            .ToArray();
    }

    private BuildIdentity CreateIdentity(string referenceSetId) => new(
        settings.Identity.ReleaseId,
        "fsharp",
        settings.Identity.ToolchainId,
        settings.Identity.CompilerVersion,
        settings.Identity.CompilerCommit,
        referenceSetId,
        settings.Identity.WorkerImageId);

    private static FSharpCompiledArtifact CreateArtifact(
        byte[] pe,
        byte[] pdb,
        byte[] fsharpCore,
        LoadedFSharpReferenceSet referenceSet,
        BuildIdentity identity,
        BuildOutputKind outputKind,
        IReadOnlyList<ValidatedFSharpWorkspaceFile> sourceOrder)
    {
        var files = new List<ArtifactFileDescriptor>
        {
            new("primary-assembly", $"{AssemblyName}.dll", pe.LongLength, ContentIdentity.Compute(pe).Value)
        };
        if (pdb.Length > 0)
            files.Add(new ArtifactFileDescriptor("portable-pdb", $"{AssemblyName}.pdb", pdb.LongLength, ContentIdentity.Compute(pdb).Value));
        files.Add(new ArtifactFileDescriptor(
            "support-assembly",
            "FSharp.Core.dll",
            fsharpCore.LongLength,
            ContentIdentity.Compute(fsharpCore).Value));
        var placeholder = new ArtifactRef($"sha256:{new string('0', ArtifactStoreProtocol.Sha256HexLength)}");
        var sourceOrderText = string.Join("\n", sourceOrder.Select(static file => file.Path));
        var manifest = ArtifactIdentity.WithComputedId(new ArtifactManifest(
            ContractSchemaVersions.ArtifactManifest,
            placeholder,
            new ArtifactProducer(
                identity.ReleaseId,
                identity.LanguageId,
                identity.ToolchainId,
                identity.CompilerVersion,
                identity.CompilerCommit,
                identity.WorkerImageId),
            referenceSet.Definition.Id,
            referenceSet.Definition.TargetFramework,
            "dotnet-managed-pe-v1",
            new ArtifactRuntimeRequirement(
                "coreclr",
                [new FrameworkRequirement("Microsoft.NETCore.App", referenceSet.Definition.FrameworkVersion)],
                "anycpu",
                []),
            [],
            outputKind,
            $"{AssemblyName}.dll",
            null,
            files,
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["fsharpCompilerServiceVersion"] = FSharpCompilerFacade.CompilerVersion,
                ["fsharpCorePackageVersion"] = FSharpCompilerFacade.FSharpCorePackageVersion,
                ["fsharpCoreProductVersion"] = referenceSet.FSharpCoreProductVersion,
                ["fsharpCoreLinkMode"] = "bundled-support-assembly",
                ["sourceOrderSha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sourceOrderText)))
            }));
        return new FSharpCompiledArtifact(
            manifest.ArtifactId,
            manifest.ArtifactFormat,
            AssemblyName,
            referenceSet.Definition.Id,
            referenceSet.Definition.TargetFramework,
            pe,
            pdb,
            fsharpCore,
            manifest,
            files,
            identity);
    }

    private static AstNode ConvertAstNode(FSharpAstNode node) => new(
        node.Kind,
        new TextRange(node.Range.StartLine, node.Range.StartCharacter, node.Range.EndLine, node.Range.EndCharacter),
        null,
        node.Properties.ToDictionary(static pair => pair.Key, static pair => (string?)pair.Value, StringComparer.Ordinal),
        node.Children.Select(ConvertAstNode).ToArray());

    private static int CountNodes(AstNode root) => 1 + root.Children.Sum(CountNodes);

    private static async Task<byte[]> ReadBoundedAsync(string path, int limit, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length > limit)
            throw new FSharpBuildOutputLimitExceededException($"Compiler output exceeds the {limit} byte limit.");
        return await File.ReadAllBytesAsync(path, cancellationToken);
    }

    private static string? ResolvePath(string filePath, IReadOnlyDictionary<string, string> paths)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;
        var fullPath = Path.GetFullPath(filePath);
        return paths.GetValueOrDefault(fullPath);
    }

    private static string Sanitize(string message)
    {
        var value = message.Replace('\r', ' ').Replace('\n', ' ');
        return value.Length <= 2_048 ? value : value[..2_048];
    }
}
