using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace SharpLabNext.Observability;

public static class SharpLabNextObservabilityExtensions
{
    public static IHostApplicationBuilder AddSharpLabNextObservability(
        this IHostApplicationBuilder builder,
        string serviceId,
        string releaseId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ValidateIdentity(serviceId, nameof(serviceId));
        ValidateIdentity(releaseId, nameof(releaseId));
        if (builder.Services.Any(static descriptor =>
                descriptor.ServiceType == typeof(SharpLabNextObservabilityRegistration)))
        {
            throw new InvalidOperationException("SharpLabNext observability was already registered for this host.");
        }

        var section = builder.Configuration.GetSection(SharpLabNextObservabilityOptions.SectionName);
        builder.Services.AddOptions<SharpLabNextObservabilityOptions>()
            .Bind(section)
            .ValidateOnStart();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<SharpLabNextObservabilityOptions>,
                SharpLabNextObservabilityOptionsValidator>());

        var configuredEnvironment = section[nameof(SharpLabNextObservabilityOptions.DeploymentEnvironment)];
        var deploymentEnvironment = string.IsNullOrWhiteSpace(configuredEnvironment)
            ? builder.Environment.EnvironmentName
            : configuredEnvironment.Trim();
        _ = SharpLabNextObservabilityOptionsValidator.TryParseOtlpEndpoint(
            section[nameof(SharpLabNextObservabilityOptions.OtlpEndpoint)],
            out var otlpEndpoint);
        var registration = new SharpLabNextObservabilityRegistration(
            serviceId,
            releaseId,
            deploymentEnvironment,
            otlpEndpoint);
        builder.Services.AddSingleton(registration);
        builder.Services.TryAddSingleton(SharpLabNextTelemetry.Metrics);

        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
            options.UseUtcTimestamp = true;
        });
        builder.Logging.Configure(options =>
            options.ActivityTrackingOptions =
                ActivityTrackingOptions.TraceId |
                ActivityTrackingOptions.SpanId |
                ActivityTrackingOptions.ParentId);
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
            logging.ParseStateValues = true;
            logging.SetResourceBuilder(ConfigureResource(ResourceBuilder.CreateDefault(), registration));
            if (registration.OtlpEndpoint is not null)
                logging.AddOtlpExporter(options => options.Endpoint = registration.OtlpEndpoint);
        });

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => ConfigureResource(resource, registration));
        telemetry.WithTracing(tracing =>
        {
            tracing.AddSource(SharpLabNextTelemetry.ActivitySourceName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation();
            if (registration.OtlpEndpoint is not null)
            {
                tracing.AddOtlpExporter(options => options.Endpoint = registration.OtlpEndpoint);
            }
        });
        telemetry.WithMetrics(metrics =>
        {
            metrics.AddMeter(SharpLabNextTelemetry.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation();
            if (registration.OtlpEndpoint is not null)
            {
                metrics.AddOtlpExporter(options => options.Endpoint = registration.OtlpEndpoint);
            }
        });

        return builder;
    }

    internal static ResourceBuilder ConfigureResource(
        ResourceBuilder resource,
        SharpLabNextObservabilityRegistration registration) =>
        resource.AddService(
                registration.ServiceId,
                serviceVersion: registration.ReleaseId,
                autoGenerateServiceInstanceId: false)
            .AddAttributes(
            [
                new KeyValuePair<string, object>(
                    "deployment.environment",
                    registration.DeploymentEnvironment)
            ]);

    internal static bool IsStableIdentity(string? value) =>
        value is { Length: > 0 and <= 128 } &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');

    private static void ValidateIdentity(string value, string parameterName)
    {
        if (!IsStableIdentity(value))
        {
            throw new ArgumentException(
                "Observability identities must be stable label values of at most 128 characters.",
                parameterName);
        }
    }
}

internal sealed record SharpLabNextObservabilityRegistration(
    string ServiceId,
    string ReleaseId,
    string DeploymentEnvironment,
    Uri? OtlpEndpoint);
