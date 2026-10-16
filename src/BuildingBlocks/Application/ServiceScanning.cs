using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SakerWatch.BuildingBlocks.Application;

/// <summary>
/// The assembly scanning behind <c>AddApplicationPipeline</c>, <c>AddIntegrationEvents</c> and <c>AddJobs</c>, written once. Every
/// registration here is safe to repeat: a host (or a test) that calls an <c>Add...</c> method twice, or two modules that both do,
/// gets each handler and behavior once, and the registries know the types of every assembly ever passed.
/// </summary>
internal static class ServiceScanning
{
    /// <summary>Registers every concrete class of <paramref name="assemblies"/> that implements a closed form of <paramref name="openInterface"/>, once.</summary>
    public static void AddImplementationsOf(IServiceCollection services, IEnumerable<Assembly> assemblies, Type openInterface)
    {
        foreach (Type implementation in assemblies.SelectMany(assembly => assembly.GetTypes()).Where(type => type is { IsClass: true, IsAbstract: false }))
        {
            foreach (Type contract in implementation.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == openInterface))
            {
                services.TryAddEnumerable(ServiceDescriptor.Scoped(contract, implementation));
            }
        }
    }

    /// <summary>
    /// The assemblies a registry is built from. The first call creates the holder and the registry; later calls only add their assemblies,
    /// so the registry (built when first resolved) covers all of them whatever the order of the calls.
    /// </summary>
    public static void AddToRegistry<TRegistry>(IServiceCollection services, IEnumerable<Assembly> assemblies, Func<IEnumerable<Assembly>, TRegistry> create)
        where TRegistry : class
    {
        AssemblySet? set = services.Select(d => d.ImplementationInstance).OfType<AssemblySet>().FirstOrDefault(existing => existing.Registry == typeof(TRegistry));
        if (set is null)
        {
            set = new AssemblySet(typeof(TRegistry));
            services.AddSingleton(set);
        }

        foreach (Assembly assembly in assemblies)
        {
            set.Items.Add(assembly);
        }

        AssemblySet shared = set;
        services.TryAddSingleton(_ => create(shared.Items));
    }

    private sealed class AssemblySet(Type registry)
    {
        public Type Registry { get; } = registry;

        public HashSet<Assembly> Items { get; } = [];
    }
}
