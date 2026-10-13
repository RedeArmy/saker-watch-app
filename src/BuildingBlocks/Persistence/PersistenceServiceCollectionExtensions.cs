using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SakerWatch.BuildingBlocks.Persistence;

/// <summary>Registration of module DbContexts with the shared PostgreSQL settings and the user-context interceptor.</summary>
public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the validated <see cref="PostgresOptions"/> (fails at startup when the connection string is missing),
    /// the <see cref="UserContextConnectionInterceptor"/> and a default anonymous <see cref="IDbUserContext"/>
    /// (replace it with the web implementation in the API).
    /// </summary>
    public static IServiceCollection AddSakerPostgres(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<PostgresOptions>()
            .Bind(configuration.GetSection(PostgresOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddScoped<IDbUserContext, AnonymousDbUserContext>();
        services.TryAddScoped<UserContextConnectionInterceptor>();
        return services;
    }

    /// <summary>Registers <typeparamref name="TContext"/> against the runtime connection string, keeping its history table in its own schema.</summary>
    /// <typeparam name="TContext">The module's DbContext.</typeparam>
    /// <param name="services">Service collection (after <see cref="AddSakerPostgres"/>).</param>
    /// <param name="historySchema">Schema of the module, which also holds its <c>__ef_migrations_history</c> table.</param>
    public static IServiceCollection AddSakerDbContext<TContext>(this IServiceCollection services, string historySchema)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(historySchema);

        services.AddDbContext<TContext>((provider, options) =>
        {
            string connectionString = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<PostgresOptions>>().Value.Postgres;
            options
                .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", historySchema))
                .AddInterceptors(provider.GetRequiredService<UserContextConnectionInterceptor>());
        });
        return services;
    }
}
