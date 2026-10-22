using Serilog;
using Serilog.Configuration;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Sinks.OpenTelemetry;

namespace SakerWatch.BuildingBlocks.Observability.Logging;

/// <summary>The one logger configuration every host uses (structured JSON, enriched, redacted).</summary>
public static class SakerLogging
{
    /// <summary>Events the console queue holds before it starts dropping the newest ones.</summary>
    public const int ConsoleQueueLength = 10_000;

    /// <summary>
    /// Applies the standard configuration: minimum level Information with quieter framework namespaces,
    /// <c>Service</c>, trace/span ids and log-context properties on every event, redaction last, and JSON lines
    /// to the console for the log collector (through a bounded asynchronous queue). Enrichers registered after this call run after redaction and are not
    /// covered, so add any extra enricher before calling it.
    /// </summary>
    /// <param name="configuration">Logger configuration to extend.</param>
    /// <param name="serviceName">Logical service name (for example <c>saker-api</c>).</param>
    /// <param name="writeToConsole">False in tests that attach their own sink.</param>
    /// <param name="otlpEndpoint">When set, logs are also exported over OTLP/gRPC (with trace context) to the Collector.</param>
    /// <param name="additionalSinks">Other sinks of the host. They are added behind the scrubbing sink like the console and OTLP ones: a sink added
    /// with <c>WriteTo</c> after this call would receive the exception of an event unscrubbed (use <see cref="WriteToScrubbed"/> there).</param>
    public static LoggerConfiguration ApplySakerDefaults(
        this LoggerConfiguration configuration,
        string serviceName,
        bool writeToConsole = true,
        string? otlpEndpoint = null,
        Action<LoggerSinkConfiguration>? additionalSinks = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        LoggerConfiguration configured = configuration
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Service", serviceName)
            .Enrich.With<ActivityEnricher>()
            .Enrich.With<RedactionEnricher>();

        // Every sink sits behind the scrubbing sink: the exception of an event is text the enricher cannot reach.
        return configured.WriteToScrubbed(
            sinks =>
            {
                if (writeToConsole)
                {
                    // The console is written on a background thread through a bounded queue: a slow or blocked stdout (a stalled log collector, a
                    // full pipe) must never hold up a request thread. When the queue is full the newest events are dropped rather than blocking.
                    sinks.Async(
                        sink => sink.Console(new RenderedCompactJsonFormatter()),
                        bufferSize: ConsoleQueueLength,
                        blockWhenFull: false);
                }

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    sinks.OpenTelemetry(options =>
                    {
                        options.Endpoint = otlpEndpoint;
                        options.Protocol = OtlpProtocol.Grpc;
                        options.ResourceAttributes = new Dictionary<string, object> { ["service.name"] = serviceName };
                    });
                }

                additionalSinks?.Invoke(sinks);
            });
    }

    /// <summary>
    /// Adds sinks behind the scrubbing sink: they receive the exception of every event with credentials and emails removed (ADR-0016). Use it
    /// instead of <c>WriteTo</c> for any sink added after <see cref="ApplySakerDefaults"/>; an architecture test refuses <c>WriteTo</c> elsewhere.
    /// </summary>
    /// <param name="configuration">Logger configuration to extend.</param>
    /// <param name="configureSinks">Adds the sinks to wrap.</param>
    public static LoggerConfiguration WriteToScrubbed(this LoggerConfiguration configuration, Action<LoggerSinkConfiguration> configureSinks)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configureSinks);

        return configuration.WriteTo.Sink(LoggerSinkConfiguration.Wrap(inner => new ScrubbingSink(inner), configureSinks));
    }
}
