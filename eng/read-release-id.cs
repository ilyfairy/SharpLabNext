#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property NuGetLockFilePath=obj/read-release-id.packages.lock.json

using System.Text.Json;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: dotnet run eng/read-release-id.cs -- PATH");
    return 64;
}

using var document = JsonDocument.Parse(File.ReadAllText(args[0]));
if (!document.RootElement.TryGetProperty("releaseId", out var releaseId) ||
    releaseId.ValueKind != JsonValueKind.String ||
    string.IsNullOrWhiteSpace(releaseId.GetString()))
{
    Console.Error.WriteLine("Release lock does not contain releaseId.");
    return 1;
}

Console.WriteLine(releaseId.GetString());
return 0;
