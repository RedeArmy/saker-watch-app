using SakerWatch.BuildingBlocks.Kernel.Transactions;

namespace SakerWatch.MarketData.Application.Lookup;

/// <summary>
/// <see cref="ICatalogSize"/> whose read is a short unit of work of its own, like <see cref="UnitScopedStockCatalog"/>: the registration of a stock asks a
/// provider first and must not hold a connection across that call (ADR-0044).
/// </summary>
/// <param name="inner">The count over the database.</param>
/// <param name="unit">The scope's unit of work.</param>
public sealed class UnitScopedCatalogSize(ICatalogSize inner, IUnitOfWork unit) : ICatalogSize
{
    /// <inheritdoc />
    public async Task<int> ActiveListingsAsync(CancellationToken cancellationToken)
    {
        await unit.BeginAsync(cancellationToken);
        try
        {
            int count = await inner.ActiveListingsAsync(cancellationToken);
            await unit.CommitAsync(cancellationToken);
            return count;
        }
        catch
        {
            await unit.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
