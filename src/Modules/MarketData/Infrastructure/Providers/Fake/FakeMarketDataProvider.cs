using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Prices;

namespace SakerWatch.MarketData.Infrastructure.Providers.Fake;

/// <summary>
/// A deterministic <see cref="IMarketDataProvider"/> for tests, CI and local runs without an API key (ADR-0027). Prices follow a fixed
/// pseudo-random walk seeded by the symbol, one bar per trading day of the market's calendar, so the same date always has the same price
/// whatever range is asked. A listing can carry a split: the adjusted series stays continuous and the raw series jumps by the ratio. Failures
/// can be scripted to exercise the ingestion's handling of rate limits and outages.
/// </summary>
public sealed class FakeMarketDataProvider : IMarketDataProvider
{
    /// <summary>The name the routing table uses for this provider.</summary>
    public const string ProviderName = "fake";

    private static readonly DateOnly _epoch = new(2000, 1, 3);

    private readonly ExchangeCalendars _calendars;
    private readonly TimeProvider _clock;
    private readonly Dictionary<ProviderListingKey, FakeListing> _listings = [];
    private readonly Queue<Error> _scriptedFailures = new();
    private int _calls;

    /// <summary>Creates the provider.</summary>
    /// <param name="calendars">Calendars that decide which days have a bar.</param>
    /// <param name="clock">Injected clock; the latest bar is the last session already closed.</param>
    public FakeMarketDataProvider(ExchangeCalendars calendars, TimeProvider clock)
    {
        _calendars = calendars;
        _clock = clock;
    }

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <summary>How many calls reached the provider, failed or not.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Adds a listing. A split of <paramref name="splitRatio"/> new shares per old one takes effect on <paramref name="splitDate"/>.</summary>
    public FakeMarketDataProvider WithListing(ProviderListing listing, decimal startPrice, DateOnly? splitDate = null, int splitRatio = 1)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(startPrice);
        ArgumentOutOfRangeException.ThrowIfLessThan(splitRatio, 1);
        _listings[listing.Key] = new FakeListing(listing, startPrice, splitDate, splitRatio);
        return this;
    }

    /// <summary>
    /// Records a dividend whose ex-date is <paramref name="exDate"/>: the adjusted prices of every earlier day are multiplied by
    /// <paramref name="factor"/> (a little below 1), as a real provider rewrites its adjusted history. Raw prices do not change.
    /// </summary>
    public FakeMarketDataProvider WithDividend(ProviderListingKey key, DateOnly exDate, decimal factor)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(factor);
        if (!_listings.TryGetValue(key, out FakeListing? listing))
        {
            throw new ArgumentException("The listing is not known to the fake provider.", nameof(key));
        }

        listing.Dividends.Add((exDate, factor));
        return this;
    }

    /// <summary>Makes the next <paramref name="times"/> calls fail with <paramref name="error"/>, in order, before behaving normally again.</summary>
    public FakeMarketDataProvider FailNext(Error error, int times = 1)
    {
        ArgumentNullException.ThrowIfNull(error);
        for (int i = 0; i < times; i++)
        {
            _scriptedFailures.Enqueue(error);
        }

        return this;
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<ProviderListing>>> SearchAsync(string symbol, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _calls);
        if (NextFailure() is { } failure)
        {
            return Task.FromResult<Result<IReadOnlyList<ProviderListing>>>(failure);
        }

        IReadOnlyList<ProviderListing> found = [.. _listings.Values
            .Where(l => string.Equals(l.Listing.Key.Symbol, symbol.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Listing)];
        return Task.FromResult<Result<IReadOnlyList<ProviderListing>>>(Result.Success(found));
    }

    /// <inheritdoc />
    public Task<Result<DailySeries>> GetDailyAsync(DailySeriesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _calls);
        if (NextFailure() is { } failure)
        {
            return Task.FromResult<Result<DailySeries>>(failure);
        }

        if (!_listings.TryGetValue(request.Key, out FakeListing? listing))
        {
            return Task.FromResult<Result<DailySeries>>(ProviderErrors.SymbolNotFound(Name, request.Key.Symbol));
        }

        // A market the product has no calendar for is one this provider does not serve: the same answer a real plan gives.
        Result<Mic> market = Mic.Create(request.Key.ProviderMic);
        Result<IExchangeCalendar> calendar = market.IsSuccess ? _calendars.TryGet(market.Value) : market.Error!;
        return Task.FromResult(calendar.IsSuccess ? Build(request, listing, calendar.Value) : Result.Failure<DailySeries>(ProviderErrors.PlanRestricted(Name)));
    }

    private Error? NextFailure() => _scriptedFailures.TryDequeue(out Error? error) ? error : null;

    private Result<DailySeries> Build(DailySeriesRequest request, FakeListing listing, IExchangeCalendar calendar)
    {
        DateOnly last = LastClosedSession(calendar);
        DateOnly to = request.To is { } wanted && wanted < last ? wanted : last;
        DateOnly from = request.From ?? to.AddDays(-365 * 5);

        var bars = new List<DailyBar>();
        uint state = Seed(listing.Listing.Key.Symbol);
        decimal previous = listing.StartPrice;
        for (DateOnly day = _epoch; day <= to; day = calendar.NextTradingDay(day))
        {
            state = unchecked((state * 1103515245) + 12345) & 0x7FFFFFFF;
            decimal change = (((int)(state % 401)) - 200) / 10000m; // -2.00 % to +2.00 %
            decimal close = Math.Round(previous * (1 + change), 4);
            if (day >= from)
            {
                decimal factor = !request.Adjusted && listing.SplitDate is { } split && day < split ? listing.SplitRatio : 1;
                if (request.Adjusted)
                {
                    factor = listing.Dividends.Where(dividend => day < dividend.ExDate).Aggregate(1m, (all, dividend) => all * dividend.Factor);
                }

                decimal open = previous * factor;
                decimal scaledClose = close * factor;
                decimal high = Math.Round(Math.Max(open, scaledClose) * 1.005m, 4);
                decimal low = Math.Round(Math.Min(open, scaledClose) * 0.995m, 4);
                bars.Add(DailyBar.Create(day, Math.Round(open, 4), high, low, Math.Round(scaledClose, 4), 1_000_000 + (state % 500_000)).Value);
            }

            previous = close;
        }

        return new DailySeries(request.Key, bars, request.Adjusted, 0);
    }

    private DateOnly LastClosedSession(IExchangeCalendar calendar)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(calendar.TimeZoneId)).DateTime);
        DateTimeOffset? close = calendar.CloseOf(today);
        return close is { } closing && now >= closing ? today : calendar.PreviousTradingDay(today);
    }

    // FNV-1a over the symbol: stable across runs and machines, unlike string.GetHashCode.
    private static uint Seed(string symbol)
    {
        uint hash = 2166136261;
        foreach (char c in symbol.ToUpperInvariant())
        {
            hash = unchecked((hash ^ c) * 16777619);
        }

        return hash & 0x7FFFFFFF;
    }

    private sealed record FakeListing(ProviderListing Listing, decimal StartPrice, DateOnly? SplitDate, int SplitRatio)
    {
        public List<(DateOnly ExDate, decimal Factor)> Dividends { get; } = [];
    }
}
