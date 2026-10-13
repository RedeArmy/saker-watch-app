using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Time;

namespace SakerWatch.BuildingBlocks.Persistence.Outbox;

/// <summary>Registration of the outbox dispatcher (Worker only).</summary>
public static class OutboxServiceCollectionExtensions
{
    /// <summary>
    /// Adds the validated <c>Outbox</c> options, the processor and the hosted dispatcher. Call it after
    /// <c>AddSakerPostgres</c> and <c>AddIntegrationEvents</c>.
    /// </summary>
    public static IServiceCollection AddOutboxDispatcher(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<OutboxOptions>()
            .Bind(configuration.GetSection(OutboxOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton(provider =>
        {
            OutboxOptions options = provider.GetRequiredService<IOptions<OutboxOptions>>().Value;
            return new ExponentialBackoff(options.BackoffBase, options.BackoffCap);
        });
        services.AddSingleton<OutboxProcessor>();
        services.AddHostedService<OutboxDispatcherService>();
        return services;
    }
}
