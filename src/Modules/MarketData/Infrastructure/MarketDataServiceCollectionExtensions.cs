using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Application.Deployment;
using SakerWatch.BuildingBlocks.Kernel.Transactions;
using SakerWatch.BuildingBlocks.Resilience;
using SakerWatch.MarketData.Application;
using SakerWatch.MarketData.Application.Fx;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Lifecycle;
using SakerWatch.MarketData.Application.Lookup;
using SakerWatch.MarketData.Application.Prices;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Contracts;
using SakerWatch.MarketData.Infrastructure.Persistence;
using SakerWatch.MarketData.Infrastructure.Providers;
using SakerWatch.MarketData.Infrastructure.Providers.Banguat;
using SakerWatch.MarketData.Infrastructure.Providers.Edgar;
using SakerWatch.MarketData.Infrastructure.Providers.TwelveData;

namespace SakerWatch.MarketData.Infrastructure;

/// <summary>Registration of the MarketData module for the hosts that fetch market data (the worker, and the API for a symbol check).</summary>
public static class MarketDataServiceCollectionExtensions
{
    /// <summary>
    /// Registers what the API needs to find and register stocks (ADR-0032): the routing table, the router, the Twelve Data provider behind its
    /// allow-listed client (used for searches only), the shared search budget and cache, and the stock directory. It starts no background work.
    /// </summary>
    [SuppressMessage("Major Code Smell", "S138:Functions should not have too many lines of code", Justification = "Declarative composition: one registration per line, nothing to branch on; splitting it would only scatter the list.")]
    public static IServiceCollection AddMarketDataLookup(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(IStockDirectory)))
        {
            return services;
        }

        services.AddOptions<MarketDataProvidersOptions>().Bind(configuration.GetSection(MarketDataProvidersOptions.SectionName));
        // Validated at start: a zero interval would loop on the database and a negative one stops the host, and its ranges were never enforced before.
        services.AddOptions<IngestionOptions>().Bind(configuration.GetSection(IngestionOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddSakerDeployment(configuration);
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
            http => TwelveDataProvider.ConfigureClient(http, baseAddress));

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
        services.TryAddScoped<StockCatalog>();
        services.TryAddScoped<IStockCatalog>(provider => new UnitScopedStockCatalog(provider.GetRequiredService<StockCatalog>(), provider.GetRequiredService<IUnitOfWork>()));
        services.TryAddScoped<CatalogSize>();
        services.TryAddScoped<ICatalogSize>(provider => new UnitScopedCatalogSize(provider.GetRequiredService<CatalogSize>(), provider.GetRequiredService<IUnitOfWork>()));
        services.TryAddScoped<CatalogCapacity>();
        services.TryAddSingleton<RegistrationFallback>();
        services.TryAddScoped<RegistrationQuota>();
        services.TryAddScoped<IStockRegistry, StockRegistry>();
        services.TryAddScoped<ProviderSearch>();
        services.TryAddScoped<StockRegistration>();
        services.TryAddScoped<IStockDirectory, StockDirectory>();

        // What a list of stocks shows: the newest price, how current it is and the performance windows.
        services.AddOptions<PerformanceOptions>().Bind(configuration.GetSection(PerformanceOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<PerformanceCacheOptions>().Bind(configuration.GetSection(PerformanceCacheOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.TryAddScoped<PerformanceReader>();
        services.TryAddScoped<IIngestionStamps, IngestionStamps>();
        services.TryAddSingleton<IPerformanceCache, RedisPerformanceCache>();
        services.TryAddScoped<IPerformanceReader>(provider => new CachedPerformanceReader(
            provider.GetRequiredService<PerformanceReader>(),
            provider.GetRequiredService<IIngestionStamps>(),
            provider.GetRequiredService<IPerformanceCache>(),
            provider.GetRequiredService<IOptions<PerformanceOptions>>(),
            provider.GetRequiredService<IOptions<PerformanceCacheOptions>>()));
        services.TryAddScoped<ILatestBars, LatestBars>();
        services.TryAddScoped<IIngestionStatuses, FailedIngestions>();
        services.TryAddScoped<IMarketSnapshots, MarketSnapshots>();

        // The price history charts and sparklines draw (ADR-0038).
        services.TryAddScoped<IPriceSeriesReader, PriceSeriesReader>();
        services.TryAddScoped<IPriceHistory, PriceHistory>();

        // The exchange rates the calculator reads (ADR-0028): the stored daily rates, the converter and the contract over it.
        services.TryAddScoped<IFxRateStore, FxRateStore>();
        services.TryAddScoped<IFxConverter, FxConverter>();
        services.TryAddScoped<IFxRates, FxRates>();
        services.AddSingleton<TwelveDataProvider>();
        services.AddSingleton<IMarketDataProvider>(provider => provider.GetRequiredService<TwelveDataProvider>());
        return services;
    }

    /// <summary>
    /// Registers everything the worker runs: the lookup, the exchange rate and performance settings, the stores and readers, the scheduler and
    /// the freshness sampler.
    /// </summary>
    [SuppressMessage("Major Code Smell", "S138:Functions should not have too many lines of code", Justification = "Declarative composition: one registration per line, nothing to branch on; splitting it would only scatter the list.")]
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
        services.TryAddScoped<IPriceStore, PriceStore>();
        services.AddOptions<FxGuardOptions>().Bind(configuration.GetSection(FxGuardOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<FxGuardOptions>, FxGuardOptionsValidator>());
        services.TryAddScoped<IFxRateScreening, FxRateScreening>();
        services.TryAddScoped<ISymbolAliases, SymbolAliases>();
        services.TryAddScoped<IIngestionState, IngestionState>();
        services.TryAddScoped<IListingLifecycleStore, ListingLifecycleStore>();
        services.TryAddScoped<ListingLifecycle>();
        services.TryAddScoped<LifecycleCommandRunner>();
        services.TryAddScoped<LifecycleCommandLine>();
        services.TryAddScoped<IFreshnessReader, FreshnessReader>();
        services.AddHostedService<MarketDataScheduler>();
        services.AddSingleton<MarketDataFreshnessSampler>();
        services.AddHostedService(provider => provider.GetRequiredService<MarketDataFreshnessSampler>());
        services.AddSingleton<BanguatFxProvider>();
        services.AddSingleton<IFxRateProvider>(provider => provider.GetRequiredService<BanguatFxProvider>());
        services.AddEdgar(configuration);
        return services;
    }

    /// <summary>
    /// Registers the SEC EDGAR adapter for company profiles and filings (ADR-0040) <b>only when</b> <c>Edgar:Enabled</c> is true, with the
    /// identity the SEC requires. Off by default: nothing calls the SEC until the owner says who is calling. The settings are always checked at
    /// startup, so a bad address or an identity without a contact stops the host instead of failing the first call.
    /// </summary>
    public static IServiceCollection AddEdgar(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<EdgarOptions>().Bind(configuration.GetSection(EdgarOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<EdgarOptions>, EdgarOptionsValidator>());

        var settings = new EdgarOptions();
        configuration.GetSection(EdgarOptions.SectionName).Bind(settings);
        if (!settings.Enabled || services.Any(descriptor => descriptor.ServiceType == typeof(EdgarProvider)))
        {
            return services;
        }

        Uri www = Uri.TryCreate(settings.WwwBaseUrl, UriKind.Absolute, out Uri? wwwUri) ? wwwUri : new Uri(new EdgarOptions().WwwBaseUrl);
        Uri data = Uri.TryCreate(settings.DataBaseUrl, UriKind.Absolute, out Uri? dataUri) ? dataUri : new Uri(new EdgarOptions().DataBaseUrl);
        services.AddSakerHttpClient(
            EdgarProvider.ClientName,
            www,
            http =>
            {
                http.AllowedHosts = [.. new[] { www, data }.Select(uri => uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}").Distinct(StringComparer.OrdinalIgnoreCase)];
                // The submissions of a large filer hold a thousand recent filings and the map of tickers has about ten thousand rows.
                http.MaxResponseBytes = 16 * 1024 * 1024;
                http.MaxRetries = 2;
                http.AttemptTimeout = TimeSpan.FromSeconds(20);
                http.TotalTimeout = TimeSpan.FromSeconds(45);
                // The gate of the adapter keeps the pace; a 429 is the SEC asking to slow down, not a sign the service is unwell.
                http.RateLimitIsTransient = false;
            });
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<EdgarProvider>();
        services.AddSingleton<IFundamentalsProvider>(provider => provider.GetRequiredService<EdgarProvider>());
        services.AddSingleton<IFilingsProvider>(provider => provider.GetRequiredService<EdgarProvider>());
        return services;
    }
}
