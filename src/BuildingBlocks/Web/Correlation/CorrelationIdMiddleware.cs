using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SakerWatch.BuildingBlocks.Kernel.Time;

namespace SakerWatch.BuildingBlocks.Web.Correlation;

/// <summary>
/// Gives every request a correlation id (ADR-0016): accepts a well-formed <c>X-Correlation-Id</c> from the caller,
/// otherwise creates one, echoes it on the response, tags the current span and opens a logging scope with it.
/// </summary>
/// <param name="next">Next middleware.</param>
/// <param name="ids">Id source for requests that arrive without a usable id.</param>
/// <param name="logger">Logger used to open the correlation scope.</param>
public sealed class CorrelationIdMiddleware(RequestDelegate next, IIdGenerator ids, ILogger<CorrelationIdMiddleware> logger)
{
    /// <summary>Header carrying the correlation id in both directions.</summary>
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>HttpContext item key under which the id is stored.</summary>
    public const string ItemKey = "saker.correlation_id";

    private const int _maxLength = 64;

    /// <summary>Processes one request.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string correlationId = ReadIncoming(context) ?? ids.NewGuid().ToString("N");
        context.Items[ItemKey] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        Activity.Current?.SetTag("saker.correlation_id", correlationId);

        using IDisposable? scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        await next(context);
    }

    /// <summary>The correlation id of the current request, or <see langword="null"/> outside the middleware.</summary>
    public static string? Get(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(ItemKey, out object? value) ? value as string : null;
    }

    // Only a conservative alphabet is accepted so the value is safe to log, echo and store.
    private static string? ReadIncoming(HttpContext context)
    {
        string? incoming = context.Request.Headers[HeaderName].FirstOrDefault();
        if (string.IsNullOrEmpty(incoming) || incoming.Length > _maxLength)
        {
            return null;
        }

        return incoming.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') ? incoming : null;
    }
}
