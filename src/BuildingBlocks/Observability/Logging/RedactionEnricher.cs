using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace SakerWatch.BuildingBlocks.Observability.Logging;

/// <summary>
/// Last line of defence against leaking secrets or personal data into logs (ADR-0016): masks the value of
/// properties whose name marks them sensitive (at any nesting depth) and scrubs JWTs and <c>Bearer</c>/<c>Basic</c>
/// credentials from any string value. It must be the last enricher so it also covers values added by others.
/// </summary>
public sealed partial class RedactionEnricher : ILogEventEnricher
{
    /// <summary>Replacement text for redacted values.</summary>
    public const string Mask = "***";

    private static readonly string[] _sensitiveSuffixes =
        ["password", "passwd", "secret", "token", "authorization", "cookie", "apikey", "connectionstring", "pepper"];

    private static readonly HashSet<string> _sensitiveNames = new(StringComparer.Ordinal)
    {
        "email", "emailaddress", "phone", "phonenumber", "displayname", "prompt", "rawresponse", "requestbody", "responsebody",
    };

    /// <inheritdoc />
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        foreach (KeyValuePair<string, LogEventPropertyValue> property in logEvent.Properties.ToArray())
        {
            LogEventPropertyValue redacted = Redact(property.Key, property.Value);
            if (!ReferenceEquals(redacted, property.Value))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(property.Key, redacted));
            }
        }
    }

    /// <summary>True when a property of this name must never be logged with its value.</summary>
    public static bool IsSensitiveName(string name)
    {
        string normalized = new([.. name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
        return _sensitiveNames.Contains(normalized) || _sensitiveSuffixes.Any(suffix => normalized.EndsWith(suffix, StringComparison.Ordinal));
    }

    private static LogEventPropertyValue Redact(string name, LogEventPropertyValue value) =>
        IsSensitiveName(name) ? new ScalarValue(Mask) : RedactValue(value);

    private static LogEventPropertyValue RedactValue(LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue { Value: string text }:
                string scrubbed = ScrubString(text);
                return ReferenceEquals(scrubbed, text) ? value : new ScalarValue(scrubbed);

            case StructureValue structure:
                LogEventProperty[] members = [.. structure.Properties.Select(p => new LogEventProperty(p.Name, Redact(p.Name, p.Value)))];
                return new StructureValue(members, structure.TypeTag);

            case SequenceValue sequence:
                return new SequenceValue(sequence.Elements.Select(RedactValue));

            case DictionaryValue dictionary:
                return new DictionaryValue(dictionary.Elements.Select(pair =>
                    new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                        pair.Key,
                        pair.Key.Value is string key ? Redact(key, pair.Value) : RedactValue(pair.Value))));

            default:
                return value;
        }
    }

    private static string ScrubString(string text)
    {
        if (text.Length < 8)
        {
            return text;
        }

        string result = JwtPattern().Replace(text, Mask);
        result = AuthSchemePattern().Replace(result, "$1 " + Mask);
        return result == text ? text : result;
    }

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*")]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]{6,}", RegexOptions.IgnoreCase)]
    private static partial Regex AuthSchemePattern();
}
