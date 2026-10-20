using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace SakerWatch.MarketData.Infrastructure.Providers.Edgar;

/// <summary>
/// Settings of the SEC EDGAR adapter, bound from <c>Edgar</c> (ADR-0040). It is off until the owner turns it on, because the SEC requires every
/// automated caller to declare who it is: a <c>User-Agent</c> with a contact. The service has no key.
/// </summary>
public sealed class EdgarOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Edgar";

    /// <summary>The name stored with each response and used in the metrics.</summary>
    public const string ProviderName = "edgar";

    /// <summary>Whether the adapter is registered at all. Off by default: nothing calls the SEC until the owner says who is calling.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Who is calling, as the SEC's fair access policy asks: a name and a contact address (for example <c>SakerWatch you@example.com</c>). Required
    /// when the adapter is on. It is sent as the <c>User-Agent</c> of every request and never logged.
    /// </summary>
    public string? UserAgent { get; set; }

    /// <summary>Address of the host that serves the submissions of a filer. Its host is one of the two the client may call.</summary>
    [Required]
    [Url]
    public string DataBaseUrl { get; set; } = "https://data.sec.gov";

    /// <summary>Address of the host that serves the list of tickers and the filed documents. Its host is the other the client may call.</summary>
    [Required]
    [Url]
    public string WwwBaseUrl { get; set; } = "https://www.sec.gov";

    /// <summary>
    /// The least time between two requests, in milliseconds. The SEC allows ten a second; 120 is eight, which leaves a margin for a clock that
    /// is not exact and for a second process.
    /// </summary>
    [Range(0, 5_000)]
    public int MinRequestIntervalMilliseconds { get; set; } = 120;

    /// <summary>How many hours the map from ticker to key is kept before it is read again.</summary>
    [Range(1, 168)]
    public int TickerMapHours { get; set; } = 24;
}

/// <summary>Requires https addresses and, when the adapter is on, a contact in the declared identity.</summary>
public sealed partial class EdgarOptionsValidator : IValidateOptions<EdgarOptions>
{
    /// <summary>Longest accepted identity.</summary>
    public const int MaxUserAgentLength = 200;

    // Something that looks like an address: the SEC refuses a caller it cannot write to, and a header must not carry control characters.
    [GeneratedRegex(@"^[\x20-\x7E]{1,200}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Printable();

    [GeneratedRegex(@"\S+@\S+\.\S+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Contact();

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, EdgarOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        foreach ((string key, string value) in new[] { ("DataBaseUrl", options.DataBaseUrl), ("WwwBaseUrl", options.WwwBaseUrl) })
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                failures.Add($"Edgar:{key} must be an absolute https address.");
            }
        }

        if (options.Enabled && (options.UserAgent is not { } agent || !Printable().IsMatch(agent) || !Contact().IsMatch(agent)))
        {
            failures.Add("Edgar:UserAgent must declare who is calling with a contact address (for example 'SakerWatch you@example.com'), in printable characters, up to 200.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
