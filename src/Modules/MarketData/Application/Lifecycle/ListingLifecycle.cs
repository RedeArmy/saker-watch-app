using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Telemetry;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Lookup;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Application.Lifecycle;

/// <summary>What a rename of a ticker found in the store.</summary>
public enum RenameOutcome
{
    /// <summary>The stock now has the new ticker and the old one is in its history.</summary>
    Done,

    /// <summary>The stock does not have the old ticker any more (it was renamed already, or it is gone).</summary>
    StockNotFound,

    /// <summary>Another stock of the market already uses the new ticker.</summary>
    TickerTaken,

    /// <summary>Another stock already has the provider symbol the rename would give this one.</summary>
    SymbolTaken,
}

/// <summary>Writes the changes to the life of a listing (ADR-0039). Each call is one unit of work with the changes it implies.</summary>
public interface IListingLifecycleStore
{
    /// <summary>Marks the stock delisted at <paramref name="at"/> and records the corporate action. Returns false when it already was.</summary>
    Task<bool> DelistAsync(StockId stock, DateTimeOffset at, DateOnly on, string source, CancellationToken cancellationToken);

    /// <summary>Marks the stock as trading again and lets ingestion try it again. Returns false when it was not delisted.</summary>
    Task<bool> RelistAsync(StockId stock, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>
    /// Gives the stock a new ticker, keeps the old one in its history, moves the provider symbols that carried the old ticker to
    /// <paramref name="providerSymbol"/> and records the corporate action.
    /// </summary>
    Task<RenameOutcome> RenameAsync(TickerChange change, DateOnly on, string providerSymbol, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Counts <paramref name="day"/> as a day the provider did not know the symbol (once per day however many times it is tried) and returns the days in a row.</summary>
    Task<int> NoteSymbolNotFoundAsync(StockId stock, DateOnly day, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>
    /// How many of the active listings of the market of <paramref name="stock"/> are in a streak of days the provider did not know their symbol
    /// (the last of them <paramref name="day"/> or the day before), out of how many are active.
    /// </summary>
    Task<NotFoundShare> NotFoundShareAsync(StockId stock, DateOnly day, CancellationToken cancellationToken);
}

/// <summary>How many listings of a market the provider currently does not know.</summary>
/// <param name="InStreak">Active listings in a streak of unknown-symbol days that is still going.</param>
/// <param name="Active">Active listings of the market.</param>
public sealed record NotFoundShare(int InStreak, int Active);

/// <summary>A stock that changes its ticker within its market.</summary>
/// <param name="Stock">The stock; it keeps its identity.</param>
/// <param name="Market">The canonical market.</param>
/// <param name="Old">The ticker it has now.</param>
/// <param name="New">The ticker it will have.</param>
public sealed record TickerChange(StockId Stock, Mic Market, Ticker Old, Ticker New);

/// <summary>The outcome of a change to the life of a listing, for the operator.</summary>
/// <param name="Market">The canonical market.</param>
/// <param name="Ticker">The ticker the stock has after the change.</param>
/// <param name="Changed">False when the stock was already as asked and nothing was written.</param>
public sealed record LifecycleChange(string Market, string Ticker, bool Changed);

/// <summary>
/// The life of a listing (ADR-0039): it stops trading, comes back, or changes its ticker. The operator runs these by hand; ingestion runs the
/// first on its own when the provider has not known the symbol of a stock for several days in a row. History and watchlists are never touched:
/// they point at the stock, not at its ticker.
/// </summary>
public sealed partial class ListingLifecycle(
    IStockCatalog catalog,
    IListingLifecycleStore store,
    IOptions<IngestionOptions> options,
    TimeProvider clock,
    ILogger<ListingLifecycle> logger)
{
    /// <summary>Code of the failure for a stock the catalog does not have.</summary>
    public const string StockNotFoundCode = "marketdata.lifecycle.stock_not_found";

    /// <summary>Code of the failure when the new ticker is used by another stock of the market.</summary>
    public const string TickerTakenCode = "marketdata.lifecycle.ticker_taken";

    /// <summary>Code of the failure when another stock already has the provider symbol the rename would give this one.</summary>
    public const string SymbolTakenCode = "marketdata.lifecycle.symbol_taken";

    /// <summary>Code of the failure when the stock does not have the ticker it was asked to rename (it already changed).</summary>
    public const string TickerChangedCode = "marketdata.lifecycle.ticker_changed";

    /// <summary>Code of the failure when the old and the new ticker are the same.</summary>
    public const string SameTickerCode = "marketdata.lifecycle.same_ticker";

    /// <summary>Source recorded for a change the operator asked for.</summary>
    public const string OperatorSource = "operator";

    /// <summary>Source recorded for a delisting ingestion decided.</summary>
    public const string IngestionSource = "ingestion";

    private const string _delist = "delist";
    private const string _relist = "relist";
    private const string _rename = "rename";
    private const string _notFound = "not_found";
    private const string _rejected = "rejected";
    private const string _done = "done";
    private const string _unchanged = "unchanged";
    private const string _providerFault = "provider_fault";

    private static readonly Counter<long> _changes = SakerTelemetry.Meter.CreateCounter<long>(
        "sakerwatch_marketdata_listing_changes_total",
        description: "Changes to the life of a listing by operation (delist, relist, rename, not_found) and outcome (done, unchanged, rejected, waiting).");

    /// <summary>Marks the stock of <paramref name="market"/> and <paramref name="ticker"/> as delisted on <paramref name="on"/> (today in UTC when not given).</summary>
    public async Task<Result<LifecycleChange>> DelistAsync(string market, string ticker, DateOnly? on, CancellationToken cancellationToken)
    {
        Result<CatalogListing> found = await FindAsync(market, ticker, cancellationToken);
        if (!found.IsSuccess)
        {
            Count(_delist, _rejected);
            return found.Error!;
        }

        DateTimeOffset now = clock.GetUtcNow();
        bool changed = await store.DelistAsync(found.Value.Id, now, on ?? DateOnly.FromDateTime(now.UtcDateTime), OperatorSource, cancellationToken);
        Count(_delist, changed ? _done : _unchanged);
        if (changed)
        {
            LogDelisted(found.Value.Market.Value, found.Value.Ticker.Value, OperatorSource);
        }

        return new LifecycleChange(found.Value.Market.Value, found.Value.Ticker.Value, changed);
    }

    /// <summary>Brings a delisted stock back: it is active again and ingestion tries it from scratch.</summary>
    public async Task<Result<LifecycleChange>> RelistAsync(string market, string ticker, CancellationToken cancellationToken)
    {
        Result<CatalogListing> found = await FindAsync(market, ticker, cancellationToken);
        if (!found.IsSuccess)
        {
            Count(_relist, _rejected);
            return found.Error!;
        }

        bool changed = await store.RelistAsync(found.Value.Id, clock.GetUtcNow(), cancellationToken);
        Count(_relist, changed ? _done : _unchanged);
        if (changed)
        {
            LogRelisted(found.Value.Market.Value, found.Value.Ticker.Value);
        }

        return new LifecycleChange(found.Value.Market.Value, found.Value.Ticker.Value, changed);
    }

    /// <summary>
    /// Gives the stock a new ticker on <paramref name="on"/> (today in UTC when not given). The provider's own symbol becomes
    /// <paramref name="providerSymbol"/>, the new ticker when not given. The old ticker keeps finding the stock through its history.
    /// </summary>
    public async Task<Result<LifecycleChange>> RenameAsync(string market, string oldTicker, string newTicker, DateOnly? on, string? providerSymbol, CancellationToken cancellationToken)
    {
        Result<Ticker> fresh = Ticker.Create(newTicker);
        if (!fresh.IsSuccess)
        {
            Count(_rename, _rejected);
            return fresh.Error!;
        }

        Result<CatalogListing> found = await FindAsync(market, oldTicker, cancellationToken);
        if (!found.IsSuccess)
        {
            Count(_rename, _rejected);
            return found.Error!;
        }

        CatalogListing listing = found.Value;
        if (listing.Ticker == fresh.Value)
        {
            Count(_rename, _rejected);
            return Error.Conflict(SameTickerCode, $"{listing.Ticker} is already the ticker of the stock.");
        }

        Result<Ticker> old = Ticker.Create(oldTicker);
        if (!old.IsSuccess || listing.Ticker != old.Value)
        {
            Count(_rename, _rejected);
            return Error.Conflict(TickerChangedCode, $"The stock is {listing.Ticker} now, not {oldTicker}.");
        }

        DateTimeOffset now = clock.GetUtcNow();
        RenameOutcome outcome = await store.RenameAsync(
            new TickerChange(listing.Id, listing.Market, old.Value, fresh.Value), on ?? DateOnly.FromDateTime(now.UtcDateTime), (providerSymbol ?? fresh.Value.Value).Trim(), now, cancellationToken);
        Count(_rename, outcome == RenameOutcome.Done ? _done : _rejected);
        return outcome switch
        {
            RenameOutcome.Done => LogRenamedAndReturn(listing, fresh.Value),
            RenameOutcome.TickerTaken => Error.Conflict(TickerTakenCode, $"Another stock of {listing.Market} already uses the ticker {fresh.Value}."),
            RenameOutcome.SymbolTaken => Error.Conflict(SymbolTakenCode, "Another stock already has that provider symbol; give the one this stock has with --provider-symbol."),
            _ => Error.NotFound(StockNotFoundCode, $"{old.Value} is not in the catalog of {listing.Market}."),
        };
    }

    /// <summary>
    /// Counts <paramref name="day"/> as a day the provider did not know the symbol of the stock. When that has happened on enough days in a row
    /// (<see cref="IngestionOptions.DelistAfterNotFoundDays"/>) the stock is delisted. Returns true when this call delisted it.
    /// </summary>
    public async Task<bool> NoteSymbolNotFoundAsync(StockId stock, DateOnly day, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        int days = await store.NoteSymbolNotFoundAsync(stock, day, now, cancellationToken);
        if (days < options.Value.DelistAfterNotFoundDays)
        {
            Count(_notFound, "waiting");
            return false;
        }

        // When a large part of the market is "unknown" at once, a provider is having a bad time and the stocks have not all left (ADR-0044).
        NotFoundShare share = await store.NotFoundShareAsync(stock, day, cancellationToken);
        IngestionOptions settings = options.Value;
        if (share.Active >= settings.MarketWideMinListings && (double)share.InStreak / share.Active >= settings.MarketWideNotFoundShare)
        {
            Count(_notFound, _providerFault);
            LogProviderFault(share.InStreak, share.Active);
            return false;
        }

        bool changed = await store.DelistAsync(stock, now, day, IngestionSource, cancellationToken);
        Count(_notFound, changed ? _done : _unchanged);
        if (changed && await catalog.FindAsync(stock, cancellationToken) is { } listing)
        {
            LogDelisted(listing.Market.Value, listing.Ticker.Value, IngestionSource);
        }

        return changed;
    }

    private static void Count(string operation, string outcome) =>
        _changes.Add(1, new KeyValuePair<string, object?>("operation", operation), new KeyValuePair<string, object?>("outcome", outcome));

    private async Task<Result<CatalogListing>> FindAsync(string market, string ticker, CancellationToken cancellationToken)
    {
        Result<Mic> mic = Mic.Create(market);
        Result<Ticker> symbol = Ticker.Create(ticker);
        if (!mic.IsSuccess || !symbol.IsSuccess)
        {
            return (mic.IsSuccess ? symbol.Error : mic.Error)!;
        }

        return await catalog.FindByKeyAsync(mic.Value, symbol.Value, cancellationToken) is { } listing
            ? Result.Success(listing)
            : Error.NotFound(StockNotFoundCode, $"{symbol.Value} is not in the catalog of {mic.Value}.");
    }

    private Result<LifecycleChange> LogRenamedAndReturn(CatalogListing listing, Ticker fresh)
    {
        LogRenamed(listing.Market.Value, listing.Ticker.Value, fresh.Value);
        return new LifecycleChange(listing.Market.Value, fresh.Value, Changed: true);
    }

    [LoggerMessage(EventId = 2601, Level = LogLevel.Warning, Message = "The stock {Market} {Ticker} was marked delisted by {Source}")]
    private partial void LogDelisted(string market, string ticker, string source);

    [LoggerMessage(EventId = 2602, Level = LogLevel.Information, Message = "The stock {Market} {Ticker} is trading again; ingestion will try it from scratch")]
    private partial void LogRelisted(string market, string ticker);

    [LoggerMessage(EventId = 2603, Level = LogLevel.Information, Message = "The stock {Market} {OldTicker} now has the ticker {NewTicker}; the old one is kept in its history")]
    private partial void LogRenamed(string market, string oldTicker, string newTicker);

    [LoggerMessage(EventId = 2608, Level = LogLevel.Warning, Message = "{InStreak} of {Active} active listings of a market are unknown to the provider at once: that looks like a fault of the provider, so nothing is delisted automatically")]
    private partial void LogProviderFault(int inStreak, int active);
}
