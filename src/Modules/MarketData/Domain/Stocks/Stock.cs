using SakerWatch.BuildingBlocks.Kernel.Domain;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Domain.Markets;

namespace SakerWatch.MarketData.Domain.Stocks;

/// <summary>
/// A listing of an instrument on one market, shared by every user (ADR-0018). Its natural key is <c>(Mic, Ticker)</c>; the trading currency
/// is the native currency of the market, so prices are never converted when stored. The catalog is filled on demand when a user adds the
/// ticker to the watchlist.
/// </summary>
public sealed class Stock : AggregateRoot<StockId>
{
    /// <summary>Longest accepted instrument name.</summary>
    public const int MaxNameLength = 200;

    // The constructor takes the already validated parts; Create is the only way in, so the parameter count stays here and not in the API.
    private Stock(StockId id, Mic market, Ticker ticker, string name, CurrencyCode currency, string timeZoneId, AssetType assetType, DateTimeOffset listedAt)
        : base(id)
    {
        Market = market;
        Ticker = ticker;
        Name = name;
        Currency = currency;
        TimeZoneId = timeZoneId;
        AssetType = assetType;
        IsActive = true;
        ListedAt = listedAt;
    }

    /// <summary>The market the instrument trades on.</summary>
    public Mic Market { get; }

    /// <summary>The ticker on that market.</summary>
    public Ticker Ticker { get; }

    /// <summary>The instrument name as the provider reports it, trimmed.</summary>
    public string Name { get; private set; }

    /// <summary>The currency the instrument trades in.</summary>
    public CurrencyCode Currency { get; }

    /// <summary>The IANA time zone of the market, which decides what "the close of day D" means.</summary>
    public string TimeZoneId { get; }

    /// <summary>The kind of instrument.</summary>
    public AssetType AssetType { get; }

    /// <summary>False once the instrument is delisted; its history is kept until the retention policy removes it.</summary>
    public bool IsActive { get; private set; }

    /// <summary>When the instrument stopped trading on its market (ADR-0039); <see langword="null"/> while it trades.</summary>
    public DateTimeOffset? DelistedAt { get; private set; }

    /// <summary>When the listing entered the catalog.</summary>
    public DateTimeOffset ListedAt { get; }

    /// <summary>Validates the parts of <paramref name="listing"/> and creates an active listing.</summary>
    public static Result<Stock> Create(StockId id, NewListing listing, DateTimeOffset listedAt)
    {
        ArgumentNullException.ThrowIfNull(listing);

        Result<Mic> mic = Mic.Create(listing.Market);
        if (!mic.IsSuccess)
        {
            return mic.Error!;
        }

        Result<Ticker> symbol = Ticker.Create(listing.Ticker);
        if (!symbol.IsSuccess)
        {
            return symbol.Error!;
        }

        Result<CurrencyCode> code = CurrencyCode.Create(listing.Currency);
        if (!code.IsSuccess)
        {
            return code.Error!;
        }

        string? trimmedName = NormalizeName(listing.Name);
        if (trimmedName is null)
        {
            return Error.Validation("marketdata.stock.name_invalid", $"The name has 1 to {MaxNameLength} characters.");
        }

        if (!IsKnownTimeZone(listing.TimeZoneId))
        {
            return Error.Validation("marketdata.stock.time_zone_invalid", "The time zone is not a known IANA time zone.");
        }

        return new Stock(id, mic.Value, symbol.Value, trimmedName, code.Value, listing.TimeZoneId!.Trim(), listing.AssetType, listedAt);
    }

    /// <summary>Takes the provider's current name for the instrument (companies rename); an empty or oversized name is refused.</summary>
    public Result Rename(string? name)
    {
        string? trimmed = NormalizeName(name);
        if (trimmed is null)
        {
            return Error.Validation("marketdata.stock.name_invalid", $"The name has 1 to {MaxNameLength} characters.");
        }

        Name = trimmed;
        return Result.Success();
    }

    /// <summary>
    /// Marks the instrument as delisted at <paramref name="at"/> (ADR-0039): it is no longer active, so ingestion leaves it alone, and its history is
    /// kept. Idempotent: delisting again keeps the first moment.
    /// </summary>
    public void Delist(DateTimeOffset at)
    {
        IsActive = false;
        DelistedAt ??= at;
    }

    /// <summary>Marks the instrument as listed again (a relisting or a wrong delisting). Idempotent.</summary>
    public void Relist()
    {
        IsActive = true;
        DelistedAt = null;
    }

    private static string? NormalizeName(string? name)
    {
        // The name is the provider's text, kept once for every user: control, bidirectional and zero-width characters and markup are dropped.
        string trimmed = string.Concat((name ?? string.Empty).Where(IsNameCharacter)).Trim();
        return trimmed.Length is >= 1 and <= MaxNameLength ? trimmed : null;
    }

    private static bool IsNameCharacter(char c) =>
        c is not ('<' or '>')
        && char.GetUnicodeCategory(c) is not (System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
            or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator
            or System.Globalization.UnicodeCategory.OtherNotAssigned);

    private static bool IsKnownTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return false;
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }
}
