using SharpLabNext.BundleBuilder;

namespace SharpLabNext.UnitTests;

public sealed class RuntimePromotionProfileLockTests
{
    [Fact]
    public async Task SameProfileCannotBeAcquiredTwiceUntilTheFirstLeaseIsDisposed()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"sharplabnext-profile-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var first = await RuntimePromotionProfileLock.AcquireAsync(
                root,
                "dotnet-10-linux-x64",
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<TimeoutException>(() =>
                RuntimePromotionProfileLock.AcquireAsync(
                    root,
                    "dotnet-10-linux-x64",
                    TimeSpan.FromMilliseconds(100),
                    TestContext.Current.CancellationToken));

            first.Dispose();
            using var reacquired = await RuntimePromotionProfileLock.AcquireAsync(
                root,
                "dotnet-10-linux-x64",
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DifferentProfilesCanBeAcquiredConcurrently()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"sharplabnext-profile-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var first = await RuntimePromotionProfileLock.AcquireAsync(
                root,
                "dotnet-10-linux-x64",
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);
            using var second = await RuntimePromotionProfileLock.AcquireAsync(
                root,
                "dotnet-11-preview-linux-x64",
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
