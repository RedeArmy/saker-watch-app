using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace SakerWatch.BuildingBlocks.Mongo;

/// <summary>Registration of the MongoDB client, database and readiness check.</summary>
public static class MongoServiceCollectionExtensions
{
    /// <summary>Health check name used in readiness.</summary>
    public const string HealthCheckName = "mongo";

    /// <summary>
    /// Registers a singleton <see cref="IMongoClient"/> and <see cref="IMongoDatabase"/> from <c>ConnectionStrings:Mongo</c>
    /// and <c>Mongo:Database</c> (validated at startup) and a readiness check tagged <c>ready</c>.
    /// </summary>
    public static IServiceCollection AddSakerMongo(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<MongoOptions>()
            .Configure(options =>
            {
                configuration.GetSection(MongoOptions.SectionName).Bind(options);
                options.ConnectionString = configuration.GetConnectionString("Mongo") ?? string.Empty;
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IMongoClient>(provider =>
        {
            var settings = MongoClientSettings.FromConnectionString(provider.GetRequiredService<IOptions<MongoOptions>>().Value.ConnectionString);
            settings.ApplicationName = "saker-watch";
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
            return new MongoClient(settings);
        });
        services.AddSingleton(provider =>
            provider.GetRequiredService<IMongoClient>().GetDatabase(provider.GetRequiredService<IOptions<MongoOptions>>().Value.Database));
        services.AddHealthChecks().AddCheck<MongoReadinessCheck>(HealthCheckName, tags: ["ready"]);
        return services;
    }
}
