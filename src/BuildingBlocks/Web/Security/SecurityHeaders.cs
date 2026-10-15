using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace SakerWatch.BuildingBlocks.Web.Security;

/// <summary>
/// Adds the response headers every API response carries (OWASP ASVS V14): no content sniffing, no framing, no referrer, a
/// content-security-policy that allows nothing (the API returns JSON, never a page) and <c>Cache-Control: no-store</c> unless an
/// endpoint chose its own caching. In Development the Swagger UI needs scripts and styles, so only it is exempt from the policy.
/// </summary>
/// <param name="next">Next middleware.</param>
/// <param name="environment">Decides whether the Swagger UI exemption applies.</param>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IHostEnvironment environment)
{
    private const string _lockedDownPolicy = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    /// <summary>Processes one request.</summary>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        bool swaggerUi = environment.IsDevelopment() && context.Request.Path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase);
        context.Response.OnStarting(static state =>
        {
            var (http, relaxed) = ((HttpContext, bool))state;
            IHeaderDictionary headers = http.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Cross-Origin-Resource-Policy"] = "same-site";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            if (!relaxed)
            {
                headers.ContentSecurityPolicy = _lockedDownPolicy;
            }

            if (!headers.ContainsKey("Cache-Control"))
            {
                headers.CacheControl = "no-store";
            }

            return Task.CompletedTask;
        }, (context, swaggerUi));

        return next(context);
    }
}
