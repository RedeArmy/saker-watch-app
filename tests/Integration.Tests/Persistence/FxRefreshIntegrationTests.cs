using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.MarketData.Application.Fx;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Domain.Fx;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Infrastructure.Persistence;

namespace SakerWatch.Integration.Tests.Persistence;

[Trait("Category", "Integration")]
public sealed class FxRefreshIntegrationTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    private static readonly CurrencyCode _usd = CurrencyCode.Parse("USD");
    private static readonly CurrencyCode _gtq = CurrencyCode.Parse("GTQ");

    private sealed class Bank(params (int Day, decimal Rate)[] published) : IFxRateProvider
    {
        public string Name => "banguat";

        public Task<Result<IReadOnlyList<FxRate>>> GetDailyAsync(CurrencyCode baseCurrency, CurrencyCode quote, DateOnly from, DateOnly to, CancellationToken cancellationToken)
        {
            IReadOnlyList<FxRate> rates = [.. published
                .Select(p => FxRate.Create(baseCurrency, quote, new DateOnly(2026, 10, p.Day), p.Rate, Name).Value)
                .Where(r => r.AsOfDate >= from && r.AsOfDate <= to)];
            return Task.FromResult(Result.Success(rates));
        }
    }

    private static async Task RunAsync(string worker, IFxRateProvider bank, DateOnly day)
    {
        await using var session = new DbSession(Options.Create(new PostgresOptions { Postgres = worker }), new AnonymousCurrentUser());
        await session.BeginAsync(CancellationToken.None);
        var handler = new FxRefreshHandler(bank, new FxRateStore(session), Options.Create(new IngestionOptions()), new FakeTimeProvider(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero)), NullLogger<FxRefreshHandler>.Instance);
        await handler.HandleAsync(new FxRefreshJob("USD", "GTQ", day), new JobContext(Guid.CreateVersion7(), "k", 1, 8, null), CancellationToken.None);
        await session.CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Refresh_StoresTheRates_AndRunningItAgainIsHarmless()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var bank = new Bank((14, 7.63988m), (15, 7.6414m), (16, 7.64572m), (17, 7.64572m));

        await RunAsync(db.Worker, bank, new DateOnly(2026, 10, 17));
        await RunAsync(db.Worker, bank, new DateOnly(2026, 10, 17));

        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.fx_rates")).Should().Be(4);
        (await db.Admin.ScalarAsync<decimal>("SELECT rate FROM marketdata.fx_rates WHERE as_of_date = '2026-10-15'")).Should().Be(7.6414m);
    }

    [Fact]
    public async Task Refresh_ALaterDay_AddsTheNewRatesAndKeepsTheOldOnes()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var bank = new Bank((14, 7.63988m), (15, 7.6414m), (16, 7.64572m), (17, 7.64572m));
        await RunAsync(db.Worker, bank, new DateOnly(2026, 10, 15));
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.fx_rates")).Should().Be(2);

        await RunAsync(db.Worker, bank, new DateOnly(2026, 10, 17));

        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.fx_rates")).Should().Be(4);
        FxRate? latest = await new FxRateStoreReader(db.Worker).OnOrBeforeAsync(_usd, _gtq, new DateOnly(2026, 10, 30));
        latest!.AsOfDate.Should().Be(new DateOnly(2026, 10, 17));
    }

    [Fact]
    public async Task Convert_OverTheStoredRates_GoesBothWays_AndAWeekendTakesFridaysRate()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        await RunAsync(db.Worker, new Bank((16, 7.64572m)), new DateOnly(2026, 10, 16));
        await using var session = new DbSession(Options.Create(new PostgresOptions { Postgres = db.Api }), new AnonymousCurrentUser());
        await session.BeginAsync(CancellationToken.None);
        var converter = new FxConverter(new FxRateStore(session), Options.Create(new IngestionOptions()), new FakeTimeProvider(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero)));

        FxConversion forward = (await converter.ConvertAsync(100m, _usd, _gtq, null, CancellationToken.None)).Value;
        FxConversion back = (await converter.ConvertAsync(764.572m, _gtq, _usd, null, CancellationToken.None)).Value;

        forward.Converted.Should().Be(764.572m);
        forward.Legs.Single().AsOfDate.Should().Be(new DateOnly(2026, 10, 16));
        back.Converted.Should().BeApproximately(100m, 0.0000000001m);
    }

    private sealed class FxRateStoreReader(string connection)
    {
        public async Task<FxRate?> OnOrBeforeAsync(CurrencyCode baseCurrency, CurrencyCode quote, DateOnly date)
        {
            await using var session = new DbSession(Options.Create(new PostgresOptions { Postgres = connection }), new AnonymousCurrentUser());
            await session.BeginAsync(CancellationToken.None);
            FxRate? rate = await new FxRateStore(session).OnOrBeforeAsync(baseCurrency, quote, date, CancellationToken.None);
            await session.CommitAsync(CancellationToken.None);
            return rate;
        }
    }
}
