using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Prices;
using SakerWatch.MarketData.Domain.Stocks;
using SakerWatch.MarketData.Infrastructure;
using SakerWatch.MarketData.Infrastructure.Providers.TwelveData;

namespace SakerWatch.MarketData.Tests.Live;

/// <summary>
/// The Twelve Data adapter against the real API with the owner's key. Nothing here is assumed: each section observes what the service does,
/// asserts only what the product depends on, and writes everything it saw to a report (default <c>/tmp/saker-live-twelvedata</c>, override
/// with <c>SAKER_LIVE_REPORT_DIR</c>). A section that fails does not stop the others, so one run gives the whole picture. It spends about
/// forty credits and takes several minutes because it keeps to the plan's eight calls per minute.
/// </summary>
public sealed class TwelveDataLiveTests
{
    private static readonly string _reportDirectory = Environment.GetEnvironmentVariable("SAKER_LIVE_REPORT_DIR") ?? "/tmp/saker-live-twelvedata";

    internal sealed class PacedBudget : IProviderCallBudget
    {
        private readonly Queue<DateTimeOffset> _window = new();

        public int Granted { get; private set; }

        public async Task<BudgetDecision> TryAcquireAsync(string provider, BudgetLimits limits, int credits, CancellationToken cancellationToken)
        {
            while (true)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                while (_window.Count > 0 && now - _window.Peek() >= TimeSpan.FromSeconds(62))
                {
                    _window.Dequeue();
                }

                if (_window.Count + credits <= limits.PerMinute)
                {
                    for (int i = 0; i < credits; i++)
                    {
                        _window.Enqueue(now);
                    }

                    Granted += credits;
                    return BudgetDecision.Allowed;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    private sealed class AlwaysAllow : IProviderCallBudget
    {
        public int Granted { get; private set; }

        public Task<BudgetDecision> TryAcquireAsync(string provider, BudgetLimits limits, int credits, CancellationToken cancellationToken)
        {
            Granted += credits;
            return Task.FromResult(BudgetDecision.Allowed);
        }
    }

    internal sealed class CapturingStore : IProviderPayloadStore
    {
        public List<ProviderPayload> Stored { get; } = [];

        public Task StoreAsync(ProviderPayload payload, CancellationToken cancellationToken)
        {
            Stored.Add(payload);
            return Task.CompletedTask;
        }
    }

    private readonly StringBuilder _report = new();
    private readonly List<string> _failures = [];
    private readonly PacedBudget _budget = new();
    private readonly CapturingStore _store = new();
    private int _rawCalls;

    [LiveFact]
    public async Task TheProviderWorksAgainstTheRealApi()
    {
        string key = Environment.GetEnvironmentVariable(LiveFactAttribute.KeyVariable)!;
        Directory.CreateDirectory(_reportDirectory);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProviderCallBudget>(_budget);
        services.AddSingleton<IProviderPayloadStore>(_store);
        services.AddMarketData(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TwelveData:ApiKey"] = key,
            ["MarketData:Providers:Markets:XNAS"] = "twelvedata",
            ["MarketData:Providers:Markets:XNYS"] = "twelvedata",
        }).Build());
        await using ServiceProvider provider = services.BuildServiceProvider();
        var twelve = provider.GetRequiredService<TwelveDataProvider>();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        Line($"# Twelve Data live check, {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        long? usageBefore = await DailyUsageAsync(factory, key);
        Line($"Daily credits used before the run: {usageBefore?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}");

        await Section("1. Symbol search", () => SearchAsync(twelve));
        await Section("2. Full adjusted history of AAPL and its agreement with the calendar", () => FullHistoryAsync(twelve));
        await Section("3. mic_code is honoured", () => MicCodeAsync(twelve));
        await Section("4. Date bounds", () => DateBoundsAsync(twelve, factory, key));
        await Section("5. Adjusted against raw closes (NVDA split, AAPL dividends)", () => AdjustmentAsync(twelve));
        await Section("6. The latest bar", () => LatestBarAsync(twelve));
        await Section("7. Tickers with a dot", () => DottedTickersAsync(twelve));
        await Section("8. Errors", () => ErrorsAsync(twelve, factory, key));
        await Section("9. What each operation costs", () => CreditCostsAsync(factory, key));
        await Section("10. The real rate limit", () => RateLimitAsync(factory, key));
        await Section("11. Identical requests", () => IdenticalRequestsAsync(factory, key));
        await Section("12. A range with no sessions", () => NoSessionsAsync(twelve, factory, key));
        await Section("13. Corporate action endpoints", () => CorporateActionsAsync(factory, key));

        await Task.Delay(TimeSpan.FromSeconds(65));
        long? usageAfter = await DailyUsageAsync(factory, key);
        Line($"## Credit accounting\nDaily credits used after the run: {usageAfter?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}");
        if (usageBefore is { } before && usageAfter is { } after)
        {
            Line($"Delta {after - before}; credits the adapter granted {_budget.Granted}; raw calls made by this test {_rawCalls}; calls of the rate-limit section are extra.");
        }

        Line($"\n## Result\n{(_failures.Count == 0 ? "all sections passed" : string.Join("\n", _failures))}");
        await File.WriteAllTextAsync(Path.Combine(_reportDirectory, "report.md"), _report.ToString());
        _failures.Should().BeEmpty("see the report in " + _reportDirectory);
    }

    private async Task SearchAsync(TwelveDataProvider twelve)
    {
        IReadOnlyList<ProviderListing> listings = (await Retry(() => twelve.SearchAsync("AAPL", CancellationToken.None))).Value;
        foreach (ProviderListing l in listings)
        {
            Line($"- {l.Key.ProviderMic} {l.Key.Symbol} | {l.Name} | {l.Currency} | {l.TimeZoneId} | {l.AssetType}");
        }

        ProviderListing us = listings.Single(l => l.Key.ProviderMic == "XNGS" && l.Key.Symbol == "AAPL");
        us.Currency.Should().Be("USD");
        us.TimeZoneId.Should().Be("America/New_York");
        us.AssetType.Should().Be(AssetType.Equity);
        Stock.Create(StockId.Create(Guid.CreateVersion7()), us.ToNewListing("XNAS"), DateTimeOffset.UtcNow).IsSuccess.Should().BeTrue("the domain accepts what the provider says");
        foreach (ProviderListing l in listings)
        {
            Ticker.Create(l.Key.Symbol).IsSuccess.Should().BeTrue($"{l.Key.Symbol} is a valid ticker");
        }
    }

    private async Task FullHistoryAsync(TwelveDataProvider twelve)
    {
        var request = new DailySeriesRequest(new ProviderListingKey("XNGS", "AAPL"), null, null, Adjusted: true);
        DailySeries series = (await Retry(() => twelve.GetDailyAsync(request, CancellationToken.None))).Value;
        Line($"Bars {series.Bars.Count}, rejected {series.Rejected}, first {series.Bars[0].Date}, last {series.Bars[^1].Date}");
        series.Bars.Count.Should().BeGreaterThan(4500);
        series.Rejected.Should().Be(0);
        series.Bars.Select(b => b.Date).Should().BeInAscendingOrder();

        IExchangeCalendar calendar = ExchangeCalendars.Default().TryGet(Mic.Parse("XNYS")).Value;
        HashSet<DateOnly> have = [.. series.Bars.Select(b => b.Date)];
        DateOnly[] onClosedDays = [.. have.Where(d => !calendar.IsTradingDay(d))];
        var expected = new List<DateOnly>();
        for (DateOnly day = series.Bars[0].Date; day <= series.Bars[^1].Date; day = calendar.NextTradingDay(day))
        {
            expected.Add(day);
        }

        DateOnly[] missing = [.. expected.Where(d => !have.Contains(d))];
        Line($"Calendar check: {onClosedDays.Length} bars on days the calendar says are closed {Show(onClosedDays)}; {missing.Length} sessions of the calendar without a bar {Show(missing)}");
        onClosedDays.Should().BeEmpty("a bar on a day the calendar closes would mean the calendar is wrong");
        missing.Length.Should().BeLessThanOrEqualTo(3, "the provider may have rare gaps, but the calendar must not invent sessions");

        string body = _store.Stored.Last(p => p.Operation == "daily").Body;
        using var meta = JsonDocument.Parse(body);
        JsonElement m = meta.RootElement.GetProperty("meta");
        Line($"meta: {m.GetRawText()}");
        m.GetProperty("mic_code").GetString().Should().Be("XNGS");
        m.GetProperty("currency").GetString().Should().Be("USD");
        await File.WriteAllTextAsync(Path.Combine(_reportDirectory, "raw-aapl-daily-adjusted.json"), body);
    }

    private async Task MicCodeAsync(TwelveDataProvider twelve)
    {
        Result<DailySeries> wrongMarket = await twelve.GetDailyAsync(new DailySeriesRequest(new ProviderListingKey("XNYS", "AAPL"), null, null, true), CancellationToken.None);
        Line($"AAPL asked on XNYS (it trades on Nasdaq): {Describe(wrongMarket)}");
        wrongMarket.IsSuccess.Should().BeFalse("an NYSE code for a Nasdaq stock must not return a series quietly");

        Result<DailySeries> canadian = await twelve.GetDailyAsync(new DailySeriesRequest(new ProviderListingKey("XTSE", "SHOP"), null, null, true), CancellationToken.None);
        Line($"SHOP asked on XTSE: {Describe(canadian)}");
        ProviderErrors.Classify(canadian.Error!).Should().Be(ProviderFailureKind.PlanRestricted);

        DailySeries us = (await Retry(() => twelve.GetDailyAsync(new DailySeriesRequest(new ProviderListingKey("XNGS", "SHOP"), new DateOnly(2026, 9, 1), null, true), CancellationToken.None))).Value;
        using var meta = JsonDocument.Parse(_store.Stored.Last(p => p.Operation == "daily").Body);
        string currency = meta.RootElement.GetProperty("meta").GetProperty("currency").GetString()!;
        Line($"SHOP asked on XNGS: {us.Bars.Count} bars, currency {currency} (the US listing, not the Canadian one)");
        currency.Should().Be("USD");
    }

    private async Task DateBoundsAsync(TwelveDataProvider twelve, IHttpClientFactory factory, string key)
    {
        foreach (string end in new[] { "2024-06-20", "2024-06-21" })
        {
            (int status, _, string body) = await Raw(factory, key, $"time_series?symbol=NVDA&mic_code=XNGS&interval=1day&order=asc&adjust=all&start_date=2024-06-03&end_date={end}&outputsize=5000");
            using var d = JsonDocument.Parse(body);
            string[] dates = [.. d.RootElement.GetProperty("values").EnumerateArray().Select(v => v.GetProperty("datetime").GetString()!)];
            Line($"raw end_date={end}: HTTP {status}, first {dates[0]}, last {dates[^1]}, {dates.Length} rows");
        }

        DailySeries series = (await Retry(() => twelve.GetDailyAsync(
            new DailySeriesRequest(new ProviderListingKey("XNGS", "NVDA"), new DateOnly(2024, 6, 3), new DateOnly(2024, 6, 20), true), CancellationToken.None))).Value;
        Line($"adapter From 2024-06-03 To 2024-06-20: first {series.Bars[0].Date}, last {series.Bars[^1].Date}, {series.Bars.Count} bars");
        series.Bars[0].Date.Should().Be(new DateOnly(2024, 6, 3), "start_date is inclusive and 2024-06-03 was a session");
        series.Bars[^1].Date.Should().Be(new DateOnly(2024, 6, 20), "the last wanted day was a session and must be included");
    }

    private async Task AdjustmentAsync(TwelveDataProvider twelve)
    {
        var nvda = new ProviderListingKey("XNGS", "NVDA");
        DateOnly from = new(2024, 6, 3), to = new(2024, 6, 14);
        DailySeries adjusted = (await Retry(() => twelve.GetDailyAsync(new DailySeriesRequest(nvda, from, to, true), CancellationToken.None))).Value;
        DailySeries raw = (await Retry(() => twelve.GetDailyAsync(new DailySeriesRequest(nvda, from, to, false), CancellationToken.None))).Value;
        foreach ((DailyBar a, DailyBar r) in adjusted.Bars.Zip(raw.Bars))
        {
            Line($"NVDA {a.Date}: adjusted {a.Close}, raw {r.Close}, ratio {r.Close / a.Close:F4}");
        }

        DailyBar rawBeforeSplit = raw.Bars.Single(b => b.Date == new DateOnly(2024, 6, 7));
        DailyBar adjBeforeSplit = adjusted.Bars.Single(b => b.Date == new DateOnly(2024, 6, 7));
        (rawBeforeSplit.Close / adjBeforeSplit.Close).Should().BeInRange(9.8m, 10.2m, "NVDA split 10 for 1 on 2024-06-10");

        // Adjusted closes are also adjusted for every dividend paid since, so after the split raw and adjusted differ by a small, constant factor
        // (about 0.3% for NVDA), and before the split the factor is that one times the split ratio.
        decimal afterFactor = raw.Bars.Single(b => b.Date == new DateOnly(2024, 6, 14)).Close / adjusted.Bars.Single(b => b.Date == new DateOnly(2024, 6, 14)).Close;
        afterFactor.Should().BeInRange(1.0m, 1.02m, "only later dividends separate raw and adjusted after the split");
        (rawBeforeSplit.Close / adjBeforeSplit.Close / afterFactor).Should().BeInRange(9.99m, 10.01m, "the split ratio is exactly 10 once the dividend factor is taken out");

        var aapl = new ProviderListingKey("XNGS", "AAPL");
        var oldFrom = new DateOnly(2020, 8, 24);
        var oldTo = new DateOnly(2020, 8, 31);
        DailySeries aAdj = (await Retry(() => twelve.GetDailyAsync(new DailySeriesRequest(aapl, oldFrom, oldTo, true), CancellationToken.None))).Value;
        DailySeries aRaw = (await Retry(() => twelve.GetDailyAsync(new DailySeriesRequest(aapl, oldFrom, oldTo, false), CancellationToken.None))).Value;
        foreach ((DailyBar a, DailyBar r) in aAdj.Bars.Zip(aRaw.Bars))
        {
            Line($"AAPL {a.Date}: adjusted {a.Close}, raw {r.Close}, ratio {r.Close / a.Close:F4}");
        }

        aRaw.Bars.Single(b => b.Date == new DateOnly(2020, 8, 28)).Close.Should().BeGreaterThan(aAdj.Bars.Single(b => b.Date == new DateOnly(2020, 8, 28)).Close * 3m, "AAPL split 4 for 1 on 2020-08-31");
    }

    private async Task LatestBarAsync(TwelveDataProvider twelve)
    {
        DateOnly from = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-45);
        DailySeries series = (await Retry(() => twelve.GetDailyAsync(new DailySeriesRequest(new ProviderListingKey("XNGS", "AAPL"), from, null, true), CancellationToken.None))).Value;
        long[] volumes = [.. series.Bars.SkipLast(1).TakeLast(20).Select(b => b.Volume).Order()];
        DailyBar last = series.Bars[^1];
        double ratio = last.Volume / (double)volumes[volumes.Length / 2];
        IExchangeCalendar calendar = ExchangeCalendars.Default().TryGet(Mic.Parse("XNYS")).Value;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var todayNy = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).DateTime);
        Line($"Last bar {last.Date}, volume {last.Volume}, {ratio:P0} of the median of the 20 before. New York date now {todayNy}; session closed: {calendar.CloseOf(todayNy) is { } c && now >= c}.");
        series.Bars[^1].Date.Should().BeOnOrBefore(todayNy);
        series.Bars.Select(b => b.Date).Should().OnlyContain(d => calendar.IsTradingDay(d));
    }

    private async Task DottedTickersAsync(TwelveDataProvider twelve)
    {
        foreach (string symbol in new[] { "BRK.B", "BF.B" })
        {
            Result<IReadOnlyList<ProviderListing>> search = await Retry(() => twelve.SearchAsync(symbol, CancellationToken.None));
            string found = Describe(search);
            if (search.IsSuccess)
            {
                found = string.Join(", ", search.Value.Select(l => $"{l.Key.ProviderMic}:{l.Key.Symbol}"));
            }

            Line($"search {symbol}: {found}");
            if (search.IsSuccess && search.Value.FirstOrDefault(l => l.Key.Symbol.StartsWith(symbol[..2], StringComparison.OrdinalIgnoreCase)) is { } listing)
            {
                Result<DailySeries> series = await twelve.GetDailyAsync(new DailySeriesRequest(listing.Key, new DateOnly(2026, 9, 1), null, true), CancellationToken.None);
                Line($"daily {listing.Key.Symbol} on {listing.Key.ProviderMic}: {Describe(series)}");
                Ticker.Create(listing.Key.Symbol).IsSuccess.Should().BeTrue($"the provider's own spelling {listing.Key.Symbol} must be a valid ticker");
            }
        }
    }

    private async Task ErrorsAsync(TwelveDataProvider twelve, IHttpClientFactory factory, string key)
    {
        Result<DailySeries> unknown = await twelve.GetDailyAsync(new DailySeriesRequest(new ProviderListingKey("XNGS", "ZZZZNOTREAL"), null, null, true), CancellationToken.None);
        Line($"unknown symbol: {Describe(unknown)}");
        ProviderErrors.Classify(unknown.Error!).Should().Be(ProviderFailureKind.SymbolNotFound);

        var bad = new TwelveDataProvider(
            factory,
            Options.Create(new TwelveDataOptions { ApiKey = key + "-wrong" }),
            _budget,
            _store,
            TimeProvider.System,
            NullLogger<TwelveDataProvider>.Instance);
        Result<DailySeries> refused = await bad.GetDailyAsync(new DailySeriesRequest(new ProviderListingKey("XNGS", "AAPL"), null, null, true), CancellationToken.None);
        Line($"wrong key: {Describe(refused)}");
        refused.IsSuccess.Should().BeFalse();
        ProviderErrors.Classify(refused.Error!).Should().Be(ProviderFailureKind.Permanent, "a refused key is never retried");
        refused.Error!.Code.Should().Be(ProviderErrors.UnauthorizedCode);

        (int status, _, string body) = await Raw(factory, key + "-wrong", "time_series?symbol=AAPL&interval=1day&outputsize=1");
        Line($"raw wrong key: HTTP {status}, body {Truncate(body)}");
    }

    private async Task CreditCostsAsync(IHttpClientFactory factory, string key)
    {
        long baseline = await UsageDeltaAsync(factory, key, () => Task.CompletedTask);
        Line($"two api_usage calls with nothing between them: daily delta {baseline}");
        foreach ((string label, string path) in new[]
        {
            ("symbol_search", "symbol_search?symbol=MSFT&outputsize=5"),
            ("time_series ok (1 row)", "time_series?symbol=MSFT&mic_code=XNGS&interval=1day&outputsize=1"),
            ("time_series, unknown symbol", "time_series?symbol=ZZZZNOTREAL&interval=1day&outputsize=1"),
            ("time_series, plan restricted", "time_series?symbol=SHOP&mic_code=XTSE&interval=1day&outputsize=1"),
        })
        {
            long delta = await UsageDeltaAsync(factory, key, async () => _ = await Raw(factory, key, path));
            Line($"{label}: daily delta {delta} (minus the api_usage baseline: {delta - baseline})");
        }
    }

    // Observation only: the product does not read these endpoints yet. Whether the free plan serves them, what they cost and what they look like
    // decide whether corporate actions are ingested from the provider or kept deduced from the rewrite of the adjusted history.
    private async Task CorporateActionsAsync(IHttpClientFactory factory, string key)
    {
        foreach ((string label, string path) in new[]
        {
            ("splits of NVDA since 2024", "splits?symbol=NVDA&mic_code=XNGS&start_date=2024-01-01&end_date=2024-12-31"),
            ("dividends of AAPL in 2025", "dividends?symbol=AAPL&mic_code=XNGS&start_date=2025-01-01&end_date=2025-12-31"),
        })
        {
            long delta = await UsageDeltaAsync(factory, key, async () =>
            {
                (int status, _, string body) = await Raw(factory, key, path);
                Line($"{label}: HTTP {status}, body {Truncate(body)}");
            });
            Line($"  daily credits moved by {delta} (one of them is the api_usage call that opens the measurement)");
        }
    }

    private async Task NoSessionsAsync(TwelveDataProvider twelve, IHttpClientFactory factory, string key)
    {
        foreach ((string label, string start, string end) in new[] { ("a holiday (Juneteenth 2024)", "2024-06-19", "2024-06-20"), ("a weekend", "2024-06-15", "2024-06-17"), ("the future", "2030-01-02", "2030-01-03") })
        {
            (int status, _, string body) = await Raw(factory, key, $"time_series?symbol=NVDA&mic_code=XNGS&interval=1day&order=asc&adjust=all&start_date={start}&end_date={end}&outputsize=5000");
            Line($"raw {label}: HTTP {status}, body {Truncate(body)}");
        }

        foreach ((DateOnly day, string label) in new[] { (new DateOnly(2024, 6, 19), "holiday"), (new DateOnly(2024, 6, 15), "Saturday") })
        {
            Result<DailySeries> result = await Retry(() => twelve.GetDailyAsync(
                new DailySeriesRequest(new ProviderListingKey("XNGS", "NVDA"), day, day, true), CancellationToken.None));
            Line($"adapter, {label} {day}: {Describe(result)}{(result.IsSuccess ? $", {result.Value.Bars.Count} bars" : string.Empty)}");
            result.IsSuccess.Should().BeTrue($"a {label} has no session, and an empty range must not be a failure");
            result.Value.Bars.Should().BeEmpty();
        }
    }

    private async Task IdenticalRequestsAsync(IHttpClientFactory factory, string key)
    {
        await Task.Delay(TimeSpan.FromSeconds(62));
        long baseline = await UsageDeltaAsync(factory, key, () => Task.CompletedTask);
        long delta = await UsageDeltaAsync(factory, key, async () =>
        {
            for (int i = 0; i < 4; i++)
            {
                (int status, _, _) = await Raw(factory, key, "time_series?symbol=KO&mic_code=XNYS&interval=1day&outputsize=1", paced: false);
                Line($"identical request {i + 1}: HTTP {status}");
            }
        });
        Line($"four identical time_series requests cost {delta - baseline} credit(s) in total (one each would be 4)");
    }

    private async Task<long> UsageDeltaAsync(IHttpClientFactory factory, string key, Func<Task> action)
    {
        long before = await DailyUsageAsync(factory, key) ?? 0;
        await action();
        long after = await DailyUsageAsync(factory, key) ?? 0;
        return after - before;
    }

    private async Task RateLimitAsync(IHttpClientFactory factory, string key)
    {
        var generous = new AlwaysAllow();
        var impatient = new TwelveDataProvider(
            factory,
            Options.Create(new TwelveDataOptions { ApiKey = key }),
            generous,
            new CapturingStore(),
            TimeProvider.System,
            NullLogger<TwelveDataProvider>.Instance);
        var kinds = new List<string>();
        var watch = Stopwatch.StartNew();
        // Different symbols on purpose: identical requests may be served without being charged (section 11).
        string[] symbols = ["AAPL", "MSFT", "NVDA", "GOOGL", "AMZN", "META", "TSLA", "COST", "ADBE", "NFLX", "AMD", "INTC", "CSCO", "PEP"];
        for (int i = 0; i < symbols.Length; i++)
        {
            Result<DailySeries> result = await impatient.GetDailyAsync(
                new DailySeriesRequest(new ProviderListingKey("XNGS", symbols[i]), new DateOnly(2026, 10, 14), null, true), CancellationToken.None);
            kinds.Add(result.IsSuccess ? "ok" : ProviderErrors.Classify(result.Error!).ToString());
        }

        Line($"14 quick time_series calls in {watch.Elapsed.TotalSeconds:F1}s: {string.Join(", ", kinds)}");

        // Look at the raw answer first, so the report has it even when an assertion below fails.
        (int status, HttpResponseHeaders headers, string body) = await Raw(factory, key, "time_series?symbol=MSFT&mic_code=XNGS&interval=1day&outputsize=1", paced: false);
        Line($"raw call right after: HTTP {status}, Retry-After {headers.RetryAfter?.ToString() ?? "absent"}, headers {string.Join("; ", headers.Select(h => h.Key + "=" + string.Join(",", h.Value)))}, body {Truncate(body)}");
        await File.WriteAllTextAsync(Path.Combine(_reportDirectory, "raw-after-rate-limit.json"), body);

        var burst = new List<string>();
        for (int i = 0; i < 14; i++)
        {
            Result<IReadOnlyList<ProviderListing>> search = await impatient.SearchAsync("MSFT", CancellationToken.None);
            burst.Add(search.IsSuccess ? "ok" : ProviderErrors.Classify(search.Error!).ToString());
        }

        Line($"for comparison, 14 quick symbol_search calls right after: {string.Join(", ", burst)}");

        kinds.Should().Contain("RateLimited", "more than eight credits in a minute must be refused by the provider");
        kinds.Where(k => k != "ok" && k != "RateLimited").Should().BeEmpty("a run of 429s must not open the circuit and turn into 'unavailable'");
    }

    private async Task Section(string title, Func<Task> body)
    {
        // SAKER_LIVE_SECTIONS=10 (or 4,10) runs only those sections, for a short follow-up run.
        string? only = Environment.GetEnvironmentVariable("SAKER_LIVE_SECTIONS");
        if (!string.IsNullOrWhiteSpace(only) && !only.Split(',').Contains(title.Split('.')[0]))
        {
            return;
        }

        Line($"\n## {title}");
        try
        {
            await body();
            Line("=> passed");
        }
        catch (Exception ex)
        {
            _failures.Add($"{title}: {ex.GetType().Name}: {Truncate(ex.Message)}");
            Line($"=> FAILED: {ex.GetType().Name}: {Truncate(ex.Message)}");
        }
    }

    private async Task<Result<T>> Retry<T>(Func<Task<Result<T>>> call)
    {
        Result<T> result = await call();
        for (int attempt = 0; attempt < 4 && !result.IsSuccess && ProviderErrors.Classify(result.Error!) == ProviderFailureKind.RateLimited; attempt++)
        {
            TimeSpan wait = ProviderErrors.RetryAfter(result.Error!) ?? TimeSpan.FromSeconds(20);
            Line($"(rate limited, waiting {wait.TotalSeconds:F0}s and trying again)");
            await Task.Delay(wait);
            result = await call();
        }

        return result;
    }

    private async Task<(int Status, HttpResponseHeaders Headers, string Body)> Raw(IHttpClientFactory factory, string key, string path, bool paced = true)
    {
        if (paced)
        {
            await _budget.TryAcquireAsync("twelvedata", new BudgetLimits(8, 800), 1, CancellationToken.None);
        }

        _rawCalls++;
        using HttpClient client = factory.CreateClient(TwelveDataProvider.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("apikey", key);
        using HttpResponseMessage response = await client.SendAsync(request);
        return ((int)response.StatusCode, response.Headers, await response.Content.ReadAsStringAsync());
    }

    private async Task<long?> DailyUsageAsync(IHttpClientFactory factory, string key)
    {
        (int status, _, string body) = await Raw(factory, key, "api_usage");
        if (status != 200)
        {
            return null;
        }

        using var d = JsonDocument.Parse(body);
        return d.RootElement.TryGetProperty("daily_usage", out JsonElement usage) && usage.TryGetInt64(out long value) ? value : null;
    }

    private void Line(string text) => _report.AppendLine(text);

    private static string Describe<T>(Result<T> result) =>
        result.IsSuccess ? "success" : $"{result.Error!.Code} ({ProviderErrors.Classify(result.Error)}) - {result.Error.Message}";

    private static string Show(DateOnly[] dates)
    {
        if (dates.Length == 0)
        {
            return string.Empty;
        }

        string more = dates.Length > 8 ? ", ..." : string.Empty;
        return "[" + string.Join(", ", dates.Take(8)) + more + "]";
    }

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300] + "...";
}
