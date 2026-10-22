using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Kernel.Transactions;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Application.Lookup;

/// <summary>
/// <see cref="IStockCatalog"/> whose every read is a short unit of work of its own. Inside a unit of work that is already open (a query, a job) it
/// joins it and changes nothing; with none open (the registration of a stock, which asks a provider first) it opens a connection for the read
/// and gives it back, so the caller does not hold one across a slow call (ADR-0044).
/// </summary>
/// <param name="inner">The catalog over the database.</param>
/// <param name="unit">The scope's unit of work.</param>
public sealed class UnitScopedStockCatalog(IStockCatalog inner, IUnitOfWork unit) : IStockCatalog
{
    /// <inheritdoc />
    public Task<CatalogStock?> FindAsync(StockId id, CancellationToken cancellationToken) => InUnitAsync(() => inner.FindAsync(id, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<CatalogListing?> FindByKeyAsync(Mic market, Ticker ticker, CancellationToken cancellationToken) =>
        InUnitAsync(() => inner.FindByKeyAsync(market, ticker, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<CatalogListing>> FindManyAsync(IReadOnlyCollection<StockId> ids, CancellationToken cancellationToken) =>
        InUnitAsync(() => inner.FindManyAsync(ids, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<StockId>> ListIngestableAsync(Mic market, CancellationToken cancellationToken) =>
        InUnitAsync(() => inner.ListIngestableAsync(market, cancellationToken), cancellationToken);

    private async Task<T> InUnitAsync<T>(Func<Task<T>> read, CancellationToken cancellationToken)
    {
        await unit.BeginAsync(cancellationToken);
        try
        {
            T result = await read();
            await unit.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await unit.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
