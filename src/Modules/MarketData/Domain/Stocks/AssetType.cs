namespace SakerWatch.MarketData.Domain.Stocks;

/// <summary>What a listing is. Equities and ETFs are the supported kinds (ADR-0018); anything else a provider reports is kept as <see cref="Other"/>.</summary>
public enum AssetType
{
    /// <summary>A common or preferred stock.</summary>
    Equity,

    /// <summary>An exchange-traded fund.</summary>
    Etf,

    /// <summary>Another instrument type (depositary receipt, unit, and so on).</summary>
    Other,
}
