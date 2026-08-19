using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SharpLabNext.Contracts;
using SharpLabNext.Http;
using SharpLabNext.InternalServices;

namespace SharpLabNext.WorkerHost;

public static class WorkerHostExtensions
{
    public static IServiceCollection AddSharpLabNextWorker(
        this IServiceCollection services,
        ServiceIdentity descriptor)
    {
        if (descriptor.Kind is not (ServiceKind.ToolchainWorker or ServiceKind.ArtifactWorker))
        {
            throw new ArgumentException("Worker descriptor must use a worker service kind.", nameof(descriptor));
        }

        services.ConfigureHttpJsonOptions(options =>
        {
            ContractJson.ApplySerializerOptions(options.SerializerOptions);
        });
        services.AddSharpLabNextProblemDetails();
        services.AddSingleton(descriptor);
        return services;
    }

    public static WebApplication MapSharpLabNextWorkerEndpoints(this WebApplication app)
    {
        app.UseSharpLabNextInternalServiceAuthentication(
            InternalServiceAuthenticationOptions.FromConfiguration(app.Configuration, app.Environment));
        app.MapGet("/health/live", () => Results.Ok(new { Status = "live" }));
        app.MapGet("/health/ready", (ServiceIdentity descriptor) =>
            Results.Ok(new
            {
                Status = "ready",
                descriptor.Id,
                Kind = descriptor.Kind.ToString(),
                descriptor.ReleaseId,
                Protocol = descriptor.Protocol.ToString()
            }));
        app.MapGet("/api/v1/worker/describe", (ServiceIdentity descriptor) => descriptor);
        return app;
    }
}
