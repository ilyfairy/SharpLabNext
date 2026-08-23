using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using SharpLabNext.ArtifactProcessing.Protocol;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.ArtifactWorker;
using SharpLabNext.Contracts;
using SharpLabNext.RuntimeProtocol;

namespace SharpLabNext.ArtifactWorker.Tests;

public sealed class ProcessorProcessTests
{
    [Fact]
    public async Task RuntimeInstrumentationRewritesManagedPeAndPreservesPortablePdb()
    {
        var root = TestSettings.CreateRoot();
        try
        {
            var artifact = PrepareArtifact(root);
            var runner = new ArtifactProcessorProcessRunner(TestSettings.Create(root));
            var result = await runner.RunAsync(
                artifact,
                ProcessorOperation.RuntimeInstrumentationV1,
                includeSequencePoints: true,
                includeCompilerGeneratedMembers: true,
                includeMetadataTokens: false,
                maxCharacters: 1_000_000,
                maxFindings: 1_000,
                DateTimeOffset.UtcNow.AddSeconds(15),
                TestContext.Current.CancellationToken,
                ProcessorProtocol.RuntimeInstrumentationProfileId);

            Assert.Equal(ProcessorOutcome.Succeeded, result.Response.Outcome);
            Assert.True(result.Response.RewriteApplied);
            Assert.True(result.Response.InstrumentationPointCount > 0);
            Assert.True(File.Exists(result.OutputPath));
            Assert.NotNull(result.PortablePdbOutputPath);
            Assert.True(File.Exists(result.PortablePdbOutputPath));

            await using var image = File.OpenRead(result.OutputPath);
            using var pe = new PEReader(image);
            var metadata = pe.GetMetadataReader();
            var flowCalls = metadata.MemberReferences
                .Select(metadata.GetMemberReference)
                .Where(reference => reference.Parent.Kind == HandleKind.TypeReference)
                .Where(reference =>
                {
                    var type = metadata.GetTypeReference((TypeReferenceHandle)reference.Parent);
                    return metadata.GetString(type.Namespace) == "SharpLab.Runtime.Internal" &&
                           metadata.GetString(type.Name) == "Flow";
                })
                .Select(reference => metadata.GetString(reference.Name))
                .ToHashSet(StringComparer.Ordinal);
            Assert.Contains("ReportMethod", flowCalls);
            Assert.Contains("ReportSequencePoint", flowCalls);
            Assert.Contains("ReportBranch", flowCalls);

            var runIl = await runner.RunAsync(
                artifact with
                {
                    AssemblyPath = result.OutputPath,
                    PortablePdbPath = result.PortablePdbOutputPath
                },
                ProcessorOperation.Il,
                includeSequencePoints: true,
                includeCompilerGeneratedMembers: true,
                includeMetadataTokens: true,
                maxCharacters: 1_000_000,
                maxFindings: 1_000,
                DateTimeOffset.UtcNow.AddSeconds(15),
                TestContext.Current.CancellationToken);
            Assert.Equal(ProcessorOutcome.Succeeded, runIl.Response.Outcome);
            var runIlText = await File.ReadAllTextAsync(
                runIl.OutputPath,
                TestContext.Current.CancellationToken);
            Assert.Contains("SharpLab.Runtime.Internal.Flow::ReportSequencePoint", runIlText, StringComparison.Ordinal);

            var runnerPath = Path.Combine(AppContext.BaseDirectory, "SharpLabNext.Runner.dll");
            var startInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(runnerPath);
            startInfo.ArgumentList.Add(result.OutputPath);
            startInfo.Environment["SHARPLABNEXT_INSTRUMENTATION"] = "execution-flow";
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start the runtime Runner.");
            var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            var frames = new List<RuntimeFrame>();
            var frameReader = new RuntimeFrameLogReader(process.StandardOutput.BaseStream);
            while (await frameReader.ReadAsync(
                       cancellationToken: TestContext.Current.CancellationToken) is { } frame)
            {
                frames.Add(frame);
            }
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.Equal(0, process.ExitCode);
            Assert.Empty(await stderr);
            Assert.Contains(frames, frame =>
                frame.Kind == RuntimeFrameKind.Stdout &&
                Encoding.UTF8.GetString(frame.Payload.Span).Contains("42", StringComparison.Ordinal));
            var flowFrames = frames.Where(static frame => frame.Kind == RuntimeFrameKind.Flow).ToArray();
            Assert.NotEmpty(flowFrames);
            Assert.All(flowFrames, frame =>
                RuntimeStructuredPayloadCodec.Validate(frame.Kind, frame.Payload.Span));
        }
        finally
        {
            TestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RuntimeInstrumentationRunsCanonicalCapabilityProbeExecutionFlow()
    {
        var root = TestSettings.CreateRoot();
        try
        {
            var artifact = PrepareArtifact(root, RuntimeCapabilityProbePath());
            var runner = new ArtifactProcessorProcessRunner(TestSettings.Create(root));
            var result = await runner.RunAsync(
                artifact,
                ProcessorOperation.RuntimeInstrumentationV1,
                includeSequencePoints: true,
                includeCompilerGeneratedMembers: true,
                includeMetadataTokens: false,
                maxCharacters: 1_000_000,
                maxFindings: 1_000,
                DateTimeOffset.UtcNow.AddSeconds(15),
                TestContext.Current.CancellationToken,
                ProcessorProtocol.RuntimeInstrumentationProfileId);

            Assert.Equal(ProcessorOutcome.Succeeded, result.Response.Outcome);
            Assert.True(result.Response.RewriteApplied);
            Assert.True(result.Response.InstrumentationPointCount > 0);
            AssertInstrumentationBodyShape(artifact.AssemblyPath, result.OutputPath);

            var runnerPath = Path.Combine(AppContext.BaseDirectory, "SharpLabNext.Runner.dll");
            var startInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(runnerPath);
            startInfo.ArgumentList.Add(result.OutputPath);
            startInfo.ArgumentList.Add("execution-flow");
            startInfo.Environment["SHARPLABNEXT_INSTRUMENTATION"] = "execution-flow";
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start the runtime Runner.");
            var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            var frames = new List<RuntimeFrame>();
            var frameReader = new RuntimeFrameLogReader(process.StandardOutput.BaseStream);
            while (await frameReader.ReadAsync(
                       cancellationToken: TestContext.Current.CancellationToken) is { } frame)
            {
                frames.Add(frame);
            }
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            var stderrText = await stderr;
            var structuredErrors = frames
                .Where(static frame => frame.Kind is RuntimeFrameKind.Exception or RuntimeFrameKind.ProtocolError)
                .Select(static frame => Encoding.UTF8.GetString(frame.Payload.Span))
                .ToArray();
            Assert.True(
                process.ExitCode == 0,
                $"Runner exited {process.ExitCode}. stderr: {stderrText}; structured: " +
                string.Join(" | ", structuredErrors));
            Assert.Empty(stderrText);
            var flow = frames
                .Where(static frame => frame.Kind == RuntimeFrameKind.Flow)
                .Select(static frame => RuntimeStructuredPayloadCodec.DeserializeFlow(frame.Payload.Span))
                .ToArray();
            Assert.Contains(flow, static payload => payload.EventKind == "sequence-point");
            Assert.Contains(flow, static payload => payload.EventKind == "branch");
            var sourceRanges = flow
                .Where(static payload =>
                    payload.Range is not null &&
                    !string.IsNullOrWhiteSpace(payload.DocumentPath))
                .Select(static payload =>
                    $"{payload.DocumentPath}\0{payload.Range!.StartLine}\0{payload.Range.StartColumn}\0" +
                    $"{payload.Range.EndLine}\0{payload.Range.EndColumn}")
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            Assert.True(
                sourceRanges.Length >= 2,
                $"Observed only {sourceRanges.Length} distinct source ranges.");
            Assert.Single(frames, static frame => frame.Kind == RuntimeFrameKind.Exit);
        }
        finally
        {
            TestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task IlRenderUsesIsolatedIlSpyAndProducesPdbLinkedRanges()
    {
        var root = TestSettings.CreateRoot();
        try
        {
            var artifact = PrepareArtifact(root);
            var runner = new ArtifactProcessorProcessRunner(TestSettings.Create(root));
            var result = await runner.RunAsync(
                artifact,
                ProcessorOperation.Il,
                includeSequencePoints: true,
                includeCompilerGeneratedMembers: true,
                includeMetadataTokens: true,
                maxCharacters: 1_000_000,
                maxFindings: 1_000,
                DateTimeOffset.UtcNow.AddSeconds(15),
                TestContext.Current.CancellationToken);

            Assert.True(
                result.Response.Outcome == ProcessorOutcome.Succeeded,
                $"{result.Response.Outcome}: {result.Response.PublicMessage}");
            var text = await File.ReadAllTextAsync(
                result.OutputPath,
                TestContext.Current.CancellationToken);
            Assert.Contains("ICSharpCode.Decompiler 10.1.0.8386", text, StringComparison.Ordinal);
            var assemblyIndex = text.IndexOf(
                ".assembly SharpLabNext.Worker.Artifacts.Default.Fixture",
                StringComparison.Ordinal);
            var classIndex = text.IndexOf(".class", StringComparison.Ordinal);
            Assert.True(assemblyIndex >= 0, "Generated IL does not contain the assembly manifest.");
            Assert.True(classIndex > assemblyIndex, "The assembly manifest must precede type definitions.");
            Assert.Contains(".ver 1:0:0:0", text, StringComparison.Ordinal);
            Assert.Contains(".method", text, StringComparison.Ordinal);
            Assert.DoesNotContain("// sequence point:", text, StringComparison.Ordinal);
            Assert.DoesNotContain('\t', text);
            Assert.NotEmpty(result.Response.LinkedRanges);
            Assert.All(result.Response.LinkedRanges, range =>
                Assert.False(Path.IsPathFullyQualified(range.SourceFilePath ?? string.Empty)));
            var visibleLines = text.Split('\n');
            Assert.All(result.Response.LinkedRanges, range =>
                Assert.InRange(range.OutputRange.StartLine, 0, visibleLines.Length - 1));
        }
        finally
        {
            TestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task DecompiledCSharpUsesPinnedIlSpy()
    {
        var root = TestSettings.CreateRoot();
        try
        {
            var artifact = PrepareArtifact(root);
            var runner = new ArtifactProcessorProcessRunner(TestSettings.Create(root));
            var result = await runner.RunAsync(
                artifact,
                ProcessorOperation.DecompiledCSharp,
                includeSequencePoints: true,
                includeCompilerGeneratedMembers: true,
                includeMetadataTokens: true,
                maxCharacters: 1_000_000,
                maxFindings: 1_000,
                DateTimeOffset.UtcNow.AddSeconds(15),
                TestContext.Current.CancellationToken);

            Assert.Equal(ProcessorOutcome.Succeeded, result.Response.Outcome);
            var text = await File.ReadAllTextAsync(
                result.OutputPath,
                TestContext.Current.CancellationToken);
            Assert.Contains("Decompiled with ICSharpCode.Decompiler 10.1.0.8386", text, StringComparison.Ordinal);
            Assert.Contains("static int Add", text, StringComparison.Ordinal);
            Assert.Contains("Add(20, 22)", text, StringComparison.Ordinal);
            Assert.Contains("WriteLine", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Add (", text, StringComparison.Ordinal);
            Assert.DoesNotContain("WriteLine (", text, StringComparison.Ordinal);
            Assert.DoesNotContain('\t', text);
        }
        finally
        {
            TestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task DecompiledCSharpHonorsCompilerGeneratedMemberOption()
    {
        var root = TestSettings.CreateRoot();
        try
        {
            var artifact = PrepareArtifact(root);
            var runner = new ArtifactProcessorProcessRunner(TestSettings.Create(root));
            var included = await runner.RunAsync(
                artifact,
                ProcessorOperation.DecompiledCSharp,
                includeSequencePoints: true,
                includeCompilerGeneratedMembers: true,
                includeMetadataTokens: false,
                maxCharacters: 1_000_000,
                maxFindings: 1_000,
                DateTimeOffset.UtcNow.AddSeconds(15),
                TestContext.Current.CancellationToken);
            var includedText = await File.ReadAllTextAsync(
                included.OutputPath,
                TestContext.Current.CancellationToken);
            var excluded = await runner.RunAsync(
                artifact,
                ProcessorOperation.DecompiledCSharp,
                includeSequencePoints: true,
                includeCompilerGeneratedMembers: false,
                includeMetadataTokens: false,
                maxCharacters: 1_000_000,
                maxFindings: 1_000,
                DateTimeOffset.UtcNow.AddSeconds(15),
                TestContext.Current.CancellationToken);

            var excludedText = await File.ReadAllTextAsync(
                excluded.OutputPath,
                TestContext.Current.CancellationToken);
            Assert.Contains("GeneratedHelper", includedText, StringComparison.Ordinal);
            Assert.Equal(1, CountOccurrences(includedText, "GeneratedHelper"));
            Assert.DoesNotContain("Explicit compiler-generated members", includedText, StringComparison.Ordinal);
            Assert.DoesNotContain("GeneratedHelper", excludedText, StringComparison.Ordinal);
            Assert.Contains("static int Add", excludedText, StringComparison.Ordinal);
        }
        finally
        {
            TestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task IlVerifyReturnsStructuredResult()
    {
        var root = TestSettings.CreateRoot();
        try
        {
            var artifact = PrepareArtifact(root);
            var runner = new ArtifactProcessorProcessRunner(TestSettings.Create(root));
            var result = await runner.RunAsync(
                artifact,
                ProcessorOperation.Verify,
                includeSequencePoints: false,
                includeCompilerGeneratedMembers: true,
                includeMetadataTokens: true,
                maxCharacters: 1_000_000,
                maxFindings: 1_000,
                DateTimeOffset.UtcNow.AddSeconds(15),
                TestContext.Current.CancellationToken);

            Assert.True(result.Response.Outcome is ProcessorOutcome.Succeeded or ProcessorOutcome.Findings);
            Assert.Equal("microsoft-ilverification", result.Response.ProcessorId);
            Assert.Equal("10.0.9", result.Response.ProcessorVersion);
            Assert.All(result.Response.Findings, finding => Assert.False(string.IsNullOrWhiteSpace(finding.Code)));
        }
        finally
        {
            TestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task IlVerifyAcceptsTheReferenceAssemblyPackUsedByProduction()
    {
        var root = TestSettings.CreateRoot();
        try
        {
            var artifact = PrepareArtifact(root) with
            {
                ReferenceSet = new ArtifactReferenceSet(
                    "net10-ref",
                    [FindNet10ReferencePath()],
                    "System.Runtime")
            };
            var runner = new ArtifactProcessorProcessRunner(TestSettings.Create(root));
            var result = await runner.RunAsync(
                artifact,
                ProcessorOperation.Verify,
                includeSequencePoints: false,
                includeCompilerGeneratedMembers: true,
                includeMetadataTokens: true,
                maxCharacters: 1_000_000,
                maxFindings: 1_000,
                DateTimeOffset.UtcNow.AddSeconds(15),
                TestContext.Current.CancellationToken);

            Assert.True(
                result.Response.Outcome is ProcessorOutcome.Succeeded or ProcessorOutcome.Findings,
                $"{result.Response.Outcome}: {result.Response.PublicMessage}");
        }
        finally
        {
            TestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task MalformedPeDoesNotPoisonTheNextProcessorRun()
    {
        var root = TestSettings.CreateRoot();
        try
        {
            var invalidRoot = Path.Combine(root, "invalid");
            Directory.CreateDirectory(invalidRoot);
            var invalidPath = Path.Combine(invalidRoot, "bad.dll");
            await File.WriteAllBytesAsync(
                invalidPath,
                [0x4d, 0x5a, 0, 1, 2, 3],
                TestContext.Current.CancellationToken);
            var invalid = new MaterializedArtifact(
                invalidRoot,
                invalidPath,
                null,
                null!,
                new ArtifactReferenceSet("net10-ref", [], null),
                "lease_invalid",
                TestSettings.CreateUnusedStoreClient());
            var runner = new ArtifactProcessorProcessRunner(TestSettings.Create(root));
            var invalidResult = await runner.RunAsync(
                invalid,
                ProcessorOperation.Il,
                true,
                true,
                true,
                1_000_000,
                1_000,
                DateTimeOffset.UtcNow.AddSeconds(15),
                TestContext.Current.CancellationToken);
            Assert.Equal(ProcessorOutcome.InvalidArtifact, invalidResult.Response.Outcome);

            var validResult = await runner.RunAsync(
                PrepareArtifact(Path.Combine(root, "valid")),
                ProcessorOperation.Il,
                true,
                true,
                true,
                1_000_000,
                1_000,
                DateTimeOffset.UtcNow.AddSeconds(15),
                TestContext.Current.CancellationToken);
            Assert.True(
                validResult.Response.Outcome == ProcessorOutcome.Succeeded,
                $"{validResult.Response.Outcome}: {validResult.Response.PublicMessage}");
        }
        finally
        {
            TestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task OutputAndProcessDeadlineAreEnforced()
    {
        var root = TestSettings.CreateRoot();
        try
        {
            var artifact = PrepareArtifact(root);
            var runner = new ArtifactProcessorProcessRunner(TestSettings.Create(root));
            var outputLimited = await runner.RunAsync(
                artifact,
                ProcessorOperation.Il,
                true,
                true,
                true,
                100,
                1_000,
                DateTimeOffset.UtcNow.AddSeconds(15),
                TestContext.Current.CancellationToken);
            Assert.Equal(ProcessorOutcome.LimitExceeded, outputLimited.Response.Outcome);

            var deadline = await runner.RunAsync(
                artifact,
                ProcessorOperation.Il,
                true,
                true,
                true,
                1_000_000,
                1_000,
                DateTimeOffset.UtcNow.AddMilliseconds(-1),
                TestContext.Current.CancellationToken);
            Assert.Equal(ProcessorOutcome.LimitExceeded, deadline.Response.Outcome);
        }
        finally
        {
            TestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CppCliMixedPeRenderKeepsUserMainAndOmitsCompilerBootstrap()
    {
        var root = TestSettings.CreateRoot();
        try
        {
            var assemblyPath = Path.Combine(root, "SharpLabNext.User.exe");
            await File.WriteAllBytesAsync(
                assemblyPath,
                CreateSyntheticCppCliMixedPe(),
                TestContext.Current.CancellationToken);
            var artifact = new MaterializedArtifact(
                root,
                assemblyPath,
                null,
                CreateCppCliManifest(),
                null,
                "lease_test",
                TestSettings.CreateUnusedStoreClient());
            var runner = new ArtifactProcessorProcessRunner(TestSettings.Create(root));

            var il = await runner.RunAsync(
                artifact,
                ProcessorOperation.Il,
                includeSequencePoints: false,
                includeCompilerGeneratedMembers: false,
                includeMetadataTokens: true,
                maxCharacters: 1_000_000,
                maxFindings: 1_000,
                DateTimeOffset.UtcNow.AddSeconds(15),
                TestContext.Current.CancellationToken);
            Assert.Equal(ProcessorOutcome.Succeeded, il.Response.Outcome);
            var ilText = await File.ReadAllTextAsync(
                il.OutputPath,
                TestContext.Current.CancellationToken);
            Assert.Contains("main ()", ilText, StringComparison.Ordinal);
            Assert.Contains("UserWidget", ilText, StringComparison.Ordinal);
            Assert.DoesNotContain("<CrtImplementationDetails>", ilText, StringComparison.Ordinal);
            AssertCppCliNativeBootstrapTypesAreOmitted(ilText);

            var csharp = await runner.RunAsync(
                artifact,
                ProcessorOperation.DecompiledCSharp,
                includeSequencePoints: false,
                includeCompilerGeneratedMembers: false,
                includeMetadataTokens: true,
                maxCharacters: 1_000_000,
                maxFindings: 1_000,
                DateTimeOffset.UtcNow.AddSeconds(15),
                TestContext.Current.CancellationToken);
            Assert.Equal(ProcessorOutcome.Succeeded, csharp.Response.Outcome);
            var csharpText = await File.ReadAllTextAsync(
                csharp.OutputPath,
                TestContext.Current.CancellationToken);
            Assert.Contains("main()", csharpText, StringComparison.Ordinal);
            Assert.Contains("UserWidget", csharpText, StringComparison.Ordinal);
            Assert.Contains("CRT and compiler bootstrap members are omitted", csharpText, StringComparison.Ordinal);
            Assert.DoesNotContain("<CrtImplementationDetails>", csharpText, StringComparison.Ordinal);
            Assert.DoesNotContain("<CppImplementationDetails>", csharpText, StringComparison.Ordinal);
            AssertCppCliNativeBootstrapTypesAreOmitted(csharpText);

            var verification = await runner.RunAsync(
                artifact,
                ProcessorOperation.Verify,
                includeSequencePoints: false,
                includeCompilerGeneratedMembers: false,
                includeMetadataTokens: true,
                maxCharacters: 1_000_000,
                maxFindings: 1_000,
                DateTimeOffset.UtcNow.AddSeconds(15),
                TestContext.Current.CancellationToken);
            var instrumentation = await runner.RunAsync(
                artifact,
                ProcessorOperation.RuntimeInstrumentationV1,
                includeSequencePoints: false,
                includeCompilerGeneratedMembers: false,
                includeMetadataTokens: false,
                maxCharacters: 1_000_000,
                maxFindings: 1_000,
                DateTimeOffset.UtcNow.AddSeconds(15),
                TestContext.Current.CancellationToken,
                ProcessorProtocol.RuntimeInstrumentationProfileId);
            Assert.Equal(ProcessorOutcome.InvalidArtifact, verification.Response.Outcome);
            Assert.Equal(ProcessorOutcome.InvalidArtifact, instrumentation.Response.Outcome);
        }
        finally
        {
            TestSettings.DeleteRoot(root);
        }
    }

    private static void AssertCppCliNativeBootstrapTypesAreOmitted(string text)
    {
        Assert.DoesNotContain("_crt_argv_mode", text, StringComparison.Ordinal);
        Assert.DoesNotContain("_crt_app_type", text, StringComparison.Ordinal);
        Assert.DoesNotContain("HINSTANCE__", text, StringComparison.Ordinal);
        Assert.DoesNotContain("_IMAGE_DOS_HEADER", text, StringComparison.Ordinal);
        Assert.DoesNotContain("_IMAGE_NT_HEADERS64", text, StringComparison.Ordinal);
    }

    private static void AssertInstrumentationBodyShape(string sourcePath, string rewrittenPath)
    {
        using var source = Mono.Cecil.AssemblyDefinition.ReadAssembly(sourcePath);
        using var rewritten = Mono.Cecil.AssemblyDefinition.ReadAssembly(rewrittenPath);
        var sourceMain = source.MainModule.Types
            .Single(static type => type.FullName == "SharpLabNext.RuntimeCapabilityProbe.Program")
            .Methods.Single(static method => method.Name == "Main");
        var rewrittenMain = rewritten.MainModule.Types
            .Single(static type => type.FullName == "SharpLabNext.RuntimeCapabilityProbe.Program")
            .Methods.Single(static method => method.Name == "Main");

        Assert.Contains(
            sourceMain.Body.Instructions,
            static instruction =>
                instruction.OpCode.OperandType == Mono.Cecil.Cil.OperandType.ShortInlineBrTarget);
        Assert.Equal(sourceMain.Body.MaxStackSize + 5, rewrittenMain.Body.MaxStackSize);
        Assert.DoesNotContain(
            rewrittenMain.Body.Instructions,
            static instruction =>
                instruction.OpCode.OperandType == Mono.Cecil.Cil.OperandType.ShortInlineBrTarget);
    }

    private static MaterializedArtifact PrepareArtifact(string root) =>
        PrepareArtifact(
            root,
            typeof(SharpLabNext.ArtifactProcessing.Fixture.Program).Assembly.Location);

    private static MaterializedArtifact PrepareArtifact(string root, string sourceAssembly)
    {
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        var assembly = Path.Combine(input, Path.GetFileName(sourceAssembly));
        File.Copy(sourceAssembly, assembly, overwrite: true);
        var sourcePdb = Path.ChangeExtension(sourceAssembly, ".pdb");
        var pdb = Path.Combine(input, Path.GetFileName(sourcePdb));
        File.Copy(sourcePdb, pdb, overwrite: true);
        return new MaterializedArtifact(
            root,
            assembly,
            pdb,
            null!,
            new ArtifactReferenceSet(
                "net10-ref",
                [Path.GetDirectoryName(typeof(object).Assembly.Location)!],
                "System.Private.CoreLib"),
            "lease_test",
            TestSettings.CreateUnusedStoreClient());
    }

    private static string RuntimeCapabilityProbePath()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("Test build configuration could not be resolved.");
        return Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "Fixtures",
            "SharpLabNext.RuntimeCapabilityProbe",
            "bin",
            configuration,
            "netcoreapp2.0",
            "SharpLabNext.RuntimeCapabilityProbe.dll");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SharpLabNext.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("SharpLabNext.slnx was not found above the test output directory.");
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static ArtifactManifest CreateCppCliManifest() => new(
        1,
        new ArtifactRef($"sha256:{new string('1', 64)}"),
        new ArtifactProducer(
            "test-release",
            "cppcli",
            "msvc-cppcli-netfx48",
            "19.51.36248",
            null,
            $"sha256:{new string('2', 64)}"),
        "netfx48-ref",
        "net48",
        ArtifactFormatContract.NetFxMixedPe,
        new ArtifactRuntimeRequirement(
            "netfx-clr-wine",
            [new FrameworkRequirement(".NETFramework", "4.8")],
            "x64",
            []),
        [],
        BuildOutputKind.Console,
        "SharpLabNext.User.exe",
        null,
        [],
        Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mixedMode"] = "true"
        });

    private static byte[] CreateSyntheticCppCliMixedPe()
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(
            0,
            metadata.GetOrAddString("SharpLabNext.User.exe"),
            metadata.GetOrAddGuid(Guid.Parse("dd6e825b-f572-4ecf-a92a-cef093ea8ab5")),
            default,
            default);
        metadata.AddAssembly(
            metadata.GetOrAddString("SharpLabNext.User"),
            new Version(1, 0, 0, 0),
            default,
            default,
            (AssemblyFlags)0,
            AssemblyHashAlgorithm.Sha256);
        metadata.AddTypeDefinition(
            TypeAttributes.NotPublic,
            default,
            metadata.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));
        metadata.AddTypeDefinition(
            TypeAttributes.NotPublic,
            default,
            metadata.GetOrAddString("<CrtImplementationDetails>.Noise"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(3));
        foreach (var typeName in new[]
                 {
                     "_crt_argv_mode",
                     "_crt_app_type",
                     "HINSTANCE__",
                     "_IMAGE_DOS_HEADER",
                     "_IMAGE_NT_HEADERS64"
                 })
        {
            metadata.AddTypeDefinition(
                TypeAttributes.NotPublic,
                default,
                metadata.GetOrAddString(typeName),
                default,
                MetadataTokens.FieldDefinitionHandle(1),
                MetadataTokens.MethodDefinitionHandle(4));
        }
        metadata.AddTypeDefinition(
            TypeAttributes.Public,
            default,
            metadata.GetOrAddString("UserWidget"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(4));

        var signatureBuilder = new BlobBuilder();
        new BlobEncoder(signatureBuilder).MethodSignature().Parameters(
            0,
            static returnType => returnType.Void(),
            static _ => { });
        var signature = metadata.GetOrAddBlob(signatureBuilder);
        var bodyStream = new BlobBuilder();
        var instructions = new BlobBuilder();
        var instructionEncoder = new InstructionEncoder(instructions);
        instructionEncoder.OpCode(ILOpCode.Ret);
        var bodyOffset = new MethodBodyStreamEncoder(bodyStream).AddMethodBody(instructionEncoder);
        metadata.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            metadata.GetOrAddString("main"),
            signature,
            bodyOffset,
            MetadataTokens.ParameterHandle(1));
        metadata.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            metadata.GetOrAddString("<CrtImplementationDetails>.Initialize"),
            signature,
            bodyOffset,
            MetadataTokens.ParameterHandle(1));
        metadata.AddMethodDefinition(
            MethodAttributes.Assembly | MethodAttributes.Static,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            metadata.GetOrAddString("Bootstrap"),
            signature,
            bodyOffset,
            MetadataTokens.ParameterHandle(1));

        var peBuilder = new ManagedPEBuilder(
            new PEHeaderBuilder(
                machine: Machine.Amd64,
                imageCharacteristics: Characteristics.ExecutableImage | Characteristics.LargeAddressAware,
                subsystem: Subsystem.WindowsCui),
            new MetadataRootBuilder(metadata),
            bodyStream,
            flags: (CorFlags)0);
        var image = new BlobBuilder();
        peBuilder.Serialize(image);
        return image.ToArray();
    }

    private static string FindNet10ReferencePath()
    {
        var materializedRoot = Environment.GetEnvironmentVariable(
            "SHARPLABNEXT_TEST_CORECLR_REFERENCE_SETS");
        if (!string.IsNullOrWhiteSpace(materializedRoot))
        {
            var materialized = Path.Combine(materializedRoot, "net10-ref");
            if (Directory.Exists(materialized))
                return materialized;
        }

        var roots = new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
            "/usr/share/dotnet"
        };
        foreach (var root in roots.Where(static value => !string.IsNullOrWhiteSpace(value)))
        {
            var path = Path.Combine(
                root!,
                "packs",
                "Microsoft.NETCore.App.Ref",
                "10.0.9",
                "ref",
                "net10.0");
            if (Directory.Exists(path))
                return path;
        }
        throw new DirectoryNotFoundException("The .NET 10 reference pack was not found.");
    }
}
