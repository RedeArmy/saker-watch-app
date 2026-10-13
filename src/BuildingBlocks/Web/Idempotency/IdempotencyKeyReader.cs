using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SakerWatch.BuildingBlocks.Application.Idempotency;
using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.BuildingBlocks.Web.Idempotency;

/// <summary>Reads the <c>Idempotency-Key</c> header into the key and scope the pipeline needs (ADR-0017).</summary>
public static class IdempotencyKeyReader
{
    /// <summary>Name of the request header.</summary>
    public const string HeaderName = "Idempotency-Key";

    /// <summary>Name of the response header added when a stored result is replayed.</summary>
    public const string ReplayedHeaderName = "Idempotent-Replayed";

    /// <summary>
    /// Returns the key and scope of the current request, or a validation error when the header is missing or malformed.
    /// Use it on endpoints that create or trigger work, where the key is required.
    /// </summary>
    public static Result<IdempotencyKeyInfo> Require(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string? key = context.Request.Headers[HeaderName].FirstOrDefault();
        if (string.IsNullOrEmpty(key))
        {
            return Error.Validation(IdempotencyErrors.KeyRequired, "The Idempotency-Key header is required.");
        }

        if (!IdempotencyKeyInfo.IsValidKey(key))
        {
            return Error.Validation(IdempotencyErrors.KeyInvalid, "Idempotency-Key must be 16-64 characters of letters, digits, '-' or '_'.");
        }

        string route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? context.Request.Path.Value ?? "/";
        return new IdempotencyKeyInfo(key, context.Request.Method.ToUpperInvariant(), route);
    }
}
