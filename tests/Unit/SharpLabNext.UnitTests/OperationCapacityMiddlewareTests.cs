using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SharpLabNext.Operations;
using SharpLabNext.Operations.Http;

namespace SharpLabNext.UnitTests;

public sealed class OperationCapacityMiddlewareTests
{
    [Fact]
    public async Task CapacityExhaustionReturnsRetryableProblemDetails()
    {
        var middleware = new OperationCapacityMiddleware(_ => Task.FromException(new OperationCapacityExceededException(17)));
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        Assert.Equal("1", context.Response.Headers["Retry-After"]);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.StartsWith("application/problem+json", context.Response.ContentType, StringComparison.Ordinal);
        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(17, body.RootElement.GetProperty("MaximumOperations").GetInt32());
        Assert.Equal(429, body.RootElement.GetProperty("Status").GetInt32());
        Assert.Equal("https://sharplabnext.dev/problems/operation-capacity-exhausted", body.RootElement.GetProperty("Type").GetString());
    }
}
