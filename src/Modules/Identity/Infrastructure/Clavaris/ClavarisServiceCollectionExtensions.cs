using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Resilience;
using SakerWatch.Identity.Contracts;

namespace SakerWatch.Identity.Infrastructure.Clavaris;

/// <summary>Registration of the Clavaris token validation.</summary>
public static class ClavarisServiceCollectionExtensions
{
    // Placeholder when the configured Authority is not a URL: the options validation fails the host at startup anyway.
    private const string _unusableHost = "authority-not-configured.invalid";

    /// <summary>
    /// Registers the validated <c>Clavaris</c> options (the host fails at startup when they are wrong), the metadata HTTP client
    /// (allow-listed to the Authority's host, HTTPS unless explicitly relaxed for a local fake) and <see cref="IAuthProvider"/>.
    /// </summary>
    public static IServiceCollection AddClavarisAuth(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ClavarisOptions>()
            .Bind(configuration.GetSection(ClavarisOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ClavarisOptions>, ClavarisOptionsValidator>();

        // The Authority is known only at startup, so read it now to allow-list exactly its host.
        var settings = new ClavarisOptions();
        configuration.GetSection(ClavarisOptions.SectionName).Bind(settings);
        Uri.TryCreate(settings.Authority, UriKind.Absolute, out Uri? authority);
        services.AddSakerHttpClient(
            ClavarisAuthProvider.MetadataClientName,
            authority ?? new UriBuilder(Uri.UriSchemeHttps, _unusableHost).Uri,
            http =>
            {
                http.AllowedHosts = [authority?.Host ?? _unusableHost];
                http.AllowInsecureHttp = !settings.RequireHttpsMetadata;
                http.MaxResponseBytes = 256 * 1024;
            });
        services.AddSingleton<IAuthProvider, ClavarisAuthProvider>();
        return services;
    }
}
