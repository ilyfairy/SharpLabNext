using SharpLabNext.Contracts;

namespace SharpLabNext.SecurityTests;

public sealed class ArchitectureBoundaryTests
{
    private static readonly string[] ForbiddenGatewayDependencies =
    [
        "Microsoft.CodeAnalysis",
        "Peachpie.CodeAnalysis",
        "Peachpie.Runtime",
        "Peachpie.Library",
        "FSharp.Compiler",
        "ICSharpCode.Decompiler",
        "Microsoft.Diagnostics.Runtime",
        "Iced",
        "Docker.DotNet"
    ];

    [Fact]
    public void GatewayDoesNotReferenceCompilerDecompilerOrDockerAssemblies()
    {
        var references = typeof(Program).Assembly.GetReferencedAssemblies().Select(static reference => reference.Name ?? string.Empty).ToArray();

        foreach (var forbidden in ForbiddenGatewayDependencies)
            Assert.DoesNotContain(references, name => name.StartsWith(forbidden, StringComparison.Ordinal));
    }

    [Fact]
    public void CoreContractsRemainBclOnly()
    {
        var references = typeof(ServiceIdentity).Assembly.GetReferencedAssemblies().Select(static reference => reference.Name ?? string.Empty).ToArray();

        Assert.All(references, static name => Assert.True(name.StartsWith("System.", StringComparison.Ordinal) || string.Equals(name, "System", StringComparison.Ordinal) || string.Equals(name, "netstandard", StringComparison.Ordinal), $"Contracts unexpectedly reference '{name}'."));
    }
}
