using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SharpLabNext.Gateway;

namespace SharpLabNext.IntegrationTests;

public sealed class GitHubOAuthTests
{
    [Fact]
    public void OAuthIsDisabledWhenNoCredentialsAreConfigured()
    {
        var options = ConfigurationOptions(
            "Production",
            ("GitHub:OAuth:Enabled", "false"));

        Assert.False(options.Available);
        Assert.Null(options.ClientId);
        Assert.Null(options.ClientSecret);
        Assert.Null(options.CallbackUri);
    }

    [Fact]
    public void ProductionAcceptsEmptyDisabledSecretPlaceholder()
    {
        var placeholder = WriteSecretFile(string.Empty);
        try
        {
            var options = ConfigurationOptions(
                "Production",
                ("GitHub:OAuth:Enabled", "false"),
                ("GitHub:OAuth:ClientSecretFile", placeholder));

            Assert.False(options.Available);
        }
        finally
        {
            File.Delete(placeholder);
        }
    }

    [Theory]
    [InlineData("GitHub:OAuth:ClientId", "client-id")]
    [InlineData("GitHub:OAuth:CallbackUri", "https://lab.example/api/v1/auth/github/callback")]
    [InlineData("GitHub:OAuth:ClientSecret", "client-secret")]
    public void PartialOAuthConfigurationIsRejected(string key, string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ConfigurationOptions("Development", (key, value)));

        Assert.Contains("requires ClientId, CallbackUri", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionRejectsInlineSecretAndRequiresHttpsCallback()
    {
        Assert.Throws<InvalidOperationException>(() => ConfigurationOptions(
            "Production",
            ("GitHub:OAuth:Enabled", "true"),
            ("GitHub:OAuth:ClientId", "client-id"),
            ("GitHub:OAuth:CallbackUri", "https://lab.example/api/v1/auth/github/callback"),
            ("GitHub:OAuth:ClientSecret", "client-secret")));

        var secretFile = WriteSecretFile("client-secret");
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => ConfigurationOptions(
                "Production",
                ("GitHub:OAuth:Enabled", "true"),
                ("GitHub:OAuth:ClientId", "client-id"),
                ("GitHub:OAuth:CallbackUri", "http://localhost:8080/api/v1/auth/github/callback"),
                ("GitHub:OAuth:ClientSecretFile", secretFile)));
            Assert.Contains("HTTPS", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(secretFile);
        }
    }

    [Fact]
    public void ProductionLoadsCompleteOAuthConfigurationFromSecretFile()
    {
        var secretFile = WriteSecretFile("client-secret");
        try
        {
            var options = ConfigurationOptions(
                "Production",
                ("GitHub:OAuth:Enabled", "true"),
                ("GitHub:OAuth:ClientId", "client-id"),
                ("GitHub:OAuth:CallbackUri", "https://lab.example/api/v1/auth/github/callback"),
                ("GitHub:OAuth:ClientSecretFile", secretFile));

            Assert.True(options.Available);
            Assert.Equal("client-id", options.ClientId);
            Assert.Equal("client-secret", options.ClientSecret);
            Assert.Equal(
                new Uri("https://lab.example/api/v1/auth/github/callback"),
                options.CallbackUri);
        }
        finally
        {
            File.Delete(secretFile);
        }
    }

    [Fact]
    public void DevelopmentAllowsHttpCallbackOnlyForLoopback()
    {
        var loopback = ConfigurationOptions(
            "Development",
            ("GitHub:OAuth:Enabled", "true"),
            ("GitHub:OAuth:ClientId", "client-id"),
            ("GitHub:OAuth:CallbackUri", "http://localhost:8080/api/v1/auth/github/callback"),
            ("GitHub:OAuth:ClientSecret", "client-secret"));
        Assert.True(loopback.Available);

        Assert.Throws<InvalidOperationException>(() => ConfigurationOptions(
            "Development",
            ("GitHub:OAuth:Enabled", "true"),
            ("GitHub:OAuth:ClientId", "client-id"),
            ("GitHub:OAuth:CallbackUri", "http://lab.example/api/v1/auth/github/callback"),
            ("GitHub:OAuth:ClientSecret", "client-secret")));
    }

    [Fact]
    public void OAuthEndpointsDefaultToGitHubHttps()
    {
        var options = ConfigurationOptions(
            "Development",
            ("GitHub:OAuth:Enabled", "true"),
            ("GitHub:OAuth:ClientId", "client-id"),
            ("GitHub:OAuth:CallbackUri", "http://localhost:8080/api/v1/auth/github/callback"),
            ("GitHub:OAuth:ClientSecret", "client-secret"));

        Assert.Equal(new Uri("https://github.com/login/oauth/authorize"), options.AuthorizationEndpoint);
        Assert.Equal(new Uri("https://github.com/login/oauth/access_token"), options.TokenEndpoint);
    }

