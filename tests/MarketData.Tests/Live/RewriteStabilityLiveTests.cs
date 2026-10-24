using System.Globalization;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Infrastructure;
using SakerWatch.MarketData.Infrastructure.Providers.TwelveData;

namespace SakerWatch.MarketData.Tests.Live;

/// <summary>
/// Does the provider's adjusted history change between two calls when no corporate action happened in between? The daily refresh reads a change of
/// more than <see cref="IngestionOptions.RewriteTolerance"/> as a rewrite and queues a two-credit reload (docs/runbooks/provider-quota.md), so a
/// series that moves on its own, most likely for low-priced stocks whose closes have few significant digits, would waste credits and leave stocks stale.
/// The check fetches the last weeks of adjusted closes of a few symbols twice, <c>SAKER_LIVE_REWRITE_WAIT_MINUTES</c> apart (default 10), and compares
/// the completed sessions; it also compares the first pass with the snapshot an earlier run left in the report directory, so running it again on another
/// day covers a longer gap. About sixteen credits a run (eight symbols, two passes); the report says how many closed under $5, the case it exists for. Symbols can be changed with <c>SAKER_LIVE_REWRITE_SYMBOLS</c> (<c>MIC:SYMBOL,...</c>).
/// </summary>
public sealed class RewriteStabilityLiveTests
{
    private const string _defaultSymbols = "XNGS:AAPL,XNYS:KO,XNYS:F,ARCX:SPY,XNGS:SIRI,XNYS:NOK,XNYS:RIG,XNYS:BB";

    // Under this price one unit of the last place of a close is as large as the tolerance (docs/runbooks/provider-quota.md), the case the check is for.
    private const decimal _cheap = 5m;

    // Fewest symbols that must answer for the comparison to mean anything.
    private const int _fewest = 3;

    // How many times a symbol is asked again after a failure that may pass, and how long to wait: the breaker of the HTTP client stays open for a while.
    private const int _attempts = 3;
    private static readonly TimeSpan _pause = TimeSpan.FromSeconds(45);

    private static readonly string _reportDirectory = Environment.GetEnvironmentVariable("SAKER_LIVE_REPORT_DIR") ?? "/tmp/saker-live-twelvedata";

    private readonly StringBuilder _report = new();

