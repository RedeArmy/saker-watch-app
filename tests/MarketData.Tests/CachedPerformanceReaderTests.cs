using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using SakerWatch.MarketData.Application.Prices;
using SakerWatch.MarketData.Domain.Performance;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Tests;

public sealed class CachedPerformanceReaderTests
{
    private static readonly StockId _apple = StockId.Create(Guid.Parse("00000000-0000-0000-0000-0000000000a1"));
    private static readonly StockId _nvidia = StockId.Create(Guid.Parse("00000000-0000-0000-0000-0000000000b2"));
    private static readonly DateTimeOffset _loaded = new(2026, 10, 20, 22, 0, 0, TimeSpan.Zero);

    private sealed class Reader(Dictionary<StockId, PerformanceReport> answers) : IPerformanceReader
    {
        public List<StockId[]> Asked { get; } = [];

        public Task<IReadOnlyDictionary<StockId, PerformanceReport>> ReadAsync(IReadOnlyCollection<StockId> stocks, CancellationToken cancellationToken)
        {
            Asked.Add([.. stocks]);
            return Task.FromResult<IReadOnlyDictionary<StockId, PerformanceReport>>(stocks.ToDictionary(id => id, id => answers[id]));
        }
    }

    private sealed class Stamps(Dictionary<StockId, DateTimeOffset> stamps) : IIngestionStamps
    {
        public Task<IReadOnlyDictionary<StockId, DateTimeOffset>> LastSuccessAsync(IReadOnlyCollection<StockId> stocks, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<StockId, DateTimeOffset>>(stamps);
    }

    private sealed class Cache : IPerformanceCache
    {
        public Dictionary<PerformanceCacheKey, PerformanceReport> Kept { get; } = [];

        public TimeSpan? LastTtl { get; private set; }

        public Task<IReadOnlyDictionary<StockId, PerformanceReport>> GetManyAsync(IReadOnlyCollection<PerformanceCacheKey> keys, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<StockId, PerformanceReport>>(keys.Where(Kept.ContainsKey).ToDictionary(key => key.Stock, key => Kept[key]));

        public Task SetManyAsync(IReadOnlyDictionary<PerformanceCacheKey, PerformanceReport> reports, TimeSpan ttl, CancellationToken cancellationToken)
        {
            LastTtl = ttl;
            foreach ((PerformanceCacheKey key, PerformanceReport report) in reports)
            {
                Kept[key] = report;
            }

            return Task.CompletedTask;
        }
    }

    private static PerformanceReport Report(decimal close)
    {
        PriceSample latest = PriceSample.Create(new DateOnly(2026, 10, 20), close).Value;
        return new PerformanceReport(latest, [.. Enum.GetValues<PerformanceWindow>().Select(window => WindowPerformance.Missing(window, PerformanceGap.InsufficientHistory))]);
    }

    private static PerformanceReport NoData() =>
        new(null, [.. Enum.GetValues<PerformanceWindow>().Select(window => WindowPerformance.Missing(window, PerformanceGap.NoData))]);

    private static CachedPerformanceReader Build(Reader inner, Dictionary<StockId, DateTimeOffset> stamps, Cache cache, bool enabled = true, int maxGapDays = 10) =>
        new(inner, new Stamps(stamps), cache, Options.Create(new PerformanceOptions { MaxGapDays = maxGapDays }), Options.Create(new PerformanceCacheOptions { Enabled = enabled }));

    [Fact]
    public async Task Read_ForAListingNotKeptYet_ComputesItAndKeepsItUnderItsStamp()
    {
        var inner = new Reader(new() { [_apple] = Report(100m) });
        var cache = new Cache();

        IReadOnlyDictionary<StockId, PerformanceReport> reports = await Build(inner, new() { [_apple] = _loaded }, cache).ReadAsync([_apple], CancellationToken.None);

        reports[_apple].Latest!.AdjustedClose.Should().Be(100m);
        cache.Kept.Keys.Should().BeEquivalentTo([new PerformanceCacheKey(_apple, _loaded, 10)]);
        cache.LastTtl.Should().Be(TimeSpan.FromHours(26));
    }

