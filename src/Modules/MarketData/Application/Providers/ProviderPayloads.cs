namespace SakerWatch.MarketData.Application.Providers;

/// <summary>
/// The raw body of a successful provider response, kept so a parsing change or a later question ("what did the provider say that day?")
/// can be answered without calling it again (ADR-0004, ADR-0027). The body is untrusted text and stays text: it is never interpreted.
/// </summary>
/// <param name="Provider">Provider name.</param>
/// <param name="Operation">What was asked (<c>daily</c>, <c>search</c>).</param>
/// <param name="Key">The listing asked about, or <see langword="null"/> for a symbol search.</param>
/// <param name="Query">The symbol typed for a search, or the first and last day wanted for a series; free text for a human, not a key.</param>
/// <param name="Adjusted">Whether adjusted prices were requested, or <see langword="null"/> when it does not apply.</param>
/// <param name="Body">The response body exactly as received.</param>
/// <param name="FetchedAt">When the response arrived.</param>
public sealed record ProviderPayload(string Provider, string Operation, ProviderListingKey? Key, string Query, bool? Adjusted, string Body, DateTimeOffset FetchedAt);

/// <summary>
/// Keeps raw provider responses. Storing is best effort: a failure here must never fail the fetch, because the data was already obtained
/// and parsed. Writing the same request twice on the same day replaces the earlier document (a deterministic id), so a re-run is idempotent.
/// </summary>
public interface IProviderPayloadStore
{
    /// <summary>Stores or replaces the document for this request and day.</summary>
    Task StoreAsync(ProviderPayload payload, CancellationToken cancellationToken);
}

/// <summary>Stores nothing, for hosts and tests that do not keep raw responses.</summary>
public sealed class NoProviderPayloadStore : IProviderPayloadStore
{
    /// <inheritdoc />
    public Task StoreAsync(ProviderPayload payload, CancellationToken cancellationToken) => Task.CompletedTask;
}
