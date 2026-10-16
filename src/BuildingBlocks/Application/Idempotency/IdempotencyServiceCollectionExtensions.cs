using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SakerWatch.BuildingBlocks.Application.Behaviors;
using SakerWatch.BuildingBlocks.Application.Messaging;

namespace SakerWatch.BuildingBlocks.Application.Idempotency;

/// <summary>Registration of the idempotency behavior.</summary>
public static class IdempotencyServiceCollectionExtensions
{
    /// <summary>
    /// Adds the idempotency behavior, innermost in the pipeline (after validation and the unit of work), plus its
    /// validated options and the scoped replay context. Call it after <c>AddApplicationPipeline</c> and register an
    /// <see cref="IIdempotencyStore"/> (the PostgreSQL store comes with <c>AddSakerPostgres</c>).
    /// </summary>
    public static IServiceCollection AddIdempotency(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<IdempotencyOptions>()
            .Bind(configuration.GetSection(IdempotencyOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddScoped<IdempotencyContext>();
        services.TryAddScoped<IIdempotencyContext>(provider => provider.GetRequiredService<IdempotencyContext>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(IPipelineBehavior<,>), typeof(IdempotencyBehavior<,>)));
        return services;
    }
}
