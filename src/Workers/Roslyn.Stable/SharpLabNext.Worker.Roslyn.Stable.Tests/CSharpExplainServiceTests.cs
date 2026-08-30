using SharpLabNext.Contracts;
using SharpLabNext.Worker.Roslyn;

namespace SharpLabNext.Worker.Roslyn.Stable.Tests;

public sealed class CSharpExplainServiceTests
{
    [Fact]
    public async Task ExplainProducesStructuredDescriptionsForEveryWorkspaceFile()
    {
        var service = CreateService();
        var request = CreateRequest(
            [
                new WorkspaceFile("Program.cs", 2, "namespace Demo; class Program { static int Main() { return Helper.Value; } }"),
                new WorkspaceFile("Helper.cs", 3, "namespace Demo; static class Helper { public static int Value => 42; }")
            ],
            revision: 17,
            selectionRevision: 9);

        var result = await service.ExecuteAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("csharp", result.Document.LanguageId);
        Assert.Equal("roslyn-stable", result.Document.ToolchainId);
        Assert.Equal(17, result.Document.WorkspaceRevision);
        Assert.Equal(9, result.Document.SelectionRevision);
        Assert.Equal("5.6.0", result.Identity?.CompilerVersion);
        Assert.Equal("net10-ref", result.Identity?.ReferenceSetId);
        Assert.Equal("development-worker-image", result.Identity?.WorkerImageId);
        Assert.Equal(["Program.cs", "Helper.cs"], result.Document.Files.Select(static file => file.Path));
        Assert.Contains(result.Document.Files.SelectMany(static file => file.Nodes), static node => node.Kind == "ClassDeclaration" && node.Title.Contains("Program", StringComparison.Ordinal));
        Assert.Contains(result.Document.Files.SelectMany(static file => file.Nodes), static node => node.Kind == "ReturnStatement" && node.Description.Contains("result", StringComparison.OrdinalIgnoreCase));
        Assert.All(result.Document.Files.SelectMany(static file => file.Nodes), static node => Assert.True(node.Range.EndLine >= node.Range.StartLine));
        Assert.False(result.Document.Truncated);
    }

    [Fact]
    public async Task ExplainRejectsNonCSharpWorkspace()
    {
        var service = CreateService();
        var request = CreateRequest([new WorkspaceFile("Program.cs", 1, "class Program { }")]);
        request = request with { Workspace = request.Workspace with { LanguageId = "visual-basic", Files = [new WorkspaceFile("Program.vb", 1, "Module Program\nEnd Module")], ActiveFile = "Program.vb", SourceOrder = ["Program.vb"] } };

        await Assert.ThrowsAsync<BuildRequestValidationException>(() => service.ExecuteAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExplainReportsTruncationAtConfiguredNodeLimit()
    {
        var service = CreateService(new AstLimits(4, 128, 1024 * 1024, 80));
        var request = CreateRequest([new WorkspaceFile("Program.cs", 1, "class C { void M() { if (true) { System.Console.WriteLine(1); } } }")]);

        var result = await service.ExecuteAsync(request, TestContext.Current.CancellationToken);

        Assert.True(result.Document.Truncated);
        Assert.Equal(4, Assert.Single(result.Document.Files).Nodes.Count);
    }

    internal static ExplainRequest CreateRequest(IReadOnlyList<WorkspaceFile> files, long revision = 1, long selectionRevision = 1)
    {
        var options = new BuildOptions(BuildConfiguration.Release, Optimize: true, BuildOutputKind.Console, AllowUnsafe: false, EmitPortablePdb: true, NullableContextMode.Enable, LanguageVersion: "14.0");
        return new ExplainRequest($"explain-{Guid.NewGuid():N}", $"explain-key-{Guid.NewGuid():N}", "pipeline-explain", new WorkspaceSnapshot(ContractSchemaVersions.WorkspaceSnapshot, revision, selectionRevision, "csharp", files, files[0].Path, files.Select(static file => file.Path).ToArray(), "net10-ref", options), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    private static CSharpExplainService CreateService(AstLimits? limits = null) =>
        new(new RoslynWorkerIdentity("development", "roslyn-stable", "5.6.0", null, "development-worker-image"), CompilationLimits.Default, limits ?? AstLimits.Default);
}
