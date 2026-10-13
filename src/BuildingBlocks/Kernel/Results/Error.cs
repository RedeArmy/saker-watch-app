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
}

/// <summary>
/// An expected failure returned through <see cref="Result"/> instead of an exception.
/// <paramref name="Code"/> is a stable, machine-readable identifier (for example <c>watchlist.item_exists</c>).
/// </summary>
/// <param name="Code">Stable identifier clients and tests can rely on.</param>
/// <param name="Message">Human-readable explanation; never contains secrets or personal data.</param>
/// <param name="Type">Failure category.</param>
/// <param name="Details">Optional field-level problems keyed by field name (validation only).</param>
public sealed record Error(
    string Code,
    string Message,
    ErrorType Type,
    IReadOnlyDictionary<string, string[]>? Details = null)
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

    /// <summary>Creates a <see cref="ErrorType.Conflict"/> error.</summary>
    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);

    /// <summary>Creates an <see cref="ErrorType.Unprocessable"/> error.</summary>
    public static Error Unprocessable(string code, string message) => new(code, message, ErrorType.Unprocessable);
}
