using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpLabNext.Contracts;
using SharpLabNext.Gateway;

namespace SharpLabNext.IntegrationTests;

public sealed class GatewayGistEndpointTests : IClassFixture<GatewayGistTestFactory>
{
    private readonly GatewayGistTestFactory _factory;

    public GatewayGistEndpointTests(GatewayGistTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PublicAndPrivateGistsFollowAnonymousAndOAuthReadRules()
    {
        var github = _factory.Services.GetRequiredService<FakeGitHubGistClient>();
        var gists = new GistShareService(github);
        var owner = GistShareServiceTests.Session("owner");
        var publicGist = await gists.CreateAsync(new CreateGistRequest("public", true, Workspace()), owner, TestContext.Current.CancellationToken);
        var privateGist = await gists.CreateAsync(new CreateGistRequest("private", false, Workspace()), owner, TestContext.Current.CancellationToken);
        using var anonymous = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        using var publicResponse = await anonymous.GetAsync($"/api/v1/shares/gists/{publicGist.Id}", TestContext.Current.CancellationToken);
        using var privateResponse = await anonymous.GetAsync($"/api/v1/shares/gists/{privateGist.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, publicResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, privateResponse.StatusCode);

        var session = CreateGatewaySession("owner");
        using var authenticated = CreateAuthenticatedClient(session);
        var loadedPrivate = await authenticated.GetFromJsonAsync<GistDocument>($"/api/v1/shares/gists/{privateGist.Id}", ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        Assert.NotNull(loadedPrivate);
        Assert.True(loadedPrivate.CanUpdate);
        Assert.Equal("private", loadedPrivate.Description);
    }

    [Fact]
    public async Task CreateAndExplicitUpdateRequireSessionCsrfAndDoNotExposeAccessToken()
    {
        var session = CreateGatewaySession("owner");
        using var authenticated = CreateAuthenticatedClient(session);
        var request = new CreateGistRequest("created", false, Workspace());

        using var missingCsrf = await authenticated.PostAsJsonAsync("/api/v1/shares/gists", request, ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, missingCsrf.StatusCode);

        authenticated.DefaultRequestHeaders.Add("X-SharpLabNext-CSRF", session.CsrfToken);
        using var createResponse = await authenticated.PostAsJsonAsync("/api/v1/shares/gists", request, ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var body = await createResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(session.AccessToken, body, StringComparison.Ordinal);
        var created = System.Text.Json.JsonSerializer.Deserialize<GistDocument>(body, ContractJson.CreateSerializerOptions());
        Assert.NotNull(created);

        using var updateRequest = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/shares/gists/{created.Id}")
        {
            Content = JsonContent.Create(
                new UpdateGistRequest("updated", Workspace() with { OutputId = "ast" }),
                options: ContractJson.CreateSerializerOptions())
        };
        using var updateResponse = await authenticated.SendAsync(updateRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<GistDocument>(ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Equal("updated", updated.Description);
        Assert.Equal("ast", updated.Workspace.OutputId);

        var statusBody = await authenticated.GetStringAsync("/api/v1/auth/github/status", TestContext.Current.CancellationToken);
        Assert.Contains("owner", statusBody, StringComparison.Ordinal);
        Assert.Contains(session.CsrfToken, statusBody, StringComparison.Ordinal);
        Assert.DoesNotContain(session.AccessToken, statusBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnonymousCreateIsRejected()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        using var response = await client.PostAsJsonAsync("/api/v1/shares/gists", new CreateGistRequest("anonymous", false, Workspace()), ContractJson.CreateSerializerOptions(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private GitHubOAuthSession CreateGatewaySession(string login)
    {
        var sessions = _factory.Services.GetRequiredService<GitHubOAuthSessionStore>();
        return sessions.CreateSession($"endpoint-secret-token-{login}", login, DateTimeOffset.UtcNow);
    }

    private HttpClient CreateAuthenticatedClient(GitHubOAuthSession session)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"SharpLabNext.GitHubSession={session.SessionId}");
        return client;
    }

    private static GistWorkspaceState Workspace() => GistShareServiceTests.Workspace([new GistSourceFile("Program.cs", "System.Console.WriteLine(42);")], "Program.cs");
}

public sealed class GatewayGistTestFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IGitHubGistClient>();
            services.AddSingleton<FakeGitHubGistClient>();
            services.AddSingleton<IGitHubGistClient>(static provider => provider.GetRequiredService<FakeGitHubGistClient>());
        });
    }
}
