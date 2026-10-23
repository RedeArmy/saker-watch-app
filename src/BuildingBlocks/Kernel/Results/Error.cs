namespace SakerWatch.BuildingBlocks.Kernel.Results;

/// <summary>Category of an expected failure; the web layer maps each category to an HTTP status.</summary>
public enum ErrorType
{
    /// <summary>Unexpected condition that is not the caller's fault (maps to 500).</summary>
    Failure,

    /// <summary>The request is malformed or breaks a validation rule (maps to 400).</summary>
    Validation,

    /// <summary>The caller is not authenticated (maps to 401).</summary>
    Unauthorized,

    /// <summary>The caller is authenticated but not allowed (maps to 403).</summary>
    Forbidden,

    /// <summary>The requested resource does not exist or is not visible to the caller (maps to 404).</summary>
    NotFound,

    /// <summary>The request conflicts with current state (maps to 409).</summary>
    Conflict,

    /// <summary>The request is well-formed but cannot be processed, for example a reused idempotency key (maps to 422).</summary>
    Unprocessable,

    /// <summary>The caller asked too often, or a shared budget behind the request is spent; trying again later may work (maps to 429).</summary>
    TooManyRequests,

    /// <summary>A service this request depends on is not reachable for now; trying again later may work (maps to 503).</summary>
    Unavailable,
}

/// <summary>
/// An expected failure returned through <see cref="Result"/> instead of an exception.
/// <paramref name="Code"/> is a stable, machine-readable identifier (for example <c>watchlist.item_exists</c>).
/// </summary>
/// <param name="Code">Stable identifier clients and tests can rely on.</param>
/// <param name="Message">Human-readable explanation; never contains secrets or personal data.</param>
/// <param name="Type">Failure category.</param>
/// <param name="Details">Optional field-level problems keyed by field name (validation only).</param>
/// <param name="Extensions">Optional data the caller needs to act on the failure, sent as members of the problem (for example what a confirmation has to name).
/// Never secrets or personal data of anyone else; the names <c>code</c>, <c>errors</c>, <c>correlationId</c> and <c>traceId</c> are the response's own.</param>
public sealed record Error(
    string Code,
    string Message,
    ErrorType Type,
    IReadOnlyDictionary<string, string[]>? Details = null,
    IReadOnlyDictionary<string, object?>? Extensions = null)
{
    /// <summary>Creates an <see cref="ErrorType.Failure"/> error.</summary>
    public static Error Failure(string code, string message) => new(code, message, ErrorType.Failure);

    /// <summary>Creates a <see cref="ErrorType.Validation"/> error, optionally with per-field details.</summary>
    public static Error Validation(string code, string message, IReadOnlyDictionary<string, string[]>? details = null) =>
        new(code, message, ErrorType.Validation, details);

    /// <summary>Creates an <see cref="ErrorType.Unauthorized"/> error.</summary>
    public static Error Unauthorized(string code, string message) => new(code, message, ErrorType.Unauthorized);

    /// <summary>Creates a <see cref="ErrorType.Forbidden"/> error.</summary>
    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);

    /// <summary>Creates a <see cref="ErrorType.NotFound"/> error.</summary>
    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    /// <summary>Creates a <see cref="ErrorType.Conflict"/> error, optionally with data the caller needs to resolve it.</summary>
    public static Error Conflict(string code, string message, IReadOnlyDictionary<string, object?>? extensions = null) =>
        new(code, message, ErrorType.Conflict, null, extensions);

    /// <summary>Creates an <see cref="ErrorType.Unprocessable"/> error.</summary>
    public static Error Unprocessable(string code, string message) => new(code, message, ErrorType.Unprocessable);

    /// <summary>Creates an <see cref="ErrorType.TooManyRequests"/> error; <paramref name="retryAfterSeconds"/> tells the client how long to wait, when known.</summary>
    public static Error TooManyRequests(string code, string message, int? retryAfterSeconds = null) =>
        new(code, message, ErrorType.TooManyRequests, RetryDetails(retryAfterSeconds));

    /// <summary>Creates an <see cref="ErrorType.Unavailable"/> error; <paramref name="retryAfterSeconds"/> tells the client how long to wait, when known.</summary>
    public static Error Unavailable(string code, string message, int? retryAfterSeconds = null) =>
        new(code, message, ErrorType.Unavailable, RetryDetails(retryAfterSeconds));

    /// <summary>Key of the detail that carries the wait, in seconds, for the <c>Retry-After</c> header.</summary>
    public const string RetryAfterSecondsKey = "retry_after_seconds";

    private static Dictionary<string, string[]>? RetryDetails(int? seconds) =>
        seconds is > 0 ? new Dictionary<string, string[]> { [RetryAfterSecondsKey] = [seconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)] } : null;
}
