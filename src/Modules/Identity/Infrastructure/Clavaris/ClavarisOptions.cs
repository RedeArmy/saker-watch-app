using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Security;

namespace SakerWatch.Identity.Infrastructure.Clavaris;

/// <summary>
/// Everything Clavaris-specific, from configuration (<c>Clavaris:*</c>), validated at startup so a wrong value fails fast
/// (docs/clavaris-contract.md). Endpoints and signing keys are discovered from the Authority, never hard-coded.
/// </summary>
public sealed class ClavarisOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Clavaris";

    /// <summary>The longest clock skew accepted, as required by the security rules.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromSeconds(60);

    /// <summary>The Organization issuer URL, for example <c>https://clavaris.example/o/{organizationId}</c>. Tokens must carry exactly this <c>iss</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Authority { get; set; } = string.Empty;

    /// <summary>The audience (<c>aud</c>) tokens must carry for this API. Required: a resource server must not accept tokens meant for others.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Audience { get; set; } = string.Empty;

    /// <summary>Tolerated difference between clocks, at most 60 seconds.</summary>
    public TimeSpan ClockSkew { get; set; } = MaxClockSkew;

    /// <summary>Shortest time between forced refreshes of the signing keys (limits refresh storms from unknown <c>kid</c> values).</summary>
    [Range(typeof(TimeSpan), "00:00:00.001", "01:00:00")]
    public TimeSpan MinimumMetadataRefresh { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long discovered metadata is trusted before it is refreshed anyway.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan MetadataLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How long the last known signing keys keep being used when the provider cannot be reached. Beyond it tokens are refused
    /// (fail closed). It also bounds how long a key the provider withdrew would still be trusted during an outage.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "7.00:00:00")]
    public TimeSpan MaximumKeyAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How long a key refresh waits for the provider while usable keys are already cached. A slow or unreachable provider then
    /// delays one request per refresh interval by at most this long; the others carry on with the cached keys.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:00:30")]
    public TimeSpan StaleRefreshTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Require HTTPS for the Authority and its discovery documents. Turn off only for a local fake provider.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;
}

/// <summary>Cross-field validation of <see cref="ClavarisOptions"/>.</summary>
internal sealed class ClavarisOptionsValidator : IValidateOptions<ClavarisOptions>
{
    public ValidateOptionsResult Validate(string? name, ClavarisOptions options)
    {
        var failures = new List<string>();

        if (!Uri.TryCreate(options.Authority, UriKind.Absolute, out Uri? authority))
        {
            failures.Add("Clavaris:Authority must be an absolute URL (the Organization issuer, https://<host>/o/<organizationId>).");
        }
        else
        {
            if (authority.Scheme != Uri.UriSchemeHttps && !(authority.Scheme == Uri.UriSchemeHttp && !options.RequireHttpsMetadata))
            {
                failures.Add("Clavaris:Authority must use https (http is allowed only when Clavaris:RequireHttpsMetadata is false).");
            }

            if (options.Authority.TrimEnd('/').Length > ExternalIdentityLimits.MaxIssuerLength)
            {
                failures.Add($"Clavaris:Authority must be at most {ExternalIdentityLimits.MaxIssuerLength} characters (it is stored as the issuer of every user).");
            }

            if (!string.IsNullOrEmpty(authority.Query) || !string.IsNullOrEmpty(authority.Fragment))
            {
                failures.Add("Clavaris:Authority must not contain a query string or fragment.");
            }
        }

        if (options.ClockSkew < TimeSpan.Zero || options.ClockSkew > ClavarisOptions.MaxClockSkew)
        {
            failures.Add("Clavaris:ClockSkew must be between 0 and 60 seconds.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
