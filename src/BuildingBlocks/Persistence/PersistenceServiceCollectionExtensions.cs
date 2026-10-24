using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Application.Audit;
using SakerWatch.BuildingBlocks.Application.Events;
using SakerWatch.BuildingBlocks.Application.Idempotency;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.BuildingBlocks.Kernel.Health;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Kernel.Transactions;
using SakerWatch.BuildingBlocks.Persistence.Audit;
using SakerWatch.BuildingBlocks.Persistence.Jobs;
using SakerWatch.BuildingBlocks.Persistence.Outbox;

namespace SakerWatch.BuildingBlocks.Persistence;

/// <summary>Registration of module DbContexts with the shared PostgreSQL settings and the user-context interceptor.</summary>
public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the validated <see cref="PostgresOptions"/> (fails at startup when the connection string is missing),
    /// the <see cref="UserContextConnectionInterceptor"/>, a readiness check tagged <c>ready</c> and a default anonymous <see cref="ICurrentUser"/>
    /// (replace it with the web implementation in the API), the scoped <see cref="DbSession"/> and its <see cref="IUnitOfWork"/>.
    /// </summary>
    public static IServiceCollection AddSakerPostgres(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<PostgresOptions>()
            .Bind(configuration.GetSection(PostgresOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.PostConfigure<PostgresOptions>(options => options.Postgres = PostgresPooling.Apply(options.Postgres, options.PostgresMaxPoolSize));
        services.AddSingleton<IValidateOptions<PostgresOptions>, PostgresOptionsValidator>();
        services.AddHealthChecks().AddCheck<PostgresReadinessCheck>("postgres", failureStatus: null, tags: ["ready"], timeout: ReadinessDefaults.CheckTimeout);
        services.TryAddScoped<ICurrentUser, AnonymousCurrentUser>();
        services.TryAddScoped<UserContextConnectionInterceptor>();
        services.TryAddScoped<DbSession>();
        services.TryAddScoped<AmbientTransactionInterceptor>();
        services.TryAddScoped<AmbientCommandInterceptor>();
        services.TryAddScoped<IIdempotencyStore, IdempotencyStore>();
        services.TryAddScoped<IOutbox, OutboxWriter>();
        services.TryAddScoped<IInbox, InboxStore>();
        services.TryAddScoped<IJobEnqueuer, JobEnqueuer>();

        // The audit trail needs a secret pepper (Audit:ActorPepper); a host that cannot audit does not start.
        services.AddOptions<AuditOptions>()
            .Bind(configuration.GetSection(AuditOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AuditOptions>, AuditOptionsValidator>();
        services.TryAddScoped<IAuditTrail, AuditTrail>();

        // Replaces the pipeline's no-op unit of work: commands now run in one database transaction.
        services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<DbSession>());
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
            // Every context of the scope shares the session's connection, so one transaction can span them.
            options
                .UseNpgsql(provider.GetRequiredService<DbSession>().Connection, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", historySchema))
                .AddInterceptors(
                    provider.GetRequiredService<UserContextConnectionInterceptor>(),
                    provider.GetRequiredService<AmbientTransactionInterceptor>(),
                    provider.GetRequiredService<AmbientCommandInterceptor>());
        });
        return services;
    }
}
