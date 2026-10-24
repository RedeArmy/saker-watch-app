using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.MarketData.Application.Lookup;

/// <summary>
/// The cap on the size of the shared catalog (ADR-0051). Every active listing is refreshed every day at two credits, so the catalog the data plan can keep
/// fresh is bounded by its credits; a stock added past that bound would be given prices that go stale because the day's credits ran out. The count is
/// read once, so registrations in parallel can pass the cap by their number: it is a margin under the plan's credits and not an exact limit.
/// </summary>
/// <param name="size">How many listings the catalog holds.</param>
/// <param name="lookup">The cap (<see cref="StockLookupOptions.MaxCatalogListings"/>).</param>
/// <param name="logger">Logger.</param>
public sealed partial class CatalogCapacity(ICatalogSize size, IOptions<StockLookupOptions> lookup, ILogger<CatalogCapacity> logger)
{
    /// <summary>The refusal when the catalog is full, or <see langword="null"/> when there is room for one more.</summary>
    public async Task<Error?> CheckAsync(CancellationToken cancellationToken)
    {
        int active = await size.ActiveListingsAsync(cancellationToken);
        if (active < lookup.Value.MaxCatalogListings)
        {
            return null;
        }

        LogCatalogFull(active);
        return Error.Conflict(
            StockDirectory.CatalogFullCode,
            "The shared catalog holds the most stocks the market data plan can keep up to date, so a stock that is not in it yet cannot be added. Stocks that are already in the catalog can still be added.");
    }

    [LoggerMessage(EventId = 2504, Level = LogLevel.Warning, Message = "A new stock was refused: the catalog holds {Active} active listings, the cap the data plan can keep fresh (MarketData:Lookup:MaxCatalogListings)")]
    private partial void LogCatalogFull(int active);
}
