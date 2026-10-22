// Persistence rows: columns documented in docs/data-model.md.
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

namespace SakerWatch.MarketData.Infrastructure.Persistence;

/// <summary>
/// <c>marketdata.stocks</c>: the shared catalog, one row per listing, unique on <c>(market_mic, ticker)</c>. Not user-owned, so no
/// row-level security; the worker writes it and the API only reads it.
/// </summary>
public sealed class StockRow
{
    public Guid Id { get; set; }

    public string MarketMic { get; set; } = string.Empty;

    public string Ticker { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Currency { get; set; } = string.Empty;

    public string TimeZone { get; set; } = string.Empty;

    public string AssetType { get; set; } = AssetTypes.Equity;

    public bool IsActive { get; set; } = true;

    public DateTimeOffset ListedAt { get; set; }

    /// <summary>When the stock stopped trading on its market (ADR-0039); null while it trades. A delisted stock is also inactive.</summary>
    public DateTimeOffset? DelistedAt { get; set; }
}

/// <summary>
/// <c>marketdata.ticker_history</c>: a ticker a stock used to have (ADR-0039). A lookup by a retired ticker finds the stock it belonged to, so
/// watchlists and links keep working after a symbol change; a ticker in use by a stock always wins over its history.
/// </summary>
public sealed class TickerHistoryRow
{
    public string MarketMic { get; set; } = string.Empty;

    public string Ticker { get; set; } = string.Empty;

    public Guid StockId { get; set; }

    /// <summary>The day the stock stopped using this ticker.</summary>
    public DateOnly ChangedOn { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Allowed values of <see cref="StockRow.AssetType"/>.</summary>
public static class AssetTypes
{
    public const string Equity = "equity";

    public const string Etf = "etf";

    public const string Other = "other";
}

/// <summary>
/// <c>marketdata.symbol_aliases</c>: how a provider names a listing (Twelve Data calls Apple <c>AAPL</c> on <c>XNGS</c>, the Nasdaq Global
/// Select segment of <c>XNAS</c>). Lets the catalog keep one canonical <c>(MIC, ticker)</c> whatever the provider says (ADR-0018, ADR-0027).
/// </summary>
public sealed class SymbolAliasRow
{
    public string Provider { get; set; } = string.Empty;

    public string ProviderMic { get; set; } = string.Empty;

    public string ProviderSymbol { get; set; } = string.Empty;

    public Guid StockId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// <c>marketdata.corporate_actions</c>: splits, cash dividends, symbol changes and delistings per listing, unique per kind and effective date
/// so a re-run of the ingestion is a no-op. A split carries the ratio (new shares per old share); a dividend carries the amount and its currency.
/// </summary>
public sealed class CorporateActionRow
{
    public Guid Id { get; set; }

    public Guid StockId { get; set; }

    public string Kind { get; set; } = string.Empty;

    public DateOnly EffectiveDate { get; set; }

    public decimal? SplitRatio { get; set; }

    public decimal? CashAmount { get; set; }

    public string? Currency { get; set; }

    public string Source { get; set; } = string.Empty;

    public DateTimeOffset IngestedAt { get; set; }
}

/// <summary>Allowed values of <see cref="CorporateActionRow.Kind"/>.</summary>
public static class CorporateActionKinds
{
    public const string Split = "split";

    public const string Dividend = "dividend";

    public const string SymbolChange = "symbol_change";

    public const string Delisting = "delisting";
}

/// <summary>
/// <c>marketdata.price_daily</c>, a Timescale hypertable partitioned by <c>trade_date</c>: one row per listing and trading day with the raw bar,
/// the adjusted close, where it came from and whether the day is final. Unique on <c>(stock_id, trade_date)</c>, so ingestion is an upsert.
/// </summary>
public sealed class PriceDailyRow
{
    public Guid StockId { get; set; }

    public DateOnly TradeDate { get; set; }

    public decimal Open { get; set; }

    public decimal High { get; set; }

    public decimal Low { get; set; }

    public decimal Close { get; set; }

    public decimal AdjustedClose { get; set; }

    public long Volume { get; set; }

    public string Source { get; set; } = string.Empty;

    public bool IsFinal { get; set; }

    public DateTimeOffset IngestedAt { get; set; }
}

/// <summary>
/// <c>marketdata.ingestion_state</c>: one row per listing saying whether ingestion works for it. A permanent failure (unknown symbol, plan, missing
/// alias or calendar) is recorded with its stable code, so the listing is not retried blindly and a person can see why prices are missing.
/// </summary>
public sealed class IngestionStateRow
{
    public Guid StockId { get; set; }

    public string Status { get; set; } = IngestionStatuses.Pending;

    public DateTimeOffset? LastSuccessAt { get; set; }

    public DateTimeOffset? LastAttemptAt { get; set; }

    public string? FailureCode { get; set; }

    /// <summary>On how many different days in a row the provider did not know the symbol; reset by any success (ADR-0039).</summary>
    public int NotFoundDays { get; set; }

    /// <summary>The last day the provider did not know the symbol, so one day counts once however many times it is tried.</summary>
    public DateOnly? LastNotFoundOn { get; set; }
}

/// <summary>Allowed values of <see cref="IngestionStateRow.Status"/>.</summary>
public static class IngestionStatuses
{
    public const string Pending = "pending";

    public const string Ok = "ok";

    public const string Failed = "failed";
}

/// <summary>
/// <c>marketdata.fx_rates</c>: one row per currency pair, day and source with the rate (units of the quote currency for one of the base).
/// Unique on <c>(base, quote, as_of_date, source)</c>, so ingestion is an upsert.
/// </summary>
public sealed class FxRateRow
{
    public string Base { get; set; } = string.Empty;

    public string Quote { get; set; } = string.Empty;

    public DateOnly AsOfDate { get; set; }

    public string Source { get; set; } = string.Empty;

    public decimal Rate { get; set; }

    public DateTimeOffset IngestedAt { get; set; }
}

/// <summary>
/// A daily exchange rate the guard did not believe (ADR-0042): kept apart with the reason and never served. One row per pair, day and source; a
/// new refusal of the same day replaces the previous one.
/// </summary>
public sealed class FxRateRejectedRow
{
    public string Base { get; set; } = string.Empty;

    public string Quote { get; set; } = string.Empty;

    public DateOnly AsOfDate { get; set; }

    public string Source { get; set; } = string.Empty;

    public decimal Rate { get; set; }

    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset SeenAt { get; set; }
}
