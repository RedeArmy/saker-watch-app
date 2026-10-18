using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Telemetry;
using SakerWatch.BuildingBlocks.Kernel.Time;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Contracts;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Application.Lookup;

/// <summary>
/// <see cref="IStockDirectory"/> over the catalog and the providers (ADR-0032). A search or a registration spends one search of a budget shared
/// by every user and replica before it calls the provider, and a copy of the answer is kept for a few minutes. A stock enters the catalog only
/// from what the provider returned for the exact market and ticker, with the time zone of our own calendar and the market's own currency, never
/// from the text a user typed.
/// </summary>
public sealed partial class StockDirectory(
    IStockCatalog catalog,
    IStockRegistry registry,
    IMarketDataProviderRouter router,
    ExchangeCalendars calendars,
    IProviderCallBudget budget,
    ISearchCache cache,
    IJobEnqueuer jobs,
    IIdGenerator ids,
    IOptions<StockLookupOptions> lookup,
    IOptions<IngestionOptions> ingestion,
    TimeProvider clock,
    ILogger<StockDirectory> logger) : IStockDirectory
{
    /// <summary>Code of the failure for a query that is not plain text.</summary>
    public const string QueryInvalidCode = "marketdata.search.query_invalid";

    /// <summary>Code of the failure when the market has no provider or no calendar here.</summary>
    public const string MarketNotAvailableCode = "marketdata.stock.market_not_available";

    /// <summary>Code of the failure when the provider does not know the exact market and ticker.</summary>
    public const string StockNotFoundCode = "marketdata.stock.not_found";

    /// <summary>Code of the failure when the shared search budget is spent.</summary>
    public const string SearchBusyCode = "marketdata.search.busy";

    /// <summary>Code of the failure when the provider cannot answer for now.</summary>
    public const string ProviderUnavailableCode = "marketdata.search.provider_unavailable";

    /// <summary>Longest accepted query.</summary>
    public const int MaxQueryLength = 40;

    private const string _searchSuffix = "_search";

    private static readonly Counter<long> _lookups = SakerTelemetry.Meter.CreateCounter<long>(
        "sakerwatch_marketdata_stock_lookups_total",
        description: "Stock searches and registrations by operation and outcome.");

    // Letters, digits, spaces and the few marks tickers and names use. The text goes to a provider as a URL parameter and to a cache as a key.
    [GeneratedRegex(@"^[\p{L}\p{N} .&_\-]{1,40}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex QueryPattern();

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<StockCandidate>>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        using Activity? span = SakerTelemetry.Source.StartActivity("MarketData.Stock.Search");
        string text = (query ?? string.Empty).Trim();
        if (!QueryPattern().IsMatch(text))
        {
            Count("search", "invalid");
            return Error.Validation(QueryInvalidCode, $"A search has 1 to {MaxQueryLength} letters, digits, spaces, '.', '&', '-' or '_'.");
        }

        var candidates = new Dictionary<(string, string), StockCandidate>();
        foreach (IMarketDataProvider provider in EnabledProviders())
        {
            Result<IReadOnlyList<CachedListing>> found = await ListingsAsync(provider, text, cancellationToken);
            if (!found.IsSuccess)
            {
                Count("search", Outcome(found.Error!));
                return found.Error!;
            }

            foreach (CachedListing listing in found.Value)
            {
                if (ToCandidate(listing) is { } candidate)
                {
                    candidates.TryAdd((candidate.Market, candidate.Ticker), candidate);
                }
            }
        }

        string upper = text.ToUpperInvariant();
        StockCandidate[] ordered =
        [
            .. candidates.Values
                .OrderBy(candidate => Rank(candidate.Ticker, upper))
                .ThenBy(candidate => candidate.Ticker, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Market, StringComparer.Ordinal)
                .Take(lookup.Value.MaxResults),
        ];
        Count("search", "success");
        return ordered;
    }

    /// <inheritdoc />
    public async Task<Result<StockSummary>> FindAsync(string market, string ticker, CancellationToken cancellationToken)
    {
        Result<Mic> mic = Mic.Create(market);
        if (!mic.IsSuccess)
        {
            return mic.Error!;
        }

        Result<Ticker> symbol = Ticker.Create(ticker);
        if (!symbol.IsSuccess)
        {
            return symbol.Error!;
        }

        return await catalog.FindByKeyAsync(mic.Value, symbol.Value, cancellationToken) is { } found
            ? Summary(found)
            : Error.NotFound(StockNotFoundCode, $"{symbol.Value} is not in the catalog of {mic.Value}.");
    }

    /// <inheritdoc />
    public async Task<Result<StockSummary>> EnsureAsync(string market, string ticker, CancellationToken cancellationToken)
    {
        using Activity? span = SakerTelemetry.Source.StartActivity("MarketData.Stock.Ensure");
        Result<Mic> mic = Mic.Create(market);
        if (!mic.IsSuccess)
        {
            return mic.Error!;
        }

        Result<Ticker> symbol = Ticker.Create(ticker);
        if (!symbol.IsSuccess)
        {
            return symbol.Error!;
        }

        if (await catalog.FindByKeyAsync(mic.Value, symbol.Value, cancellationToken) is { } existing)
        {
            Count("ensure", "catalogued");
            return Summary(existing);
        }

        Result<StockSummary> registered = await RegisterAsync(mic.Value, symbol.Value, cancellationToken);
        Count("ensure", registered.IsSuccess ? "registered" : Outcome(registered.Error!));
        return registered;
    }

    // An exact ticker first, then the tickers that start with the text, then the rest.
    private static int Rank(string ticker, string upper)
    {
        if (ticker == upper)
        {
            return 0;
        }

        return ticker.StartsWith(upper, StringComparison.Ordinal) ? 1 : 2;
    }

    private static string Outcome(Error error) => error.Code switch
    {
        SearchBusyCode => "busy",
        ProviderUnavailableCode => "unavailable",
        StockNotFoundCode => "not_found",
        MarketNotAvailableCode => "market_not_available",
        _ => "invalid",
    };

    private static void Count(string operation, string outcome) =>
        _lookups.Add(1, new KeyValuePair<string, object?>("operation", operation), new KeyValuePair<string, object?>("outcome", outcome));

    private static StockSummary Summary(CatalogListing listing) =>
        new(listing.Id.Value, listing.Market.Value, listing.Ticker.Value, listing.Name, listing.Currency.Value, AssetTypeText(listing.AssetType));

    private static string AssetTypeText(AssetType type) => type switch
    {
        AssetType.Equity => "equity",
        AssetType.Etf => "etf",
        _ => "other",
    };

    // The providers that serve at least one enabled market, each once (one provider usually serves several markets).
    private List<IMarketDataProvider> EnabledProviders()
    {
        var providers = new Dictionary<string, IMarketDataProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (Mic market in router.EnabledMarkets)
        {
            Result<IMarketDataProvider> provider = router.For(market);
            if (provider.IsSuccess)
            {
                providers.TryAdd(provider.Value.Name, provider.Value);
            }
        }

        return [.. providers.Values];
    }

    // The listing as a candidate, or null when it is not something the catalog accepts: a market that is not enabled or has no calendar, a
    // ticker or a name that is not valid, or a currency that is not the market's own.
    private StockCandidate? ToCandidate(CachedListing listing)
    {
        string canonical = MarketCatalogRules.CanonicalMarket(listing.ProviderMic);
        if (Mic.Create(canonical) is not { IsSuccess: true } mic || !IsServed(mic.Value))
        {
            return null;
        }

        Result<Stock> stock = BuildStock(listing, mic.Value, StockId.Create(Guid.Empty), clock.GetUtcNow());
        return stock.IsSuccess
            ? new StockCandidate(stock.Value.Market.Value, stock.Value.Ticker.Value, stock.Value.Name, stock.Value.Currency.Value, AssetTypeText(stock.Value.AssetType))
            : null;
    }

    private bool IsServed(Mic market) => router.EnabledMarkets.Contains(market) && calendars.TryGet(market).IsSuccess;

    // The domain validates every part; the time zone is ours, and the currency must be the one the market trades in.
    private Result<Stock> BuildStock(CachedListing listing, Mic market, StockId id, DateTimeOffset now)
    {
        CurrencyCode? expected = MarketCatalogRules.ExpectedCurrency(market);
        string? currency = listing.Currency?.Trim().ToUpperInvariant();
        if (expected is null || currency != expected.Value)
        {
            return Error.Validation("marketdata.stock.currency_mismatch", "The listing does not trade in the currency of its market.");
        }

        string timeZone = calendars.TryGet(market).Value.TimeZoneId;
        return Stock.Create(id, new NewListing(market.Value, listing.Symbol, listing.Name, currency, timeZone, listing.AssetType), now);
    }

    private async Task<Result<StockSummary>> RegisterAsync(Mic market, Ticker ticker, CancellationToken cancellationToken)
    {
        if (!IsServed(market))
        {
            return Error.Validation(MarketNotAvailableCode, $"The market {market} is not available.");
        }

        IMarketDataProvider provider = router.For(market).Value;
        Result<IReadOnlyList<CachedListing>> found = await ListingsAsync(provider, ticker.Value, cancellationToken);
        if (!found.IsSuccess)
        {
            return found.Error!;
        }

        CachedListing? match = found.Value.FirstOrDefault(listing =>
            MarketCatalogRules.CanonicalMarket(listing.ProviderMic) == market.Value
            && string.Equals(listing.Symbol?.Trim(), ticker.Value, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return Error.NotFound(StockNotFoundCode, $"{ticker} was not found on {market}.");
        }

        DateTimeOffset now = clock.GetUtcNow();
        Result<Stock> stock = BuildStock(match, market, ids.New<StockId>(), now);
        if (!stock.IsSuccess)
        {
            return Error.NotFound(StockNotFoundCode, $"{ticker} was not found on {market}.");
        }

        StockId id = await registry.RegisterAsync(stock.Value, provider.Name, new ProviderListingKey(match.ProviderMic, match.Symbol), now, cancellationToken);
        await jobs.EnqueueAsync(
            BackfillStockJob.KeyFor(id),
            new BackfillStockJob(id.Value),
            new JobEnqueueOptions(MaxAttempts: ingestion.Value.BackfillMaxAttempts),
            cancellationToken);
        LogRegistered(market.Value);
        return new StockSummary(id.Value, stock.Value.Market.Value, stock.Value.Ticker.Value, stock.Value.Name, stock.Value.Currency.Value, AssetTypeText(stock.Value.AssetType));
    }

    // The listings the provider returns for the text: from the cache when a copy is fresh, otherwise from the provider after spending one search of
    // the shared budget. Failures of the provider are told apart only as busy or unavailable; its own message never reaches the caller.
    private async Task<Result<IReadOnlyList<CachedListing>>> ListingsAsync(IMarketDataProvider provider, string text, CancellationToken cancellationToken)
    {
        StockLookupOptions settings = lookup.Value;
        if (await cache.GetAsync(provider.Name, text, cancellationToken) is { } cached)
        {
            return Result.Success(cached);
        }

        BudgetDecision decision = await budget.TryAcquireAsync(
            provider.Name + _searchSuffix, new BudgetLimits(settings.SearchesPerMinute, settings.SearchesPerDay), 1, cancellationToken);
        if (!decision.Granted)
        {
            return Error.TooManyRequests(SearchBusyCode, "Searches are busy. Try again in a moment.", (int)Math.Ceiling(decision.RetryAfter.TotalSeconds));
        }

        Result<IReadOnlyList<ProviderListing>> response = await provider.SearchAsync(text, cancellationToken);
        if (!response.IsSuccess)
        {
            LogProviderFailed(provider.Name, response.Error!.Code);
            return ProviderErrors.Classify(response.Error) == ProviderFailureKind.RateLimited
                ? Error.TooManyRequests(SearchBusyCode, "Searches are busy. Try again in a moment.", (int)Math.Ceiling((ProviderErrors.RetryAfter(response.Error) ?? TimeSpan.FromSeconds(30)).TotalSeconds))
                : Error.Unavailable(ProviderUnavailableCode, "The market data provider cannot answer for now. Try again later.", 30);
        }

        CachedListing[] listings = [.. response.Value.Select(listing => new CachedListing(listing.Key.ProviderMic, listing.Key.Symbol, listing.Name, listing.Currency, listing.AssetType))];
        if (settings.CacheMinutes > 0)
        {
            await cache.SetAsync(provider.Name, text, listings, TimeSpan.FromMinutes(settings.CacheMinutes), cancellationToken);
        }

        return listings;
    }

    [LoggerMessage(EventId = 2501, Level = LogLevel.Information, Message = "A stock of {Market} was registered in the catalog and its history queued")]
    private partial void LogRegistered(string market);

    [LoggerMessage(EventId = 2502, Level = LogLevel.Warning, Message = "The search at {Provider} failed with {Code}")]
    private partial void LogProviderFailed(string provider, string code);
}
