using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using SharpLabNext.Observability;

namespace SharpLabNext.Observability.Tests;

public sealed class ObservabilityTests
{
    [Fact]
    public async Task DefaultConfigurationRegistersProvidersWithoutOtlpExporter()
    {
        var builder = CreateBuilder("Staging");
        builder.AddSharpLabNextObservability("gateway", "release-1");
        Assert.Contains(builder.Services, static descriptor =>
            descriptor.ServiceType == typeof(ConsoleFormatter) &&
            descriptor.ImplementationType?.FullName ==
                "Microsoft.Extensions.Logging.Console.JsonConsoleFormatter");
        using var host = builder.Build();
        Assert.Contains(
            host.Services.GetServices<ILoggerProvider>(),
            static provider => provider is OpenTelemetryLoggerProvider);

        await host.StartAsync(TestContext.Current.CancellationToken);

        var registration = host.Services.GetRequiredService<SharpLabNextObservabilityRegistration>();
        Assert.Null(registration.OtlpEndpoint);
        Assert.Equal("Staging", registration.DeploymentEnvironment);
        Assert.Same(
            SharpLabNextTelemetry.Metrics,
            host.Services.GetRequiredService<SharpLabNextMetrics>());
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExplicitEndpointEnablesOtlpAndResourceUsesUnifiedIdentity()
    {
        var builder = CreateBuilder("Production");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Observability:OtlpEndpoint"] = "https://collector.example:4317",
            ["Observability:DeploymentEnvironment"] = "private-prod"
        });
        builder.AddSharpLabNextObservability("runtime-supervisor", "2026.07.12");
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);

        var registration = host.Services.GetRequiredService<SharpLabNextObservabilityRegistration>();
        Assert.Equal(new Uri("https://collector.example:4317"), registration.OtlpEndpoint);
        var resource = SharpLabNextObservabilityExtensions
            .ConfigureResource(ResourceBuilder.CreateEmpty(), registration)
            .Build();
        var attributes = resource.Attributes.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        Assert.Equal("runtime-supervisor", attributes["service.name"]);
        Assert.Equal("2026.07.12", attributes["service.version"]);
        Assert.Equal("private-prod", attributes["deployment.environment"]);
        Assert.DoesNotContain("service.instance.id", attributes.Keys);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("")]
    [InlineData("collector:4317")]
    [InlineData("ftp://collector.example")]
    [InlineData("https://user:secret@collector.example")]
    [InlineData("https://collector.example?token=secret")]
    public async Task InvalidExplicitEndpointFailsOnHostStart(string endpoint)
    {
        var builder = CreateBuilder("Test");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Observability:OtlpEndpoint"] = endpoint
        });
        builder.AddSharpLabNextObservability("gateway", "release-1");
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Observability:OtlpEndpoint", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidExplicitDeploymentEnvironmentFailsOnHostStart()
    {
        var builder = CreateBuilder("Test");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Observability:DeploymentEnvironment"] = "private prod"
        });
        builder.AddSharpLabNextObservability("gateway", "release-1");
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Observability:DeploymentEnvironment", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WebApplicationBuilderSupportsTheTwoIdentityArgumentExtension()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Test"
        });

        builder.AddSharpLabNextObservability("gateway", "release-1");

        Assert.Contains(builder.Services, static descriptor =>
            descriptor.ServiceType == typeof(SharpLabNextObservabilityRegistration));
    }

    [Fact]
    public void RegistrationRejectsUnstableServiceIdentityAndDuplicates()
    {
        var builder = CreateBuilder("Test");
        Assert.Throws<ArgumentException>(() =>
            builder.AddSharpLabNextObservability(null!, "release-1"));
        Assert.Throws<ArgumentException>(() =>
            builder.AddSharpLabNextObservability("gateway with spaces", "release-1"));

        builder.AddSharpLabNextObservability("gateway", "release-1");
        Assert.Throws<InvalidOperationException>(() =>
            builder.AddSharpLabNextObservability("gateway", "release-1"));
    }

    [Fact]
    public void ActivitySourceAndMetricHelpersEmitOwnedTelemetry()
    {
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == SharpLabNextTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(activityListener);
        using var activity = SharpLabNextTelemetry.ActivitySource.StartActivity("test.build");
        Assert.NotNull(activity);

        var measurements = new List<MeasurementRecord>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == SharpLabNextTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add(new MeasurementRecord(instrument.Name, value, Tags(tags))));
        meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add(new MeasurementRecord(instrument.Name, value, Tags(tags))));
        meterListener.Start();

        var metrics = SharpLabNextTelemetry.Metrics;
        metrics.RecordQueueDepth("gateway-build", 3);
        metrics.RecordQueueWait(
            "gateway-build",
            TimeSpan.FromMilliseconds(250),
            SharpLabNextTelemetryOutcome.Succeeded);
        metrics.RecordQueueRejection("gateway-build");
        metrics.SessionStarted("csharp", "roslyn-stable");
        metrics.SessionEnded("csharp", "roslyn-stable", SharpLabNextTelemetryOutcome.Cancelled);
        metrics.RecordBuild(
            "csharp",
            "roslyn-stable",
            TimeSpan.FromSeconds(1.5),
            SharpLabNextTelemetryOutcome.Succeeded,
            cacheHit: true);
        metrics.RecordRuntime(
            SharpLabNextRuntimeOperation.Jit,
            "dotnet-10-linux-x64",
            TimeSpan.FromSeconds(2),
            SharpLabNextTelemetryOutcome.TimedOut);
        metrics.RecordContainerPhase(
            SharpLabNextContainerPhase.Create,
            "dotnet-10-linux-x64",
            TimeSpan.FromMilliseconds(400),
            SharpLabNextTelemetryOutcome.Succeeded);
        metrics.RecordReaperPass("default", TimeSpan.FromSeconds(3), removedContainers: 2, failures: 1);

        Assert.Contains(measurements, static item =>
            item.Name == "sharplabnext.queue.depth" && item.Value == 3);
        Assert.Contains(measurements, static item =>
            item.Name == "sharplabnext.build.duration" &&
            item.Value == 1.5 &&
            item.Tags["sharplabnext.cache.hit"] is true);
        Assert.Contains(measurements, static item =>
            item.Name == "sharplabnext.session.active" && item.Value == 1);
        Assert.Contains(measurements, static item =>
            item.Name == "sharplabnext.runtime.duration" &&
            item.Tags["sharplabnext.operation.type"]?.ToString() == "jit" &&
            item.Tags["sharplabnext.outcome"]?.ToString() == "timed-out");
        Assert.Contains(measurements, static item =>
            item.Name == "sharplabnext.runtime.container.duration" &&
            item.Value == 0.4 &&
            item.Tags["sharplabnext.container.phase"]?.ToString() == "create");
        Assert.Contains(measurements, static item =>
            item.Name == "sharplabnext.reaper.failures" && item.Value == 1);
    }

    [Fact]
    public void MetricHelpersRejectHighCardinalityOrInvalidValues()
    {
        var metrics = SharpLabNextTelemetry.Metrics;
        Assert.Throws<ArgumentException>(() => metrics.RecordQueueDepth("queue with spaces", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => metrics.RecordQueueDepth("queue", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => metrics.RecordBuild(
            "csharp",
            "roslyn-stable",
            TimeSpan.FromSeconds(-1),
            SharpLabNextTelemetryOutcome.Failed,
            cacheHit: false));
    }

    private static HostApplicationBuilder CreateBuilder(string environmentName) =>
        Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = environmentName,
            DisableDefaults = true
        });

    private static Dictionary<string, object?> Tags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            result[tag.Key] = tag.Value;
        }
        return result;
    }

    private sealed record MeasurementRecord(
        string Name,
        double Value,
        IReadOnlyDictionary<string, object?> Tags);
}
