using System.Net;
using System.Text.Json;
using SharpLabNext.Contracts;
using SharpLabNext.Gateway;

namespace SharpLabNext.IntegrationTests;

public sealed class GistShareServiceTests
{
    [Fact]
    public async Task NewGistRoundTripsMultipleFilesThroughHiddenVersionedMetadata()
    {
        var github = new FakeGitHubGistClient();
        var service = new GistShareService(github);
        var session = Session("owner");
        var workspace = Workspace(
            [
                new GistSourceFile("src/Program.cs", "System.Console.WriteLine(Helper.Value);"),
                new GistSourceFile("Helper.cs", "static class Helper { public static int Value => 42; }")
            ],
            activeFile: "src/Program.cs");

        var document = await service.CreateAsync(new CreateGistRequest("multi-file", IsPublic: false, workspace), session, TestContext.Current.CancellationToken);

        Assert.Equal("sharplabnext-v1", document.SourceFormat);
        Assert.Equal(workspace.Files, document.Workspace.Files);
        Assert.Equal(workspace.SourceOrder, document.Workspace.SourceOrder);
        Assert.True(document.CanUpdate);
        var write = Assert.Single(github.CreatedRequests);
        Assert.False(write.IsPublic);
        var metadataText = write.Files[GistShareService.MetadataFileName];
        Assert.NotNull(metadataText);
        using var metadata = JsonDocument.Parse(metadataText);
        Assert.Equal("SharpLabNext", metadata.RootElement.GetProperty("product").GetString());
        Assert.Equal(1, metadata.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(2, metadata.RootElement.GetProperty("files").GetArrayLength());
        Assert.DoesNotContain("System.Console.WriteLine", metadataText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacySharpLabGistRestoresMetadataAndUrlOverrides()
    {
        var github = new FakeGitHubGistClient();
        github.Seed(new GitHubGist(
            "abcde12345",
            "https://gist.github.com/abcde12345",
            "legacy-owner",
            true,
            "legacy",
            DateTimeOffset.UtcNow,
            new Dictionary<string, GitHubGistFile>(StringComparer.Ordinal)
            {
                ["Sample.cs"] = File("Sample.cs", "class Sample { }") ,
                [GistShareService.MetadataFileName] = File(
                    GistShareService.MetadataFileName,
                    """{"version":1,"target":"IL","mode":"Debug","branch":"main"}""")
            }));
        var service = new GistShareService(github);

        var document = await service.GetAsync("abcde12345", new GistLoadOverrides("asm", "roslyn-main", BuildConfiguration.Release), null, TestContext.Current.CancellationToken);

        Assert.Equal("sharplab-v1", document.SourceFormat);
        Assert.Equal("csharp", document.Workspace.LanguageId);
        Assert.Equal("jit-asm", document.Workspace.OutputId);
        Assert.Equal("roslyn-main", document.Workspace.LegacyBranchId);
        Assert.Equal(BuildConfiguration.Release, document.Workspace.BuildMode);
        Assert.Contains(document.Warnings, static warning => warning.Contains("legacy", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(document.Warnings, static warning => warning.Contains("override", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LegacyPhpGistRestoresPhpLanguageAndSourceFiles()
    {
        var github = new FakeGitHubGistClient();
        github.Seed(new GitHubGist(
            "abcde54321",
            "https://gist.github.com/abcde54321",
            "legacy-owner",
            true,
            "legacy PHP",
            DateTimeOffset.UtcNow,
            new Dictionary<string, GitHubGistFile>(StringComparer.Ordinal)
            {
                ["index.php"] = File("index.php", "<?php echo 'Hello';"),
                ["Helper.php"] = File("Helper.php", "<?php function helper(): int { return 42; }")
            }));
        var service = new GistShareService(github);

        var document = await service.GetAsync("abcde54321", new GistLoadOverrides(null, null, null), null, TestContext.Current.CancellationToken);

        Assert.Equal("github-gist", document.SourceFormat);
        Assert.Equal("php", document.Workspace.LanguageId);
        Assert.Equal("decompiled-csharp", document.Workspace.OutputId);
        Assert.Equal("index.php", document.Workspace.ActiveFile);
        Assert.Equal(["index.php", "Helper.php"], document.Workspace.SourceOrder);
    }

    [Fact]
    public async Task ExplicitUpdateDeletesOnlyPriorWorkspaceFilesAndPreservesUnrelatedFiles()
    {
        var github = new FakeGitHubGistClient();
        var service = new GistShareService(github);
        var session = Session("owner");
        var created = await service.CreateAsync(new CreateGistRequest("first", false, Workspace([new GistSourceFile("Old.cs", "class Old { }")], "Old.cs")), session, TestContext.Current.CancellationToken);
        github.AddFile(created.Id, "notes.md", "keep me");

        var updated = await service.UpdateAsync(created.Id, new UpdateGistRequest("second", Workspace([new GistSourceFile("New.cs", "class New { }")], "New.cs")), session, TestContext.Current.CancellationToken);

        Assert.Equal("second", updated.Description);
        Assert.Equal("New.cs", Assert.Single(updated.Workspace.Files).Path);
        var remote = await github.GetAsync(created.Id, session.AccessToken, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Old.cs", remote.Files.Keys);
        Assert.Contains("New.cs", remote.Files.Keys);
        Assert.Contains("notes.md", remote.Files.Keys);
    }

    [Fact]
    public async Task UpdateRejectsAuthenticatedNonOwner()
    {
        var github = new FakeGitHubGistClient();
        var service = new GistShareService(github);
        var owner = Session("owner");
        var created = await service.CreateAsync(new CreateGistRequest("owned", false, Workspace([new GistSourceFile("A.cs", "class A { }")], "A.cs")), owner, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GistAuthorizationException>(() => service.UpdateAsync(created.Id, new UpdateGistRequest("stolen", Workspace([new GistSourceFile("A.cs", "class B { }")], "A.cs")), Session("other"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WorkspaceTraversalAndOversizedInputAreRejectedBeforeGitHubAccess()
    {
        var github = new FakeGitHubGistClient();
        var service = new GistShareService(github);
        var invalid = Workspace([new GistSourceFile("../secret.cs", "class C { }")], "../secret.cs");

        await Assert.ThrowsAsync<GistValidationException>(() => service.CreateAsync(new CreateGistRequest("invalid", false, invalid), Session("owner"), TestContext.Current.CancellationToken));
        Assert.Empty(github.CreatedRequests);
    }

    internal static GistWorkspaceState Workspace(IReadOnlyList<GistSourceFile> files, string activeFile) => new(ContractSchemaVersions.WorkspaceSnapshot, "csharp", "roslyn-stable", "net10-ref", "il", null, BuildConfiguration.Release, "development", activeFile, files.Select(static file => file.Path).ToArray(), files);

    internal static GitHubOAuthSession Session(string login) => new($"session-{login}", $"token-{login}", login, $"csrf-{login}", DateTimeOffset.UtcNow.AddHours(1));

    private static GitHubGistFile File(string name, string content) => new(name, content, false, null, content.Length);
}

internal sealed class FakeGitHubGistClient : IGitHubGistClient
{
    private readonly Dictionary<string, GitHubGist> _gists = new(StringComparer.Ordinal);
    private int _nextId = 0xabcde;

    public List<GitHubGistWriteRequest> CreatedRequests { get; } = [];

    public Task<string> GetLoginAsync(string accessToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(accessToken.StartsWith("token-", StringComparison.Ordinal) ? accessToken[6..] : "owner");
    }

    public Task<GitHubGist> GetAsync(string id, string? accessToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_gists.TryGetValue(id, out var gist) || (!gist.IsPublic && accessToken is null))
            throw new GitHubApiException(HttpStatusCode.NotFound, "The Gist was not found or is private.");
        return Task.FromResult(gist);
    }

    public Task<GitHubGist> CreateAsync(GitHubGistWriteRequest request, string accessToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CreatedRequests.Add(request);
        var id = $"{Interlocked.Increment(ref _nextId):x}";
        var owner = accessToken.StartsWith("token-", StringComparison.Ordinal) ? accessToken[6..] : "owner";
        var gist = new GitHubGist(
            id,
            $"https://gist.github.com/{id}",
            owner,
            request.IsPublic == true,
            request.Description,
            DateTimeOffset.UtcNow,
            request.Files.Where(static item => item.Value is not null).ToDictionary(static item => item.Key, static item => new GitHubGistFile(item.Key, item.Value, false, null, item.Value!.Length), StringComparer.Ordinal));
        _gists[id] = gist;
        return Task.FromResult(gist);
    }

    public Task<GitHubGist> UpdateAsync(string id, GitHubGistWriteRequest request, string accessToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = _gists[id];
        var files = current.Files.ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal);
        foreach (var (name, content) in request.Files)
        {
            if (content is null)
                files.Remove(name);
            else
                files[name] = new GitHubGistFile(name, content, false, null, content.Length);
        }
        var updated = current with { Description = request.Description, UpdatedAtUtc = DateTimeOffset.UtcNow, Files = files };
        _gists[id] = updated;
        return Task.FromResult(updated);
    }

    public void Seed(GitHubGist gist) => _gists[gist.Id] = gist;

    public void AddFile(string id, string name, string content)
    {
        var current = _gists[id];
        var files = current.Files.ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal);
        files[name] = new GitHubGistFile(name, content, false, null, content.Length);
        _gists[id] = current with { Files = files };
    }
}
