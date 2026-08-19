using System.Reflection;
using System.Text;
using SharpLabNext.ArtifactWorker.Sdk;
using SharpLabNext.Contracts;
using SharpLabNext.Worker.Artifacts.ConstGenerics.Protocol;

namespace SharpLabNext.Worker.Artifacts.ConstGenerics.Tests;

public sealed class ConstGenericsArtifactProcessorTests
{
    [Fact]
    public void ProtocolIdentityComesFromAssemblyMetadata()
    {
        var metadata = typeof(ConstGenericsProcessorProtocol).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value, StringComparer.Ordinal);

        Assert.Equal(
            ConstGenericsProcessorProtocol.IlSpyCommit,
            metadata["SharpLabNext.ConstGenericsIlSpyCommit"]);
        Assert.Equal(
            ConstGenericsProcessorProtocol.RuntimeCommit,
            metadata["SharpLabNext.ConstGenericsRuntimeCommit"]);
        Assert.Equal(
            ConstGenericsProcessorProtocol.IlSpyProcessorVersion,
            metadata["SharpLabNext.ConstGenericsIlSpyProcessorVersion"]);
        Assert.Equal(
            ConstGenericsProcessorProtocol.VerificationProcessorVersion,
            metadata["SharpLabNext.ConstGenericsVerificationProcessorVersion"]);
        Assert.Equal(
            ConstGenericsProcessorProtocol.IlSpyCommit,
            ConstGenericsProcessorProtocol.IlSpyProcessorVersion);
        Assert.StartsWith(
            ConstGenericsProcessorProtocol.RuntimeCommit + "+",
            ConstGenericsProcessorProtocol.VerificationProcessorVersion,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealShortLivedProcessorProducesIlDecompiledCSharpAndVerification()
    {
        var root = ConstGenericsTestInfrastructure.CreateRoot();
        try
        {
            var handler = new ConstGenericsArtifactStoreHandler();
            var client = ConstGenericsTestInfrastructure.CreateClient(handler);
            var settings = ConstGenericsTestInfrastructure.Settings(root);
            var capability = ConstGenericsTestInfrastructure.CapabilityManifest();
            var runner = new ConstGenericsProcessorRunner(settings, capability);
            var processor = new ConstGenericsArtifactProcessor(
                new ConstGenericsArtifactMaterializer(client, settings, capability),
                runner,
                client,
                settings,
                capability);

            var il = await processor.RenderAsync(
                ConstGenericsTestInfrastructure.RenderRequest(handler.ArtifactRef, "il"),
                "op_il",
                ConstGenericsProcessorOperation.Il,
                TestContext.Current.CancellationToken);
            var ilResult = Assert.IsType<RenderArtifactResult>(il.Result);
            Assert.Equal(ArtifactJobOutcome.Succeeded, ilResult.Outcome);
            Assert.Equal("artifacts-const-generics", ilResult.Identity?.ProcessorId);
            Assert.Equal(ConstGenericsProcessorProtocol.IlSpyProcessorVersion, ilResult.Identity?.ProcessorVersion);
            var ilContent = Encoding.UTF8.GetString(handler.GetUploadedContent(Assert.IsType<ContentRef>(ilResult.ContentRef)));
            Assert.Contains(
                $"ConstGenerics ILSpy {ConstGenericsProcessorProtocol.IlSpyCommit}",
                ilContent);
            var assemblyIndex = ilContent.IndexOf(
                ".assembly SharpLabNext.Worker.Artifacts.Default.Fixture",
                StringComparison.Ordinal);
            var classIndex = ilContent.IndexOf(".class", StringComparison.Ordinal);
            Assert.True(assemblyIndex >= 0, "Generated IL does not contain the assembly manifest.");
            Assert.True(classIndex > assemblyIndex, "The assembly manifest must precede type definitions.");
            Assert.Contains(".ver 1:0:0:0", ilContent, StringComparison.Ordinal);
            Assert.Contains(".method", ilContent, StringComparison.Ordinal);
            Assert.DoesNotContain("// sequence point:", ilContent, StringComparison.Ordinal);
            Assert.DoesNotContain('\t', ilContent);

            var csharp = await processor.RenderAsync(
                ConstGenericsTestInfrastructure.RenderRequest(handler.ArtifactRef, "decompiled-csharp"),
                "op_csharp",
                ConstGenericsProcessorOperation.DecompiledCSharp,
                TestContext.Current.CancellationToken);
            var csharpResult = Assert.IsType<RenderArtifactResult>(csharp.Result);
            Assert.Equal(ArtifactJobOutcome.Succeeded, csharpResult.Outcome);
            var csharpContent = Encoding.UTF8.GetString(
                handler.GetUploadedContent(Assert.IsType<ContentRef>(csharpResult.ContentRef)));
            Assert.Contains("Decompiled with ConstGenerics ILSpy", csharpContent);
            Assert.Contains("class Program", csharpContent);
            Assert.Contains("GeneratedHelper", csharpContent, StringComparison.Ordinal);
            Assert.Equal(1, CountOccurrences(csharpContent, "GeneratedHelper"));
            Assert.DoesNotContain(
                "Explicit compiler-generated members",
                csharpContent,
                StringComparison.Ordinal);
            Assert.DoesNotContain('\t', csharpContent);

            var verification = await processor.VerifyAsync(
                ConstGenericsTestInfrastructure.VerifyRequest(handler.ArtifactRef),
                "op_verify",
                TestContext.Current.CancellationToken);
            var verificationResult = Assert.IsType<VerifyArtifactResult>(verification.Result);
            Assert.True(
                verificationResult.Outcome is ArtifactVerificationOutcome.Valid or ArtifactVerificationOutcome.Findings,
                $"Unexpected verification outcome: {verificationResult.Outcome}");
            Assert.Equal("ilverification-const-generics", verificationResult.VerifierId);
            Assert.Equal(
                ConstGenericsProcessorProtocol.VerificationProcessorVersion,
                verificationResult.VerifierVersion);
            Assert.Equal(
                ConstGenericsProcessorProtocol.VerificationProcessorVersion,
                verificationResult.Identity?.ProcessorVersion);

            Assert.Equal(3, runner.StartedProcessCount);
            Assert.Equal(3, handler.LeaseAcquisitionCount);
            Assert.Equal(3, handler.LeaseReleaseCount);
            Assert.Equal(3, handler.FileDownloadCount);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            ConstGenericsTestInfrastructure.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RealShortLivedProcessorHidesSequencePointCommentsAndPreservesLinkedRanges()
    {
        var root = ConstGenericsTestInfrastructure.CreateRoot();
        try
        {
            var artifactRoot = Path.Combine(root, "artifact");
            Directory.CreateDirectory(artifactRoot);
            var fixtureAssembly = typeof(SharpLabNext.ArtifactProcessing.Fixture.Program).Assembly.Location;
            var fixturePdb = Path.ChangeExtension(fixtureAssembly, ".pdb");
            var assemblyPath = Path.Combine(artifactRoot, "app.dll");
            var pdbPath = Path.Combine(artifactRoot, "app.pdb");
            File.Copy(fixtureAssembly, assemblyPath);
            File.Copy(fixturePdb, pdbPath);

            var handler = new ConstGenericsArtifactStoreHandler();
            var client = ConstGenericsTestInfrastructure.CreateClient(handler);
            var runner = new ConstGenericsProcessorRunner(
                ConstGenericsTestInfrastructure.Settings(root),
                ConstGenericsTestInfrastructure.CapabilityManifest());
            var artifact = new MaterializedConstGenericsArtifact(
                handler.ArtifactRef,
                artifactRoot,
                assemblyPath,
                pdbPath,
                handler.Bundle.Manifest,
                "unused",
                client);

            var result = await runner.RunAsync(
                artifact,
                ConstGenericsProcessorOperation.Il,
                includeSequencePoints: true,
                includeCompilerGeneratedMembers: true,
                includeMetadataTokens: true,
                maxCharacters: 1_000_000,
                maxFindings: 1_000,
                TestContext.Current.CancellationToken);

            Assert.Equal(ConstGenericsProcessorOutcome.Succeeded, result.Response.Outcome);
            var content = await File.ReadAllTextAsync(
                result.OutputPath,
                TestContext.Current.CancellationToken);
            Assert.DoesNotContain("// sequence point:", content, StringComparison.Ordinal);
            Assert.NotEmpty(result.Response.LinkedRanges);
            var visibleLines = content.Split('\n');
            Assert.All(result.Response.LinkedRanges, range =>
            {
                Assert.InRange(range.OutputRange.StartLine, 0, visibleLines.Length - 1);
                var linkedLine = visibleLines[range.OutputRange.StartLine].TrimStart();
                Assert.True(
                    linkedLine.StartsWith("IL_", StringComparison.Ordinal),
                    $"Linked range points to a non-instruction line: '{linkedLine}'.");
            });
        }
        finally
        {
            ConstGenericsTestInfrastructure.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ProcessorHealthReportsExactForkIdentity()
    {
        var root = ConstGenericsTestInfrastructure.CreateRoot();
        try
        {
            var runner = new ConstGenericsProcessorRunner(
                ConstGenericsTestInfrastructure.Settings(root),
                ConstGenericsTestInfrastructure.CapabilityManifest());
            var health = await runner.CheckHealthAsync(TestContext.Current.CancellationToken);

            Assert.True(health.IsHealthy, health.Message);
            Assert.Contains(ConstGenericsProcessorProtocol.IlSpyCommit[..12], health.Message);
            Assert.Equal(1, runner.StartedProcessCount);
        }
        finally
        {
            ConstGenericsTestInfrastructure.DeleteRoot(root);
        }
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
}
