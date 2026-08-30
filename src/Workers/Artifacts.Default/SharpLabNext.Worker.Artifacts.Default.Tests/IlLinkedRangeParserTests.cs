using SharpLabNext.ArtifactProcessing;

namespace SharpLabNext.ArtifactWorker.Tests;

public sealed class IlLinkedRangeParserTests
{
    [Fact]
    public async Task ParseAndStripRemovesMarkersAndMapsRangesToFilteredLines()
    {
        var root = TestSettings.CreateRoot();
        try
        {
            var path = Path.Combine(root, "output.il");
            await File.WriteAllTextAsync(
                path,
                """
                .method private static void Main() cil managed
                {
                    // sequence point: (line 9, col 27) to (line 9, col 36) in C:\repo\Program.cs
                    IL_0000: nop
                    // sequence point: hidden
                    // sequence point: (line 10, col 5) to (line 10, col 12) in C:\repo\Program.cs
                    IL_0001: ret
                }
                """.Replace("\r\n", "\n", StringComparison.Ordinal),
                TestContext.Current.CancellationToken);

            var result = await IlLinkedRangeParser.ParseAndStripAsync(path, TestContext.Current.CancellationToken);

            var filtered = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(
                """
                .method private static void Main() cil managed
                {
                    IL_0000: nop
                    IL_0001: ret
                }
                """.Replace("\r\n", "\n", StringComparison.Ordinal),
                filtered);
            Assert.Equal(filtered.Length, result.CharactersWritten);
            Assert.Collection(
                result.LinkedRanges,
                range =>
                {
                    Assert.Equal("C:/repo/Program.cs", range.SourceFilePath);
                    Assert.Equal(2, range.OutputRange.StartLine);
                    Assert.Equal(8, range.SourceRange?.StartLine);
                    Assert.Equal(26, range.SourceRange?.StartCharacter);
                },
                range =>
                {
                    Assert.Equal("C:/repo/Program.cs", range.SourceFilePath);
                    Assert.Equal(3, range.OutputRange.StartLine);
                    Assert.Equal(9, range.SourceRange?.StartLine);
                    Assert.Equal(4, range.SourceRange?.StartCharacter);
                });
        }
        finally
        {
            TestSettings.DeleteRoot(root);
        }
    }
}
