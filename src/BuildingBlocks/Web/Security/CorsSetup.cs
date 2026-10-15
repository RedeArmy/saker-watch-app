using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SakerWatch.BuildingBlocks.Web.Security;

/// <summary>
/// The product UI reaches the API through its own backend (the Next.js BFF is mandatory, ADR-0009), so browsers never call the
/// API cross-origin and CORS is <b>closed by default</b>. <c>Cors:AllowedOrigins</c> may list exact origins (https, or
/// http only for localhost) for a deliberate exception; wildcards, paths and credentials are never accepted.
/// </summary>
public static class CorsSetup
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Cors";

    /// <summary>Response headers a browser client may read.</summary>
    public static readonly string[] ExposedHeaders = ["Idempotent-Replayed", "Retry-After", "X-Correlation-Id", "api-supported-versions"];

    /// <summary>Request headers a browser client may send.</summary>
    public static readonly string[] AllowedHeaders = ["Authorization", "Content-Type", "Idempotency-Key", "X-Correlation-Id", "If-Match", "If-None-Match"];

    /// <summary>Registers CORS; the default policy exists only when origins are configured.</summary>
    public static IServiceCollection AddSakerCors(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        string[] origins = configuration.GetSection(SectionName).GetSection("AllowedOrigins").Get<string[]>() ?? [];
        foreach (string origin in origins)
        {
            Validate(origin);
        }

        services.AddCors(options =>
        {
            if (origins.Length > 0)
            {
                options.AddDefaultPolicy(policy => policy
                    .WithOrigins(origins)
                    .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
                    .WithHeaders(AllowedHeaders)
                    .WithExposedHeaders(ExposedHeaders)
                    .SetPreflightMaxAge(TimeSpan.FromMinutes(10)));
            }
        });
        return services;
    }

    private static void Validate(string origin)
    {
        bool valid = Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)
            && uri.AbsolutePath == "/"
            && !origin.EndsWith('/')
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && string.IsNullOrEmpty(uri.UserInfo)
            && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));
        if (!valid)
        {
            throw new InvalidOperationException($"{SectionName}:AllowedOrigins entry '{origin}' must be an exact origin: https://host[:port] (http only for localhost), without a path, wildcard or credentials.");
        }
    }
}
