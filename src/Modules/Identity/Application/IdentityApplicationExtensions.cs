using Microsoft.Extensions.DependencyInjection;
using SakerWatch.BuildingBlocks.Kernel.Security;

namespace SakerWatch.Identity.Application;

/// <summary>Registration of the Identity module's application services.</summary>
public static class IdentityApplicationExtensions
{
    /// <summary>
    /// Registers <see cref="IUserResolver"/> and its cache. Request handlers and validators are picked up by
    /// <c>AddApplicationPipeline</c> when the host passes this assembly.
    /// </summary>
    public static IServiceCollection AddIdentityApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ResolvedUserCache>();
        services.AddScoped<IUserResolver, UserResolver>();
        return services;
    }
}
