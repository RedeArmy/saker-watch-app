using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Stocks;
using SakerWatch.MarketData.Infrastructure.Providers.Fake;

namespace SakerWatch.MarketData.Tests;

public sealed class FakeMarketDataProviderTests
{
    private static readonly ProviderListingKey _aapl = new("XNGS", "AAPL");
    private static readonly ProviderListing _listing = new(_aapl, "Apple Inc.", "USD", "America/New_York", AssetType.Equity);

    // Saturday 2026-10-17, 12:00 UTC: the last closed session is Friday the 16th.
    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));

    private static FakeMarketDataProvider Provider(FakeTimeProvider? clock = null, DateOnly? split = null, int ratio = 1) =>
        new FakeMarketDataProvider(ExchangeCalendars.Default(), clock ?? Clock()).WithListing(_listing, 100m, split, ratio);

    private static DailySeriesRequest Request(DateOnly? from = null, DateOnly? to = null, bool adjusted = true, ProviderListingKey? key = null) =>
        new(key ?? _aapl, from, to, adjusted);

    [Fact]
    public async Task GetDaily_IsDeterministic_AcrossInstancesAndRanges()
    {
        DailySeries first = (await Provider().GetDailyAsync(Request(), CancellationToken.None)).Value;
        DailySeries second = (await Provider().GetDailyAsync(Request(), CancellationToken.None)).Value;
        DailySeries narrow = (await Provider().GetDailyAsync(Request(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)), CancellationToken.None)).Value;

        second.Bars.Should().Equal(first.Bars);
        narrow.Bars.Should().OnlyContain(b => first.Bars.Contains(b), "the same date has the same price whatever range is asked");
        narrow.Bars.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetDaily_HasOneBarPerTradingDay_UpToTheLastClosedSession()
    {
        DailySeries series = (await Provider().GetDailyAsync(Request(new DateOnly(2026, 6, 1), null), CancellationToken.None)).Value;
        IExchangeCalendar calendar = ExchangeCalendars.Default().TryGet(Mic.Parse("XNYS")).Value;

        series.Bars.Select(b => b.Date).Should().BeInAscendingOrder().And.OnlyContain(d => calendar.IsTradingDay(d));
        series.Bars[^1].Date.Should().Be(new DateOnly(2026, 10, 16));
        series.Bars.Select(b => b.Date).Should().Contain(new DateOnly(2026, 6, 1)).And.NotContain(new DateOnly(2026, 7, 3), "July 3 2026 was the observed holiday");
        series.Rejected.Should().Be(0);
    }

    [Theory]
    [InlineData(20, 15, false)] // Friday 2026-10-16 15:00 UTC is before the 16:00 EDT close (20:00 UTC)
    [InlineData(20, 21, true)]
    public async Task GetDaily_TheCurrentSessionAppearsOnlyOnceItHasClosed(int day, int utcHour, bool includesToday)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, utcHour, 0, 0, TimeSpan.Zero));

        DailySeries series = (await Provider(clock).GetDailyAsync(Request(new DateOnly(2026, 10, 1), null), CancellationToken.None)).Value;

        (series.Bars[^1].Date == new DateOnly(2026, 10, 16)).Should().Be(includesToday, $"clock at {day}:{utcHour}");
    }

    [Fact]
    public async Task GetDaily_ASplit_KeepsTheAdjustedSeriesContinuousAndMakesTheRawOneJump()
    {
        var splitDay = new DateOnly(2026, 9, 15);
        FakeMarketDataProvider provider = Provider(split: splitDay, ratio: 10);
        var range = (new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 18));

        DailySeries adjusted = (await provider.GetDailyAsync(Request(range.Item1, range.Item2, adjusted: true), CancellationToken.None)).Value;
        DailySeries raw = (await provider.GetDailyAsync(Request(range.Item1, range.Item2, adjusted: false), CancellationToken.None)).Value;

        adjusted.Bars.Zip(raw.Bars).Where(pair => pair.First.Date >= splitDay).Should().OnlyContain(pair => pair.First.Close == pair.Second.Close);
        raw.Bars.Where(b => b.Date < splitDay).Zip(adjusted.Bars).Should().OnlyContain(pair => pair.First.Close == Math.Round(pair.Second.Close * 10, 4));
        Math.Abs(adjusted.Bars.Single(b => b.Date == splitDay).Close / adjusted.Bars.Single(b => b.Date == new DateOnly(2026, 9, 14)).Close - 1).Should().BeLessThan(0.03m);
        raw.Bars.Single(b => b.Date == new DateOnly(2026, 9, 14)).Close.Should().BeGreaterThan(raw.Bars.Single(b => b.Date == splitDay).Close * 5m);
    }

    [Fact]
    public async Task Search_FindsEveryListingOfASymbol_IgnoringCase()
    {
        FakeMarketDataProvider provider = Provider().WithListing(new ProviderListing(new ProviderListingKey("XTSE", "AAPL"), "Apple CDR", "CAD", "America/Toronto", AssetType.Other), 20m);

        IReadOnlyList<ProviderListing> found = (await provider.SearchAsync(" aapl ", CancellationToken.None)).Value;

        found.Select(l => l.Key.ProviderMic).Should().BeEquivalentTo("XNGS", "XTSE");
        (await provider.SearchAsync("MSFT", CancellationToken.None)).Value.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDaily_AnUnknownListing_IsSymbolNotFound() =>
        ProviderErrors.Classify((await Provider().GetDailyAsync(Request(key: new ProviderListingKey("XNGS", "NOPE")), CancellationToken.None)).Error!)
            .Should().Be(ProviderFailureKind.SymbolNotFound);

    [Fact]
    public async Task GetDaily_AMarketWithoutACalendar_IsAPlanRestriction() =>
        ProviderErrors.Classify((await Provider().WithListing(new ProviderListing(new ProviderListingKey("XTSE", "SHOP"), "Shopify", "CAD", "America/Toronto", AssetType.Equity), 50m)
            .GetDailyAsync(Request(key: new ProviderListingKey("XTSE", "SHOP")), CancellationToken.None)).Error!)
            .Should().Be(ProviderFailureKind.PlanRestricted);

    [Fact]
    public async Task ScriptedFailures_AreServedInOrder_ThenTheProviderRecovers()
    {
        FakeMarketDataProvider provider = Provider()
            .FailNext(ProviderErrors.RateLimited("fake", TimeSpan.FromSeconds(30)))
            .FailNext(ProviderErrors.Unavailable("fake"), times: 2);

        ProviderFailureKind[] kinds =
        [
            ProviderErrors.Classify((await provider.GetDailyAsync(Request(), CancellationToken.None)).Error!),
            ProviderErrors.Classify((await provider.SearchAsync("AAPL", CancellationToken.None)).Error!),
            ProviderErrors.Classify((await provider.GetDailyAsync(Request(), CancellationToken.None)).Error!),
        ];

        kinds.Should().Equal(ProviderFailureKind.RateLimited, ProviderFailureKind.Transient, ProviderFailureKind.Transient);
        (await provider.GetDailyAsync(Request(), CancellationToken.None)).IsSuccess.Should().BeTrue();
        provider.Calls.Should().Be(4);
    }

    [Fact]
    public async Task ACancelledCall_Throws()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Func<Task> search = () => Provider().SearchAsync("AAPL", cancelled.Token);
        Func<Task> daily = () => Provider().GetDailyAsync(Request(), cancelled.Token);

        await search.Should().ThrowAsync<OperationCanceledException>();
        await daily.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void WithListing_RefusesNonsense()
    {
        FakeMarketDataProvider provider = Provider();

        Action price = () => provider.WithListing(_listing, 0m);
        Action ratio = () => provider.WithListing(_listing, 10m, null, 0);

        price.Should().Throw<ArgumentOutOfRangeException>();
        ratio.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void TheFakeIsNamedForTheRoutingTable() => Provider().Name.Should().Be("fake");
}
