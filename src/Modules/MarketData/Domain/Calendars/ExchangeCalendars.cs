using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Domain.Markets;

namespace SakerWatch.MarketData.Domain.Calendars;

/// <summary>
/// The calendars the product knows, by market. A market without a calendar is not supported yet: ingestion cannot schedule it and
/// <see cref="TryGet"/> says so. Adding a market means adding its calendar here and a provider mapping (ADR-0018, ADR-0027).
/// </summary>
public sealed class ExchangeCalendars
{
    // The US markets that share the NYSE and Nasdaq schedule.
    private static readonly string[] _usMarkets = ["XNYS", "XNAS", "XASE", "ARCX", "BATS", "XNGS", "XNMS", "XNCM"];

    private readonly Dictionary<Mic, IExchangeCalendar> _byMarket;

    /// <summary>Creates a registry from explicit calendars.</summary>
    public ExchangeCalendars(IEnumerable<IExchangeCalendar> calendars)
    {
        ArgumentNullException.ThrowIfNull(calendars);
        _byMarket = calendars.ToDictionary(calendar => calendar.Market);
    }

    /// <summary>The markets that have a calendar.</summary>
    public IReadOnlyCollection<Mic> Markets => _byMarket.Keys;

    /// <summary>The registry with every calendar implemented so far: the US markets.</summary>
    public static ExchangeCalendars Default() =>
        new(_usMarkets.Select(code => (IExchangeCalendar)new UsEquityCalendar(Mic.Parse(code))));

    /// <summary>The calendar of <paramref name="market"/>, or a failure when the market is not supported yet.</summary>
    public Result<IExchangeCalendar> TryGet(Mic market)
    {
        ArgumentNullException.ThrowIfNull(market);
        return _byMarket.TryGetValue(market, out IExchangeCalendar? calendar)
            ? Result.Success(calendar)
            : Error.NotFound("marketdata.calendar.not_found", $"There is no calendar for the market {market}.");
    }
}
