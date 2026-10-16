using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SakerWatch.BuildingBlocks.Kernel.Tracing;

namespace SakerWatch.BuildingBlocks.Application.Events;

/// <summary>Registration of integration events and their handlers.</summary>
public static class EventServiceCollectionExtensions
{
    /// <summary>
    /// Registers the event registry (names from <see cref="IntegrationEventNameAttribute"/>), every
    /// <see cref="IIntegrationEventConsumer{TEvent}"/> found, and the scoped correlation context.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="assemblies">Assemblies that define events or handlers.</param>
    public static IServiceCollection AddIntegrationEvents(this IServiceCollection services, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);

        ServiceScanning.AddToRegistry(services, assemblies, IntegrationEventRegistry.FromAssemblies);
        services.TryAddScoped<CorrelationContext>();
        services.TryAddScoped<ICorrelationContext>(provider => provider.GetRequiredService<CorrelationContext>());
        ServiceScanning.AddImplementationsOf(services, assemblies, typeof(IIntegrationEventConsumer<>));

        return services;
    }
}
