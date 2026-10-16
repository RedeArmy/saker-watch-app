using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Kernel.Time;

namespace SakerWatch.BuildingBlocks.Persistence.Jobs;

/// <summary>Registration of the job dispatcher (Worker only).</summary>
public static class JobDispatcherServiceCollectionExtensions
{
    /// <summary>
    /// Adds the validated <c>Jobs</c> options, the processor, the queue gauges and the hosted dispatcher. Call it after
    /// <c>AddSakerPostgres</c> and <c>AddJobs</c>.
    /// </summary>
    public static IServiceCollection AddJobDispatcher(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<JobOptions>()
            .Bind(configuration.GetSection(JobOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<JobProcessor>(provider =>
        {
            JobOptions options = provider.GetRequiredService<IOptions<JobOptions>>().Value;
            return ActivatorUtilities.CreateInstance<JobProcessor>(provider, new ExponentialBackoff(options.BackoffBase, options.BackoffCap));
        });
        // The worker's scopes get a settable current user (anonymous by default), replacing the anonymous default of AddSakerPostgres.
        services.AddScoped<JobCurrentUser>();
        services.AddScoped<ICurrentUser>(provider => provider.GetRequiredService<JobCurrentUser>());
        services.AddSingleton<JobQueueMetrics>();
        services.AddHostedService<JobDispatcherService>();
        return services;
    }
}
