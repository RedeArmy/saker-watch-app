using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Web.Http;
using SakerWatch.BuildingBlocks.Web.Problems;
using SakerWatch.BuildingBlocks.Web.RateLimiting;
using SakerWatch.MarketData.Application.Features.GetPriceSeries;
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

/// <summary>One stored day of a stock.</summary>
/// <param name="Date">The trading day (ISO-8601 date).</param>
/// <param name="Close">The close as traded (a decimal string).</param>
/// <param name="AdjustedClose">The close adjusted for splits and dividends, the one to draw because it is continuous (a decimal string).</param>
/// <param name="IsFinal">False while the day may still change.</param>
public sealed record PricePointResponse(DateOnly Date, decimal Close, decimal AdjustedClose, bool IsFinal);

/// <summary>The price history of a stock over a range.</summary>
/// <param name="Stock">The stock.</param>
/// <param name="Range">The range asked for.</param>
/// <param name="Interval">The interval used: <c>day</c>, <c>week</c> or <c>month</c>.</param>
/// <param name="From">The first day the range covers, or null for all the history.</param>
/// <param name="To">The day of the newest stored price, or null when there is none.</param>
/// <param name="Count">How many points there are.</param>
/// <param name="Points">The days, oldest first; empty when the stock has no price in the range.</param>
/// <param name="PriceStatus"><c>current</c>, <c>stale</c> (the last known price is the newest), <c>pending</c>, <c>unavailable</c> or <c>delisted</c> (the stock no longer trades).</param>
/// <param name="StaleSince">When the newest price went stale (ISO-8601 UTC), when it did.</param>
/// <param name="DelistedAt">When the stock stopped trading (ISO-8601 UTC), only for <c>delisted</c>.</param>
public sealed record PriceSeriesResponse(
    WatchlistStockResponse Stock,
    string Range,
    string Interval,
    DateOnly? From,
    DateOnly? To,
    int Count,
    IReadOnlyList<PricePointResponse> Points,
    string PriceStatus,
    DateTimeOffset? StaleSince,
    DateTimeOffset? DelistedAt);

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

        v1.MapGet("/stocks/{market}/{ticker}/prices", PricesAsync)
            .WithName("GetStockPrices")
            .WithSummary("Price history of a stock")
            .WithDescription("The stored days of a stock of the catalog over a range (1W, 1M, 3M, 6M, YTD, 1Y, 5Y or MAX; default 1Y), oldest first, each with the close as traded and the adjusted close (draw the adjusted one: it is continuous across splits and dividends). A range starts where the performance window of the same name does. The interval is day, week or month (a week or a month is shown by its last trading day); auto uses days up to a year, weeks for five years and months for all the history. A stock with no price yet answers an empty series with its status. The answer carries an ETag. Informational only, not financial advice.")
            .WithTags(_tag)
            .Produces<PriceSeriesResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
    }

    private static async Task<IResult> PricesAsync(string market, string ticker, ISender sender, CancellationToken cancellationToken, string? range = null, string? interval = null)
    {
        var result = await sender.Send(new GetPriceSeriesQuery(market, ticker, range, interval), cancellationToken);
        return result.ToHttpResult(series => ConditionalResults.Json(new PriceSeriesResponse(
            new WatchlistStockResponse(series.Stock.Id, series.Stock.Market, series.Stock.Ticker, series.Stock.Name, series.Stock.Currency, series.Stock.AssetType),
            series.Range,
            series.Interval,
            series.From,
            series.To,
            series.Points.Count,
            [.. series.Points.Select(point => new PricePointResponse(point.Date, point.Close, point.AdjustedClose, point.IsFinal))],
            series.PriceStatus,
            series.StaleSince,
            series.DelistedAt)));
    }

    private static async Task<IResult> SearchAsync(string? q, ISender sender, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new SearchStocksQuery(q ?? string.Empty), cancellationToken);
        return result.ToHttpResult(candidates => TypedResults.Ok(new StockSearchResponse(
            [.. candidates.Select(c => new StockCandidateResponse(c.Market, c.Ticker, c.Name, c.Currency, c.AssetType))])));
    }
}
