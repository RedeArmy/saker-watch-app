using SakerWatch.BuildingBlocks.Observability.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace SakerWatch.Observability.Tests;

/// <summary>Captures log events and renders them, so tests assert on exactly what a real sink would see.</summary>
internal sealed class CollectingSink : ILogEventSink
{
    private readonly List<LogEvent> _events = [];

    public IReadOnlyList<LogEvent> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    public void Emit(LogEvent logEvent)
    {
        lock (_events)
        {
            _events.Add(logEvent);
        }
    }

    public static (ILogger Logger, CollectingSink Sink) Create(string service = "saker-test")
    {
        var sink = new CollectingSink();
        ILogger logger = new LoggerConfiguration().ApplySakerDefaults(service, writeToConsole: false).WriteTo.Sink(sink).CreateLogger();
        return (logger, sink);
    }

    public string RenderedText() => string.Join('\n', Events.Select(e => e.RenderMessage(System.Globalization.CultureInfo.InvariantCulture) + " " + string.Join(' ', e.Properties.Select(p => $"{p.Key}={p.Value}"))));
}
