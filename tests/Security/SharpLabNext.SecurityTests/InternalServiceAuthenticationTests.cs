using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SharpLabNext.InternalServices;

namespace SharpLabNext.SecurityTests;

public sealed class InternalServiceAuthenticationTests
{
    private const string Token = "shared-internal-service-token-for-security-tests";

    [Theory]
    [InlineData("/api/v1/worker/describe")]
    [InlineData("/internal/v1/artifacts/sha256/abc")]
    [InlineData("/internal/v1/jobs/run")]
    [InlineData("/internal/v1/capabilities/preflight")]
    public async Task ProtectedRequestsRejectMissingOrWrongTokenAndAcceptExpectedToken(string path)
    {
        var options = CreateOptions("Testing", ("InternalServiceAuth:Required", "true"), ("InternalServiceAuth:Token", Token));
        var nextCalled = false;
        var middleware = new InternalServiceAuthenticationMiddleware(
            context =>
            {
                nextCalled = true;
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            options);

        var missing = CreateContext(path);
        await middleware.InvokeAsync(missing);
        Assert.Equal(HttpStatusCode.Unauthorized, (HttpStatusCode)missing.Response.StatusCode);
        Assert.False(nextCalled);
        Assert.Equal("Bearer", missing.Response.Headers.WWWAuthenticate);

        var wrong = CreateContext(path, new string('x', Token.Length));
        await middleware.InvokeAsync(wrong);
        Assert.Equal(HttpStatusCode.Unauthorized, (HttpStatusCode)wrong.Response.StatusCode);
        Assert.False(nextCalled);

        var authorized = CreateContext(path, Token);
        await middleware.InvokeAsync(authorized);
        Assert.Equal(HttpStatusCode.NoContent, (HttpStatusCode)authorized.Response.StatusCode);
        Assert.True(nextCalled);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthRequestsRemainAnonymous(string path)
    {
        var options = CreateOptions("Testing", ("InternalServiceAuth:Required", "true"), ("InternalServiceAuth:Token", Token));
        var nextCalled = false;
        var middleware = new InternalServiceAuthenticationMiddleware(
            context =>
            {
                nextCalled = true;
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            options);

        var context = CreateContext(path);
        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(HttpStatusCode.OK, (HttpStatusCode)context.Response.StatusCode);
    }

    [Fact]
    public void ProductionRequiresASecretFileAndRejectsInlineTokens()
    {
        Assert.Throws<InvalidOperationException>(() => CreateOptions("Production"));
        Assert.Throws<InvalidOperationException>(() => CreateOptions("Production", ("InternalServiceAuth:Token", Token)));

        var tokenFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tokenFile, Token + Environment.NewLine);
            var options = CreateOptions("Production", ("InternalServiceAuth:TokenFile", tokenFile));
            using var client = new HttpClient();

            options.ConfigureClient(client);

            Assert.True(options.Required);
            Assert.True(options.Enabled);
            Assert.Equal(new AuthenticationHeaderValue("Bearer", Token), client.DefaultRequestHeaders.Authorization);
        }
        finally
        {
            File.Delete(tokenFile);
        }
    }

    private static DefaultHttpContext CreateContext(string path, string? token = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        if (token is not null)
            context.Request.Headers.Authorization = $"Bearer {token}";
        return context;
    }

    private static InternalServiceAuthenticationOptions CreateOptions(string environmentName, params (string Key, string? Value)[] values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(static value => value.Key, static value => value.Value)).Build();
        return InternalServiceAuthenticationOptions.FromConfiguration(configuration, new TestHostEnvironment(environmentName));
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "SharpLabNext.SecurityTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
