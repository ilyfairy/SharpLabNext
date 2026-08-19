using System.Net;
using System.Net.Http.Json;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using SharpLabNext.ArtifactProcessing.Protocol;
using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.ArtifactWorker;
using SharpLabNext.Contracts;

namespace SharpLabNext.ArtifactWorker.Tests;

public sealed class ArtifactStoreIntegrationTests
{
    public static IEnumerable<object[]> FrameworkManifestContracts =>
        NetFxManagedReferenceSets.ById.Values.Select(static contract => new object[]
        {
            contract.ReferenceSetId,
            contract.TargetFramework,
            contract.FrameworkVersion
        });

    [Fact]
    public async Task RenderMaterializesArtifactUploadsContentAndUsesCompletedCache()
    {
        var root = TestSettings.CreateRoot();
        try
        {
            var assemblyBytes = await File.ReadAllBytesAsync(
                typeof(SharpLabNext.ArtifactProcessing.Fixture.Program).Assembly.Location,
                TestContext.Current.CancellationToken);
            var handler = new ArtifactStoreHandler(assemblyBytes, assemblyBytes);
            var storeClient = CreateClient(handler);
            var settings = TestSettings.Create(root);
            var runner = new RecordingProcessorRunner();
            using var executor = new ArtifactJobExecutor(
                new ArtifactBundleMaterializer(storeClient, settings),
                runner,
                storeClient,
                settings);
            var options = new RenderArtifactOptions(
                IncludeSequencePoints: true,
                IncludeCompilerGeneratedMembers: true,
                MaxCharacters: 10_000);

            var first = await executor.RenderAsync(
                Request("request-1", "key-1", handler.ArtifactRef, options),
                "op_first",
                TestContext.Current.CancellationToken);
            var second = await executor.RenderAsync(
                Request("request-2", "key-2", handler.ArtifactRef, options),
                "op_second",
                TestContext.Current.CancellationToken);

            var firstResult = Assert.IsType<RenderArtifactResult>(first.Result);
            var secondResult = Assert.IsType<RenderArtifactResult>(second.Result);
            var expectedContentRef = ContentIdentity.Compute(Encoding.UTF8.GetBytes(RecordingProcessorRunner.Output));
            Assert.Equal(ArtifactJobOutcome.Succeeded, firstResult.Outcome);
            Assert.Equal(expectedContentRef, firstResult.ContentRef);
            Assert.Equal(firstResult, secondResult);
            Assert.Equal(expectedContentRef, handler.UploadedContentRef);
            Assert.Equal(1, handler.LeaseAcquisitionCount);
            Assert.Equal(1, handler.LeaseReleaseCount);
            Assert.Equal(1, handler.FileDownloadCount);
            Assert.Equal(1, handler.ContentUploadCount);
            Assert.Equal(1, handler.ContentReadCount);
            Assert.Equal(1, runner.RunCount);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            TestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RenderReprocessesArtifactWhenCachedContentHasExpired()
    {
        var root = TestSettings.CreateRoot();
        try
        {
            var assemblyBytes = await File.ReadAllBytesAsync(
                typeof(SharpLabNext.ArtifactProcessing.Fixture.Program).Assembly.Location,
                TestContext.Current.CancellationToken);
            var handler = new ArtifactStoreHandler(assemblyBytes, assemblyBytes);
            var storeClient = CreateClient(handler);
            var settings = TestSettings.Create(root);
            var runner = new RecordingProcessorRunner();
            using var executor = new ArtifactJobExecutor(
                new ArtifactBundleMaterializer(storeClient, settings),
                runner,
                storeClient,
                settings);
            var options = new RenderArtifactOptions(
                IncludeSequencePoints: true,
                IncludeCompilerGeneratedMembers: true,
                MaxCharacters: 10_000);

            var first = await executor.RenderAsync(
                Request("request-1", "key-1", handler.ArtifactRef, options),
                "op_first",
                TestContext.Current.CancellationToken);
            handler.ExpireUploadedContent();
            var second = await executor.RenderAsync(
                Request("request-2", "key-2", handler.ArtifactRef, options),
                "op_second",
                TestContext.Current.CancellationToken);

            Assert.Equal(first.Result, second.Result);
            Assert.Equal(2, handler.LeaseAcquisitionCount);
            Assert.Equal(2, handler.LeaseReleaseCount);
            Assert.Equal(2, handler.FileDownloadCount);
            Assert.Equal(2, handler.ContentUploadCount);
            Assert.Equal(1, handler.ContentReadCount);
            Assert.Equal(2, runner.RunCount);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            TestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task MaterializerRejectsDigestMismatchReleasesLeaseAndCleansWorkDirectory()
    {
        var root = TestSettings.CreateRoot();
        try
        {
            var expected = await File.ReadAllBytesAsync(
                typeof(SharpLabNext.ArtifactProcessing.Fixture.Program).Assembly.Location,
                TestContext.Current.CancellationToken);
            var corrupted = expected.ToArray();
            corrupted[^1] ^= 0xff;
            var handler = new ArtifactStoreHandler(expected, corrupted);
            var storeClient = CreateClient(handler);
            var materializer = new ArtifactBundleMaterializer(storeClient, TestSettings.Create(root));

            await Assert.ThrowsAsync<ArtifactRequestValidationException>(() => materializer.MaterializeAsync(
                handler.ArtifactRef,
                "op_digest_mismatch",
                TestContext.Current.CancellationToken));

            Assert.Equal(1, handler.LeaseAcquisitionCount);
            Assert.Equal(1, handler.LeaseReleaseCount);
            Assert.Equal(1, handler.FileDownloadCount);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            TestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task MaterializerRejectsContentRefThatDoesNotMatchManifestDigest()
    {
        var root = TestSettings.CreateRoot();
        try
        {
            var expected = await File.ReadAllBytesAsync(
                typeof(SharpLabNext.ArtifactProcessing.Fixture.Program).Assembly.Location,
                TestContext.Current.CancellationToken);
            var different = expected.ToArray();
            different[^1] ^= 0xff;
            var handler = new ArtifactStoreHandler(
                expected,
                different,
                contentRefUsesServedAssembly: true);
            var storeClient = CreateClient(handler);
            var materializer = new ArtifactBundleMaterializer(storeClient, TestSettings.Create(root));

            await Assert.ThrowsAsync<ArtifactRequestValidationException>(() => materializer.MaterializeAsync(
                handler.ArtifactRef,
                "op_content_ref_mismatch",
                TestContext.Current.CancellationToken));

            Assert.Equal(1, handler.LeaseAcquisitionCount);
            Assert.Equal(1, handler.LeaseReleaseCount);
            Assert.Equal(0, handler.FileDownloadCount);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            TestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task MixedPeMaterializesAndSupportsOnlyIlSpyRenderOutputs()
    {
        var root = TestSettings.CreateRoot();
        try
        {
            var assemblyBytes = await File.ReadAllBytesAsync(
                typeof(SharpLabNext.ArtifactProcessing.Fixture.Program).Assembly.Location,
                TestContext.Current.CancellationToken);
            var handler = new ArtifactStoreHandler(
                assemblyBytes,
                assemblyBytes,
                artifactFormat: ArtifactFormatContract.NetFxMixedPe);
            var storeClient = CreateClient(handler);
            var settings = TestSettings.Create(root);
            var runner = new RecordingProcessorRunner();
            using var executor = new ArtifactJobExecutor(
                new ArtifactBundleMaterializer(storeClient, settings),
                runner,
                storeClient,
                settings);
            var options = new RenderArtifactOptions(MaxCharacters: 10_000);

            var il = await executor.RenderAsync(
                Request("request-mixed-il", "key-mixed-il", handler.ArtifactRef, options),
                "op_mixed_il",
                TestContext.Current.CancellationToken);
            var csharp = await executor.RenderAsync(
                Request(
                    "request-mixed-csharp",
                    "key-mixed-csharp",
                    handler.ArtifactRef,
                    options,
                    "decompiled-csharp"),
                "op_mixed_csharp",
                TestContext.Current.CancellationToken);

            Assert.Equal(ArtifactJobOutcome.Succeeded, Assert.IsType<RenderArtifactResult>(il.Result).Outcome);
            Assert.Equal(ArtifactJobOutcome.Succeeded, Assert.IsType<RenderArtifactResult>(csharp.Result).Outcome);
            Assert.Equal(2, runner.RunCount);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            TestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task FrameworkManagedPeMaterializesAndIdentityPreservesItsRealFormat()
    {
        var root = TestSettings.CreateRoot();
        try
        {
            var assemblyBytes = await File.ReadAllBytesAsync(
                typeof(SharpLabNext.ArtifactProcessing.Fixture.Program).Assembly.Location,
                TestContext.Current.CancellationToken);
            var handler = new ArtifactStoreHandler(
                assemblyBytes,
                assemblyBytes,
                artifactFormat: ArtifactFormatContract.NetFxManagedPe);
            var storeClient = CreateClient(handler);
            var settings = TestSettings.Create(root);
            var runner = new RecordingProcessorRunner();
            using var executor = new ArtifactJobExecutor(
                new ArtifactBundleMaterializer(storeClient, settings),
                runner,
                storeClient,
                settings);

            var render = await executor.RenderAsync(
                Request(
                    "request-framework-il",
                    "key-framework-il",
                    handler.ArtifactRef,
                    new RenderArtifactOptions(MaxCharacters: 10_000)),
                "op_framework_il",
                TestContext.Current.CancellationToken);
            var identity = await executor.TransformAsync(
                new TransformArtifactRequest(
                    "request-framework-identity",
                    "key-framework-identity",
                    "pipeline-test",
                    handler.ArtifactRef,
                    "artifacts-default",
                    "identity",
                    new TransformArtifactOptions(),
                    DateTimeOffset.UtcNow.AddSeconds(30)),
                "op_framework_identity",
                TestContext.Current.CancellationToken);

            Assert.Equal(
                ArtifactJobOutcome.Succeeded,
                Assert.IsType<RenderArtifactResult>(render.Result).Outcome);
            var identityResult = Assert.IsType<TransformArtifactResult>(identity.Result);
            Assert.Equal(ArtifactJobOutcome.Succeeded, identityResult.Outcome);
            Assert.Equal(ArtifactFormatContract.NetFxManagedPe, identityResult.ArtifactFormat);
            Assert.Equal(1, runner.RunCount);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            TestSettings.DeleteRoot(root);
        }
    }

    [Theory]
    [MemberData(nameof(FrameworkManifestContracts))]
    public async Task FrameworkManagedPeAcceptsEveryExactReferenceContract(
        string referenceSetId,
        string targetFramework,
        string frameworkVersion)
    {
        var root = TestSettings.CreateRoot();
        try
        {
            var assemblyBytes = await File.ReadAllBytesAsync(
                typeof(SharpLabNext.ArtifactProcessing.Fixture.Program).Assembly.Location,
                TestContext.Current.CancellationToken);
            var handler = new ArtifactStoreHandler(
                assemblyBytes,
                assemblyBytes,
                artifactFormat: ArtifactFormatContract.NetFxManagedPe,
                referenceSetId: referenceSetId,
                targetFramework: targetFramework,
                runtimeFrameworkVersion: frameworkVersion);
            var materializer = new ArtifactBundleMaterializer(
                CreateClient(handler),
                TestSettings.Create(root));

            await using var materialized = await materializer.MaterializeAsync(
                handler.ArtifactRef,
                $"op_framework_exact_{referenceSetId}",
                TestContext.Current.CancellationToken);

            Assert.Equal(referenceSetId, materialized.Manifest.ReferenceSetId);
            Assert.Equal(targetFramework, materialized.Manifest.TargetFramework);
        }
        finally
        {
            TestSettings.DeleteRoot(root);
        }
    }

    [Theory]
    [MemberData(nameof(FrameworkManifestContracts))]
    public async Task FrameworkManagedPeRejectsEveryCrossVersionContract(
        string referenceSetId,
        string targetFramework,
        string frameworkVersion)
    {
        var root = TestSettings.CreateRoot();
        try
        {
            var assemblyBytes = await File.ReadAllBytesAsync(
                typeof(SharpLabNext.ArtifactProcessing.Fixture.Program).Assembly.Location,
                TestContext.Current.CancellationToken);
            var wrongTargetFramework = targetFramework == "net48" ? "net472" : "net48";
            var wrongFrameworkVersion = frameworkVersion == "4.8" ? "4.7.2" : "4.8";
            foreach (var handler in new[]
            {
                new ArtifactStoreHandler(
                    assemblyBytes,
                    assemblyBytes,
                    artifactFormat: ArtifactFormatContract.NetFxManagedPe,
                    referenceSetId: referenceSetId,
                    targetFramework: wrongTargetFramework,
                    runtimeFrameworkVersion: frameworkVersion),
                new ArtifactStoreHandler(
                    assemblyBytes,
                    assemblyBytes,
                    artifactFormat: ArtifactFormatContract.NetFxManagedPe,
                    referenceSetId: referenceSetId,
                    targetFramework: targetFramework,
                    runtimeFrameworkVersion: wrongFrameworkVersion)
            })
            {
                var materializer = new ArtifactBundleMaterializer(
                    CreateClient(handler),
                    TestSettings.Create(root));

                var exception = await Assert.ThrowsAsync<ArtifactRequestValidationException>(() =>
                    materializer.MaterializeAsync(
                        handler.ArtifactRef,
                        $"op_framework_mismatch_{Guid.NewGuid():N}",
                        TestContext.Current.CancellationToken));

                Assert.Contains("exact .NET Framework contract", exception.Message, StringComparison.Ordinal);
                Assert.Equal(0, handler.FileDownloadCount);
                Assert.Equal(1, handler.LeaseReleaseCount);
                Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            }
        }
        finally
        {
            TestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task FrameworkManagedPeRejectsUnknownReferenceSet()
    {
        var root = TestSettings.CreateRoot();
        try
        {
            var assemblyBytes = await File.ReadAllBytesAsync(
                typeof(SharpLabNext.ArtifactProcessing.Fixture.Program).Assembly.Location,
                TestContext.Current.CancellationToken);
            var handler = new ArtifactStoreHandler(
                assemblyBytes,
                assemblyBytes,
                artifactFormat: ArtifactFormatContract.NetFxManagedPe,
                referenceSetId: "netfx49-managed-ref",
                targetFramework: "net49",
                runtimeFrameworkVersion: "4.9");
            var materializer = new ArtifactBundleMaterializer(
                CreateClient(handler),
                TestSettings.Create(root));

            var exception = await Assert.ThrowsAsync<ArtifactRequestValidationException>(() =>
                materializer.MaterializeAsync(
                    handler.ArtifactRef,
                    "op_framework_unknown",
                    TestContext.Current.CancellationToken));

            Assert.Contains("exact .NET Framework contract", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, handler.FileDownloadCount);
            Assert.Equal(1, handler.LeaseReleaseCount);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            TestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task MixedPeRejectsVerifyInstrumentationAndRunIlBeforeProcessorInvocation()
    {
        var root = TestSettings.CreateRoot();
        try
        {
            var assemblyBytes = await File.ReadAllBytesAsync(
                typeof(SharpLabNext.ArtifactProcessing.Fixture.Program).Assembly.Location,
                TestContext.Current.CancellationToken);
            var handler = new ArtifactStoreHandler(
                assemblyBytes,
                assemblyBytes,
                artifactFormat: ArtifactFormatContract.NetFxMixedPe);
            var storeClient = CreateClient(handler);
            var settings = TestSettings.Create(root);
            var runner = new RecordingProcessorRunner();
            using var executor = new ArtifactJobExecutor(
                new ArtifactBundleMaterializer(storeClient, settings),
                runner,
                storeClient,
                settings);

            var verification = await executor.VerifyAsync(
                new VerifyArtifactRequest(
                    "request-mixed-verify",
                    "key-mixed-verify",
                    "pipeline-test",
                    handler.ArtifactRef,
                    "artifacts-default",
                    new VerifyArtifactOptions("default"),
                    DateTimeOffset.UtcNow.AddSeconds(30)),
                "op_mixed_verify",
                TestContext.Current.CancellationToken);
            var transformation = await executor.TransformAsync(
                new TransformArtifactRequest(
                    "request-mixed-transform",
                    "key-mixed-transform",
                    "pipeline-test",
                    handler.ArtifactRef,
                    "artifacts-default",
                    "runtime-instrumentation-v1",
                    new TransformArtifactOptions(
                        RewriterProfileId: ProcessorProtocol.RuntimeInstrumentationProfileId),
                    DateTimeOffset.UtcNow.AddSeconds(30)),
                "op_mixed_transform",
                TestContext.Current.CancellationToken);
            var identity = await executor.TransformAsync(
                new TransformArtifactRequest(
                    "request-mixed-identity",
                    "key-mixed-identity",
                    "pipeline-test",
                    handler.ArtifactRef,
                    "artifacts-default",
                    "identity",
                    new TransformArtifactOptions(),
                    DateTimeOffset.UtcNow.AddSeconds(30)),
                "op_mixed_identity",
                TestContext.Current.CancellationToken);
            var runIl = await executor.RenderAsync(
                Request(
                    "request-mixed-run-il",
                    "key-mixed-run-il",
                    handler.ArtifactRef,
                    new RenderArtifactOptions(MaxCharacters: 10_000),
                    "run-il"),
                "op_mixed_run_il",
                TestContext.Current.CancellationToken);

            var verifyResult = Assert.IsType<VerifyArtifactResult>(verification.Result);
            Assert.Equal(ArtifactVerificationOutcome.UnsupportedArtifact, verifyResult.Outcome);
            Assert.Contains(
                verifyResult.Findings,
                static finding => finding.Code == "mixed-pe-verification-unsupported");
            Assert.Equal(
                ArtifactJobOutcome.UnsupportedArtifact,
                Assert.IsType<TransformArtifactResult>(transformation.Result).Outcome);
            var identityResult = Assert.IsType<TransformArtifactResult>(identity.Result);
            Assert.Equal(ArtifactJobOutcome.Succeeded, identityResult.Outcome);
            Assert.Equal(ArtifactFormatContract.NetFxMixedPe, identityResult.ArtifactFormat);
            Assert.Equal(
                ArtifactJobOutcome.UnsupportedArtifact,
                Assert.IsType<RenderArtifactResult>(runIl.Result).Outcome);
            Assert.Equal(0, runner.RunCount);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            TestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task JSharpClr2PeSupportsOnlyIlAndDecompiledCSharpProcessing()
    {
        var root = TestSettings.CreateRoot();
        try
        {
            var assemblyBytes = JSharpArtifactFixture.CreateManagedPe();
            var handler = new ArtifactStoreHandler(
                assemblyBytes,
                assemblyBytes,
                artifactFormat: ArtifactFormatContract.NetFxManagedPe,
                jsharp: true);
            var storeClient = CreateClient(handler);
            var settings = TestSettings.Create(root);
            var runner = new RecordingProcessorRunner();
            using var executor = new ArtifactJobExecutor(
                new ArtifactBundleMaterializer(storeClient, settings),
                runner,
                storeClient,
                settings);
            var renderOptions = new RenderArtifactOptions(MaxCharacters: 10_000);

            var il = await executor.RenderAsync(
                Request("request-jsharp-il", "key-jsharp-il", handler.ArtifactRef, renderOptions),
                "op_jsharp_il",
                TestContext.Current.CancellationToken);
            var csharp = await executor.RenderAsync(
                Request(
                    "request-jsharp-csharp",
                    "key-jsharp-csharp",
                    handler.ArtifactRef,
                    renderOptions,
                    "decompiled-csharp"),
                "op_jsharp_csharp",
                TestContext.Current.CancellationToken);
            var runIl = await executor.RenderAsync(
                Request(
                    "request-jsharp-run-il",
                    "key-jsharp-run-il",
                    handler.ArtifactRef,
                    renderOptions,
                    "run-il"),
                "op_jsharp_run_il",
                TestContext.Current.CancellationToken);
            var verification = await executor.VerifyAsync(
                new VerifyArtifactRequest(
                    "request-jsharp-verify",
                    "key-jsharp-verify",
                    "pipeline-test",
                    handler.ArtifactRef,
                    "artifacts-default",
                    new VerifyArtifactOptions("default"),
                    DateTimeOffset.UtcNow.AddSeconds(30)),
                "op_jsharp_verify",
                TestContext.Current.CancellationToken);
            var transformation = await executor.TransformAsync(
                new TransformArtifactRequest(
                    "request-jsharp-transform",
                    "key-jsharp-transform",
                    "pipeline-test",
                    handler.ArtifactRef,
                    "artifacts-default",
                    "runtime-instrumentation-v1",
                    new TransformArtifactOptions(
                        RewriterProfileId: ProcessorProtocol.RuntimeInstrumentationProfileId),
                    DateTimeOffset.UtcNow.AddSeconds(30)),
                "op_jsharp_transform",
                TestContext.Current.CancellationToken);

            Assert.Equal(ArtifactJobOutcome.Succeeded, Assert.IsType<RenderArtifactResult>(il.Result).Outcome);
            Assert.Equal(ArtifactJobOutcome.Succeeded, Assert.IsType<RenderArtifactResult>(csharp.Result).Outcome);
            Assert.Equal(
                ArtifactJobOutcome.UnsupportedArtifact,
                Assert.IsType<RenderArtifactResult>(runIl.Result).Outcome);
            var verifyResult = Assert.IsType<VerifyArtifactResult>(verification.Result);
            Assert.Equal(ArtifactVerificationOutcome.UnsupportedArtifact, verifyResult.Outcome);
            Assert.Contains(
                verifyResult.Findings,
                static finding => finding.Code == "jsharp20-verification-unsupported");
            Assert.Equal(
                ArtifactJobOutcome.UnsupportedArtifact,
                Assert.IsType<TransformArtifactResult>(transformation.Result).Outcome);
            Assert.Equal(2, runner.RunCount);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            TestSettings.DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData("x86", "jsharp20-ref", "net20", "runtime.jsharp20-wine")]
    [InlineData("anycpu", "jsharp20-ref", "net20", "runtime.jsharp20-wine")]
    [InlineData("x64", "netfx48-managed-ref", "net20", "runtime.jsharp20-wine")]
    [InlineData("x64", "jsharp20-ref", "net48", "runtime.jsharp20-wine")]
    [InlineData("x64", "jsharp20-ref", "net20", "runtime.netfx48-wine")]
    public async Task JSharpManifestRejectsArchitectureAndNet48Substitutions(
        string architecture,
        string referenceSetId,
        string targetFramework,
        string runtimeFeatureTag)
    {
        var root = TestSettings.CreateRoot();
        try
        {
            var assemblyBytes = JSharpArtifactFixture.CreateManagedPe();
            var handler = new ArtifactStoreHandler(
                assemblyBytes,
                assemblyBytes,
                artifactFormat: ArtifactFormatContract.NetFxManagedPe,
                jsharp: true,
                referenceSetId: referenceSetId,
                targetFramework: targetFramework,
                runtimeArchitecture: architecture,
                runtimeFeatureTag: runtimeFeatureTag);
            var materializer = new ArtifactBundleMaterializer(
                CreateClient(handler),
                TestSettings.Create(root));

            var exception = await Assert.ThrowsAsync<ArtifactRequestValidationException>(() =>
                materializer.MaterializeAsync(
                    handler.ArtifactRef,
                    $"op_jsharp_manifest_{Guid.NewGuid():N}",
                    TestContext.Current.CancellationToken));

            Assert.Contains("x64 CLR 2.0", exception.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            TestSettings.DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData(Machine.I386, CorFlags.ILOnly)]
    [InlineData(Machine.Amd64, CorFlags.ILOnly | CorFlags.Prefers32Bit)]
    [InlineData(Machine.Amd64, CorFlags.ILOnly | CorFlags.Requires32Bit)]
    public async Task JSharpEntryAssemblyRejectsNonX64ClrFlags(Machine machine, CorFlags flags)
    {
        var root = TestSettings.CreateRoot();
        try
        {
            var assemblyBytes = JSharpArtifactFixture.CreateManagedPe(machine, flags);
            var handler = new ArtifactStoreHandler(
                assemblyBytes,
                assemblyBytes,
                artifactFormat: ArtifactFormatContract.NetFxManagedPe,
                jsharp: true);
            var materializer = new ArtifactBundleMaterializer(
                CreateClient(handler),
                TestSettings.Create(root));

            var exception = await Assert.ThrowsAsync<ArtifactRequestValidationException>(() =>
                materializer.MaterializeAsync(
                    handler.ArtifactRef,
                    $"op_jsharp_pe_{Guid.NewGuid():N}",
                    TestContext.Current.CancellationToken));

            Assert.Contains("AMD64 PE32+", exception.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            TestSettings.DeleteRoot(root);
        }
    }

    private static RenderArtifactRequest Request(
        string requestId,
        string idempotencyKey,
        ArtifactRef artifactRef,
        RenderArtifactOptions options,
        string outputId = "il") => new(
            requestId,
            idempotencyKey,
            "pipeline-test",
            artifactRef,
            "artifacts-default",
            outputId,
            options,
            DateTimeOffset.UtcNow.AddSeconds(30));

    private static ArtifactStoreClient CreateClient(HttpMessageHandler handler) => new(new HttpClient(handler)
    {
        BaseAddress = new Uri("http://artifact-store.test")
    });

    private sealed class RecordingProcessorRunner : IArtifactProcessorRunner
    {
        public const string Output = "isolated artifact output\n";
        private int _runCount;

        public int RunCount => Volatile.Read(ref _runCount);

        public async Task<ProcessorRunResult> RunAsync(
            MaterializedArtifact artifact,
            ProcessorOperation operation,
            bool includeSequencePoints,
            bool includeCompilerGeneratedMembers,
            bool includeMetadataTokens,
            int maxCharacters,
            int maxFindings,
            DateTimeOffset deadlineUtc,
            CancellationToken cancellationToken,
            string? rewriterProfileId = null)
        {
            Interlocked.Increment(ref _runCount);
            var outputPath = TemporaryArtifactDirectory.ResolvePath(artifact.RootPath, "test-output.txt");
            await File.WriteAllTextAsync(outputPath, Output, new UTF8Encoding(false), cancellationToken);
            return new ProcessorRunResult(
                new ProcessorResponse(
                    ProcessorProtocol.Version,
                    ProcessorOutcome.Succeeded,
                    "ilspy-reflection-disassembler",
                    ProcessorProtocol.IlSpyVersion,
                    "text/plain; charset=utf-8",
                    Output.Length,
                    [],
                    [],
                    false,
                    null),
                outputPath);
        }
    }

    private sealed class ArtifactStoreHandler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = ContractJson.CreateSerializerOptions();
        private readonly ArtifactBundleDescriptor _bundle;
        private readonly byte[] _servedAssembly;
        private readonly string _entryAssembly;
        private int _contentUploadCount;
        private int _contentReadCount;
        private int _fileDownloadCount;
        private int _leaseAcquisitionCount;
        private int _leaseReleaseCount;
        private byte[]? _uploadedContent;

        public ArtifactStoreHandler(
            byte[] expectedAssembly,
            byte[] servedAssembly,
            bool contentRefUsesServedAssembly = false,
            string artifactFormat = ArtifactFormatContract.ManagedPe,
            bool jsharp = false,
            string? referenceSetId = null,
            string? targetFramework = null,
            string? runtimeArchitecture = null,
            string? runtimeFeatureTag = null,
            string? runtimeFrameworkVersion = null)
        {
            _servedAssembly = servedAssembly;
            var netFx = ArtifactFormatContract.IsNetFx(artifactFormat);
            var mixedPe = ArtifactFormatContract.IsNetFxMixedPe(artifactFormat);
            _entryAssembly = netFx ? "SharpLabNext.User.exe" : "app.dll";
            var manifestContentRef = ContentIdentity.Compute(expectedAssembly);
            var bundleContentRef = contentRefUsesServedAssembly
                ? ContentIdentity.Compute(servedAssembly)
                : manifestContentRef;
            var placeholder = new ArtifactRef($"sha256:{new string('0', ArtifactStoreProtocol.Sha256HexLength)}");
            var manifest = ArtifactIdentity.WithComputedId(new ArtifactManifest(
                ArtifactStoreProtocol.ArtifactManifestVersion,
                placeholder,
                new ArtifactProducer(
                    "test-release",
                    jsharp ? "jsharp" : mixedPe ? "cppcli" : netFx ? "framework-fixture" : "csharp",
                    jsharp ? "vjc-jsharp20" : mixedPe ? "msvc-cppcli-netfx48" : netFx ? "framework-fixture" : "roslyn-stable",
                    jsharp ? "2.0.50727.937" : mixedPe ? "19.51.36248" : netFx ? "fixture" : "5.6.0",
                    null,
                    $"sha256:{new string('1', ArtifactStoreProtocol.Sha256HexLength)}"),
                referenceSetId ?? (jsharp ? "jsharp20-ref" : mixedPe ? "netfx48-ref" : netFx ? "netfx48-managed-ref" : "net10-ref"),
                targetFramework ?? (jsharp ? "net20" : netFx ? "net48" : "net10.0"),
                artifactFormat,
                new ArtifactRuntimeRequirement(
                    netFx ? "netfx-clr-wine" : "coreclr",
                    netFx
                        ? [new FrameworkRequirement(
                            ".NETFramework",
                            runtimeFrameworkVersion ?? (jsharp ? "2.0" : "4.8"))]
                        : [new FrameworkRequirement("Microsoft.NETCore.App", "10.0.9")],
                    runtimeArchitecture ?? (mixedPe || jsharp ? "x64" : "anycpu"),
                    runtimeFeatureTag is not null
                        ? [runtimeFeatureTag]
                        : jsharp
                            ? ["runtime.jsharp20-wine"]
                            : []),
                [],
                netFx ? BuildOutputKind.Console : BuildOutputKind.Library,
                _entryAssembly,
                jsharp ? "Program::main" : netFx && !mixedPe ? "Program.Main()" : null,
                [new ArtifactFileDescriptor(
                    "primary-assembly",
                    _entryAssembly,
                    expectedAssembly.LongLength,
                    manifestContentRef.Value)],
                Metadata: mixedPe
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["mixedMode"] = "true",
                        ["portablePdb"] = "false"
                    }
                    : jsharp
                        ? new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["clrMetadataVersion"] = "v2.0.50727",
                            ["portablePdb"] = "false"
                        }
                    : null));
            _bundle = new ArtifactBundleDescriptor(
                manifest,
                [new ArtifactBundleEntry(
                    _entryAssembly,
                    expectedAssembly.LongLength,
                    manifestContentRef.Value,
                    "primary-assembly",
                    bundleContentRef)]);
        }

        public ArtifactRef ArtifactRef => _bundle.Manifest.ArtifactId;
        public ContentRef? UploadedContentRef { get; private set; }
        public int ContentUploadCount => Volatile.Read(ref _contentUploadCount);
        public int ContentReadCount => Volatile.Read(ref _contentReadCount);
        public int FileDownloadCount => Volatile.Read(ref _fileDownloadCount);
        public int LeaseAcquisitionCount => Volatile.Read(ref _leaseAcquisitionCount);
        public int LeaseReleaseCount => Volatile.Read(ref _leaseReleaseCount);

        public void ExpireUploadedContent() => _uploadedContent = null;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var artifactPath = $"{ArtifactStoreProtocol.ApiPrefix}/artifacts/sha256/{ArtifactStoreProtocol.GetDigest(ArtifactRef)}";
            if (request.Method == HttpMethod.Post && path == $"{artifactPath}/leases")
            {
                Interlocked.Increment(ref _leaseAcquisitionCount);
                return Json(new ArtifactLeaseResponse(
                    "lease_test",
                    ArtifactRef,
                    "artifacts-default:test",
                    DateTimeOffset.UtcNow.AddMinutes(1)));
            }

            if (request.Method == HttpMethod.Get && path == artifactPath)
                return Json(_bundle);

            if (request.Method == HttpMethod.Get && path == $"{artifactPath}/files/{_entryAssembly}")
            {
                Interlocked.Increment(ref _fileDownloadCount);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(_servedAssembly)
                };
            }

            if (request.Method == HttpMethod.Delete && path == $"{ArtifactStoreProtocol.ApiPrefix}/leases/lease_test")
            {
                Interlocked.Increment(ref _leaseReleaseCount);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            var contentPrefix = $"{ArtifactStoreProtocol.ApiPrefix}/contents/sha256/";
            if (request.Method == HttpMethod.Get && path.StartsWith(contentPrefix, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _contentReadCount);
                var contentRef = ArtifactStoreProtocol.ContentRefFromDigest(path[contentPrefix.Length..]);
                return _uploadedContent is { } uploaded && UploadedContentRef == contentRef
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(uploaded) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (request.Method == HttpMethod.Put && path.StartsWith(contentPrefix, StringComparison.Ordinal))
            {
                var bytes = await (request.Content ?? throw new InvalidOperationException("Content is required."))
                    .ReadAsByteArrayAsync(cancellationToken);
                var contentRef = ArtifactStoreProtocol.ContentRefFromDigest(path[contentPrefix.Length..]);
                if (ContentIdentity.Compute(bytes) != contentRef)
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                UploadedContentRef = contentRef;
                _uploadedContent = bytes;
                Interlocked.Increment(ref _contentUploadCount);
                return Json(new PutContentResponse(
                    contentRef,
                    bytes.LongLength,
                    DateTimeOffset.UtcNow.AddHours(1),
                    false));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(value, options: JsonOptions)
        };
    }
}
