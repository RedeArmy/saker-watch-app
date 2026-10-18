using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Resilience;
using SakerWatch.MarketData.Application;
using SakerWatch.MarketData.Application.Fx;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Lookup;
using SakerWatch.MarketData.Application.Prices;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Contracts;
using SakerWatch.MarketData.Infrastructure.Persistence;
using SakerWatch.MarketData.Infrastructure.Providers;
using SakerWatch.MarketData.Infrastructure.Providers.Banguat;
using SakerWatch.MarketData.Infrastructure.Providers.TwelveData;

namespace SakerWatch.MarketData.Infrastructure;

/// <summary>Registration of the MarketData module for the hosts that fetch market data (the worker, and the API for a symbol check).</summary>
public static class MarketDataServiceCollectionExtensions
{
    /// <summary>
    /// Registers what the API needs to find and register stocks (ADR-0032): the routing table, the router, the Twelve Data provider behind its
    /// allow-listed client (used for searches only), the shared search budget and cache, and the stock directory. It starts no background work.
    /// </summary>
    public static IServiceCollection AddMarketDataLookup(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(IStockDirectory)))
        {
            return services;
        }

        services.AddOptions<MarketDataProvidersOptions>().Bind(configuration.GetSection(MarketDataProvidersOptions.SectionName));
        services.AddOptions<IngestionOptions>().Bind(configuration.GetSection(IngestionOptions.SectionName));
        services.AddOptions<StockLookupOptions>().Bind(configuration.GetSection(StockLookupOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
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

        services.TryAddSingleton<ISearchCache, RedisSearchCache>();
        services.TryAddScoped<IStockCatalog, StockCatalog>();
        services.TryAddScoped<IStockRegistry, StockRegistry>();
        services.TryAddScoped<ProviderSearch>();
        services.TryAddScoped<StockRegistration>();
        services.TryAddScoped<IStockDirectory, StockDirectory>();

        // What a list of stocks shows: the newest price, how current it is and the performance windows.
        services.AddOptions<PerformanceOptions>().Bind(configuration.GetSection(PerformanceOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.TryAddScoped<IPerformanceReader, PerformanceReader>();
        services.TryAddScoped<ILatestBars, LatestBars>();
        services.TryAddScoped<IIngestionStatuses, FailedIngestions>();
        services.TryAddScoped<IMarketSnapshots, MarketSnapshots>();
        services.AddSingleton<TwelveDataProvider>();
        services.AddSingleton<IMarketDataProvider>(provider => provider.GetRequiredService<TwelveDataProvider>());
        return services;
    }

    /// <summary>
    /// Registers everything the worker runs: the lookup, the exchange rate and performance settings, the stores and readers, the scheduler and
    /// the freshness sampler.
    /// </summary>
    public static IServiceCollection AddMarketData(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(BanguatFxProvider)))
        {
            return services;
        }

        services.AddMarketDataLookup(configuration);

        services.AddOptions<BanguatOptions>().Bind(configuration.GetSection(BanguatOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<BanguatOptions>, BanguatOptionsValidator>());
        var banguat = new BanguatOptions();
        configuration.GetSection(BanguatOptions.SectionName).Bind(banguat);
        Uri banguatAddress = Uri.TryCreate(banguat.BaseUrl, UriKind.Absolute, out Uri? bank) ? bank : new Uri(new BanguatOptions().BaseUrl);
        services.AddSakerHttpClient(
            BanguatFxProvider.ClientName,
            banguatAddress,
            http =>
            {
                http.AllowedHosts = [banguatAddress.IsDefaultPort ? banguatAddress.Host : $"{banguatAddress.Host}:{banguatAddress.Port}"];
                // Ten years of one currency are about 400 KB.
                http.MaxResponseBytes = 4 * 1024 * 1024;
                http.AttemptTimeout = TimeSpan.FromSeconds(20);
                http.TotalTimeout = TimeSpan.FromSeconds(30);
            });

        services.TryAddScoped<ListingIngestion>();
        services.TryAddScoped<FollowUpJobs>();
        services.TryAddScoped<IFxConverter, FxConverter>();
        services.TryAddScoped<IPriceStore, PriceStore>();
        services.TryAddScoped<IFxRateStore, FxRateStore>();
        services.TryAddScoped<ISymbolAliases, SymbolAliases>();
        services.TryAddScoped<IIngestionState, IngestionState>();
        services.TryAddScoped<IFreshnessReader, FreshnessReader>();
        services.AddHostedService<MarketDataScheduler>();
        services.AddSingleton<MarketDataFreshnessSampler>();
        services.AddHostedService(provider => provider.GetRequiredService<MarketDataFreshnessSampler>());
        services.AddSingleton<BanguatFxProvider>();
        services.AddSingleton<IFxRateProvider>(provider => provider.GetRequiredService<BanguatFxProvider>());
        return services;
    }
}
