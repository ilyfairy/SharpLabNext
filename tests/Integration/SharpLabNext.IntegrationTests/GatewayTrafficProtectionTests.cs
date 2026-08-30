using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using SharpLabNext.Gateway;

namespace SharpLabNext.IntegrationTests;

public sealed class GatewayTrafficProtectionTests
{
    [Fact]
    public async Task GatewayPipelineRejectsExcessApiRequestsButNotHealthChecks()
    {
        await using var factory = new TrafficGatewayFactory();
        using var client = factory.CreateClient();

        using var first = await client.GetAsync("/api/v1/system", TestContext.Current.CancellationToken);
        using var second = await client.GetAsync("/api/v1/system", TestContext.Current.CancellationToken);
        using var rejected = await client.GetAsync("/api/v1/system", TestContext.Current.CancellationToken);
        using var health = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var problem = JsonDocument.Parse(await rejected.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal("request-rate-limit-exceeded", problem.RootElement.GetProperty("Code").GetString());
    }

    [Fact]
    public async Task GatewayPipelineRejectsOversizedKnownRequestBody()
    {
        await using var factory = new TrafficGatewayFactory();
        using var client = factory.CreateClient();
        using var content = new ByteArrayContent(new byte[1024 * 1024 + 1]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await client.PostAsync("/api/v1/selections/resolve", content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal("request-body-too-large", problem.RootElement.GetProperty("Code").GetString());
    }

    [Fact]
    public async Task RuntimeSubmissionsUseGlobalAndPerClientLimits()
    {
        var options = ValidOptions();
        options.PublicGlobalPermitLimit = 20;
        options.PublicPermitLimit = 10;
        options.RuntimeGlobalPermitLimit = 3;
        options.RuntimeClientPermitLimit = 2;
        await using var limiter = GatewayTrafficProtectionExtensions.CreateLimiter(options);
        var clientOne = Context("/api/v1/runs", "192.0.2.10");
        var clientTwo = Context("/api/v1/jit", "192.0.2.11");

        using var first = await limiter.AcquireAsync(clientOne, cancellationToken: TestContext.Current.CancellationToken);
        using var second = await limiter.AcquireAsync(clientOne, cancellationToken: TestContext.Current.CancellationToken);
        using var perClientRejected = await limiter.AcquireAsync(clientOne, cancellationToken: TestContext.Current.CancellationToken);
        using var thirdGlobal = await limiter.AcquireAsync(clientTwo, cancellationToken: TestContext.Current.CancellationToken);
        using var globalRejected = await limiter.AcquireAsync(clientTwo, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(first.IsAcquired);
        Assert.True(second.IsAcquired);
        Assert.False(perClientRejected.IsAcquired);
        Assert.True(thirdGlobal.IsAcquired);
        Assert.False(globalRejected.IsAcquired);
    }

    [Fact]
    public async Task HealthAndStaticRequestsBypassPublicLimits()
    {
        var options = ValidOptions();
        options.PublicGlobalPermitLimit = 10;
        options.PublicPermitLimit = 1;
        await using var limiter = GatewayTrafficProtectionExtensions.CreateLimiter(options);

        for (var index = 0; index < 5; index++)
        {
            using var health = await limiter.AcquireAsync(Context("/health/ready", "192.0.2.20", HttpMethods.Get), cancellationToken: TestContext.Current.CancellationToken);
            using var staticAsset = await limiter.AcquireAsync(Context("/assets/app.js", "192.0.2.20", HttpMethods.Get), cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(health.IsAcquired);
            Assert.True(staticAsset.IsAcquired);
        }
    }

    [Fact]
    public async Task OversizedKnownBodyReturnsProblemDetailsBeforeEndpointExecution()
    {
        var options = ValidOptions();
        var nextCalled = false;
        var middleware = new GatewayRequestBodyLimitMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            options);
        var context = new DefaultHttpContext();
        context.Request.ContentLength = options.MaximumRequestBodyBytes + 1;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        Assert.StartsWith("application/problem+json", context.Response.ContentType, StringComparison.Ordinal);
        context.Response.Body.Position = 0;
        using var problem = await JsonDocument.ParseAsync(context.Response.Body, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("request-body-too-large", problem.RootElement.GetProperty("Code").GetString());
    }

    [Fact]
    public void PerClientRuntimeLimitCannotExceedGlobalLimit()
    {
        var options = ValidOptions();
        options.RuntimeClientPermitLimit = options.RuntimeGlobalPermitLimit + 1;

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains(nameof(options.RuntimeClientPermitLimit), exception.Message, StringComparison.Ordinal);
    }

    private static GatewayTrafficOptions ValidOptions() => new()
    {
        MaximumRequestBodyBytes = 4 * 1024 * 1024,
        PublicGlobalPermitLimit = 30_000,
        PublicPermitLimit = 6_000,
        PublicWindow = TimeSpan.FromMinutes(1),
        RuntimeGlobalPermitLimit = 600,
        RuntimeClientPermitLimit = 120,
        RuntimeWindow = TimeSpan.FromMinutes(1)
    };

    private static DefaultHttpContext Context(string path, string address, string method = "POST")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        return context;
    }

    private sealed class TrafficGatewayFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("GatewayTraffic:MaximumRequestBodyBytes", "1048576");
            builder.UseSetting("GatewayTraffic:PublicGlobalPermitLimit", "100");
            builder.UseSetting("GatewayTraffic:PublicPermitLimit", "2");
            builder.UseSetting("GatewayTraffic:PublicWindow", "01:00:00");
        }
    }
}
