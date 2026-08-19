using SharpLabNext.Catalog;

namespace SharpLabNext.UnitTests;

public sealed class ReferenceSetIdentityResolverTests
{
    [Fact]
    public void UnavailableMatrixReferenceSetDoesNotBlockReleaseClosure()
    {
        var available = new ReferenceSetManifest
        {
            Id = "net10-ref",
            DisplayName = ".NET 10",
            TargetFramework = "net10.0",
            Digest = "sha512-net10",
            RuntimeFamily = "coreclr",
            Availability = new ComponentAvailability { Installed = true, Health = "healthy" }
        };
        var blocked = new ReferenceSetManifest
        {
            Id = "net8-ref",
            DisplayName = ".NET 8",
            TargetFramework = "net8.0",
            Digest = "sha512-net8",
            RuntimeFamily = "coreclr",
            Availability = new ComponentAvailability
            {
                Installed = false,
                Health = "not-installed",
                Reason = "The candidate image has not been materialized."
            }
        };
        var catalog = new CatalogDocument
        {
            SchemaVersion = 1,
            Revision = "test",
            ReleaseId = "test",
            Languages = [],
            Toolchains = [],
            ReferenceSets = [available, blocked],
            Runtimes = [],
            ArtifactProcessors = [],
            Outputs = [],
            Compatibility = [],
            Presets = []
        };
        var releaseLock = new ReleaseLockDocument
        {
            SchemaVersion = 1,
            ReleaseId = "test",
            ResolvedAt = DateTimeOffset.UtcNow,
            Components = new Dictionary<string, LockedComponent>(StringComparer.Ordinal)
            {
                ["net10-ref"] = new()
                {
                    Kind = "reference-set",
                    ResolvedVersion = "10.0.0",
                    Package = "Microsoft.NETCore.App.Ref",
                    PackageContentHash = "sha512-net10"
                }
            }
        };

        var result = ReferenceSetIdentityResolver.ResolveExpectedDigests(catalog, releaseLock);

        Assert.Equal(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["net10-ref"] = "sha512-net10"
        }, result);
    }

    [Fact]
    public void UnavailableReferenceSetHostedBySelectableToolchainRemainsInReleaseClosure()
    {
        var referenceSet = new ReferenceSetManifest
        {
            Id = "netfx30-managed-ref",
            DisplayName = ".NET Framework 3.0",
            TargetFramework = "net30",
            Digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            RuntimeFamily = "netfx-clr-wine",
            Availability = new ComponentAvailability
            {
                Installed = false,
                Health = "not-installed",
                Reason = "The matching runtime has not been promoted."
            }
        };
        var catalog = new CatalogDocument
        {
            SchemaVersion = 1,
            Revision = "test",
            ReleaseId = "test",
            Languages = [],
            Toolchains =
            [
                new ToolchainManifest
                {
                    Id = "roslyn-framework",
                    DisplayName = "Roslyn Framework",
                    ReleaseTrack = "stable",
                    ResolvedVersion = "1.0.0",
                    WorkerId = "roslyn-framework",
                    SupportedLanguageIds = ["csharp"],
                    DefaultReferenceSetId = referenceSet.Id,
                    AllowedReferenceSetIds = [referenceSet.Id],
                    ProducesArtifactFormats = ["dotnet-framework-managed-pe-v1"],
                    Capabilities = ["managed-pe"],
                    Availability = new ComponentAvailability { Installed = true, Health = "healthy" }
                }
            ],
            ReferenceSets = [referenceSet],
            Runtimes = [],
            ArtifactProcessors = [],
            Outputs = [],
            Compatibility = [],
            Presets = []
        };
        var releaseLock = new ReleaseLockDocument
        {
            SchemaVersion = 1,
            ReleaseId = "test",
            ResolvedAt = DateTimeOffset.UtcNow,
            Components = new Dictionary<string, LockedComponent>(StringComparer.Ordinal)
            {
                [referenceSet.Id] = new()
                {
                    Kind = "reference-set",
                    ResolvedVersion = "net30-union-v1",
                    Digest = referenceSet.Digest
                }
            }
        };

        var result = ReferenceSetIdentityResolver.ResolveExpectedDigests(catalog, releaseLock);

        Assert.Equal(referenceSet.Digest, Assert.Single(result).Value);
    }
}
