using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Providers;

namespace SakerWatch.MarketData.Application.Lookup;

/// <summary>
/// The listings a provider returns for a text (ADR-0032): from the cache when a copy is fresh, otherwise from the provider after spending one search
/// of the budget shared by every user and replica. Failures of the provider are told apart only as busy or unavailable; its own message never
/// reaches the caller or a log.
/// </summary>
public sealed partial class ProviderSearch(IProviderCallBudget budget, ISearchCache cache, IOptions<StockLookupOptions> lookup, ILogger<ProviderSearch> logger)
{
    private const string _searchSuffix = "_search";

    // What a provider answers is kept in Redis; a hostile or broken one cannot make it keep more than a page of candidates.
    private const int _maxCachedRows = 50;

    // The wait a user is told when the provider itself is rate limited: what the provider asked for, but never more than a few minutes.
    private const double _maxClientWaitSeconds = 300;

    /// <summary>Most candidates a search returns.</summary>
    public int MaxResults => lookup.Value.MaxResults;

    /// <summary>The listings for <paramref name="text"/> at <paramref name="provider"/>, or why they could not be had.</summary>
    public async Task<Result<IReadOnlyList<CachedListing>>> ListingsAsync(IMarketDataProvider provider, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        StockLookupOptions settings = lookup.Value;
        if (await cache.GetAsync(provider.Name, text, cancellationToken) is { } cached)
        {
            return Result.Success(cached);
        }

        BudgetDecision decision = await budget.TryAcquireAsync(
            provider.Name + _searchSuffix, new BudgetLimits(settings.SearchesPerMinute, settings.SearchesPerDay), 1, cancellationToken);
        if (!decision.Granted)
        {
            return Error.TooManyRequests(StockDirectory.SearchBusyCode, "Searches are busy. Try again in a moment.", (int)Math.Ceiling(decision.RetryAfter.TotalSeconds));
        }

        Result<IReadOnlyList<ProviderListing>> response = await provider.SearchAsync(text, cancellationToken);
        if (!response.IsSuccess)
        {
            LogProviderFailed(provider.Name, response.Error!.Code);
            return ProviderErrors.Classify(response.Error) == ProviderFailureKind.RateLimited
                ? Error.TooManyRequests(StockDirectory.SearchBusyCode, "Searches are busy. Try again in a moment.", (int)Math.Ceiling(Math.Min((ProviderErrors.RetryAfter(response.Error) ?? TimeSpan.FromSeconds(30)).TotalSeconds, _maxClientWaitSeconds)))
                : Error.Unavailable(StockDirectory.ProviderUnavailableCode, "The market data provider cannot answer for now. Try again later.", 30);
        }

        CachedListing[] listings = [.. response.Value.Take(_maxCachedRows).Select(listing => new CachedListing(listing.Key.ProviderMic, listing.Key.Symbol, listing.Name, listing.Currency, listing.AssetType))];
        if (settings.CacheMinutes > 0)
        {
            await cache.SetAsync(provider.Name, text, listings, TimeSpan.FromMinutes(settings.CacheMinutes), cancellationToken);
        }

        return listings;
    }

    [LoggerMessage(EventId = 2502, Level = LogLevel.Warning, Message = "The search at {Provider} failed with {Code}")]
    private partial void LogProviderFailed(string provider, string code);
}
