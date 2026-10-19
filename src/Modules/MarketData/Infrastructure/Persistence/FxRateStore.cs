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
