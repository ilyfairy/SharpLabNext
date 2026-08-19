using Microsoft.Extensions.DependencyInjection;

namespace SharpLabNext.ArtifactWorker.Sdk;

public static class ArtifactWorkerServiceCollectionExtensions
{
    public static IServiceCollection AddArtifactTransformHandler<THandler>(this IServiceCollection services)
        where THandler : class, IArtifactTransformHandler
    {
        services.AddSingleton<IArtifactTransformHandler, THandler>();
        return services;
    }

    public static IServiceCollection AddArtifactRenderHandler<THandler>(this IServiceCollection services)
        where THandler : class, IArtifactRenderHandler
    {
        services.AddSingleton<IArtifactRenderHandler, THandler>();
        return services;
    }

    public static IServiceCollection AddArtifactVerificationHandler<THandler>(this IServiceCollection services)
        where THandler : class, IArtifactVerificationHandler
    {
        services.AddSingleton<IArtifactVerificationHandler, THandler>();
        return services;
    }

    public static IServiceCollection AddArtifactWorkerReadinessCheck<TCheck>(this IServiceCollection services)
        where TCheck : class, IArtifactWorkerReadinessCheck
    {
        services.AddSingleton<IArtifactWorkerReadinessCheck, TCheck>();
        return services;
    }
}
