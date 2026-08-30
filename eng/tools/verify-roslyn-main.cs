#:property TargetFramework=net10.0
#:property RestorePackagesWithLockFile=false
#:property PublishAot=false
#:property NoWarn=IL2026

using System.Reflection;
using System.Runtime.Loader;

if (args.Length != 3)
    throw new ArgumentException("Usage: verify-roslyn-main.cs <assembly-directory> <version> <commit>");

var assemblyDirectory = Path.GetFullPath(args[0]);
var expectedVersion = args[1];
var expectedCommit = args[2];
var requiredAssemblies = new[]
{
    "Microsoft.CodeAnalysis",
    "Microsoft.CodeAnalysis.CSharp",
    "Microsoft.CodeAnalysis.VisualBasic",
    "Microsoft.CodeAnalysis.Workspaces",
    "Microsoft.CodeAnalysis.CSharp.Workspaces",
    "Microsoft.CodeAnalysis.VisualBasic.Workspaces",
    "Microsoft.CodeAnalysis.Features",
    "Microsoft.CodeAnalysis.CSharp.Features",
    "Microsoft.CodeAnalysis.VisualBasic.Features",
    "Microsoft.CodeAnalysis.Scripting"
};

foreach (var name in requiredAssemblies)
{
    var path = Path.Combine(assemblyDirectory, $"{name}.dll");
    if (!File.Exists(path))
        throw new FileNotFoundException($"Required Roslyn assembly '{name}' was not produced.", path);
}

var context = new AssemblyLoadContext("roslyn-main-verification", isCollectible: true);
context.Resolving += (_, name) =>
{
    var candidate = Path.Combine(assemblyDirectory, $"{name.Name}.dll");
    return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
};

foreach (var name in new[] { "Microsoft.CodeAnalysis.CSharp", "Microsoft.CodeAnalysis.VisualBasic" })
{
    var assembly = context.LoadFromAssemblyPath(Path.Combine(assemblyDirectory, $"{name}.dll"));
    var version = assembly.GetName().Version is { } assemblyVersion
        ? $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}" : "unknown";
    var commit = assembly.GetCustomAttributesData().Where(static attribute => attribute.AttributeType.FullName == "Microsoft.CodeAnalysis.CommitHashAttribute").SelectMany(static attribute => attribute.ConstructorArguments).Select(static argument => argument.Value as string).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    if (!string.Equals(version, expectedVersion, StringComparison.Ordinal))
        throw new InvalidDataException($"{name} version '{version}' does not match '{expectedVersion}'.");
    if (!string.Equals(commit, expectedCommit, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException($"{name} commit '{commit ?? "<missing>"}' does not match '{expectedCommit}'.");
}

Console.WriteLine($"Verified Roslyn {expectedVersion} source build at {expectedCommit}.");
