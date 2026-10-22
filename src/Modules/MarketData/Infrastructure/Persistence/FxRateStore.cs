using Npgsql;
using NpgsqlTypes;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.MarketData.Application.Fx;
using SakerWatch.MarketData.Domain.Fx;
using SakerWatch.MarketData.Domain.Markets;

namespace SakerWatch.MarketData.Infrastructure.Persistence;

/// <summary>
/// <see cref="IFxRateStore"/> over <c>marketdata.fx_rates</c>. A batch is one statement: the rates travel as arrays and are unnested, so ten
/// years of a pair is one round trip and every value is a parameter.
/// </summary>
/// <param name="session">The scope's database connection and transaction.</param>
public sealed class FxRateStore(DbSession session) : IFxRateStore
{
    // About 3 700 rows are ten years of one pair, so the usual load is a single statement.
    private const int _batchSize = 5000;

    private const string _upsert = """
        INSERT INTO marketdata.fx_rates (base, quote, as_of_date, source, rate, ingested_at)
        SELECT t.base, t.quote, t.as_of_date, t.source, t.rate, @ingested_at
        FROM unnest(@bases, @quotes, @dates, @sources, @rates) AS t(base, quote, as_of_date, source, rate)
        ON CONFLICT (base, quote, as_of_date, source) DO UPDATE SET
            rate = EXCLUDED.rate,
            ingested_at = EXCLUDED.ingested_at
        """;

    /// <inheritdoc />
    public async Task<int> UpsertAsync(IReadOnlyList<FxRate> rates, DateTimeOffset ingestedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rates);

        int written = 0;
        foreach (FxRate[] batch in rates.Chunk(_batchSize))
        {
            await using NpgsqlCommand command = Command(_upsert);
            command.Parameters.AddWithValue("ingested_at", ingestedAt.ToUniversalTime());
            command.Parameters.AddWithValue("bases", batch.Select(r => r.Base.Value).ToArray());
            command.Parameters.AddWithValue("quotes", batch.Select(r => r.Quote.Value).ToArray());
            command.Parameters.AddWithValue("dates", batch.Select(r => r.AsOfDate).ToArray());
            command.Parameters.AddWithValue("sources", batch.Select(r => r.Source).ToArray());
            command.Parameters.AddWithValue("rates", batch.Select(r => r.Rate).ToArray());
            written += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return written;
    }

    /// <inheritdoc />
    public async Task<FxRate?> OnOrBeforeAsync(CurrencyCode baseCurrency, CurrencyCode quote, DateOnly date, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseCurrency);
        ArgumentNullException.ThrowIfNull(quote);

        await using NpgsqlCommand command = Command(
            "SELECT as_of_date, rate, source FROM marketdata.fx_rates WHERE base = @base AND quote = @quote AND as_of_date <= @date ORDER BY as_of_date DESC, source LIMIT 1");
        command.Parameters.AddWithValue("base", baseCurrency.Value);
        command.Parameters.AddWithValue("quote", quote.Value);
        command.Parameters.Add(new NpgsqlParameter("date", NpgsqlDbType.Date) { Value = date });

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? FxRate.Create(baseCurrency, quote, reader.GetFieldValue<DateOnly>(0), reader.GetDecimal(1), reader.GetString(2)).Value
            : null;
    }

    /// <inheritdoc />
    public async Task<DateOnly?> LatestDateAsync(CurrencyCode baseCurrency, CurrencyCode quote, string source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseCurrency);
        ArgumentNullException.ThrowIfNull(quote);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        await using NpgsqlCommand command = Command(
            "SELECT max(as_of_date) FROM marketdata.fx_rates WHERE base = @base AND quote = @quote AND source = @source");
        command.Parameters.AddWithValue("base", baseCurrency.Value);
        command.Parameters.AddWithValue("quote", quote.Value);
        command.Parameters.AddWithValue("source", source);

        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return value is DateOnly latest ? latest : null;
    }

    private NpgsqlCommand Command(string sql) => new(sql, session.Connection, session.Transaction);
}

/// <summary><see cref="IFxRateScreening"/> over <c>marketdata.fx_rates</c> and <c>marketdata.fx_rates_rejected</c> (ADR-0042).</summary>
/// <param name="session">The scope's database connection and transaction.</param>
public sealed class FxRateScreening(DbSession session) : IFxRateScreening
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<FxRate>> StoredAsync(CurrencyCode baseCurrency, CurrencyCode quote, string source, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseCurrency);
        ArgumentNullException.ThrowIfNull(quote);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        await using var command = new NpgsqlCommand(
            "SELECT as_of_date, rate FROM marketdata.fx_rates WHERE base = @base AND quote = @quote AND source = @source AND as_of_date BETWEEN @from AND @to ORDER BY as_of_date",
            session.Connection,
            session.Transaction);
        command.Parameters.AddWithValue("base", baseCurrency.Value);
        command.Parameters.AddWithValue("quote", quote.Value);
        command.Parameters.AddWithValue("source", source);
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Date) { Value = from });
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Date) { Value = to });

        var stored = new List<FxRate>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            stored.Add(FxRate.Create(baseCurrency, quote, reader.GetFieldValue<DateOnly>(0), reader.GetDecimal(1), source).Value);
        }

        return stored;
    }

    /// <inheritdoc />
    public async Task RejectAsync(IReadOnlyList<RejectedFxRate> rejected, DateTimeOffset seenAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rejected);
        if (rejected.Count == 0)
        {
            return;
        }

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO marketdata.fx_rates_rejected (base, quote, as_of_date, source, rate, reason, seen_at)
            SELECT t.base, t.quote, t.as_of_date, t.source, t.rate, t.reason, @seen_at
            FROM unnest(@bases, @quotes, @dates, @sources, @rates, @reasons) AS t(base, quote, as_of_date, source, rate, reason)
            ON CONFLICT (base, quote, as_of_date, source) DO UPDATE SET rate = EXCLUDED.rate, reason = EXCLUDED.reason, seen_at = EXCLUDED.seen_at
            """,
            session.Connection,
            session.Transaction);
        command.Parameters.AddWithValue("seen_at", seenAt.ToUniversalTime());
        command.Parameters.AddWithValue("bases", rejected.Select(r => r.Rate.Base.Value).ToArray());
        command.Parameters.AddWithValue("quotes", rejected.Select(r => r.Rate.Quote.Value).ToArray());
        command.Parameters.AddWithValue("dates", rejected.Select(r => r.Rate.AsOfDate).ToArray());
        command.Parameters.AddWithValue("sources", rejected.Select(r => r.Rate.Source).ToArray());
        command.Parameters.AddWithValue("rates", rejected.Select(r => r.Rate.Rate).ToArray());
        command.Parameters.AddWithValue("reasons", rejected.Select(r => r.Reason).ToArray());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