    [Fact]
    public async Task Read_ForAListingKept_DoesNotAskTheDatabase()
    {
        var inner = new Reader(new() { [_apple] = Report(100m) });
        var cache = new Cache();
        cache.Kept[new PerformanceCacheKey(_apple, _loaded, 10)] = Report(55m);

        IReadOnlyDictionary<StockId, PerformanceReport> reports = await Build(inner, new() { [_apple] = _loaded }, cache).ReadAsync([_apple], CancellationToken.None);

        reports[_apple].Latest!.AdjustedClose.Should().Be(55m, "the kept report is the one served");
        inner.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task Read_WhenOnlySomeAreKept_AsksTheDatabaseOnlyForTheOthers()
    {
        var inner = new Reader(new() { [_apple] = Report(100m), [_nvidia] = Report(200m) });
        var cache = new Cache();
        cache.Kept[new PerformanceCacheKey(_apple, _loaded, 10)] = Report(100m);

        IReadOnlyDictionary<StockId, PerformanceReport> reports = await Build(inner, new() { [_apple] = _loaded, [_nvidia] = _loaded }, cache).ReadAsync([_apple, _nvidia], CancellationToken.None);

        reports.Keys.Should().BeEquivalentTo([_apple, _nvidia]);
        inner.Asked.Should().ContainSingle().Which.Should().BeEquivalentTo([_nvidia]);
        cache.Kept.Keys.Select(key => key.Stock).Should().BeEquivalentTo([_apple, _nvidia]);
    }

    [Fact]
    public async Task Read_AfterTheListingLoadedNewData_IsAMissUnderTheNewStamp()
    {
        var inner = new Reader(new() { [_apple] = Report(101m) });
        var cache = new Cache();
        cache.Kept[new PerformanceCacheKey(_apple, _loaded, 10)] = Report(100m);

        IReadOnlyDictionary<StockId, PerformanceReport> reports = await Build(inner, new() { [_apple] = _loaded.AddDays(1) }, cache).ReadAsync([_apple], CancellationToken.None);

        reports[_apple].Latest!.AdjustedClose.Should().Be(101m, "the report of the old load is never read again");
        cache.Kept.Should().HaveCount(2);
    }

    [Fact]
    public async Task Read_AfterTheGapSettingChanged_IsAMiss()
    {
        var inner = new Reader(new() { [_apple] = Report(100m) });
        var cache = new Cache();
        cache.Kept[new PerformanceCacheKey(_apple, _loaded, 10)] = Report(55m);

        await Build(inner, new() { [_apple] = _loaded }, cache, maxGapDays: 20).ReadAsync([_apple], CancellationToken.None);

        inner.Asked.Should().ContainSingle();
    }

    [Fact]
    public async Task Read_ForAListingThatNeverLoaded_IsComputedAndNotKept()
    {
        var inner = new Reader(new() { [_apple] = NoData() });
        var cache = new Cache();

        IReadOnlyDictionary<StockId, PerformanceReport> reports = await Build(inner, [], cache).ReadAsync([_apple], CancellationToken.None);

        reports[_apple].Latest.Should().BeNull();
        cache.Kept.Should().BeEmpty();
    }

    [Fact]
    public async Task Read_ForAReportWithoutAPrice_IsNotKept()
    {
        var inner = new Reader(new() { [_apple] = NoData() });
        var cache = new Cache();

        await Build(inner, new() { [_apple] = _loaded }, cache).ReadAsync([_apple], CancellationToken.None);

        cache.Kept.Should().BeEmpty("a listing with no price is cheap to read and may get one at any moment");
    }

    [Fact]
    public async Task Read_WhenTheCacheIsOff_GoesStraightToTheDatabaseAndKeepsNothing()
    {
        var inner = new Reader(new() { [_apple] = Report(100m) });
        var cache = new Cache();
        cache.Kept[new PerformanceCacheKey(_apple, _loaded, 10)] = Report(55m);

        IReadOnlyDictionary<StockId, PerformanceReport> reports = await Build(inner, new() { [_apple] = _loaded }, cache, enabled: false).ReadAsync([_apple], CancellationToken.None);

        reports[_apple].Latest!.AdjustedClose.Should().Be(100m);
        cache.LastTtl.Should().BeNull();
    }

    [Fact]
    public async Task Read_CountsHitsAndMisses()
    {
        var counted = new Dictionary<string, long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "sakerwatch_marketdata_perf_cache_total")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                lock (counted)
                {
                    counted[(string)tag.Value!] = counted.GetValueOrDefault((string)tag.Value!) + value;
                }
            }
        });
        listener.Start();
        var inner = new Reader(new() { [_apple] = Report(100m), [_nvidia] = Report(200m) });
        var cache = new Cache();
        cache.Kept[new PerformanceCacheKey(_apple, _loaded, 10)] = Report(100m);

        await Build(inner, new() { [_apple] = _loaded, [_nvidia] = _loaded }, cache).ReadAsync([_apple, _nvidia], CancellationToken.None);

        counted.Should().Contain(new KeyValuePair<string, long>("hit", 1)).And.Contain(new KeyValuePair<string, long>("miss", 1));
    }
}
