using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace SakerWatch.BuildingBlocks.Web.Http;

/// <summary>
/// A JSON response with an <c>ETag</c> that honours <c>If-None-Match</c> (RFC 9110): the tag is a hash of the exact bytes sent, so it changes when
/// and only when the body does, and a client that already holds that body gets a <c>304</c> with no body. The response is private and must be
/// revalidated, because it belongs to the caller. The work to build the body still runs; the saving is the transfer.
/// </summary>
public static class ConditionalResults
{
    /// <summary>Serializes <paramref name="body"/> with the API's JSON options and answers 200 with its ETag, or 304 when the caller's tag matches.</summary>
    public static IResult Json<T>(T body) => new ConditionalJsonResult<T>(body);

    /// <summary>The ETag of <paramref name="bytes"/>: 128 bits of their SHA-256, quoted.</summary>
    public static string ETagOf(ReadOnlySpan<byte> bytes) => $"\"{Convert.ToHexString(SHA256.HashData(bytes))[..32].ToLowerInvariant()}\"";

    /// <summary>True when the <c>If-None-Match</c> value lists <paramref name="etag"/> (weak or strong) or is <c>*</c>.</summary>
    public static bool Matches(string? ifNoneMatch, string etag)
    {
        if (string.IsNullOrWhiteSpace(ifNoneMatch))
        {
            return false;
        }

        foreach (string candidate in ifNoneMatch.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string tag = candidate.StartsWith("W/", StringComparison.Ordinal) ? candidate[2..] : candidate;
            if (tag == "*" || tag == etag)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class ConditionalJsonResult<T>(T body) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            JsonSerializerOptions options = httpContext.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body, options);
            string etag = ETagOf(bytes);

            httpContext.Response.Headers.ETag = etag;
            httpContext.Response.Headers.CacheControl = "private, no-cache";
            httpContext.Response.Headers.Append(HeaderNames.Vary, HeaderNames.Authorization);

            if (HttpMethods.IsGet(httpContext.Request.Method) && Matches(httpContext.Request.Headers.IfNoneMatch.ToString(), etag))
            {
                httpContext.Response.StatusCode = StatusCodes.Status304NotModified;
                return;
            }

            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            httpContext.Response.ContentType = "application/json; charset=utf-8";
            httpContext.Response.ContentLength = bytes.Length;
            await httpContext.Response.Body.WriteAsync(bytes, httpContext.RequestAborted);
        }
    }
}
