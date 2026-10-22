using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Health;
using StackExchange.Redis;

namespace SakerWatch.BuildingBlocks.Redis;

/// <summary>Registration of the shared Redis connection and its readiness check.</summary>
public static class RedisServiceCollectionExtensions
{
    /// <summary>Health check name used in readiness.</summary>
    public const string HealthCheckName = "redis";

    /// <summary>
    /// Registers a singleton <see cref="IConnectionMultiplexer"/> from <c>ConnectionStrings:Redis</c> (validated at startup)
    /// that keeps retrying in the background instead of failing the host when Redis is briefly down, and a readiness check.
    /// While Redis is not connected a command fails at once (<see cref="BacklogPolicy.FailFast"/>) instead of waiting in a backlog for the
    /// command timeout: the callers treat a failure as a miss or as "allowed", and a wait of five seconds in each request would turn "Redis is down,
    /// the cache is missed" into "every request takes five seconds longer". The connection is made when the host starts, so that wait (up to the
    /// connect timeout, <c>connectTimeout</c> in the connection string, 5 s by default) is not paid by the first request.
    /// </summary>
    public static IServiceCollection AddSakerRedis(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RedisOptions>()
            .Configure(options => options.ConnectionString = configuration.GetConnectionString("Redis") ?? string.Empty)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IConnectionMultiplexer>(provider =>
        {
            var options = ConfigurationOptions.Parse(provider.GetRequiredService<IOptions<RedisOptions>>().Value.ConnectionString);
            options.AbortOnConnectFail = false;
            options.BacklogPolicy = BacklogPolicy.FailFast;
            options.ClientName = "saker-watch";
            return ConnectionMultiplexer.Connect(options);
        });
        services.AddHostedService<RedisConnectionStarter>();
        services.AddHealthChecks().AddCheck<RedisReadinessCheck>(HealthCheckName, failureStatus: null, tags: ["ready"], timeout: ReadinessDefaults.CheckTimeout);
        return services;
    }
}

/// <summary>Makes the connection to Redis when the host starts, so the first request does not wait for it (it is a singleton made on first use).</summary>
/// <param name="services">The provider that makes the multiplexer.</param>
internal sealed class RedisConnectionStarter(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        Task.Run(() => services.GetRequiredService<IConnectionMultiplexer>(), cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
