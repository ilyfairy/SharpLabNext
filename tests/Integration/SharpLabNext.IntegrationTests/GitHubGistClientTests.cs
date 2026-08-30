using System.Net;
using System.Net.Http.Headers;
using System.Text;
using SharpLabNext.Gateway;

namespace SharpLabNext.IntegrationTests;

public sealed class GitHubGistClientTests
{
    [Fact]
    public async Task PublicReadIsAnonymousAndPrivateReadUsesPerRequestBearerToken()
    {
        var handler = new GistRecordingHandler(request =>
        {
            var id = request.RequestUri!.Segments[^1];
            return JsonResponse("""
                {
                  "id":"__ID__",
                  "html_url":"https://gist.github.com/__ID__",
                  "public":true,
                  "description":"test",
                  "owner":{"login":"owner"},
                  "files":{"Program.cs":{"filename":"Program.cs","content":"class Program {}","truncated":false,"size":16}}
                }
                """.Replace("__ID__", id, StringComparison.Ordinal));
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.test/") };
        var client = new GitHubGistClient(http);

        await client.GetAsync("abcde1", null, TestContext.Current.CancellationToken);
        await client.GetAsync("abcde2", "secret-token", TestContext.Current.CancellationToken);

        Assert.Null(handler.Authorization[0]);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "secret-token"), handler.Authorization[1]);
        Assert.DoesNotContain("secret-token", handler.RequestUris[1].AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TruncatedSourceRejectsRawUrlsOutsidePinnedGitHubHost()
    {
        var handler = new GistRecordingHandler(_ => JsonResponse("""
            {
              "id":"abcde1",
              "html_url":"https://gist.github.com/abcde1",
              "public":true,
              "files":{
                "Program.cs":{
                  "filename":"Program.cs",
                  "content":null,
                  "truncated":true,
                  "raw_url":"https://attacker.example/source.cs",
                  "size":1000000
                }
              }
            }
            """));
        var client = new GitHubGistClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.test/") });

        var exception = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetAsync("abcde1", null, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.Single(handler.RequestUris);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class GistRecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];
        public List<AuthenticationHeaderValue?> Authorization { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestUris.Add(request.RequestUri!);
            Authorization.Add(request.Headers.Authorization);
            return Task.FromResult(responseFactory(request));
        }
    }
}
