#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property NuGetLockFilePath=obj/resolve-source-provenance.packages.lock.json
#:project ../src/Tools/SharpLabNext.BundleBuilder/SharpLabNext.BundleBuilder.csproj

using SharpLabNext.BundleBuilder;

string? repositoryRoot = null;
string? requestedRevision = null;
var allowUncommittedSourceForDevelopment = false;
for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--repository-root":
            repositoryRoot = RequiredValue(args, ref index);
            break;
        case "--source-revision":
            requestedRevision = RequiredValue(args, ref index);
            break;
        case "--allow-uncommitted-source-for-development":
            allowUncommittedSourceForDevelopment = true;
            break;
        default:
            Console.Error.WriteLine($"Unknown argument '{args[index]}'.");
            return 64;
    }
}

if (string.IsNullOrWhiteSpace(repositoryRoot))
{
    Console.Error.WriteLine(
        "Usage: dotnet run eng/resolve-source-provenance.cs -- --repository-root PATH " +
        "[--source-revision REVISION] [--allow-uncommitted-source-for-development]");
    return 64;
}

try
{
    var source = await RepositorySourceProvenanceResolver.ResolveAsync(
        repositoryRoot,
        requestedRevision,
        allowUncommittedSourceForDevelopment);
    Console.WriteLine($"SHARPLABNEXT_SOURCE_REVISION={source.Revision}");
    return 0;
}
catch (BundleValidationException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static string RequiredValue(string[] values, ref int index)
{
    index++;
    if (index >= values.Length || string.IsNullOrWhiteSpace(values[index]))
    {
        throw new BundleValidationException("An option value is missing.");
    }

    return values[index];
}
