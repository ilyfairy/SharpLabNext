#:property TargetFramework=net10.0
#:property PublishAot=false
#:property NoWarn=IL2026

using System.Reflection;
using System.Runtime.Loader;

if (args.Length != 3)
    throw new ArgumentException("Usage: verify-roslyn-const-generics.cs <assembly-directory> <version> <commit>");

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
    "Microsoft.CodeAnalysis.Scripting",
    "System.Reflection.Metadata",
    "System.Collections.Immutable"
};

foreach (var name in requiredAssemblies)
{
    var path = Path.Combine(assemblyDirectory, $"{name}.dll");
    if (!File.Exists(path))
        throw new FileNotFoundException($"Required ConstGenerics assembly '{name}' was not produced.", path);
}

var context = new AssemblyLoadContext("roslyn-const-generics-verification", isCollectible: true);
context.Resolving += (_, name) =>
{
    var candidate = Path.Combine(assemblyDirectory, $"{name.Name}.dll");
    return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
};

var compilerDependencyVersion = new Version(8, 0, 0, 0);
var immutable = context.LoadFromAssemblyPath(Path.Combine(assemblyDirectory, "System.Collections.Immutable.dll"));
var metadata = context.LoadFromAssemblyPath(Path.Combine(assemblyDirectory, "System.Reflection.Metadata.dll"));
foreach (var dependency in new[] { immutable, metadata })
{
    if (dependency.GetName().Version != compilerDependencyVersion)
    {
        throw new InvalidDataException(
            $"{dependency.GetName().Name} version '{dependency.GetName().Version}' does not match '{compilerDependencyVersion}'.");
    }
}

var immutableReference = metadata.GetReferencedAssemblies()
    .SingleOrDefault(static reference => reference.Name == "System.Collections.Immutable");
if (immutableReference?.Version != compilerDependencyVersion)
{
    throw new InvalidDataException(
        $"System.Reflection.Metadata references System.Collections.Immutable '{immutableReference?.Version}', expected '{compilerDependencyVersion}'.");
}

var common = context.LoadFromAssemblyPath(Path.Combine(assemblyDirectory, "Microsoft.CodeAnalysis.dll"));
var csharp = context.LoadFromAssemblyPath(Path.Combine(assemblyDirectory, "Microsoft.CodeAnalysis.CSharp.dll"));
var visualBasic = context.LoadFromAssemblyPath(Path.Combine(assemblyDirectory, "Microsoft.CodeAnalysis.VisualBasic.dll"));
foreach (var assembly in new[] { csharp, visualBasic })
{
    var version = assembly.GetName().Version is { } assemblyVersion
        ? $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}"
        : "unknown";
    var commit = assembly.GetCustomAttributesData()
        .Where(static attribute => attribute.AttributeType.FullName == "Microsoft.CodeAnalysis.CommitHashAttribute")
        .SelectMany(static attribute => attribute.ConstructorArguments)
        .Select(static argument => argument.Value as string)
        .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    if (!string.Equals(version, expectedVersion, StringComparison.Ordinal))
        throw new InvalidDataException($"{assembly.GetName().Name} version '{version}' does not match '{expectedVersion}'.");
    if (!string.Equals(commit, expectedCommit, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException($"{assembly.GetName().Name} commit '{commit ?? "<missing>"}' does not match '{expectedCommit}'.");
}

if (csharp.GetType("Microsoft.CodeAnalysis.CSharp.Syntax.LiteralTypeArgumentSyntax") is null)
    throw new InvalidDataException("The source-built C# compiler does not expose LiteralTypeArgumentSyntax.");
if (common.GetType("Microsoft.CodeAnalysis.ITypeParameterSymbol")?.GetProperty("Type") is null)
    throw new InvalidDataException("The source-built compiler does not expose const type parameter metadata.");

if (metadata.GetType("System.Reflection.Metadata.GenericParameter")?.GetProperty("Type") is null)
    throw new InvalidDataException("The matching metadata build does not expose GenericParameter.Type.");
if (metadata.GetType("System.Reflection.Metadata.Ecma335.SignatureTypeEncoder")?
        .GetMethod("ConstValueType", BindingFlags.Public | BindingFlags.Instance) is null)
{
    throw new InvalidDataException("The matching metadata build does not expose SignatureTypeEncoder.ConstValueType.");
}

Console.WriteLine($"Verified ConstGenerics Roslyn {expectedVersion} source build at {expectedCommit}.");
