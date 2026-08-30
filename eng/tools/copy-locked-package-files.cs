#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property RestorePackagesWithLockFile=false

using System.Text.Json;

if (args.Length < 5 || (args.Length - 2) % 3 != 0)
{
    Console.Error.WriteLine("Usage: dotnet run copy-locked-package-files.cs -- LOCK NUGET_ROOT PACKAGE RELATIVE_PATH OUTPUT [PACKAGE RELATIVE_PATH OUTPUT ...]");
    return 64;
}

var lockPath = Path.GetFullPath(args[0]);
var packageRoot = Path.GetFullPath(args[1]);
using var document = JsonDocument.Parse(File.ReadAllBytes(lockPath));
var dependencies = document.RootElement.GetProperty("dependencies");

for (var index = 2; index < args.Length; index += 3)
{
    var packageId = args[index];
    var relativePath = args[index + 1];
    var outputPath = Path.GetFullPath(args[index + 2]);
    var versions = dependencies.EnumerateObject().SelectMany(static framework => framework.Value.EnumerateObject()).Where(package => string.Equals(package.Name, packageId, StringComparison.OrdinalIgnoreCase)).Select(static package => package.Value.GetProperty("resolved").GetString()).Where(static version => !string.IsNullOrWhiteSpace(version)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    if (versions.Length != 1)
        throw new InvalidDataException($"Package '{packageId}' does not resolve to exactly one version in '{lockPath}'.");

    var packageDirectory = Path.GetFullPath(Path.Combine(packageRoot, packageId.ToLowerInvariant(), versions[0]!));
    var sourcePath = Path.GetFullPath(Path.Combine(packageDirectory, relativePath));
    if (!sourcePath.StartsWith(packageDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(sourcePath))
    {
        throw new FileNotFoundException($"Locked package file '{packageId}/{versions[0]}/{relativePath}' is unavailable.", sourcePath);
    }

    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    File.Copy(sourcePath, outputPath, overwrite: true);
    Console.WriteLine(outputPath);
}

return 0;
