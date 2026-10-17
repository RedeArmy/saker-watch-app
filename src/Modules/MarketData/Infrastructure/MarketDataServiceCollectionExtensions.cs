using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Resilience;
using SakerWatch.MarketData.Application;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Prices;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Infrastructure.Persistence;
using SakerWatch.MarketData.Infrastructure.Providers;
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
        services.AddOptions<IngestionOptions>().Bind(configuration.GetSection(IngestionOptions.SectionName));
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
                // The shared Redis budget tracks the quota; a 429 is a quota answer and not a sign that the service is unwell.
                http.RateLimitIsTransient = false;
            });

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IProviderCallBudget, RedisProviderCallBudget>();
        if (configuration.GetValue("MarketData:Payloads:Enabled", true))
        {
            services.TryAddSingleton<IProviderPayloadStore, MongoProviderPayloadStore>();
        }
        else
        {
            services.TryAddSingleton<IProviderPayloadStore, NoProviderPayloadStore>();
        }

        services.TryAddScoped<IPriceStore, PriceStore>();
        services.TryAddScoped<IStockCatalog, StockCatalog>();
        services.TryAddScoped<ISymbolAliases, SymbolAliases>();
        services.TryAddScoped<IIngestionState, IngestionState>();
        services.AddSingleton<TwelveDataProvider>();
        services.AddSingleton<IMarketDataProvider>(provider => provider.GetRequiredService<TwelveDataProvider>());
        return services;
    }
}
