using System.Text;
using SharpLabNext.BundleBuilder;

namespace SharpLabNext.UnitTests;

public sealed class RuntimeMatrixBaseImageBindingsTests
{
    [Fact]
    public void ParserRejectsDuplicateLinuxRuntimeRows()
    {
        var digest = new string('a', 64);
        var bytes = Encoding.UTF8.GetBytes($$"""
            {
              "schemaVersion": 1,
              "coreClr": [
                { "id": "dotnet-5", "linuxBaseImage": "example/runtime:5@sha256:{{digest}}" },
                { "id": "dotnet-5", "linuxBaseImage": "example/runtime:5@sha256:{{digest}}" }
              ]
            }
            """);

        var exception = Assert.Throws<BundleValidationException>(() => RuntimeMatrixBaseImageBindings.Parse(bytes));

        Assert.Contains("duplicate Linux runtime row", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParserRejectsUnpinnedLinuxBaseImage()
    {
        var bytes = Encoding.UTF8.GetBytes("""
            {
              "schemaVersion": 1,
              "coreClr": [
                { "id": "dotnet-5", "linuxBaseImage": "example/runtime:5" }
              ]
            }
            """);

        var exception = Assert.Throws<BundleValidationException>(() => RuntimeMatrixBaseImageBindings.Parse(bytes));

        Assert.Contains("not pinned by SHA-256 digest", exception.Message, StringComparison.Ordinal);
    }
}
