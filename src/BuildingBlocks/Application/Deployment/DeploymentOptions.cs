using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace SakerWatch.BuildingBlocks.Application.Deployment;

/// <summary>
/// How the application is deployed, for the few places where behavior depends on it (ADR-0048). Bound from <c>Deployment</c>. One setting for the whole
/// process, so that "how many API instances are there" is said once and not in each feature that needs to know.
/// </summary>
public sealed class DeploymentOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Deployment";

    /// <summary>
    /// How many API processes run side by side. One today (a single API instance on a single node, ADR-0021). With more than one the rate limiters count in
    /// Redis, so a caller has one allowance and not one for each process, and a count kept by each process (the registration cap while Redis is down)
    /// is divided among them. Say it before scaling the API out; if you forget, the API processes see each other in Redis and count with the larger number
    /// (<see cref="IApiInstances"/>), and say so.
    /// </summary>
    [Range(1, 64)]
    public int ApiReplicas { get; set; } = 1;
}

/// <summary>How many API processes there are right now, for the places where behavior depends on it (ADR-0048).</summary>
public interface IApiInstances
{
    /// <summary>The number of API processes to count with: what <see cref="DeploymentOptions.ApiReplicas"/> declares, or more if more have been seen.</summary>
    int Count { get; }
}

/// <summary>The number of API processes as declared in <see cref="DeploymentOptions"/>: all there is where nothing can look (the worker, a tool, a test).</summary>
/// <param name="options">The deployment settings.</param>
public sealed class DeclaredApiInstances(IOptions<DeploymentOptions> options) : IApiInstances
{
    /// <inheritdoc />
    public int Count => options.Value.ApiReplicas;
}

/// <summary>Registration of <see cref="DeploymentOptions"/>.</summary>
public static class DeploymentServiceCollectionExtensions
{
    /// <summary>Binds and validates <c>Deployment</c> at startup. Safe to call from several registrations.</summary>
    public static IServiceCollection AddSakerDeployment(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DeploymentOptions>()
            .Bind(configuration.GetSection(DeploymentOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddSingleton<IApiInstances, DeclaredApiInstances>();
        return services;
    }
}
