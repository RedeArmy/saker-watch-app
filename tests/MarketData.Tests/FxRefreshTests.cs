using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Application.Fx;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Fx;
using SakerWatch.MarketData.Domain.Markets;

namespace SakerWatch.MarketData.Tests;

public sealed class FxRefreshTests
{
    private static readonly CurrencyCode _usd = CurrencyCode.Parse("USD");
    private static readonly CurrencyCode _gtq = CurrencyCode.Parse("GTQ");
    private static readonly DateTimeOffset _now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly _day = new(2026, 10, 17);

    private sealed class Provider(Func<DateOnly, DateOnly, Result<IReadOnlyList<FxRate>>> answer) : IFxRateProvider
    {
        public List<(DateOnly From, DateOnly To)> Asked { get; } = [];

        public string Name => "banguat";

        public Task<Result<IReadOnlyList<FxRate>>> GetDailyAsync(CurrencyCode baseCurrency, CurrencyCode quote, DateOnly from, DateOnly to, CancellationToken cancellationToken)
        {
            Asked.Add((from, to));
            return Task.FromResult(answer(from, to));
        }
    }

    private sealed class Store(DateOnly? latest = null) : IFxRateStore
    {
        public List<FxRate> Written { get; } = [];

        public Task<int> UpsertAsync(IReadOnlyList<FxRate> rates, DateTimeOffset ingestedAt, CancellationToken cancellationToken)
        {
            Written.AddRange(rates);
            return Task.FromResult(rates.Count);
        }

        public Task<FxRate?> OnOrBeforeAsync(CurrencyCode baseCurrency, CurrencyCode quote, DateOnly date, CancellationToken cancellationToken) => Task.FromResult<FxRate?>(null);

        public Task<DateOnly?> LatestDateAsync(CurrencyCode baseCurrency, CurrencyCode quote, string source, CancellationToken cancellationToken) => Task.FromResult(latest);
    }

    private static FxRate Rate(int day) => FxRate.Create(_usd, _gtq, new DateOnly(2026, 10, day), 7.6m, "banguat").Value;

    private static Result<IReadOnlyList<FxRate>> Rates(params FxRate[] rates) => rates;

    private static Task RunAsync(Provider provider, Store store, FxRefreshJob? job = null) =>
        new FxRefreshHandler(provider, store, Options.Create(new IngestionOptions()), new FakeTimeProvider(_now), NullLogger<FxRefreshHandler>.Instance)
            .HandleAsync(job ?? new FxRefreshJob("USD", "GTQ", _day), new JobContext(Guid.CreateVersion7(), "k", 1, 8, null), CancellationToken.None);

    [Fact]
    public async Task Refresh_WithNothingStored_AsksForTenYearsAndStoresWhatComesBack()
    {
        var provider = new Provider((_, _) => Rates(Rate(16), Rate(17)));
        var store = new Store();

        await RunAsync(provider, store);

        provider.Asked.Should().ContainSingle().Which.Should().Be((new DateOnly(2016, 10, 17), _day));
        store.Written.Should().HaveCount(2);
    }

    [Fact]
    public async Task Refresh_WithRatesStored_AsksOnlyForTheLastFewDaysBeforeTheNewestOne()
    {
        var provider = new Provider((_, _) => Rates(Rate(17)));

        await RunAsync(provider, new Store(latest: new DateOnly(2026, 10, 14)));

        provider.Asked.Should().ContainSingle().Which.Should().Be((new DateOnly(2026, 10, 9), _day));
    }

    [Fact]
    public async Task Refresh_WhenTheNewestRateIsAfterTheJobsDay_NeverAsksForABackwardsRange()
    {
        // The bank publishes tomorrow's rate, so a job that ran earlier can find a newer day stored.
        var provider = new Provider((_, _) => Rates());

        await RunAsync(provider, new Store(latest: new DateOnly(2026, 10, 30)), new FxRefreshJob("USD", "GTQ", new DateOnly(2026, 10, 17)));

        provider.Asked.Should().ContainSingle().Which.From.Should().BeOnOrBefore(_day);
    }

    [Fact]
    public async Task Refresh_AnEmptyAnswer_IsFine()
    {
        var store = new Store();

        await RunAsync(new Provider((_, _) => Rates()), store);

        store.Written.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ProviderErrors.UnavailableCode)]
    [InlineData(ProviderErrors.RateLimitedCode)]
    public async Task Refresh_AFailureThatRetryingCanFix_ThrowsSoTheQueueRetries(string code)
    {
        var provider = new Provider((_, _) => new Error(code, "x", ErrorType.Failure));
        var store = new Store();

        Func<Task> act = () => RunAsync(provider, store);

        (await act.Should().ThrowAsync<IngestionRetryException>()).Which.Code.Should().Be(code);
        store.Written.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ProviderErrors.InvalidResponseCode)]
    [InlineData(ProviderErrors.UnauthorizedCode)]
    public async Task Refresh_AFailureThatRetryingCannotFix_EndsTheJobWithoutWritingAnything(string code)
    {
        var store = new Store();

        Func<Task> act = () => RunAsync(new Provider((_, _) => new Error(code, "x", ErrorType.Failure)), store);

        await act.Should().NotThrowAsync();
        store.Written.Should().BeEmpty();
    }

    [Fact]
    public async Task Refresh_AJobWithAMalformedCurrency_EndsWithoutCallingTheProvider()
    {
        var provider = new Provider((_, _) => Rates());

        await RunAsync(provider, new Store(), new FxRefreshJob("US", "GTQ", _day));

        provider.Asked.Should().BeEmpty();
    }

    [Fact]
    public void Key_IsThePairAndTheDay() =>
        FxRefreshJob.KeyFor(_usd, _gtq, _day).Should().Be("marketdata.fx-refresh:USD:GTQ:20261017");

    [Fact]
    public async Task Refresh_CountsItsOutcomesAndTheRowsWritten()
    {
        var outcomes = new List<string>();
        long rows = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name is "sakerwatch_marketdata_fx_refreshes_total" or "sakerwatch_marketdata_fx_rows_total")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            lock (outcomes)
            {
                if (instrument.Name.EndsWith("rows_total", StringComparison.Ordinal))
                {
                    rows += value;
                }
                else
                {
                    outcomes.AddRange(tags.ToArray().Select(t => (string)t.Value!));
                }
            }
        });
        listener.Start();

        await RunAsync(new Provider((_, _) => Rates(Rate(16), Rate(17))), new Store());
        await RunAsync(new Provider((_, _) => new Error(ProviderErrors.InvalidResponseCode, "x", ErrorType.Failure)), new Store());
        Func<Task> retried = () => RunAsync(new Provider((_, _) => ProviderErrors.Unavailable("banguat")), new Store());
        await retried.Should().ThrowAsync<IngestionRetryException>();

        lock (outcomes)
        {
            outcomes.Should().Contain(["completed", "failed", "retry"]);
            rows.Should().BeGreaterThanOrEqualTo(2);
        }
    }
}
