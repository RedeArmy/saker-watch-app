using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Resilience;
using SakerWatch.MarketData.Application;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Infrastructure.Providers.TwelveData;

namespace SakerWatch.MarketData.Infrastructure;

/// <summary>Registration of the MarketData module for the hosts that fetch market data (the worker, and the API for a symbol check).</summary>
public static class MarketDataServiceCollectionExtensions
{
    /// <summary>
    /// Registers the routing table (<c>MarketData:Providers</c>), the router and the Twelve Data provider behind its allow-listed client.
    /// A market is served only if the table routes it to a registered provider.
    /// </summary>
    public static IServiceCollection AddMarketData(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(TwelveDataProvider)))
        {
            return services;
        }

        services.AddOptions<MarketDataProvidersOptions>().Bind(configuration.GetSection(MarketDataProvidersOptions.SectionName));
        services.AddMarketDataApplication();

        services.AddOptions<TwelveDataOptions>()
            .Bind(configuration.GetSection(TwelveDataOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TwelveDataOptions>, TwelveDataOptionsValidator>());

        // The base address is known only at startup: read it now to allow-list exactly its host and port.
        var settings = new TwelveDataOptions();
        configuration.GetSection(TwelveDataOptions.SectionName).Bind(settings);
        Uri baseAddress = Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out Uri? uri) ? uri : new Uri(new TwelveDataOptions().BaseUrl);
        services.AddSakerHttpClient(
            TwelveDataProvider.ClientName,
            baseAddress,
            http =>
            {
                http.AllowedHosts = [baseAddress.IsDefaultPort ? baseAddress.Host : $"{baseAddress.Host}:{baseAddress.Port}"];
                http.MaxResponseBytes = 4 * 1024 * 1024;
                http.MaxRetries = 2;
            });

        services.AddSingleton<TwelveDataProvider>();
        services.AddSingleton<IMarketDataProvider>(provider => provider.GetRequiredService<TwelveDataProvider>());
        return services;
    }
}