    [Theory]
    [InlineData("GitHub:OAuth:AuthorizationEndpoint", "http://localhost:8081/oauth/authorize")]
    [InlineData("GitHub:OAuth:TokenEndpoint", "http://127.0.0.1:8081/oauth/token")]
    public void DevelopmentAllowsLoopbackHttpOAuthEndpoints(string key, string value)
    {
        var options = ConfigurationOptions(
            "Development",
            ("GitHub:OAuth:Enabled", "true"),
            ("GitHub:OAuth:ClientId", "client-id"),
            ("GitHub:OAuth:CallbackUri", "http://localhost:8080/api/v1/auth/github/callback"),
            ("GitHub:OAuth:ClientSecret", "client-secret"),
            (key, value));

        var endpoint = key.EndsWith("AuthorizationEndpoint", StringComparison.Ordinal)
            ? options.AuthorizationEndpoint
            : options.TokenEndpoint;
        Assert.Equal(new Uri(value), endpoint);
    }

    [Theory]
    [InlineData("GitHub:OAuth:AuthorizationEndpoint", "http://github.example/oauth/authorize")]
    [InlineData("GitHub:OAuth:TokenEndpoint", "http://github.example/oauth/token")]
    public void DevelopmentRejectsRemoteHttpOAuthEndpoints(string key, string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => ConfigurationOptions(
            "Development",
            ("GitHub:OAuth:Enabled", "true"),
            ("GitHub:OAuth:ClientId", "client-id"),
            ("GitHub:OAuth:CallbackUri", "http://localhost:8080/api/v1/auth/github/callback"),
            ("GitHub:OAuth:ClientSecret", "client-secret"),
            (key, value)));

        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
        Assert.Contains("loopback", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("GitHub:OAuth:AuthorizationEndpoint", "http://localhost:8081/oauth/authorize")]
    [InlineData("GitHub:OAuth:TokenEndpoint", "http://localhost:8081/oauth/token")]
    public void ProductionRequiresHttpsOAuthEndpoints(string key, string value)
    {
        var secretFile = WriteSecretFile("client-secret");
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => ConfigurationOptions(
                "Production",
                ("GitHub:OAuth:Enabled", "true"),
                ("GitHub:OAuth:ClientId", "client-id"),
                ("GitHub:OAuth:CallbackUri", "https://lab.example/api/v1/auth/github/callback"),
                ("GitHub:OAuth:ClientSecretFile", secretFile),
                (key, value)));

