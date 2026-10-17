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
