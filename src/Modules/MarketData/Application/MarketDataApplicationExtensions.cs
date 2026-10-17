using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Calendars;

namespace SakerWatch.MarketData.Application;

/// <summary>Registration of the MarketData use cases and ports.</summary>
public static class MarketDataApplicationExtensions
{
    /// <summary>Registers the provider router and its validated routing table. Providers themselves are registered by the Infrastructure.</summary>
    public static IServiceCollection AddMarketDataApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Every host that lists the module calls this; the option validators the framework adds are not de-duplicated by themselves.
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IMarketDataProviderRouter)))
        {
            return services;
        }

        services.AddOptions<MarketDataProvidersOptions>().ValidateDataAnnotations().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<MarketDataProvidersOptions>, MarketDataProvidersOptionsValidator>());
        services.TryAddSingleton<IMarketDataProviderRouter, MarketDataProviderRouter>();
        services.AddOptions<IngestionOptions>().ValidateDataAnnotations().ValidateOnStart();
        services.TryAddSingleton(ExchangeCalendars.Default());
        services.TryAddScoped<ListingIngestion>();
        services.TryAddScoped<FollowUpJobs>();
        return services;
    }
}
