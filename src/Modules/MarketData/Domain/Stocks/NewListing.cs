namespace SakerWatch.MarketData.Domain.Stocks;

/// <summary>
/// The raw, unvalidated description of a listing as a provider or a request gives it. <see cref="Stock.Create"/> validates and normalizes
/// each part and names the first one that is wrong, so callers pass text and never half-built value objects.
/// </summary>
/// <param name="Market">MIC of the market (<c>XNAS</c>).</param>
/// <param name="Ticker">Ticker on that market.</param>
/// <param name="Name">Instrument name.</param>
/// <param name="Currency">Trading currency code.</param>
/// <param name="TimeZoneId">IANA time zone of the market.</param>
/// <param name="AssetType">Kind of instrument.</param>
public sealed record NewListing(string? Market, string? Ticker, string? Name, string? Currency, string? TimeZoneId, AssetType AssetType);
