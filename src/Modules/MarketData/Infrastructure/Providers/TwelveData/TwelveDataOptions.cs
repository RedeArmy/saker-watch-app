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

        return Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("TwelveData:BaseUrl must be an absolute https address.");
    }
}
