using SakerWatch.MarketData.Domain.Prices;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Application.Providers;

/// <summary>
/// How a provider names a listing: its own market code and symbol. A provider may use a segment of a market (Twelve Data calls the Nasdaq
/// Global Select segment <c>XNGS</c>); the catalog keeps the canonical market and maps the pair through <c>symbol_aliases</c>.
/// </summary>
/// <param name="ProviderMic">The market code as the provider writes it.</param>
/// <param name="Symbol">The symbol as the provider writes it.</param>
public sealed record ProviderListingKey(string ProviderMic, string Symbol);

/// <summary>
/// A listing found at a provider, as untrusted text: nothing here has been validated. <see cref="ToNewListing"/> hands it to the domain, which
/// validates every part.
/// </summary>
/// <param name="Key">The provider's own identification of the listing.</param>
/// <param name="Name">Instrument name.</param>
/// <param name="Currency">Trading currency code.</param>
/// <param name="TimeZoneId">IANA time zone of the market.</param>
/// <param name="AssetType">Kind of instrument.</param>
public sealed record ProviderListing(ProviderListingKey Key, string? Name, string? Currency, string? TimeZoneId, AssetType AssetType)
{
    /// <summary>The description the domain validates when the listing enters the catalog under <paramref name="canonicalMarket"/>.</summary>
    public NewListing ToNewListing(string canonicalMarket) => new(canonicalMarket, Key.Symbol, Name, Currency, TimeZoneId, AssetType);
}

/// <summary>What to fetch: the listing, an optional date range (inclusive, in the market's time zone) and whether closes are adjusted.</summary>
/// <param name="Key">The provider's identification of the listing.</param>
/// <param name="From">First day wanted, or <see langword="null"/> for as far back as the provider allows.</param>
/// <param name="To">Last day wanted, or <see langword="null"/> for the latest.</param>
/// <param name="Adjusted">True for prices adjusted for splits and dividends, false for the raw prices.</param>
/// <param name="Lane">Whose budget the call spends (ADR-0043); the scheduled refresh by default.</param>
public sealed record DailySeriesRequest(ProviderListingKey Key, DateOnly? From, DateOnly? To, bool Adjusted, CallLane Lane = CallLane.Scheduled);

/// <summary>
/// Whose share of a provider's daily credits a call spends (ADR-0043). The scheduled refresh keeps the credits that on-demand work cannot touch:
/// loading the history of a stock somebody just added draws on a smaller allowance of its own.
/// </summary>
public enum CallLane
{
    /// <summary>The daily refresh of the stocks already in the catalog: only the plan's own limit applies.</summary>
    Scheduled,

    /// <summary>History loaded because a stock entered the catalog (or was reloaded): also limited by the on-demand allowance of the day.</summary>
    OnDemand,
}

/// <summary>A run of daily bars in ascending date order, with no date twice.</summary>
/// <param name="Key">The provider's identification of the listing.</param>
/// <param name="Bars">The validated bars.</param>
/// <param name="Adjusted">True when the prices are adjusted for splits and dividends.</param>
/// <param name="Rejected">How many provider rows were refused because they were inconsistent.</param>
/// <param name="Truncated">
/// True when the provider answered with as many rows as one request can hold, so more may exist on the other side of the range. With no start
/// date the provider returns the newest rows, so the oldest are the ones missing; with a start date it returns the oldest after that date, so
/// the newest are. The caller narrows the range or asks again for the rest.
/// </param>
public sealed record DailySeries(ProviderListingKey Key, IReadOnlyList<DailyBar> Bars, bool Adjusted, int Rejected, bool Truncated = false);
