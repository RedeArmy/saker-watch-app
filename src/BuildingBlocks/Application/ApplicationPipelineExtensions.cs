using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SakerWatch.BuildingBlocks.Application.Behaviors;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Transactions;

namespace SakerWatch.BuildingBlocks.Application;

/// <summary>Registration of the use-case pipeline.</summary>
public static class ApplicationPipelineExtensions
{
    /// <summary>
    /// Registers <see cref="ISender"/>, the standard behaviors (outermost first: tracing, logging, validation, unit of work),
    /// and every request handler and validator found in <paramref name="applicationAssemblies"/>.
    /// Behaviors added later (idempotency, unit of work) must be registered after this call to run closer to the handler.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="applicationAssemblies">Module Application assemblies to scan.</param>
    public static IServiceCollection AddApplicationPipeline(this IServiceCollection services, params Assembly[] applicationAssemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(applicationAssemblies);

        services.AddScoped<ISender, Sender>();
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TracingBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(UnitOfWorkBehavior<,>));
        services.TryAddScoped<IUnitOfWork, NoOpUnitOfWork>();

        foreach (Assembly assembly in applicationAssemblies)
        {
            foreach (Type implementation in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false }))
            {
                foreach (Type handlerInterface in implementation.GetInterfaces()
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)))
                {
                    services.AddScoped(handlerInterface, implementation);
                }
            }
        }

        services.AddValidatorsFromAssemblies(applicationAssemblies, ServiceLifetime.Scoped, includeInternalTypes: true);
        return services;
    }
}
