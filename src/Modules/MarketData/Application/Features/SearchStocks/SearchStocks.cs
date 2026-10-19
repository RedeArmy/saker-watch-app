using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Contracts;

namespace SakerWatch.MarketData.Application.Features.SearchStocks;

/// <summary>Searches the enabled markets for stocks a user can add to a watchlist (ADR-0032).</summary>
/// <param name="Query">The text typed: part of a ticker or of a name.</param>
public sealed record SearchStocksQuery(string Query) : IQuery<IReadOnlyList<StockCandidate>>;

/// <summary>Runs the search through the stock directory, which validates the text and spends the shared search budget.</summary>
public sealed class SearchStocksHandler(IStockDirectory directory) : IRequestHandler<SearchStocksQuery, Result<IReadOnlyList<StockCandidate>>>
{
    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<StockCandidate>>> Handle(SearchStocksQuery request, CancellationToken cancellationToken) =>
        await directory.SearchAsync(request.Query, cancellationToken);
}
