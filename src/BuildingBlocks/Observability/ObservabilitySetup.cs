using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SakerWatch.BuildingBlocks.Observability.Logging;
using Serilog;

namespace SakerWatch.BuildingBlocks.Observability;

/// <summary>One-call registration of logging, tracing and metrics for a host (ADR-0016).</summary>
public static class ObservabilitySetup
{
    /// <summary>Name of the activity source and meter that carry Saker Watch's own telemetry.</summary>
    public const string TelemetryName = "SakerWatch";

    private const string _otlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>
    /// Registers Serilog (JSON, redacted, trace-correlated) and OpenTelemetry for traces and metrics.
    /// Telemetry leaves the process only when the standard <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is configured, so local
    /// runs and tests export nothing.
    /// </summary>
    /// <param name="builder">The host builder (web or worker).</param>
    /// <param name="serviceName">Logical service name, also the OpenTelemetry <c>service.name</c>.</param>
    /// <param name="configureTracing">Host-specific instrumentation, for example ASP.NET Core.</param>
    /// <param name="configureMetrics">Host-specific instrumentation, for example ASP.NET Core.</param>
    public static TBuilder AddSakerObservability<TBuilder>(
        this TBuilder builder,
        string serviceName,
        Action<TracerProviderBuilder>? configureTracing = null,
        Action<MeterProviderBuilder>? configureMetrics = null)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        string? otlpEndpoint = builder.Configuration[_otlpEndpointKey];
        builder.Services.AddSerilog(logger => logger.ApplySakerDefaults(serviceName, otlpEndpoint: otlpEndpoint));

        IOpenTelemetryBuilder telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName, serviceVersion: typeof(ObservabilitySetup).Assembly.GetName().Version?.ToString())
                .AddAttributes([new KeyValuePair<string, object>("deployment.environment.name", builder.Environment.EnvironmentName)]))
            .WithTracing(tracing =>
            {
                tracing.AddSource(TelemetryName).AddHttpClientInstrumentation();
                configureTracing?.Invoke(tracing);
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(TelemetryName).AddHttpClientInstrumentation().AddRuntimeInstrumentation();
                configureMetrics?.Invoke(metrics);
            });

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            telemetry.UseOtlpExporter();
        }

        return builder;
    }
}
