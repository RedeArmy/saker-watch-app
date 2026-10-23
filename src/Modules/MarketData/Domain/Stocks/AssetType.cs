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

/// <summary>
/// The text of an <see cref="AssetType"/> in the database and in the contracts: the one place that maps it either way, so a new kind is added here and a place that
/// forgot it cannot show it as another.
/// </summary>
public static class AssetTypeNames
{
    /// <summary>The text of the kind: <c>equity</c>, <c>etf</c> or <c>other</c>.</summary>
    public static string ToText(this AssetType type) => type switch
    {
        AssetType.Equity => "equity",
        AssetType.Etf => "etf",
        _ => "other",
    };

    /// <summary>The kind of a stored text; anything that is not <c>equity</c> or <c>etf</c> is <see cref="AssetType.Other"/>.</summary>
    public static AssetType Parse(string? text) => text switch
    {
        "equity" => AssetType.Equity,
        "etf" => AssetType.Etf,
        _ => AssetType.Other,
    };
}
