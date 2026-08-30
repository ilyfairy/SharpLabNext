using SharpLabNext.ArtifactWorker;

namespace SharpLabNext.ArtifactWorker.Tests;

public sealed class TemporaryArtifactDirectoryTests
{
    [Fact]
    public async Task DeleteRetriesTransientWindowsFileLock()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = TestSettings.CreateRoot();
        var path = Path.Combine(root, "locked.dll");
        await File.WriteAllBytesAsync(path, [1], TestContext.Current.CancellationToken);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var release = Task.Run(async () =>
            {
                await Task.Delay(75, TestContext.Current.CancellationToken);
                await stream.DisposeAsync();
            }, TestContext.Current.CancellationToken);

            TemporaryArtifactDirectory.Delete(root);
            await release;

            Assert.False(Directory.Exists(root));
        }
        finally
        {
            await stream.DisposeAsync();
            TestSettings.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task DeleteDoesNotSwallowPersistentWindowsFileLock()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = TestSettings.CreateRoot();
        var path = Path.Combine(root, "locked.dll");
        await File.WriteAllBytesAsync(path, [1], TestContext.Current.CancellationToken);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var exception = Record.Exception(() => TemporaryArtifactDirectory.Delete(root));

            Assert.True(exception is IOException or UnauthorizedAccessException, $"Expected a file-lock exception, but received {exception?.GetType().FullName ?? "no exception"}.");
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            await stream.DisposeAsync();
            TestSettings.DeleteRoot(root);
        }
    }
}
