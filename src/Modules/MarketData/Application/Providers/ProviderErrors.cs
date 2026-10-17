using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.MarketData.Application.Providers;

/// <summary>How ingestion should react to a provider failure.</summary>
public enum ProviderFailureKind
{
    /// <summary>Anything not recognized; treated as permanent so it is never retried blindly.</summary>
    Permanent,

    /// <summary>The call budget is exhausted: wait (the error may carry how long) and try again within the budget.</summary>
    RateLimited,

    /// <summary>The provider or the network failed for now: retry with backoff.</summary>
    Transient,

    /// <summary>The plan does not include this market or data: retrying cannot help until the plan or the routing changes.</summary>
    PlanRestricted,

    /// <summary>The provider does not know the symbol.</summary>
    SymbolNotFound,
}

/// <summary>The failures an adapter reports, with stable codes the ingestion maps to retry, wait or give up.</summary>
public static class ProviderErrors
{
    /// <summary>Code for <see cref="ProviderFailureKind.RateLimited"/>.</summary>
    public const string RateLimitedCode = "marketdata.provider.rate_limited";

    /// <summary>Code for <see cref="ProviderFailureKind.Transient"/>.</summary>
    public const string UnavailableCode = "marketdata.provider.unavailable";

    /// <summary>Code for <see cref="ProviderFailureKind.PlanRestricted"/>.</summary>
    public const string PlanRestrictedCode = "marketdata.provider.plan_restricted";

    /// <summary>Code for <see cref="ProviderFailureKind.SymbolNotFound"/>.</summary>
    public const string SymbolNotFoundCode = "marketdata.provider.symbol_not_found";

    /// <summary>Code for a refused or missing credential (a permanent failure until the key is fixed).</summary>
    public const string UnauthorizedCode = "marketdata.provider.unauthorized";

    /// <summary>Code for a response that cannot be understood (a permanent failure).</summary>
    public const string InvalidResponseCode = "marketdata.provider.invalid_response";

    private const string _retryAfterKey = "retry_after_seconds";

    /// <summary>The call budget is used up; <paramref name="retryAfter"/> is how long the provider asked to wait, when it said.</summary>
    public static Error RateLimited(string provider, TimeSpan? retryAfter = null) =>
        new(RateLimitedCode, $"{provider} refused the call: the rate limit was reached.", ErrorType.Failure, RetryDetails(retryAfter));

    /// <summary>The provider or the network failed; retrying later may work.</summary>
    public static Error Unavailable(string provider) =>
        Error.Failure(UnavailableCode, $"{provider} is not reachable or answered with a server error.");

    /// <summary>The plan does not include what was asked.</summary>
    public static Error PlanRestricted(string provider) =>
        Error.Forbidden(PlanRestrictedCode, $"The {provider} plan does not include this market or data.");

    /// <summary>The symbol is unknown to the provider.</summary>
    public static Error SymbolNotFound(string provider, string symbol) =>
        Error.NotFound(SymbolNotFoundCode, $"{provider} does not know the symbol {symbol}.");

    /// <summary>The provider refused the credential; retrying cannot help until the key is fixed.</summary>
    public static Error Unauthorized(string provider) =>
        Error.Unauthorized(UnauthorizedCode, $"{provider} refused the API key.");

    /// <summary>The response is malformed or not what the contract says.</summary>
    public static Error InvalidResponse(string provider) =>
        Error.Failure(InvalidResponseCode, $"{provider} returned a response that could not be understood.");

    /// <summary>Maps an error to the reaction ingestion should have.</summary>
    public static ProviderFailureKind Classify(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Code switch
        {
            RateLimitedCode => ProviderFailureKind.RateLimited,
            UnavailableCode => ProviderFailureKind.Transient,
            PlanRestrictedCode => ProviderFailureKind.PlanRestricted,
            SymbolNotFoundCode => ProviderFailureKind.SymbolNotFound,
            _ => ProviderFailureKind.Permanent,
        };
    }

    /// <summary>How long the provider asked to wait, when the error carries it.</summary>
    public static TimeSpan? RetryAfter(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Details is not null
            && error.Details.TryGetValue(_retryAfterKey, out string[]? values)
            && values.Length == 1
            && double.TryParse(values[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds)
            && seconds >= 0
                ? TimeSpan.FromSeconds(seconds)
                : null;
    }

    private static Dictionary<string, string[]>? RetryDetails(TimeSpan? retryAfter) =>
        retryAfter is { } wait
            ? new Dictionary<string, string[]> { [_retryAfterKey] = [wait.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)] }
            : null;
}