    [LiveFact]
    public async Task TheAdjustedClosesDoNotMoveByThemselves()
    {
        string key = Environment.GetEnvironmentVariable(LiveFactAttribute.KeyVariable)!;
        double tolerance = new IngestionOptions().RewriteTolerance;
        var wait = TimeSpan.FromMinutes(int.TryParse(Environment.GetEnvironmentVariable("SAKER_LIVE_REWRITE_WAIT_MINUTES"), CultureInfo.InvariantCulture, out int minutes) ? minutes : 10);
        ProviderListingKey[] symbols = [.. (Environment.GetEnvironmentVariable("SAKER_LIVE_REWRITE_SYMBOLS") ?? _defaultSymbols)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(pair => pair.Split(':')).Select(parts => new ProviderListingKey(parts[0], parts[1]))];
        Directory.CreateDirectory(_reportDirectory);

        var services = new ServiceCollection();
        // What the adapter logs when a call fails is the only trace of why (a timeout, a refused connection, an open circuit), so it goes into the report.
        services.AddLogging(logging => logging.AddProvider(new ReportLogger(Line)));
        services.AddSingleton<IProviderCallBudget>(new TwelveDataLiveTests.PacedBudget());
        services.AddSingleton<IProviderPayloadStore>(new TwelveDataLiveTests.CapturingStore());
        services.AddMarketData(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TwelveData:ApiKey"] = key,
            ["MarketData:Providers:Markets:XNAS"] = "twelvedata",
            ["MarketData:Providers:Markets:XNYS"] = "twelvedata",
        }).Build());
        await using ServiceProvider provider = services.BuildServiceProvider();
        var twelve = provider.GetRequiredService<TwelveDataProvider>();

        Line($"# Stability of the adjusted closes, {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC, tolerance {tolerance.ToString("0.0E+0", CultureInfo.InvariantCulture)}, {wait.TotalMinutes} minutes between passes");
        Dictionary<string, Dictionary<string, string>> first = await PassAsync(twelve, symbols);
        if (first.Count < _fewest)
        {
            // Nothing to compare and nothing worth waiting ten minutes for; the snapshot of an earlier run is left alone.
            Line($"\n## Result\nonly {first.Count} of {symbols.Length} symbols answered in the first pass (at least {_fewest} are needed): the run stops here and the snapshot of the earlier run is untouched. The lines above say why each symbol failed.");
            await File.WriteAllTextAsync(Path.Combine(_reportDirectory, "rewrite-stability-report.md"), _report.ToString());
            first.Count.Should().BeGreaterThanOrEqualTo(_fewest, "see the report in " + _reportDirectory);
        }

        string snapshotPath = Path.Combine(_reportDirectory, "rewrite-stability-snapshot.json");
        int crossings = 0;
        if (File.Exists(snapshotPath) && DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(snapshotPath) > TimeSpan.FromHours(1))
        {
            var previous = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(await File.ReadAllTextAsync(snapshotPath))!;
            Line($"\n## Against the snapshot of {File.GetLastWriteTimeUtc(snapshotPath):yyyy-MM-dd HH:mm} UTC");
            crossings += Compare(previous, first, tolerance);
        }

        await File.WriteAllTextAsync(snapshotPath, JsonSerializer.Serialize(first));
        await Task.Delay(wait);
        Dictionary<string, Dictionary<string, string>> second = await PassAsync(twelve, symbols);
        Line($"\n## Second pass against the first, {wait.TotalMinutes} minutes later");
        crossings += Compare(first, second, tolerance);

        int cheap = second.Values.Count(closes => closes.Count > 0 && decimal.Parse(closes.OrderBy(pair => pair.Key, StringComparer.Ordinal).Last().Value, CultureInfo.InvariantCulture) < _cheap);
        Line($"\n## Coverage\n{cheap} of {second.Count} symbols closed under ${_cheap} on their last session. "
            + (cheap == 0 ? "**None is cheap, so the case this check exists for was not tested:** name some with SAKER_LIVE_REWRITE_SYMBOLS and run it again." : "The low-priced case is covered."));
        Line($"\n## Result\n{(crossings == 0 ? "no completed session moved beyond the tolerance" : $"{crossings} closes moved beyond the tolerance without a corporate action: RewriteTolerance reads provider noise as a rewrite for them")}");
        await File.WriteAllTextAsync(Path.Combine(_reportDirectory, "rewrite-stability-report.md"), _report.ToString());
        second.Count.Should().BeGreaterThanOrEqualTo(_fewest, "at least " + _fewest + " symbols must answer the second pass for the comparison to mean anything");
        crossings.Should().Be(0, "see the report in " + _reportDirectory);
    }

    // The completed sessions of the last three weeks (the refresh compares the last ten days); today's bar is left out because it is still moving.
    private async Task<Dictionary<string, Dictionary<string, string>>> PassAsync(TwelveDataProvider twelve, ProviderListingKey[] symbols)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var pass = new Dictionary<string, Dictionary<string, string>>();
        foreach (ProviderListingKey symbol in symbols)
        {
            var request = new DailySeriesRequest(symbol, today.AddDays(-21), null, Adjusted: true);
            var result = await twelve.GetDailyAsync(request, CancellationToken.None);
            for (int attempt = 1; attempt < _attempts && !result.IsSuccess && ProviderErrors.Classify(result.Error!) is ProviderFailureKind.Transient or ProviderFailureKind.RateLimited; attempt++)
            {
                Line($"- {symbol.ProviderMic}:{symbol.Symbol}: {result.Error!.Code}; waiting {_pause.TotalSeconds:0} s before attempt {attempt + 1} of {_attempts}");
                await Task.Delay(_pause);
                result = await twelve.GetDailyAsync(request, CancellationToken.None);
            }

            if (!result.IsSuccess)
            {
                Line($"- {symbol.ProviderMic}:{symbol.Symbol}: not read ({result.Error!.Code}: {result.Error.Message}); left out");
                continue;
            }

            pass[$"{symbol.ProviderMic}:{symbol.Symbol}"] = result.Value.Bars.Where(bar => bar.Date < today)
                .ToDictionary(bar => bar.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), bar => bar.Close.ToString(CultureInfo.InvariantCulture));
        }

        return pass;
    }

    private int Compare(Dictionary<string, Dictionary<string, string>> before, Dictionary<string, Dictionary<string, string>> after, double tolerance)
    {
        int crossings = 0;
        Line("| symbol | last close | sessions compared | largest relative change | beyond the tolerance |\n|---|---|---|---|---|");
        foreach ((string symbol, Dictionary<string, string> now) in after)
        {
            if (!before.TryGetValue(symbol, out Dictionary<string, string>? then))
            {
                continue;
            }

            string[] days = [.. now.Keys.Intersect(then.Keys).Order(StringComparer.Ordinal)];
            if (days.Length == 0)
            {
                continue;
            }

            double largest = 0;
            int beyond = 0;
            foreach (string day in days)
            {
                decimal old = decimal.Parse(then[day], CultureInfo.InvariantCulture), fresh = decimal.Parse(now[day], CultureInfo.InvariantCulture);
                double change = old > 0 ? (double)(Math.Abs(fresh - old) / old) : 0;
                largest = Math.Max(largest, change);
                if (change > tolerance)
                {
                    beyond++;
                    Line($"  - {symbol} {day}: {old} -> {fresh}");
                }
            }

            crossings += beyond;
            Line($"| {symbol} | {now[days[^1]]} | {days.Length} | {largest.ToString("0.0E+0", CultureInfo.InvariantCulture)} | {beyond} |");
        }

        return crossings;
    }

    private void Line(string text) => _report.AppendLine(text);

    // Warnings and errors of the adapter and its HTTP client, with the type and message of the exception: no secret is in them (the key travels in a header).
    private sealed class ReportLogger(Action<string> write) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Sink(write, categoryName);

        public void Dispose()
        {
        }

        private sealed class Sink(Action<string> write, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    write($"  [{logLevel}] {category}: {formatter(state, exception)}{(exception is null ? string.Empty : $" ({exception.GetType().Name}: {exception.Message})")}");
                }
            }
        }
    }
}
