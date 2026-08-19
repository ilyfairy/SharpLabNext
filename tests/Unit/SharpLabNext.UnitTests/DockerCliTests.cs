using SharpLabNext.BundleBuilder;

namespace SharpLabNext.UnitTests;

public sealed class DockerCliTests
{
    private const string ImageId =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData("relative/file.dll")]
    [InlineData("/opt//sharplabnext/file.dll")]
    [InlineData("/opt/sharplabnext/file.dll/")]
    [InlineData("/opt/sharplabnext/../file.dll")]
    [InlineData("/opt\\sharplabnext\\file.dll")]
    public async Task InspectImageFileRejectsNonCanonicalContainerPaths(string path)
    {
        var docker = new DockerCli("command-must-not-run");

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() =>
            docker.InspectImageFileAsync(
                ImageId,
                path,
                maximumBytes: 1024,
                TestContext.Current.CancellationToken));

        Assert.Contains("canonical absolute container path", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("runtime:latest")]
    [InlineData("sha256:ABCDEF")]
    [InlineData("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task InspectImageFileRejectsMutableOrMalformedImageReferences(string image)
    {
        var docker = new DockerCli("command-must-not-run");

        var exception = await Assert.ThrowsAsync<BundleValidationException>(() =>
            docker.InspectImageFileAsync(
                image,
                "/opt/sharplabnext/file.dll",
                maximumBytes: 1024,
                TestContext.Current.CancellationToken));

        Assert.Contains("captured sha256 image ID", exception.Message, StringComparison.Ordinal);
    }
}
