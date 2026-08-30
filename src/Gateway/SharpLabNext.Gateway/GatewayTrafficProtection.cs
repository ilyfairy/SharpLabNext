using System.Globalization;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using SharpLabNext.Contracts;

namespace SharpLabNext.Gateway;

public static class GatewayTrafficProtectionExtensions
{
    public static IServiceCollection AddSharpLabNextGatewayTrafficProtection(this IServiceCollection services, GatewayTrafficOptions traffic)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(traffic);
        traffic.Validate();

        services.AddSingleton(traffic);
        services.Configure<KestrelServerOptions>(options => options.Limits.MaxRequestBodySize = traffic.MaximumRequestBodyBytes);
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = CreateLimiter(traffic);
            options.OnRejected = GatewayTrafficProblemWriter.WriteRateLimitAsync;
        });
        return services;
    }

    public static IApplicationBuilder UseSharpLabNextGatewayTrafficProtection(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseMiddleware<GatewayRequestBodyLimitMiddleware>();
        app.UseRateLimiter();
        return app;
    }

    internal static PartitionedRateLimiter<HttpContext> CreateLimiter(GatewayTrafficOptions traffic)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        traffic.Validate();

        var publicClientLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => IsPublicRequest(context.Request) ? RateLimitPartition.GetFixedWindowLimiter($"public:{ClientKey(context)}", _ => FixedWindow(traffic.PublicPermitLimit, traffic.PublicWindow)) : RateLimitPartition.GetNoLimiter("public:bypass"));
        var publicGlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => IsPublicRequest(context.Request) ? RateLimitPartition.GetFixedWindowLimiter("public:global", _ => FixedWindow(traffic.PublicGlobalPermitLimit, traffic.PublicWindow)) : RateLimitPartition.GetNoLimiter("public-global:bypass"));
        var runtimeGlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => IsRuntimeSubmission(context.Request) ? RateLimitPartition.GetFixedWindowLimiter("runtime:global", _ => FixedWindow(traffic.RuntimeGlobalPermitLimit, traffic.RuntimeWindow)) : RateLimitPartition.GetNoLimiter("runtime-global:bypass"));
        var runtimeClientLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => IsRuntimeSubmission(context.Request) ? RateLimitPartition.GetFixedWindowLimiter($"runtime-client:{ClientKey(context)}", _ => FixedWindow(traffic.RuntimeClientPermitLimit, traffic.RuntimeWindow)) : RateLimitPartition.GetNoLimiter("runtime-client:bypass"));

        return PartitionedRateLimiter.CreateChained(runtimeClientLimiter, runtimeGlobalLimiter, publicClientLimiter, publicGlobalLimiter);
    }

    internal static bool IsRuntimeSubmission(HttpRequest request) =>
        HttpMethods.IsPost(request.Method) &&
        (request.Path.Equals("/api/v1/runs", StringComparison.Ordinal) || request.Path.Equals("/api/v1/jit", StringComparison.Ordinal));

    private static bool IsPublicRequest(HttpRequest request) =>
        request.Path.StartsWithSegments("/api", StringComparison.Ordinal) ||
        request.Path.StartsWithSegments("/ws", StringComparison.Ordinal);

    private static string ClientKey(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null)
            return "unknown";
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return address.ToString();
    }

    private static FixedWindowRateLimiterOptions FixedWindow(int permitLimit, TimeSpan window) => new()
    {
        PermitLimit = permitLimit,
        Window = window,
        QueueLimit = 0,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        AutoReplenishment = true
    };
}

public sealed class GatewayRequestBodyLimitMiddleware(RequestDelegate next, GatewayTrafficOptions traffic)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.ContentLength > traffic.MaximumRequestBodyBytes)
        {
            return GatewayTrafficProblemWriter.WriteAsync(context.Response, StatusCodes.Status413PayloadTooLarge, "request-body-too-large", "Request body too large", $"The request body exceeds the {traffic.MaximumRequestBodyBytes.ToString(CultureInfo.InvariantCulture)} byte limit.", context.RequestAborted);
        }

        return next(context);
    }
}

internal static class GatewayTrafficProblemWriter
{
    private static readonly JsonSerializerOptions JsonOptions = ContractJson.CreateSerializerOptions();

    public static ValueTask WriteRateLimitAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var value)
            ? value : TimeSpan.FromSeconds(1);
        context.HttpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        var scope = GatewayTrafficProtectionExtensions.IsRuntimeSubmission(context.HttpContext.Request)
            ? "runtime-submission" : "public-api";
        return new ValueTask(WriteAsync(
            context.HttpContext.Response,
            StatusCodes.Status429TooManyRequests,
            "request-rate-limit-exceeded",
            "Request rate limit exceeded",
            "The request rate is above the configured service limit. Retry later.",
            cancellationToken,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Retryable"] = true,
                ["Scope"] = scope
            }));
    }

    public static async Task WriteAsync(HttpResponse response, int statusCode, string code, string title, string detail, CancellationToken cancellationToken, IReadOnlyDictionary<string, object?>? extensions = null)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/problem+json";
        response.Headers.CacheControl = "no-store";
        var problem = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            // Dictionary keys bypass JsonNamingPolicy, so use the public
            // contract spelling explicitly for problem details.
            ["Type"] = $"https://sharplabnext.dev/problems/{code}",
            ["Title"] = title,
            ["Status"] = statusCode,
            ["Detail"] = detail,
            ["Code"] = code
        };
        if (extensions is not null)
        {
            foreach (var pair in extensions)
                problem[pair.Key] = pair.Value;
        }

        await JsonSerializer.SerializeAsync(response.Body, problem, JsonOptions, cancellationToken);
    }
}