            Assert.Contains(key, exception.Message, StringComparison.Ordinal);
            Assert.Contains("HTTPS", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(secretFile);
        }
    }

    [Theory]
    [InlineData("Production", "http://localhost:8080/", "HTTPS")]
    [InlineData("Development", "http://github.example/", "loopback")]
    public void GitHubApiBaseAddressRejectsInsecureTransport(
        string environmentName,
        string value,
        string expectedMessage)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => GitHubExternalEndpoint.Parse(
            value,
            "GitHub:ApiBaseAddress",
            new TestHostEnvironment(environmentName)));

        Assert.Contains("GitHub:ApiBaseAddress", exception.Message, StringComparison.Ordinal);
        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Development", "http://localhost:8080/")]
    [InlineData("Test", "http://127.0.0.1:8080/")]
    [InlineData("Production", "https://api.github.com/")]
    public void GitHubApiBaseAddressAcceptsHttpsOrNonProductionLoopback(
        string environmentName,
        string value)
    {
        var endpoint = GitHubExternalEndpoint.Parse(
            value,
            "GitHub:ApiBaseAddress",
            new TestHostEnvironment(environmentName));

        Assert.Equal(new Uri(value), endpoint);
    }

    [Fact]
    public void ExplicitDisableRejectsCredentialConfiguration()
    {
        Assert.Throws<InvalidOperationException>(() => ConfigurationOptions(
            "Development",
            ("GitHub:OAuth:Enabled", "false"),
            ("GitHub:OAuth:ClientId", "client-id"),
            ("GitHub:OAuth:CallbackUri", "https://lab.example/api/v1/auth/github/callback"),
            ("GitHub:OAuth:ClientSecret", "client-secret")));
    }

    [Fact]
    public async Task HttpsCallbackMakesStateAndSessionCookiesSecureBehindHttpTlsTerminator()
    {
        using var factory = new OAuthGatewayFactory(
            "https://lab.example/api/v1/auth/github/callback");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("http://gateway.internal"),
            HandleCookies = false
        });

        using var startResponse = await client.GetAsync(
            "/api/v1/auth/github/start",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);
        var stateCookie = CookieHeader(startResponse, "SharpLabNext.GitHubOAuthState");
        Assert.True(HasCookieAttribute(stateCookie, "Secure"));

        var state = CookieValue(stateCookie, "SharpLabNext.GitHubOAuthState");
        using var callbackRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/auth/github/callback?code=test-code&state={Uri.EscapeDataString(state)}");
        callbackRequest.Headers.Add("Cookie", $"SharpLabNext.GitHubOAuthState={state}");
        using var callbackResponse = await client.SendAsync(
            callbackRequest,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Found, callbackResponse.StatusCode);
        Assert.True(HasCookieAttribute(
            CookieHeader(callbackResponse, "SharpLabNext.GitHubOAuthState"),
            "Secure"));
        Assert.True(HasCookieAttribute(
            CookieHeader(callbackResponse, "SharpLabNext.GitHubSession"),
            "Secure"));
    }

    [Fact]
    public async Task LocalHttpCallbackDoesNotTrustForwardedProtoForCookieSecurity()
    {
        using var factory = new OAuthGatewayFactory(
            "http://localhost:8080/api/v1/auth/github/callback");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            HandleCookies = false
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/github/start");
        request.Headers.Add("X-Forwarded-Proto", "https");

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(HasCookieAttribute(
            CookieHeader(response, "SharpLabNext.GitHubOAuthState"),
            "Secure"));
    }

    [Fact]
    public async Task AuthorizationCodeFlowUsesStateRedirectAndNeverPlacesTokenInAuthorizationUrl()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"access_token":"server-secret-token","token_type":"bearer","scope":"gist"}""", Encoding.UTF8, "application/json")
        });
        var options = Options();
        var client = new GitHubOAuthClient(new HttpClient(handler), options);
        var callback = new Uri("https://lab.example/api/v1/auth/github/callback");

        var authorization = client.CreateAuthorizationUri("state-value", callback);
        var token = await client.ExchangeCodeAsync("code-value", callback, TestContext.Current.CancellationToken);

        Assert.Contains("client_id=client-id", authorization.Query, StringComparison.Ordinal);
        Assert.Contains("state=state-value", authorization.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("server-secret-token", authorization.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("server-secret-token", token);
        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("client_secret=client-secret", handler.LastRequestBody, StringComparison.Ordinal);
        Assert.Contains("code=code-value", handler.LastRequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public void PendingStateIsCookieBoundOneTimeAndSessionCsrfIsIndependent()
    {
        var store = new GitHubOAuthSessionStore(Options());
        var now = DateTimeOffset.UtcNow;
        var pending = store.CreatePending("/#gist:abcde", now);

        Assert.False(store.TryTakePending(pending.State, "wrong", now, out _));
        Assert.True(store.TryTakePending(pending.State, pending.State, now, out var accepted));
        Assert.Equal("/#gist:abcde", accepted!.ReturnPath);
        Assert.False(store.TryTakePending(pending.State, pending.State, now, out _));

        var session = store.CreateSession("secret-token", "owner", now);
        Assert.True(store.TryGetSession(session.SessionId, now, out var restored));
        Assert.Equal("secret-token", restored!.AccessToken);
        Assert.True(store.ValidateCsrf(restored, restored.CsrfToken));
        Assert.False(store.ValidateCsrf(restored, pending.State));
    }

    private static GitHubOAuthOptions Options() => new(
        "client-id",
        "client-secret",
        new Uri("https://github.com/login/oauth/authorize"),
        new Uri("https://github.com/login/oauth/access_token"),
        new Uri("https://lab.example/api/v1/auth/github/callback"),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromHours(8));

    private static GitHubOAuthOptions ConfigurationOptions(
        string environmentName,
        params (string Key, string? Value)[] values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(static value => value.Key, static value => value.Value))
            .Build();
        return GitHubOAuthOptions.FromConfiguration(
            configuration,
            new TestHostEnvironment(environmentName));
    }

    private static string WriteSecretFile(string secret)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, secret + Environment.NewLine);
        return path;
    }

    private static string CookieHeader(HttpResponseMessage response, string cookieName) =>
        Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            header => header.StartsWith($"{cookieName}=", StringComparison.Ordinal));

    private static string CookieValue(string header, string cookieName)
    {
        var end = header.IndexOf(';');
        var pair = end < 0 ? header : header[..end];
        return pair[(cookieName.Length + 1)..];
    }

    private static bool HasCookieAttribute(string header, string attribute) =>
        header.Split(';').Skip(1).Any(value =>
            string.Equals(value.Trim(), attribute, StringComparison.OrdinalIgnoreCase));

    private sealed class OAuthGatewayFactory(string callbackUri) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("GitHub:OAuth:Enabled", "true");
            builder.UseSetting("GitHub:OAuth:ClientId", "client-id");
            builder.UseSetting("GitHub:OAuth:ClientSecret", "client-secret");
            builder.UseSetting("GitHub:OAuth:CallbackUri", callbackUri);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<GitHubOAuthClient>();
                services.AddHttpClient<GitHubOAuthClient>()
                    .ConfigurePrimaryHttpMessageHandler(static () => new RecordingHandler(_ =>
                        new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(
                                """{"access_token":"token-owner","token_type":"bearer","scope":"gist"}""",
                                Encoding.UTF8,
                                "application/json")
                        }));
                services.RemoveAll<IGitHubGistClient>();
                services.AddSingleton<IGitHubGistClient>(new FakeGitHubGistClient());
            });
        }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responseFactory(request);
        }
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "SharpLabNext.IntegrationTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
