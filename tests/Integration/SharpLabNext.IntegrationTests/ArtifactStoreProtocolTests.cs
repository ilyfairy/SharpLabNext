using System.Text;
using SharpLabNext.ArtifactStore.Client;
using SharpLabNext.Contracts;

namespace SharpLabNext.IntegrationTests;

public sealed class ArtifactStoreProtocolTests
{
    [Theory]
    [InlineData("../app.dll")]
    [InlineData("bin/../app.dll")]
    [InlineData("bin//app.dll")]
    [InlineData("./app.dll")]
    [InlineData("/app.dll")]
    [InlineData("C:/app.dll")]
    [InlineData("bin\\app.dll")]
    [InlineData("app.dll\0hidden")]
    public void UnsafeArtifactPathsAreRejected(string path)
    {
        _ = Assert.Throws<ArgumentException>(() => ArtifactPath.Normalize(path));
    }

    [Fact]
    public void DuplicateArtifactPathsAreRejected()
    {
        _ = Assert.Throws<ArgumentException>(() => ArtifactPath.NormalizeDistinct(["a.dll", "a.dll"]));
    }

    [Fact]
    public void ContentAndArtifactIdentitiesAreDeterministic()
    {
        var firstBytes = Encoding.UTF8.GetBytes("first");
        var secondBytes = Encoding.UTF8.GetBytes("second");
        var first = ArtifactStoreTestData.CreateManifest(
            ("app.dll", firstBytes, "primary-assembly"),
            ("app.pdb", secondBytes, "portable-pdb"));
        var second = ArtifactStoreTestData.CreateManifest(
            ("app.dll", firstBytes, "primary-assembly"),
            ("app.pdb", secondBytes, "portable-pdb"));

        Assert.Equal(ContentIdentity.Compute(firstBytes), ContentIdentity.Compute(firstBytes));
        Assert.Equal(first.ArtifactId, second.ArtifactId);
        ArtifactIdentity.Validate(first);
    }

    [Fact]
    public void ManifestOrderIsPartOfArtifactIdentity()
    {
        var firstBytes = Encoding.UTF8.GetBytes("first");
        var secondBytes = Encoding.UTF8.GetBytes("second");
        var first = ArtifactStoreTestData.CreateManifest(
            ("app.dll", firstBytes, "primary-assembly"),
            ("app.pdb", secondBytes, "portable-pdb"));
        var reordered = ArtifactStoreTestData.CreateManifest(
            ("app.pdb", secondBytes, "portable-pdb"),
            ("app.dll", firstBytes, "primary-assembly"));

        Assert.NotEqual(first.ArtifactId, reordered.ArtifactId);
    }

    [Theory]
    [InlineData(BuildOutputKind.Auto)]
    [InlineData((BuildOutputKind)999)]
    public void ArtifactIdentityRejectsNonConcreteOutputKinds(BuildOutputKind outputKind)
    {
        var manifest = ArtifactStoreTestData.CreateManifest(
            ("app.dll", Encoding.UTF8.GetBytes("assembly"), "primary-assembly")) with
        {
            OutputKind = outputKind
        };

        var exception = Assert.Throws<ArgumentException>(() => ArtifactIdentity.Compute(manifest));

        Assert.Contains("concrete output kind", exception.Message, StringComparison.Ordinal);
    }
}
