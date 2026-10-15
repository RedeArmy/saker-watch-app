using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SakerWatch.Retention.Application;

namespace SakerWatch.Retention.Infrastructure;

/// <summary>Registration of the Retention module for the worker host.</summary>
public static class RetentionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the retention rules, their PostgreSQL store and the daily scheduler. The host must also call
    /// <c>AddJobs</c> with the <see cref="RetentionRunJob"/> assembly so the queued job finds its handler.
    /// </summary>
    public static IServiceCollection AddRetention(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RetentionOptions>()
            .Bind(configuration.GetSection(RetentionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddRetentionApplication();
        services.AddScoped<IRetentionStore, PlatformRetentionStore>();
        services.AddHostedService<RetentionScheduler>();
        return services;
    }
}
