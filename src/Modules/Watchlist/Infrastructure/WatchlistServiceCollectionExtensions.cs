using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SakerWatch.Watchlist.Application;
using SakerWatch.Watchlist.Infrastructure.Persistence;

namespace SakerWatch.Watchlist.Infrastructure;

/// <summary>Registration of the Watchlist module for the API.</summary>
public static class WatchlistServiceCollectionExtensions
{
    /// <summary>
    /// Registers the limits (<c>Watchlist:Limits</c>, validated at startup against the ceilings of the database) and the repository. The request
    /// handlers and validators are picked up by <c>AddApplicationPipeline</c> when the host passes the application assembly.
    /// </summary>
    public static IServiceCollection AddWatchlist(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<WatchlistOptions>().Bind(configuration.GetSection(WatchlistOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.TryAddScoped<IWatchlistRepository, WatchlistRepository>();
        services.TryAddScoped<IPortfolioRepository, PortfolioRepository>();
        return services;
    }
}
