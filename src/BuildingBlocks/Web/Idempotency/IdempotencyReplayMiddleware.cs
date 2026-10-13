using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SakerWatch.BuildingBlocks.Application.Idempotency;

namespace SakerWatch.BuildingBlocks.Web.Idempotency;

/// <summary>Adds <c>Idempotent-Replayed: true</c> to responses that were answered from a stored first attempt.</summary>
/// <param name="next">Next middleware.</param>
public sealed class IdempotencyReplayMiddleware(RequestDelegate next)
{
    /// <summary>Processes one request.</summary>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.OnStarting(static state =>
        {
            var http = (HttpContext)state;
            if (http.RequestServices.GetService<IIdempotencyContext>() is { Replayed: true })
            {
                http.Response.Headers[IdempotencyKeyReader.ReplayedHeaderName] = "true";
            }

            return Task.CompletedTask;
        }, context);

        return next(context);
    }
}
