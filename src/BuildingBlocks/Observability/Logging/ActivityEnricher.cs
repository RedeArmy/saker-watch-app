using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace SakerWatch.BuildingBlocks.Observability.Logging;

/// <summary>Adds <c>TraceId</c> and <c>SpanId</c> of the current <see cref="Activity"/> so logs and traces join on the trace id (ADR-0016).</summary>
public sealed class ActivityEnricher : ILogEventEnricher
{
    /// <inheritdoc />
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        Activity? activity = Activity.Current;
        if (activity is null)
        {
            return;
        }

        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("TraceId", activity.TraceId.ToString()));
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("SpanId", activity.SpanId.ToString()));
    }
}
