using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SakerWatch.BuildingBlocks.Application.Behaviors;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Security;
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

        services.TryAddScoped<ISender, Sender>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(IPipelineBehavior<,>), typeof(TracingBehavior<,>)));
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>)));
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>)));
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(IPipelineBehavior<,>), typeof(UnitOfWorkBehavior<,>)));
        services.TryAddScoped<IUnitOfWork, NoOpUnitOfWork>();
        services.TryAddScoped<ICurrentUser, AnonymousCurrentUser>();

        ServiceScanning.AddImplementationsOf(services, applicationAssemblies, typeof(IRequestHandler<,>));

        services.AddValidatorsFromAssemblies(applicationAssemblies, ServiceLifetime.Scoped, includeInternalTypes: true);
        return services;
    }
}
