using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace SakerWatch.BuildingBlocks.Observability.Logging;

/// <summary>The one logger configuration every host uses (structured JSON, enriched, redacted).</summary>
public static class SakerLogging
{
    /// <summary>
    /// Applies the standard configuration: minimum level Information with quieter framework namespaces,
    /// <c>Service</c>, trace/span ids and log-context properties on every event, redaction last, and JSON lines
    /// to the console for the log collector. Enrichers registered after this call run after redaction and are not
    /// covered, so add any extra enricher before calling it.
    /// </summary>
    /// <param name="configuration">Logger configuration to extend.</param>
    /// <param name="serviceName">Logical service name (for example <c>saker-api</c>).</param>
    /// <param name="writeToConsole">False in tests that attach their own sink.</param>
    public static LoggerConfiguration ApplySakerDefaults(this LoggerConfiguration configuration, string serviceName, bool writeToConsole = true)
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

        return writeToConsole ? configured.WriteTo.Console(new RenderedCompactJsonFormatter()) : configured;
    }
}
