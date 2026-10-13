using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SakerWatch.BuildingBlocks.Kernel.Tracing;

namespace SakerWatch.BuildingBlocks.Application.Jobs;

/// <summary>Registration of job payloads and their handlers.</summary>
public static class JobServiceCollectionExtensions
{
    /// <summary>
    /// Registers the job registry (names from <see cref="JobTypeAttribute"/>), every <see cref="IJobHandler{TPayload}"/> found,
    /// and the scoped correlation context.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="assemblies">Assemblies that define job payloads or handlers.</param>
    public static IServiceCollection AddJobs(this IServiceCollection services, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);

        services.AddSingleton(JobRegistry.FromAssemblies(assemblies));
        services.TryAddScoped<CorrelationContext>();
        services.TryAddScoped<ICorrelationContext>(provider => provider.GetRequiredService<CorrelationContext>());

        foreach (Type implementation in assemblies.SelectMany(a => a.GetTypes()).Where(t => t is { IsClass: true, IsAbstract: false }))
        {
            foreach (Type handlerInterface in implementation.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IJobHandler<>)))
            {
                services.AddScoped(handlerInterface, implementation);
            }
        }

        return services;
    }
}
