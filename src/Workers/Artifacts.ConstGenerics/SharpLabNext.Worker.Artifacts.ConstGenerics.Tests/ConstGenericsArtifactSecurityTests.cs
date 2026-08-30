using SharpLabNext.Artifacts.Contracts;
using SharpLabNext.ArtifactWorker.Sdk;
using SharpLabNext.Worker.Artifacts.ConstGenerics.Protocol;

namespace SharpLabNext.Worker.Artifacts.ConstGenerics.Tests;

public sealed class ConstGenericsArtifactSecurityTests
{
    [Fact]
    public async Task RejectsEveryNonAtomicManifestBeforeLeaseOrFileDownload()
    {
        var root = ConstGenericsTestInfrastructure.CreateRoot();
        try
        {
            var mutations = new Func<ArtifactManifest, ArtifactManifest>[]
            {
                manifest => manifest with { Producer = manifest.Producer with { ToolchainId = "roslyn-stable" } },
                manifest => manifest with { ReferenceSetId = "net10-ref" },
                manifest => manifest with { TargetFramework = "net10.0" },
                manifest => manifest with { RuntimeRequirement = manifest.RuntimeRequirement with { Family = "coreclr" } },
                manifest => manifest with { RuntimeRequirement = manifest.RuntimeRequirement with { RequiredRuntimeFeatureTags = [] } },
                manifest => manifest with
                {
                    RuntimeRequirement = manifest.RuntimeRequirement with
                    {
                        RequiredRuntimeFeatureTags =
                        [
                            ConstGenericsProcessorProtocol.RuntimeFeatureTag,
                            "runtime.unapproved"
                        ]
                    }
                },
                manifest => manifest with
                {
                    RuntimeRequirement = manifest.RuntimeRequirement with
                    {
                        Frameworks =
                        [
                            new FrameworkRequirement("Microsoft.NETCore.App", "9.0.0")
                        ]
                    }
                },
                manifest => manifest with
                {
                    RuntimeRequirement = manifest.RuntimeRequirement with
                    {
                        Frameworks =
                        [
                            new FrameworkRequirement("Microsoft.AspNetCore.App", "9.0.0-constgenerics.1.23470.1")
                        ]
                    }
                },
                manifest => manifest with
                {
                    RuntimeRequirement = manifest.RuntimeRequirement with
                    {
                        Frameworks =
                        [
                            new FrameworkRequirement("Microsoft.NETCore.App", "9.0.0-constgenerics.1.23470.1"),
                            new FrameworkRequirement("Microsoft.AspNetCore.App", "9.0.0")
                        ]
                    }
                },
                manifest => manifest with { MetadataFeatureTags = [] },
                manifest => manifest with
                {
                    MetadataFeatureTags =
                    [
                        ConstGenericsProcessorProtocol.MetadataFeatureTag,
                        "metadata.unapproved"
                    ]
                },
                manifest => manifest with
                {
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["compatibilityGroup"] = "ordinary"
                    }
                }
            };

            foreach (var mutation in mutations)
            {
                var handler = new ConstGenericsArtifactStoreHandler(mutation);
                var materializer = new ConstGenericsArtifactMaterializer(ConstGenericsTestInfrastructure.CreateClient(handler), ConstGenericsTestInfrastructure.Settings(root), ConstGenericsTestInfrastructure.CapabilityManifest());
                await Assert.ThrowsAsync<ArtifactWorkerIncompatibleArtifactException>(() => materializer.MaterializeAsync(handler.ArtifactRef, $"op_{Guid.NewGuid():N}", TestContext.Current.CancellationToken));
                Assert.Equal(0, handler.LeaseAcquisitionCount);
                Assert.Equal(0, handler.FileDownloadCount);
            }
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            ConstGenericsTestInfrastructure.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task DigestMismatchReleasesLeaseAndRemovesMaterializedFiles()
    {
        var root = ConstGenericsTestInfrastructure.CreateRoot();
        try
        {
            var handler = new ConstGenericsArtifactStoreHandler(corruptContent: true);
            var materializer = new ConstGenericsArtifactMaterializer(ConstGenericsTestInfrastructure.CreateClient(handler), ConstGenericsTestInfrastructure.Settings(root), ConstGenericsTestInfrastructure.CapabilityManifest());

            await Assert.ThrowsAsync<ArtifactWorkerIncompatibleArtifactException>(() => materializer.MaterializeAsync(handler.ArtifactRef, "op_digest_mismatch", TestContext.Current.CancellationToken));

            Assert.Equal(1, handler.LeaseAcquisitionCount);
            Assert.Equal(1, handler.LeaseReleaseCount);
            Assert.Equal(1, handler.FileDownloadCount);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            ConstGenericsTestInfrastructure.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task OrdinaryArtifactDoesNotStartTheIsolatedProcessor()
    {
        var root = ConstGenericsTestInfrastructure.CreateRoot();
        try
        {
            var handler = new ConstGenericsArtifactStoreHandler(manifest => manifest with { MetadataFeatureTags = [], Metadata = null });
            var client = ConstGenericsTestInfrastructure.CreateClient(handler);
            var settings = ConstGenericsTestInfrastructure.Settings(root);
            var capability = ConstGenericsTestInfrastructure.CapabilityManifest();
            var runner = new ConstGenericsProcessorRunner(settings, capability);
            var processor = new ConstGenericsArtifactProcessor(new ConstGenericsArtifactMaterializer(client, settings, capability), runner, client, settings, capability);

            await Assert.ThrowsAsync<ArtifactWorkerIncompatibleArtifactException>(() => processor.RenderAsync(ConstGenericsTestInfrastructure.RenderRequest(handler.ArtifactRef, "il"), "op_ordinary", ConstGenericsProcessorOperation.Il, TestContext.Current.CancellationToken));

            Assert.Equal(0, runner.StartedProcessCount);
            Assert.Equal(0, handler.LeaseAcquisitionCount);
            Assert.Equal(0, handler.FileDownloadCount);
        }
        finally
        {
            ConstGenericsTestInfrastructure.DeleteRoot(root);
        }
    }
}
