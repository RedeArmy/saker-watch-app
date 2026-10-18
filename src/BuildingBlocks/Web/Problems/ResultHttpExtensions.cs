using Microsoft.AspNetCore.Http;
using SakerWatch.BuildingBlocks.Application.Idempotency;
using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.BuildingBlocks.Web.Problems;

/// <summary>Maps <see cref="Result"/> values to HTTP responses (RFC 9457 ProblemDetails for failures).</summary>
public static class ResultHttpExtensions
{
    /// <summary>Base of the <c>type</c> URI of problem responses; the error code is appended.</summary>
    public const string ProblemTypeBase = "urn:sakerwatch:error:";

    /// <summary>HTTP status for an error category.</summary>
    public static int StatusFor(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.Unprocessable => StatusCodes.Status422UnprocessableEntity,
        ErrorType.TooManyRequests => StatusCodes.Status429TooManyRequests,
        ErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>204 on success, a problem response on failure.</summary>
    public static IResult ToHttpResult(this Result result) =>
        result.IsSuccess ? Results.NoContent() : result.Error!.ToProblem();

    /// <summary>Maps the success value with <paramref name="onSuccess"/>, or returns a problem response on failure.</summary>
    public static IResult ToHttpResult<T>(this Result<T> result, Func<T, IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        return result.IsSuccess ? onSuccess(result.Value) : result.Error!.ToProblem();
    }

    /// <summary>
    /// A ProblemDetails response for <paramref name="error"/>. Correlation and trace ids are added centrally by the
    /// problem-details customization, and unexpected failures never leak their message to the client.
    /// </summary>
    public static IResult ToProblem(this Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        int status = StatusFor(error.Type);
        var extensions = new Dictionary<string, object?> { ["code"] = error.Code };
        string? retryAfter = null;
        if (error.Details is { Count: > 0 })
        {
            // The wait travels in the Retry-After header and not as a field error.
            if (error.Details.TryGetValue(Error.RetryAfterSecondsKey, out string[]? seconds) && seconds.Length == 1)
            {
                retryAfter = seconds[0];
            }

            Dictionary<string, string[]> fields = error.Details.Where(pair => pair.Key != Error.RetryAfterSecondsKey).ToDictionary(pair => pair.Key, pair => pair.Value);
            if (fields.Count > 0)
            {
                extensions["errors"] = fields;
            }
        }

        IResult problem = Results.Problem(
            statusCode: status,
            title: error.Type == ErrorType.Failure ? "An unexpected error occurred." : error.Message,
            detail: error.Type == ErrorType.Failure ? null : error.Message,
            type: ProblemTypeBase + error.Code,
            extensions: extensions);

        // A duplicate that arrived while the first request is still running should retry shortly (ADR-0017).
        if (error.Code == IdempotencyErrors.InProgress)
        {
            return new HeaderResult(problem, "Retry-After", "1");
        }

        return retryAfter is not null ? new HeaderResult(problem, "Retry-After", retryAfter) : problem;
    }

    private sealed class HeaderResult(IResult inner, string name, string value) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers[name] = value;
            return inner.ExecuteAsync(httpContext);
        }
    }
}
