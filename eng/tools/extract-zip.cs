#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property RestorePackagesWithLockFile=false

using System.IO.Compression;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: dotnet run extract-zip.cs -- ARCHIVE DESTINATION");
    return 64;
}

const int maximumEntries = 20_000;
const long maximumExpandedBytes = 512L * 1024 * 1024;
var archivePath = Path.GetFullPath(args[0]);
var destination = Path.GetFullPath(args[1]);
var destinationPrefix = destination.EndsWith(Path.DirectorySeparatorChar) ? destination : destination + Path.DirectorySeparatorChar;
Directory.CreateDirectory(destination);

using var archive = ZipFile.OpenRead(archivePath);
if (archive.Entries.Count > maximumEntries)
    throw new InvalidDataException("ZIP archive contains too many entries.");

long expandedBytes = 0;
foreach (var entry in archive.Entries)
{
    expandedBytes = checked(expandedBytes + entry.Length);
    if (expandedBytes > maximumExpandedBytes)
        throw new InvalidDataException("ZIP archive exceeds the expanded-size limit.");

    var outputPath = Path.GetFullPath(Path.Combine(destination, entry.FullName));
    if (!outputPath.StartsWith(destinationPrefix, StringComparison.Ordinal))
        throw new InvalidDataException("ZIP archive contains an unsafe path.");

    if (string.IsNullOrEmpty(entry.Name))
    {
        Directory.CreateDirectory(outputPath);
        continue;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    entry.ExtractToFile(outputPath, overwrite: false);
}

return 0;
