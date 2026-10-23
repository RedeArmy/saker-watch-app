using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using SakerWatch.MarketData.Application.Providers;

namespace SakerWatch.MarketData.Infrastructure.Providers.TwelveData;

/// <summary>
/// Twelve Data connection settings, bound from <c>TwelveData</c>. The API key comes from the environment, .NET user secrets or a Docker
/// secret, never from a file in the repository, and it is required only when a market is routed to this provider.
/// </summary>
public sealed class TwelveDataOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "TwelveData";

    /// <summary>The name the routing table uses for this provider.</summary>
    public const string ProviderName = "twelvedata";

    /// <summary>Base address of the API. Its host is the only one the client may call.</summary>
    [Required]
    [Url]
    public string BaseUrl { get; set; } = "https://api.twelvedata.com/";

    /// <summary>The API key, sent in the <c>Authorization</c> header and never in a URL.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Credits per minute the plan allows. The free Basic plan allows 8; set a lower number to keep a margin. A history load asks for its two credits
    /// together (ADR-0049), so at least 2 is required and an odd number leaves one credit of every minute unused.
    /// </summary>
    [Range(2, 100000)]
    public int CreditsPerMinute { get; set; } = 8;

    /// <summary>Credits per UTC day the plan allows. The free Basic plan allows 800; paid plans have no daily cap, so set a large number. At least 2: a history costs two credits.</summary>
    [Range(2, 100000000)]
    public int CreditsPerDay { get; set; } = 800;

    /// <summary>
    /// Credits per UTC day that loading the history of new stocks may spend (ADR-0043), out of <see cref="CreditsPerDay"/>; the rest is kept for the
    /// daily refresh. A history costs two credits, so 200 are 100 new stocks a day. 0 closes the lane; 1 could never pay a history, so it is refused.
    /// </summary>
    [Range(0, 100000000)]
    public int OnDemandCreditsPerDay { get; set; } = 200;
}

/// <summary>Requires the API key when at least one market is routed to Twelve Data, so a host without a key still starts when it does not use it.</summary>
/// <param name="routing">The market routing table.</param>
public sealed class TwelveDataOptionsValidator(IOptions<MarketDataProvidersOptions> routing) : IValidateOptions<TwelveDataOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, TwelveDataOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        bool routed = routing.Value.Markets.Values.Any(provider => string.Equals(provider.Trim(), TwelveDataOptions.ProviderName, StringComparison.OrdinalIgnoreCase));
        if (routed && string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return ValidateOptionsResult.Fail("TwelveData:ApiKey is required because a market is routed to twelvedata (set it as an environment variable, a user secret or a Docker secret).");
        }

        if (options.OnDemandCreditsPerDay == 1)
        {
            return ValidateOptionsResult.Fail("TwelveData:OnDemandCreditsPerDay must be 0 (lane closed) or at least 2: a history load reserves its two credits together and one credit can never pay it.");
        }

        if (options.OnDemandCreditsPerDay > options.CreditsPerDay)
        {
            return ValidateOptionsResult.Fail("TwelveData:OnDemandCreditsPerDay is a share of TwelveData:CreditsPerDay and cannot be larger.");
        }

        return Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("TwelveData:BaseUrl must be an absolute https address.");
    }
}
