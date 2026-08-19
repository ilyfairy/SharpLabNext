using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using SharpLabNext.Http;

namespace SharpLabNext.UnitTests;

public sealed class PascalCaseProblemDetailsTests
{
    [Fact]
    public async Task ProblemDetailsServiceWritesPascalCaseStandardMembers()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSharpLabNextProblemDetails();
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = provider
        };
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.Body = new MemoryStream();

        var service = provider.GetRequiredService<IProblemDetailsService>();
        var written = await service.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Type = "https://sharplabnext.dev/problems/invalid-argument",
                Title = "invalid-argument",
                Status = StatusCodes.Status400BadRequest,
                Detail = "The request is invalid.",
                Instance = "/api/v1/build",
                Extensions = { ["TraceId"] = "trace-1" }
            }
        });

        Assert.True(written);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(
            context.Response.Body,
            cancellationToken: TestContext.Current.CancellationToken);
        var root = document.RootElement;
        Assert.Equal("invalid-argument", root.GetProperty("Title").GetString());
        Assert.Equal(400, root.GetProperty("Status").GetInt32());
        Assert.Equal("trace-1", root.GetProperty("TraceId").GetString());
        Assert.False(root.TryGetProperty("title", out _));
        Assert.False(root.TryGetProperty("status", out _));
    }

    [Fact]
    public async Task ResultsProblemUsesThePascalCaseWriter()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSharpLabNextProblemDetails();
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = provider
        };
        context.Response.Body = new MemoryStream();

        var result = Results.Problem(
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "invalid-input",
            detail: "The input is invalid.",
            extensions: new Dictionary<string, object?> { ["TraceId"] = "trace-2" });
        await result.ExecuteAsync(context);

        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(
            context.Response.Body,
            cancellationToken: TestContext.Current.CancellationToken);
        var root = document.RootElement;
        Assert.Equal(422, root.GetProperty("Status").GetInt32());
        Assert.Equal("invalid-input", root.GetProperty("Title").GetString());
        Assert.Equal("trace-2", root.GetProperty("TraceId").GetString());
        Assert.False(root.TryGetProperty("status", out _));
        Assert.False(root.TryGetProperty("title", out _));
        Assert.All(root.EnumerateObject(), property =>
            Assert.True(
                property.Name.Length == 0 || char.IsUpper(property.Name[0]),
                $"ProblemDetails member '{property.Name}' is not PascalCase."));
    }
}
