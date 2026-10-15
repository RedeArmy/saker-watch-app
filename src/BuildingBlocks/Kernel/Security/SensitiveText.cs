using System.Text.RegularExpressions;

namespace SakerWatch.BuildingBlocks.Kernel.Security;

/// <summary>
/// Removes credentials (and optionally email addresses) from free text before it is logged or stored. It is the one place that knows
/// what a secret looks like inside a string: the log redaction and the error text kept on failed jobs and outbox messages both use it.
/// Exception messages are the usual way a secret ends up in text, for example a connection string quoted by a driver.
/// </summary>
public static partial class SensitiveText
{
    /// <summary>Replacement for removed values.</summary>
    public const string Mask = "***";

    /// <summary>
    /// Returns <paramref name="text"/> with JWTs, <c>Bearer</c>/<c>Basic</c> credentials and <c>name=value</c> pairs for secret-looking names
    /// (<c>password</c>, <c>pwd</c>, <c>secret</c>, <c>token</c>, <c>apikey</c>...) masked; with <paramref name="includeEmails"/> also email
    /// addresses. The same instance comes back when nothing matched.
    /// </summary>
    public static string Scrub(string text, bool includeEmails = false)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length < 8)
        {
            return text;
        }

        try
        {
            return ScrubCore(text, includeEmails);
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathological input: hiding the whole text is safer than passing along something that could not be checked.
            return Mask;
        }
    }

    private static string ScrubCore(string text, bool includeEmails)
    {
        string result = text;
        if (result.Contains("eyJ", StringComparison.Ordinal))
        {
            result = JwtPattern().Replace(result, Mask);
        }

        if (result.Contains("Bearer", StringComparison.OrdinalIgnoreCase) || result.Contains("Basic", StringComparison.OrdinalIgnoreCase))
        {
            result = AuthSchemePattern().Replace(result, "$1 " + Mask);
        }

        if (result.Contains('=', StringComparison.Ordinal) || result.Contains(':', StringComparison.Ordinal))
        {
            result = SecretPairPattern().Replace(result, "$1" + Mask);
        }

        if (includeEmails && result.Contains('@', StringComparison.Ordinal))
        {
            result = EmailPattern().Replace(result, Mask);
        }

        return result == text ? text : result;
    }

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]{6,}", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AuthSchemePattern();

    // name=value or name: value for names that end in a secret word; the value runs to the next separator or quote.
    [GeneratedRegex(@"(\b[A-Za-z0-9_.-]*(?:password|passwd|pwd|secret|token|apikey|api_key|authorization|connectionstring)[""']?\s*[=:]\s*)(?:""[^""]*""|'[^']*'|[^;,\s""']+)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SecretPairPattern();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EmailPattern();
}
