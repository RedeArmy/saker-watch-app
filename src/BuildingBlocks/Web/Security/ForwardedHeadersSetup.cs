using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using IPNetwork = System.Net.IPNetwork;

namespace SakerWatch.BuildingBlocks.Web.Security;

/// <summary>
/// Behind Caddy the TCP peer is the proxy, so the real client address and scheme arrive in <c>X-Forwarded-*</c>. Those headers are
/// trusted <b>only</b> from the proxies named in <c>ForwardedHeaders:KnownProxies</c> / <c>ForwardedHeaders:KnownNetworks</c>
/// (loopback when nothing is configured), one hop deep; trusting them from anywhere would let a caller pick its own IP and so
/// evade the per-IP rate limit. Malformed entries fail startup.
/// </summary>
public static class ForwardedHeadersSetup
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "ForwardedHeaders";

    /// <summary>Registers the forwarded-headers options from configuration.</summary>
    public static IServiceCollection AddSakerForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        IConfigurationSection section = configuration.GetSection(SectionName);
        string[] proxies = section.GetSection("KnownProxies").Get<string[]>() ?? [];
        string[] networks = section.GetSection("KnownNetworks").Get<string[]>() ?? [];

        var parsedProxies = proxies.Select(entry => IPAddress.TryParse(entry, out IPAddress? address)
            ? address
            : throw new InvalidOperationException($"{SectionName}:KnownProxies entry '{entry}' is not an IP address.")).ToList();
        var parsedNetworks = networks.Select(entry => IPNetwork.TryParse(entry, out IPNetwork network)
            ? network
            : throw new InvalidOperationException($"{SectionName}:KnownNetworks entry '{entry}' is not a CIDR range such as 10.0.0.0/8.")).ToList();

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            options.ForwardLimit = 1;
            if (parsedProxies.Count > 0 || parsedNetworks.Count > 0)
            {
                options.KnownProxies.Clear();
                options.KnownIPNetworks.Clear();
                foreach (IPAddress proxy in parsedProxies)
                {
                    options.KnownProxies.Add(proxy);
                }

                foreach (IPNetwork network in parsedNetworks)
                {
                    options.KnownIPNetworks.Add(network);
                }
            }
        });
        return services;
    }
}
