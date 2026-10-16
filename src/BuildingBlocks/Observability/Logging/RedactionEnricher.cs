using SakerWatch.BuildingBlocks.Kernel.Security;
using Serilog.Core;
using Serilog.Events;

namespace SakerWatch.BuildingBlocks.Observability.Logging;

/// <summary>
/// Last line of defence against leaking secrets or personal data into logs (ADR-0016): masks the value of
/// properties whose name marks them sensitive (at any nesting depth) and scrubs JWTs and <c>Bearer</c>/<c>Basic</c>
/// credentials from any string value. It must be the last enricher so it also covers values added by others.
/// </summary>
public sealed class RedactionEnricher : ILogEventEnricher
{
    /// <summary>Replacement text for redacted values.</summary>
    public const string Mask = SensitiveText.Mask;

    private const int _maxNameLength = 128;

    private static readonly string[] _sensitiveSuffixes =
        ["password", "passwd", "secret", "token", "authorization", "cookie", "apikey", "connectionstring", "pepper"];

    private static readonly HashSet<string> _sensitiveNames = new(StringComparer.Ordinal)
    {
        "email", "emailaddress", "phone", "phonenumber", "displayname", "prompt", "rawresponse", "requestbody", "responsebody",
    };

    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _sensitiveNameLookup = _sensitiveNames.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <inheritdoc />
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        // Most events carry nothing to change: look first, and only then (rarely) collect the replacements and apply them after the loop.
        List<LogEventProperty>? replacements = null;
        foreach (KeyValuePair<string, LogEventPropertyValue> property in logEvent.Properties)
        {
            LogEventPropertyValue redacted = Redact(property.Key, property.Value);
            if (!ReferenceEquals(redacted, property.Value))
            {
                (replacements ??= []).Add(new LogEventProperty(property.Key, redacted));
            }
        }

        if (replacements is not null)
        {
            foreach (LogEventProperty replacement in replacements)
            {
                logEvent.AddOrUpdateProperty(replacement);
            }
        }
    }

    /// <summary>True when a property of this name must never be logged with its value.</summary>
    public static bool IsSensitiveName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        // Letters and digits only, lower case, compared on a stack buffer: this runs for every property of every log event.
        Span<char> normalized = name.Length <= _maxNameLength ? stackalloc char[_maxNameLength] : new char[name.Length];
        int length = 0;
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                normalized[length++] = char.ToLowerInvariant(c);
            }
        }

        ReadOnlySpan<char> text = normalized[..length];
        if (_sensitiveNameLookup.Contains(text))
        {
            return true;
        }

        foreach (string suffix in _sensitiveSuffixes)
        {
            if (text.EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static LogEventPropertyValue Redact(string name, LogEventPropertyValue value) =>
        IsSensitiveName(name) ? new ScalarValue(Mask) : RedactValue(value);

    // Containers are rebuilt only when something inside changed, so an event with nothing to hide allocates nothing here.
    private static LogEventPropertyValue RedactValue(LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue { Value: string text }:
                string scrubbed = SensitiveText.Scrub(text);
                return ReferenceEquals(scrubbed, text) ? value : new ScalarValue(scrubbed);

            case StructureValue structure:
                return RedactStructure(structure);

            case SequenceValue sequence:
                return RedactSequence(sequence);

            case DictionaryValue dictionary:
                return RedactDictionary(dictionary);

            default:
                return value;
        }
    }

    private static StructureValue RedactStructure(StructureValue structure)
    {
        LogEventProperty[]? members = null;
        for (int i = 0; i < structure.Properties.Count; i++)
        {
            LogEventProperty member = structure.Properties[i];
            LogEventPropertyValue redacted = Redact(member.Name, member.Value);
            if (members is null && ReferenceEquals(redacted, member.Value))
            {
                continue;
            }

            members ??= [.. structure.Properties];
            members[i] = new LogEventProperty(member.Name, redacted);
        }

        return members is null ? structure : new StructureValue(members, structure.TypeTag);
    }

    private static SequenceValue RedactSequence(SequenceValue sequence)
    {
        LogEventPropertyValue[]? elements = null;
        for (int i = 0; i < sequence.Elements.Count; i++)
        {
            LogEventPropertyValue redacted = RedactValue(sequence.Elements[i]);
            if (elements is null && ReferenceEquals(redacted, sequence.Elements[i]))
            {
                continue;
            }

            elements ??= [.. sequence.Elements];
            elements[i] = redacted;
        }

        return elements is null ? sequence : new SequenceValue(elements);
    }

    private static DictionaryValue RedactDictionary(DictionaryValue dictionary)
    {
        List<KeyValuePair<ScalarValue, LogEventPropertyValue>>? entries = null;
        int index = 0;
        foreach (KeyValuePair<ScalarValue, LogEventPropertyValue> pair in dictionary.Elements)
        {
            LogEventPropertyValue redacted = pair.Key.Value is string key ? Redact(key, pair.Value) : RedactValue(pair.Value);
            if (entries is null && !ReferenceEquals(redacted, pair.Value))
            {
                entries = [.. dictionary.Elements.Take(index)];
            }

            entries?.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(pair.Key, redacted));
            index++;
        }

        return entries is null ? dictionary : new DictionaryValue(entries);
    }
}
