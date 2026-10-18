using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace SakerWatch.MarketData.Infrastructure.Providers.Banguat;

/// <summary>Settings of the Banco de Guatemala exchange rate service, bound from <c>Banguat</c>. The service is public: there is no key.</summary>
public sealed class BanguatOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Banguat";

    /// <summary>The name stored with each rate.</summary>
    public const string ProviderName = "banguat";

    /// <summary>Address of the SOAP endpoint. Its host is the only one the client may call.</summary>
    [Required]
    [Url]
    public string BaseUrl { get; set; } = "https://www.banguat.gob.gt/variables/ws/TipoCambio.asmx";
}

/// <summary>Requires the endpoint to be an absolute https address.</summary>
public sealed class BanguatOptionsValidator : IValidateOptions<BanguatOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, BanguatOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Banguat:BaseUrl must be an absolute https address.");
    }
}
