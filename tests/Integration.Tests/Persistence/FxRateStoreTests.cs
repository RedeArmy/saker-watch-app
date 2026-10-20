using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Npgsql;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.MarketData.Domain.Fx;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Infrastructure.Persistence;

namespace SakerWatch.Integration.Tests.Persistence;

[Trait("Category", "Integration")]
public sealed class FxRateStoreTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    private static readonly DateTimeOffset _now = new(2026, 10, 17, 3, 0, 0, TimeSpan.Zero);
    private static readonly CurrencyCode _usd = CurrencyCode.Parse("USD");
    private static readonly CurrencyCode _gtq = CurrencyCode.Parse("GTQ");

    private static FxRate Rate(int day, decimal rate, string source = "banguat", string quote = "GTQ") =>
        FxRate.Create(_usd, CurrencyCode.Parse(quote), new DateOnly(2026, 10, day), rate, source).Value;

    private static async Task<T> InSessionAsync<T>(string connection, Func<FxRateStore, Task<T>> action)
    {
        await using var session = new DbSession(Options.Create(new PostgresOptions { Postgres = connection }), new AnonymousCurrentUser());
        await session.BeginAsync(CancellationToken.None);
        T result = await action(new FxRateStore(session));
        await session.CommitAsync(CancellationToken.None);
        return result;
    }

    [Fact]
    public async Task Upsert_StoresEveryDay_AndTheSameBatchTwiceIsStillOneRowPerDay()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        FxRate[] rates = [Rate(14, 7.63988m), Rate(15, 7.6414m), Rate(16, 7.64572m)];

        await InSessionAsync(db.Worker, store => store.UpsertAsync(rates, _now, CancellationToken.None));
        int again = await InSessionAsync(db.Worker, store => store.UpsertAsync(rates, _now, CancellationToken.None));

        again.Should().Be(3);
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.fx_rates")).Should().Be(3);
    }

    [Fact]
    public async Task Upsert_KeepsTheRateExactly_AndALaterPublicationReplacesIt()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        await InSessionAsync(db.Worker, store => store.UpsertAsync([Rate(16, 7.6457249999m)], _now, CancellationToken.None));
        (await db.Admin.ScalarAsync<decimal>("SELECT rate FROM marketdata.fx_rates")).Should().Be(7.6457249999m);

        await InSessionAsync(db.Worker, store => store.UpsertAsync([Rate(16, 7.65m)], _now.AddDays(1), CancellationToken.None));

        (await db.Admin.ScalarAsync<decimal>("SELECT rate FROM marketdata.fx_rates")).Should().Be(7.65m);
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.fx_rates")).Should().Be(1);
    }

    [Fact]
    public async Task Upsert_WithNothingToWrite_WritesNothing()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();

        (await InSessionAsync(db.Worker, store => store.UpsertAsync([], _now, CancellationToken.None))).Should().Be(0);
    }

    [Fact]
    public async Task OnOrBefore_OnAPublishedDay_ReturnsThatDay()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        await InSessionAsync(db.Worker, store => store.UpsertAsync([Rate(15, 7.6414m), Rate(16, 7.64572m)], _now, CancellationToken.None));

        FxRate? found = await InSessionAsync(db.Worker, store => store.OnOrBeforeAsync(_usd, _gtq, new DateOnly(2026, 10, 16), CancellationToken.None));

        found.Should().Be(Rate(16, 7.64572m));
    }

    [Fact]
    public async Task OnOrBefore_OnADayWithoutARate_ReturnsTheEarlierOneWithItsOwnDate()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        await InSessionAsync(db.Worker, store => store.UpsertAsync([Rate(9, 7.6m), Rate(16, 7.64572m)], _now, CancellationToken.None));

        FxRate? found = await InSessionAsync(db.Worker, store => store.OnOrBeforeAsync(_usd, _gtq, new DateOnly(2026, 10, 20), CancellationToken.None));

        found!.AsOfDate.Should().Be(new DateOnly(2026, 10, 16), "the rate is never given a date it was not published for");
    }

    [Fact]
    public async Task OnOrBefore_BeforeTheFirstRate_OrForAnotherPair_ReturnsNothing()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        await InSessionAsync(db.Worker, store => store.UpsertAsync([Rate(16, 7.64572m)], _now, CancellationToken.None));

        (await InSessionAsync(db.Worker, store => store.OnOrBeforeAsync(_usd, _gtq, new DateOnly(2026, 10, 1), CancellationToken.None))).Should().BeNull();
        (await InSessionAsync(db.Worker, store => store.OnOrBeforeAsync(_usd, CurrencyCode.Parse("CAD"), new DateOnly(2026, 10, 20), CancellationToken.None))).Should().BeNull();
        (await InSessionAsync(db.Worker, store => store.OnOrBeforeAsync(_gtq, _usd, new DateOnly(2026, 10, 20), CancellationToken.None))).Should().BeNull("a rate is not inverted implicitly");
    }

    [Fact]
    public async Task OnOrBefore_TwoSourcesOnTheSameDay_AnswerWithTheSameOneEveryTime()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        await InSessionAsync(db.Worker, store => store.UpsertAsync([Rate(16, 7.7m, "zeta"), Rate(16, 7.6m, "alpha")], _now, CancellationToken.None));

        FxRate? found = await InSessionAsync(db.Worker, store => store.OnOrBeforeAsync(_usd, _gtq, new DateOnly(2026, 10, 16), CancellationToken.None));

        found!.Source.Should().Be("alpha");
    }

    [Fact]
    public async Task LatestDate_IsPerPairAndSource()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        await InSessionAsync(db.Worker, store => store.UpsertAsync([Rate(14, 7.6m), Rate(16, 7.64572m), Rate(18, 1.4m, "other")], _now, CancellationToken.None));

        (await InSessionAsync(db.Worker, store => store.LatestDateAsync(_usd, _gtq, "banguat", CancellationToken.None))).Should().Be(new DateOnly(2026, 10, 16));
        (await InSessionAsync(db.Worker, store => store.LatestDateAsync(_usd, _gtq, "nobody", CancellationToken.None))).Should().BeNull();
    }

    [Fact]
    public async Task Table_Grants_ApiReadsWorkerWritesNobodyDeletes()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        const string insert = "INSERT INTO marketdata.fx_rates (base, quote, as_of_date, source, rate, ingested_at) VALUES ('USD', 'GTQ', '2026-10-16', 'banguat', 7.6, now())";

        await db.Worker.ExecuteAsync(insert);

        (await db.Api.AsUserScalarAsync<long>(null, "SELECT count(*) FROM marketdata.fx_rates")).Should().Be(1);
        await ExpectAsync(() => db.Api.AsUserExecuteAsync(null, insert.Replace("2026-10-16", "2026-10-17", StringComparison.Ordinal)), "42501");
        await ExpectAsync(() => db.Api.AsUserExecuteAsync(null, "UPDATE marketdata.fx_rates SET rate = 1"), "42501");
        (await db.Worker.ExecuteAsync("UPDATE marketdata.fx_rates SET rate = 7.7")).Should().Be(1);
        await ExpectAsync(() => db.Worker.ExecuteAsync("DELETE FROM marketdata.fx_rates"), "42501");
    }

    [Theory]
    [InlineData("'usd', 'GTQ', 7.6, 'banguat'")]
    [InlineData("'USD', 'USD', 1, 'banguat'")]
    [InlineData("'USD', 'GTQ', 0, 'banguat'")]
    [InlineData("'USD', 'GTQ', 7.6, 'Banguat'")]
    public async Task Table_Checks_RefuseAMalformedRow(string values)
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();

        await ExpectAsync(
            () => db.Worker.ExecuteAsync($"INSERT INTO marketdata.fx_rates (base, quote, rate, source, as_of_date, ingested_at) SELECT v.a, v.b, v.c, v.d, '2026-10-16', now() FROM (VALUES ({values})) AS v(a, b, c, d)"),
            "23514");
    }

    private static async Task ExpectAsync(Func<Task> action, string sqlState)
    {
        PostgresException error = (await action.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(sqlState);
    }
}
