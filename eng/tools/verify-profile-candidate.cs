#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:project ../src/Tools/SharpLabNext.ProfileUpdater/SharpLabNext.ProfileUpdater.csproj

using SharpLabNext.ProfileUpdater;

const string usage = "Usage: dotnet run eng/verify-profile-candidate.cs -- --lock PATH --catalog PATH --endpoints PATH --bundle PATH [--timeout-seconds N]";

try
{
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    var timeout = TimeSpan.FromMinutes(5);
    for (var index = 0; index < args.Length; index++)
    {
        var argument = args[index];
        if (argument is "--help" or "-h")
        {
            Console.WriteLine(usage);
            return 0;
        }
        if (argument == "--timeout-seconds")
        {
            var value = RequiredValue(args, ref index, argument);
            if (!int.TryParse(value, out var seconds) || seconds <= 0 || seconds > 1800)
                throw new ArgumentException("--timeout-seconds must be an integer from 1 through 1800.");
            timeout = TimeSpan.FromSeconds(seconds);
            continue;
        }
        if (argument is not ("--lock" or "--catalog" or "--endpoints" or "--bundle"))
            throw new ArgumentException($"Unknown argument '{argument}'.");
        if (!values.TryAdd(argument, Path.GetFullPath(RequiredValue(args, ref index, argument))))
            throw new ArgumentException($"Argument '{argument}' was specified more than once.");
    }

    foreach (var required in new[] { "--lock", "--catalog", "--endpoints", "--bundle" })
    {
        if (!values.ContainsKey(required))
            throw new ArgumentException($"Missing required argument '{required}'.");
    }

    using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    var verifier = new ProfileCandidateDeploymentVerifier(http);
    var result = await verifier.VerifyAsync(new ProfileCandidateVerificationOptions
    {
        LockPath = values["--lock"],
        CatalogPath = values["--catalog"],
        EndpointsPath = values["--endpoints"],
        BundlePath = values["--bundle"],
        Timeout = timeout
    });
    Console.WriteLine(
        $"Verified candidate {result.ReleaseId} ({result.CatalogRevision}): " +
        $"{result.WorkersVerified} workers and {result.RuntimesVerified} runtimes.");
    return 0;
}
catch (Exception exception) when (exception is not OperationCanceledException)
{
    Console.Error.WriteLine($"Candidate verification failed: {exception.Message}");
    Console.Error.WriteLine(usage);
    return 1;
}

static string RequiredValue(string[] arguments, ref int index, string option)
{
    if (++index >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index]))
        throw new ArgumentException($"{option} requires a value.");
    return arguments[index];
}
