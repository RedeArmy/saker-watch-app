using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Web.Problems;
using SakerWatch.BuildingBlocks.Web.RateLimiting;
using SakerWatch.MarketData.Application.Features.SearchStocks;

namespace SakerWatch.Hosts.Api.Api;

/// <summary>A stock found by a search.</summary>
/// <param name="Market">Canonical market code (MIC), for example <c>XNAS</c>.</param>
/// <param name="Ticker">Ticker on that market.</param>
/// <param name="Name">Instrument name as the provider reports it.</param>
/// <param name="Currency">Trading currency (ISO 4217).</param>
/// <param name="AssetType">Kind of instrument: <c>equity</c>, <c>etf</c> or <c>other</c>.</param>
public sealed record StockCandidateResponse(string Market, string Ticker, string Name, string Currency, string AssetType);

/// <summary>Result of <c>GET /v1/stocks/search</c>.</summary>
/// <param name="Items">The matches, an exact ticker first.</param>
public sealed record StockSearchResponse(IReadOnlyList<StockCandidateResponse> Items);

/// <summary>Endpoints under <c>/v1/stocks</c>.</summary>
internal static class StocksEndpoints
{
    private const string _tag = "Stocks";

    /// <summary>Maps the endpoints on the version group.</summary>
    public static void MapStocks(this RouteGroupBuilder v1)
    {
        v1.MapGet("/stocks/search", SearchAsync)
            .WithName("SearchStocks")
            .WithSummary("Search stocks to add to the watchlist")
            .WithDescription("Finds stocks in the enabled markets by ticker or name, an exact ticker first. The market in each match is the canonical one to use in the watchlist routes. Searches are limited per user and, together, per minute and per day; a busy answer carries Retry-After. This is a research aid, not a recommendation.")
            .WithTags(_tag)
            .RequireRateLimiting(RateLimitPolicies.Expensive)
            .Produces<StockSearchResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> SearchAsync(string? q, ISender sender, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new SearchStocksQuery(q ?? string.Empty), cancellationToken);
        return result.ToHttpResult(candidates => TypedResults.Ok(new StockSearchResponse(
            [.. candidates.Select(c => new StockCandidateResponse(c.Market, c.Ticker, c.Name, c.Currency, c.AssetType))])));
    }
}
