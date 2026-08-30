using SharpLabNext.RuntimeProtocol;

namespace SharpLabNext.IntegrationTests;

public sealed class RuntimeArtifactLoadContextTests
{
    [Fact]
    public void ProbesRidSpecificAndRootNativeLibrariesWithoutAllowingTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SharpLabNext-NativeProbe-{Guid.NewGuid():N}");
        try
        {
            var ridDirectory = Path.Combine(root, "runtimes", "linux-x64", "native");
            Directory.CreateDirectory(ridDirectory);
            var ridLibrary = Path.Combine(ridDirectory, "libMono.Unix.so");
            File.WriteAllBytes(ridLibrary, [1]);
            File.WriteAllBytes(Path.Combine(root, "libMono.Unix.so"), [2]);

            Assert.Equal(ridLibrary, RuntimeArtifactLoadContext.ProbeUnmanagedLibrary(root, "linux-x64", "Mono.Unix"));
            Assert.Null(RuntimeArtifactLoadContext.ProbeUnmanagedLibrary(root, "../linux-x64", "Mono.Unix"));
            Assert.Null(RuntimeArtifactLoadContext.ProbeUnmanagedLibrary(root, "linux-x64", "../Mono.Unix"));

            File.Delete(ridLibrary);
            Assert.Equal(Path.Combine(root, "libMono.Unix.so"), RuntimeArtifactLoadContext.ProbeUnmanagedLibrary(root, "linux-x64", "Mono.Unix"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
