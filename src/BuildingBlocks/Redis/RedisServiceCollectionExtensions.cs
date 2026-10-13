using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
            ConfigurationOptions options = ConfigurationOptions.Parse(provider.GetRequiredService<IOptions<RedisOptions>>().Value.ConnectionString);
            options.AbortOnConnectFail = false;
            options.ConnectTimeout = 5000;
            options.ClientName = "saker-watch";
            return ConnectionMultiplexer.Connect(options);
        });
        services.AddHealthChecks().AddCheck<RedisReadinessCheck>(HealthCheckName, tags: ["ready"]);
        return services;
    }
}
