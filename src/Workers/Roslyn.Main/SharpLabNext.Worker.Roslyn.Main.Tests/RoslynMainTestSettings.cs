using SharpLabNext.Testing;

namespace SharpLabNext.Worker.Roslyn.Main.Tests;

internal static class RoslynMainTestSettings
{
    public const string LockedCommit = "708c0a9669c6c996b7e13ea4b161d841bbfdf8b2";
    public const string InternalServiceToken = "sharplabnext-development-internal-token-only-2026";
#if ROSLYN_MAIN_SOURCE_BUILD
    public const string LocalValidationCommit = LockedCommit;
    public static bool IsSourceBuild => true;
#else
    public const string LocalValidationCommit = "83ca1a6465bb861e28a51cdbb4b56074b69cb5eb";
    public static bool IsSourceBuild => false;
#endif

    public static TestReferenceSet Net10ReferenceSet => TestReferenceSets.Net10;

    public static TestReferenceSet Net11ReferenceSet => TestReferenceSets.Net11;

    public static string GetInternalServiceTokenFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "deploy", "secrets", "internal-service-token.dev");
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the internal service token test fixture.");
    }
}
