using SakerWatch.BuildingBlocks.Kernel.Security;
using Serilog.Core;
using Serilog.Events;

namespace SakerWatch.BuildingBlocks.Observability.Logging;

/// <summary>
/// Passes events to the sink it wraps with the exception's text scrubbed (ADR-0016). The redaction enricher can only change properties, and the
/// exception of an event is where a driver quotes a connection string, a key-value detail with an email or a provider's request text. The
/// wrapped sinks see a <see cref="ScrubbedException"/> with the same message, stack trace and inner exceptions in text form, minus what
/// <see cref="SensitiveText"/> removes.
/// </summary>
/// <param name="inner">The sink that receives the events.</param>
public sealed class ScrubbingSink(ILogEventSink inner) : ILogEventSink
{
    /// <inheritdoc />
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        if (logEvent.Exception is null)
        {
            inner.Emit(logEvent);
            return;
        }

        inner.Emit(new LogEvent(
            logEvent.Timestamp,
            logEvent.Level,
            new ScrubbedException(logEvent.Exception),
            logEvent.MessageTemplate,
            [.. logEvent.Properties.Select(property => new LogEventProperty(property.Key, property.Value))],
            logEvent.TraceId ?? default,
            logEvent.SpanId ?? default));
    }
}

/// <summary>An exception reduced to scrubbed text: the type name of the original, its message and its full text (stack trace and inner exceptions) without credentials or emails.</summary>
public sealed class ScrubbedException : Exception
{
    private readonly string _text;

    /// <summary>Scrubs <paramref name="original"/>.</summary>
    public ScrubbedException(Exception original)
        : base(SensitiveText.Scrub(original?.Message ?? string.Empty, includeEmails: true))
    {
        ArgumentNullException.ThrowIfNull(original);
        _text = SensitiveText.Scrub(original.ToString(), includeEmails: true);
        OriginalType = original.GetType().FullName ?? original.GetType().Name;
    }

    /// <summary>The full name of the type of the exception that was scrubbed.</summary>
    public string OriginalType { get; }

    /// <inheritdoc />
    public override string? StackTrace => null;

    /// <inheritdoc />
    public override string ToString() => _text;
}
