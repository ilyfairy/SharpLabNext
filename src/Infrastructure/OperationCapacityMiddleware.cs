using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SharpLabNext.Contracts;
using System.Text.Json;

namespace SharpLabNext.Operations.Http;

public static class OperationCapacityMiddlewareExtensions
{
    public static IApplicationBuilder UseSharpLabNextOperationCapacityHandling(this IApplicationBuilder app) =>
        app.UseMiddleware<OperationCapacityMiddleware>();
}

public sealed class OperationCapacityMiddleware(RequestDelegate next)
{
    private static readonly JsonSerializerOptions JsonOptions = ContractJson.CreateSerializerOptions();

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (OperationCapacityExceededException exception)
        {
            if (context.Response.HasStarted)
            {
                throw;
            }

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.ContentType = "application/problem+json";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Retry-After"] = "1";
            await JsonSerializer.SerializeAsync(context.Response.Body, new OperationCapacityProblem(
                "https://sharplabnext.dev/problems/operation-capacity-exhausted",
                "Operation capacity is exhausted",
                StatusCodes.Status429TooManyRequests,
                "The service is retaining the maximum number of operations. Retry shortly.",
                exception.MaximumOperations,
                context.TraceIdentifier),
                JsonOptions,
                context.RequestAborted).ConfigureAwait(false);
        }
    }
}

public sealed record OperationCapacityProblem(string Type, string Title, int Status, string Detail, int MaximumOperations, string TraceId);
