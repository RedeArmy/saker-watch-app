using SakerWatch.BuildingBlocks.Kernel.Security;

namespace SakerWatch.BuildingBlocks.Persistence;

/// <summary>
/// The text kept in <c>last_error</c> of a failed job or outbox message: the exception type and a short message, never the stack. The
/// message is scrubbed of credentials and email addresses first (a driver may quote a connection string or a value), flattened to one
/// line and cut to a fixed length, because this column is read by operators and shows up in tickets and dashboards.
/// </summary>
internal static class ErrorText
{
    public const int MaxLength = 500;

    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string text = $"{exception.GetType().Name}: {exception.Message}";
        text = SensitiveText.Scrub(text, includeEmails: true);
        text = string.Concat(text.Select(c => char.IsControl(c) ? ' ' : c));
        return text.Length <= MaxLength ? text : text[..MaxLength];
    }
}
