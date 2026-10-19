using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.MarketData.Application.Fx;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Stocks;
using SakerWatch.MarketData.Infrastructure;

namespace SakerWatch.MarketData.Tests;

public sealed class FreshnessTests
{
    private static readonly IExchangeCalendar _calendar = new UsEquityCalendar(Mic.Parse("XNYS"));
    private static readonly TimeSpan _settle = TimeSpan.FromHours(3);

    // The session after Thursday 2026-10-15 is Friday the 16th: it closes at 20:00 UTC and settles at 23:00 UTC.
    [Theory]
    [InlineData(2026, 10, 16, 22, 59, false)]
    [InlineData(2026, 10, 16, 23, 0, true)]
    [InlineData(2026, 10, 17, 12, 0, true)]
    public void StaleSince_StartsWhenTheNextSessionSettles(int y, int mo, int d, int h, int mi, bool stale)
    {
        DateTimeOffset? since = Freshness.StaleSince(_calendar, new DateOnly(2026, 10, 15), new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero), _settle);

        (since is not null).Should().Be(stale);
        if (stale)
        {
            since.Should().Be(new DateTimeOffset(2026, 10, 16, 23, 0, 0, TimeSpan.Zero));
        }
    }

    [Fact]
    public void StaleSince_OverAWeekend_WaitsForMondaysSession()
    {
        // Friday's data is current all weekend; it goes stale when Monday 2026-10-19 settles (23:00 UTC).
        var friday = new DateOnly(2026, 10, 16);

        Freshness.StaleSince(_calendar, friday, new DateTimeOffset(2026, 10, 19, 22, 59, 0, TimeSpan.Zero), _settle).Should().BeNull();
        Freshness.StaleSince(_calendar, friday, new DateTimeOffset(2026, 10, 19, 23, 0, 0, TimeSpan.Zero), _settle).Should().Be(new DateTimeOffset(2026, 10, 19, 23, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void StaleSince_WithNoHistory_IsNotAStalenessReading() =>
        Freshness.StaleSince(_calendar, null, DateTimeOffset.UtcNow, _settle).Should().BeNull();

    private sealed class FakeReader(Dictionary<string, ListingLatest[]> byMarket, bool fail = false) : IFreshnessReader
    {
        public Task<IReadOnlyList<ListingLatest>> LatestPerListingAsync(Mic market, CancellationToken cancellationToken) =>
            fail ? throw new InvalidOperationException("database not ready") : Task.FromResult<IReadOnlyList<ListingLatest>>(byMarket.GetValueOrDefault(market.Value, []));
    }

    private sealed class NoProvider : IMarketDataProvider
    {
        public string Name => "fake";

        public Task<SakerWatch.BuildingBlocks.Kernel.Results.Result<IReadOnlyList<ProviderListing>>> SearchAsync(string symbol, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SakerWatch.BuildingBlocks.Kernel.Results.Result<DailySeries>> GetDailyAsync(DailySeriesRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeRates(DateOnly? latest, bool fail = false) : IFxRateStore
    {
        public Task<int> UpsertAsync(IReadOnlyList<SakerWatch.MarketData.Domain.Fx.FxRate> rates, DateTimeOffset ingestedAt, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SakerWatch.MarketData.Domain.Fx.FxRate?> OnOrBeforeAsync(CurrencyCode baseCurrency, CurrencyCode quote, DateOnly date, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DateOnly?> LatestDateAsync(CurrencyCode baseCurrency, CurrencyCode quote, string source, CancellationToken cancellationToken) =>
            fail ? throw new InvalidOperationException("database not ready") : Task.FromResult(latest);
    }

    private static MarketDataFreshnessSampler Sampler(IFreshnessReader reader, DateTimeOffset now, params string[] markets) => Sampler(reader, null, now, markets);

    private static MarketDataFreshnessSampler Sampler(IFreshnessReader reader, IFxRateStore? rates, DateTimeOffset now, params string[] markets)
    {
        var services = new ServiceCollection().AddSingleton(reader);
        if (rates is not null)
        {
            services.AddSingleton(rates);
        }

        ServiceProvider provider = services.BuildServiceProvider();
        var router = new MarketDataProviderRouter(
            [new NoProvider()],
            Options.Create(new MarketDataProvidersOptions { Markets = markets.ToDictionary(m => m, _ => "fake") }));
        return new MarketDataFreshnessSampler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            router,
            ExchangeCalendars.Default(),
            Options.Create(new IngestionOptions()),
            new FakeTimeProvider(now),
            NullLogger<MarketDataFreshnessSampler>.Instance);
    }

    private static ListingLatest Listing(DateOnly? latest) => new(StockId.Create(Guid.CreateVersion7()), latest);

    [Fact]
    public async Task Sample_CountsFreshStaleAndPendingListings_AndTheAgeOfTheStalest()
    {
        // Saturday 2026-10-17 12:00 UTC. Friday the 16th settled at 23:00 UTC that day, 13 hours earlier.
        var now = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        var reader = new FakeReader(new()
        {
            ["XNAS"] =
            [
                Listing(new DateOnly(2026, 10, 16)), // fresh
                Listing(new DateOnly(2026, 10, 15)), // missing Friday: stale since Friday 23:00 UTC
                Listing(new DateOnly(2026, 10, 14)), // missing Thursday and Friday: stale since Thursday 23:00 UTC
                Listing(null), // pending
            ],
        });
        MarketDataFreshnessSampler sampler = Sampler(reader, now, "XNAS");

        await sampler.SampleAsync(CancellationToken.None);

        MarketDataFreshnessSampler.MarketFreshness xnas = sampler.GetSnapshot()["XNAS"];
        xnas.Should().Be(new MarketDataFreshnessSampler.MarketFreshness(1, 2, 1, TimeSpan.FromHours(37).TotalSeconds));
    }

    [Fact]
    public async Task Sample_WhenEveryListingIsCurrent_ReportsZeroStaleness()
    {
        MarketDataFreshnessSampler sampler = Sampler(
            new FakeReader(new() { ["XNAS"] = [Listing(new DateOnly(2026, 10, 16))] }), new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero), "XNAS");

        await sampler.SampleAsync(CancellationToken.None);

        sampler.GetSnapshot()["XNAS"].Should().Be(new MarketDataFreshnessSampler.MarketFreshness(1, 0, 0, 0));
    }

    [Fact]
    public async Task Sample_AMarketThatCannotBeRead_KeepsItsLastMeasurement_AndAMarketWithoutACalendarIsSkipped()
    {
        var now = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        MarketDataFreshnessSampler good = Sampler(new FakeReader(new() { ["XNAS"] = [Listing(new DateOnly(2026, 10, 16))] }), now, "XNAS", "XTSE");
        await good.SampleAsync(CancellationToken.None);
        good.GetSnapshot().Keys.Should().BeEquivalentTo("XNAS");

        MarketDataFreshnessSampler broken = Sampler(new FakeReader([], fail: true), now, "XNAS");
        await broken.SampleAsync(CancellationToken.None);

        broken.GetSnapshot().Should().BeEmpty("nothing was ever measured, and the failure did not throw");
    }

    [Fact]
    public async Task Sample_MeasuresTheAgeOfTheNewestExchangeRate_InGuatemalanDays()
    {
        // 2026-10-17 04:00 UTC is still the 16th in Guatemala; the newest rate is the 14th: two days old.
        MarketDataFreshnessSampler sampler = Sampler(new FakeReader([]), new FakeRates(new DateOnly(2026, 10, 14)), new DateTimeOffset(2026, 10, 17, 4, 0, 0, TimeSpan.Zero));

        await sampler.SampleAsync(CancellationToken.None);

        sampler.FxAgeDays.Should().Be(2);
    }

    [Fact]
    public async Task Sample_WithoutARateStoreOrWithoutRates_HasNoExchangeRateAge()
    {
        var now = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        MarketDataFreshnessSampler noStore = Sampler(new FakeReader([]), now);
        MarketDataFreshnessSampler empty = Sampler(new FakeReader([]), new FakeRates(null), now);

        await noStore.SampleAsync(CancellationToken.None);
        await empty.SampleAsync(CancellationToken.None);

        noStore.FxAgeDays.Should().BeNull();
        empty.FxAgeDays.Should().BeNull();
    }

    [Fact]
    public async Task Sample_WhenTheExchangeRateCannotBeRead_KeepsTheLastAge_AndDoesNotThrow()
    {
        var now = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        MarketDataFreshnessSampler good = Sampler(new FakeReader([]), new FakeRates(new DateOnly(2026, 10, 16)), now);
        await good.SampleAsync(CancellationToken.None);
        MarketDataFreshnessSampler broken = Sampler(new FakeReader([]), new FakeRates(null, fail: true), now);

        await broken.SampleAsync(CancellationToken.None);

        good.FxAgeDays.Should().Be(1);
        broken.FxAgeDays.Should().BeNull("nothing was ever measured, and the failure did not throw");
    }

    [Fact]
    public async Task TheGauges_ExposeTheSnapshotByMarketAndState()
    {
        var now = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        MarketDataFreshnessSampler sampler = Sampler(
            new FakeReader(new() { ["XNAS"] = [Listing(new DateOnly(2026, 10, 16)), Listing(new DateOnly(2026, 10, 15)), Listing(null)] }), now, "XNAS");
        await sampler.SampleAsync(CancellationToken.None);
        var listings = new Dictionary<string, long>();
        var staleness = new List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name.StartsWith("sakerwatch_marketdata_", StringComparison.Ordinal))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "sakerwatch_marketdata_listings")
            {
                string state = tags.ToArray().Single(t => t.Key == "state").Value!.ToString()!;
                listings[state] = value;
            }
        });
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
        {
            if (instrument.Name == "sakerwatch_marketdata_price_staleness_seconds")
            {
                staleness.Add(value);
            }
        });
        listener.Start();

        listener.RecordObservableInstruments();

        listings.Should().HaveCount(3);
        listings["fresh"].Should().Be(1);
        listings["stale"].Should().Be(1);
        listings["pending"].Should().Be(1);
        staleness.Should().ContainSingle().Which.Should().Be(TimeSpan.FromHours(13).TotalSeconds);
        sampler.Dispose();
    }
}
