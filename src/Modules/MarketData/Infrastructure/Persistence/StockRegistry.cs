using Npgsql;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.MarketData.Application.Lookup;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Infrastructure.Persistence;

/// <summary>
/// <see cref="IStockRegistry"/> through <c>marketdata.register_stock</c>, the one door the API role has to the catalog (ADR-0032): a
/// security-definer function that inserts the stock, the provider's name for it and its pending ingestion state, and never changes a stock that
/// already exists. It runs in the command's unit of work.
/// </summary>
/// <param name="session">The scope's database session.</param>
public sealed class StockRegistry(DbSession session) : IStockRegistry
{
    /// <inheritdoc />
    public async Task<StockId> RegisterAsync(Stock stock, string provider, ProviderListingKey providerKey, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(providerKey);

        await using var command = new NpgsqlCommand(
            "SELECT marketdata.register_stock(@id, @market, @ticker, @name, @currency, @time_zone, @asset_type, @provider, @provider_mic, @provider_symbol, @now)",
            session.Connection,
            session.Transaction);
        command.Parameters.AddWithValue("id", stock.Id.Value);
        command.Parameters.AddWithValue("market", stock.Market.Value);
        command.Parameters.AddWithValue("ticker", stock.Ticker.Value);
        command.Parameters.AddWithValue("name", stock.Name);
        command.Parameters.AddWithValue("currency", stock.Currency.Value);
        command.Parameters.AddWithValue("time_zone", stock.TimeZoneId);
        command.Parameters.AddWithValue("asset_type", stock.AssetType switch { AssetType.Equity => "equity", AssetType.Etf => "etf", _ => "other" });
        command.Parameters.AddWithValue("provider", provider);
        command.Parameters.AddWithValue("provider_mic", providerKey.ProviderMic);
        command.Parameters.AddWithValue("provider_symbol", providerKey.Symbol);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());

        object? id = await command.ExecuteScalarAsync(cancellationToken);
        return StockId.Create((Guid)id!);
    }
}
