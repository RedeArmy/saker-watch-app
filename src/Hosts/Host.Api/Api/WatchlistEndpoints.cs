using Microsoft.AspNetCore.Mvc;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Web.Http;
using SakerWatch.BuildingBlocks.Web.Idempotency;
using SakerWatch.BuildingBlocks.Web.Problems;
using SakerWatch.BuildingBlocks.Web.RateLimiting;
using SakerWatch.Watchlist.Application.Features.GetWatchlist;
using SakerWatch.Watchlist.Application.Features.PutWatchlistItem;
using SakerWatch.Watchlist.Application.Features.RemoveWatchlistItem;

namespace SakerWatch.Hosts.Api.Api;

/// <summary>The stock a watchlist item points to.</summary>
/// <param name="Id">Catalog id of the stock.</param>
/// <param name="Market">Canonical market code (MIC).</param>
/// <param name="Ticker">Ticker on that market.</param>
/// <param name="Name">Instrument name.</param>
/// <param name="Currency">Trading currency (ISO 4217).</param>
/// <param name="AssetType">Kind of instrument: <c>equity</c>, <c>etf</c> or <c>other</c>.</param>
public sealed record WatchlistStockResponse(Guid Id, string Market, string Ticker, string Name, string Currency, string AssetType);

/// <summary>A stock on the caller's watchlist.</summary>
/// <param name="Id">Item id.</param>
/// <param name="Stock">The stock.</param>
/// <param name="Notes">The caller's notes, or null.</param>
/// <param name="TargetPrice">The caller's target price in the stock's currency, or null. A note, never an order.</param>
/// <param name="CreatedAt">When the stock was added (ISO-8601 UTC).</param>
/// <param name="UpdatedAt">When the item last changed (ISO-8601 UTC).</param>
public sealed record WatchlistItemResponse(Guid Id, WatchlistStockResponse Stock, string? Notes, decimal? TargetPrice, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>The newest stored bar of a stock.</summary>
/// <param name="Value">The close as traded, in the stock's currency (a decimal string).</param>
/// <param name="Date">The trading day (ISO-8601 date).</param>
/// <param name="IsFinal">False while the day may still change.</param>
public sealed record PriceResponse(decimal Value, DateOnly Date, bool IsFinal);

/// <summary>One performance window of a stock, from adjusted closes (ADR-0006, ADR-0030).</summary>
/// <param name="Window">One of <c>1D</c>, <c>1W</c>, <c>1M</c>, <c>3M</c>, <c>6M</c>, <c>YTD</c>, <c>1Y</c>, <c>5Y</c>.</param>
/// <param name="Change">The change as a fraction (a decimal string; <c>0.0123</c> is 1.23 %), or null when there is a reason.</param>
/// <param name="BaselineDate">The day of the close the change is measured from, or null.</param>
/// <param name="Reason">Why there is no change: <c>no_data</c>, <c>insufficient_history</c>, <c>history_gap</c> or <c>calendar_unavailable</c>; null when there is one.</param>
public sealed record WindowResponse(string Window, decimal? Change, DateOnly? BaselineDate, string? Reason);

/// <summary>A stock on the caller's watchlist as the list shows it.</summary>
/// <param name="Id">Item id.</param>
/// <param name="Stock">The stock.</param>
/// <param name="Notes">The caller's notes, or null.</param>
/// <param name="TargetPrice">The caller's target price, or null. A note, never an order.</param>
/// <param name="CreatedAt">When the stock was added (ISO-8601 UTC).</param>
/// <param name="UpdatedAt">When the item last changed (ISO-8601 UTC).</param>
/// <param name="PriceStatus"><c>current</c>, <c>stale</c> (the last known price is shown), <c>pending</c> (no history yet) or <c>unavailable</c> (loading failed for good).</param>
/// <param name="Price">The newest stored bar, or null when there is none.</param>
/// <param name="StaleSince">When the price went stale (ISO-8601 UTC), only for <c>stale</c>.</param>
/// <param name="Windows">The eight performance windows, in order.</param>
public sealed record WatchlistEntryResponse(
    Guid Id,
    WatchlistStockResponse Stock,
    string? Notes,
    decimal? TargetPrice,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string PriceStatus,
    PriceResponse? Price,
    DateTimeOffset? StaleSince,
    IReadOnlyList<WindowResponse> Windows);

/// <summary>A page of the caller's watchlist.</summary>
/// <param name="Items">The entries of the page.</param>
/// <param name="Total">How many stocks match the filters, in all pages.</param>
/// <param name="Limit">Items per page asked for.</param>
/// <param name="Offset">Items skipped.</param>
public sealed record WatchlistPageResponse(IReadOnlyList<WatchlistEntryResponse> Items, int Total, int Limit, int Offset);

/// <summary>Body of <c>PUT /v1/watchlist/items/{market}/{ticker}</c>. Both fields replace what is stored; leaving one out clears it.</summary>
/// <param name="Notes">Notes, up to 2000 characters, or null.</param>
/// <param name="TargetPrice">Target price greater than zero with at most eight decimals, or null.</param>
public sealed record PutWatchlistItemRequest(string? Notes, decimal? TargetPrice);

/// <summary>The query string of <c>GET /v1/watchlist</c>; every part is optional.</summary>
/// <param name="Sort">Sort key.</param>
/// <param name="Order"><c>asc</c> or <c>desc</c>.</param>
/// <param name="Market">Canonical market code.</param>
/// <param name="AssetType"><c>equity</c>, <c>etf</c> or <c>other</c>.</param>
/// <param name="Q">Text in the ticker or the name.</param>
/// <param name="PriceStatus">Price state.</param>
/// <param name="Portfolio">Id of one of the caller's portfolios.</param>
/// <param name="Limit">Items per page, 1 to 100.</param>
/// <param name="Offset">Items to skip.</param>
internal sealed record WatchlistListParameters(
    [FromQuery(Name = "sort")] string? Sort = null,
    [FromQuery(Name = "order")] string? Order = null,
    [FromQuery(Name = "market")] string? Market = null,
    [FromQuery(Name = "assetType")] string? AssetType = null,
    [FromQuery(Name = "q")] string? Q = null,
    [FromQuery(Name = "priceStatus")] string? PriceStatus = null,
    [FromQuery(Name = "portfolio")] Guid? Portfolio = null,
    [FromQuery(Name = "limit")] int Limit = 50,
    [FromQuery(Name = "offset")] int Offset = 0);

/// <summary>Endpoints under <c>/v1/watchlist</c>.</summary>
internal static class WatchlistEndpoints
{
    private const string _tag = "Watchlist";

    /// <summary>Maps the endpoints on the version group.</summary>
    public static void MapWatchlist(this RouteGroupBuilder v1)
    {
        v1.MapGet("/watchlist", ListAsync)
            .WithName("GetWatchlist")
            .WithSummary("List the watchlist")
            .WithDescription("The caller's stocks with the newest price, how current it is (priceStatus, and staleSince when the last known price is shown) and the performance windows 1D, 1W, 1M, 3M, 6M, YTD, 1Y and 5Y, computed from adjusted closes in the stock's own currency; a window without enough history carries a reason instead of a number. Filter by market, assetType, q (ticker or name), priceStatus and portfolio (the id of one of the caller's portfolios; 404 when it is not one of theirs); sort by ticker, name, market, createdAt, price or a window (a missing value sorts last); page with limit (1 to 100, default 50) and offset. The answer carries an ETag: send it back in If-None-Match to get 304 when nothing changed. Informational only, not financial advice.")
            .WithTags(_tag)
            .Produces<WatchlistPageResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        v1.MapPut("/watchlist/items/{market}/{ticker}", PutAsync)
            .WithName("PutWatchlistItem")
            .WithSummary("Put a stock on the watchlist")
            .WithDescription("Adds the stock to the caller's watchlist with these notes and target price, or replaces the notes and target of the item already there: the same request twice leaves the same state, so it needs no Idempotency-Key. A stock the catalog does not have yet is registered from the provider's answer for that exact market and ticker and its history is queued; if the provider cannot confirm it the answer is 503 and nothing is stored. Returns 201 when the stock was added and 200 when an item was updated. Because a new stock can spend a provider search, the route shares the stricter per-user limit of the search; a busy answer carries Retry-After.")
            .WithTags(_tag)
            .ExemptFromIdempotencyKey("A PUT is idempotent by nature: the same market, ticker and body always end in the same state.")
            .RequireRateLimiting(RateLimitPolicies.Expensive)
            .Accepts<PutWatchlistItemRequest>("application/json")
            .Produces<WatchlistItemResponse>(StatusCodes.Status200OK)
            .Produces<WatchlistItemResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        v1.MapDelete("/watchlist/items/{market}/{ticker}", RemoveAsync)
            .WithName("RemoveWatchlistItem")
            .WithSummary("Take a stock off the watchlist")
            .WithDescription("Removes the stock from the caller's watchlist and from every portfolio it was in. Removing a stock that is not there is not an error: the answer is 204 either way.")
            .WithTags(_tag)
            .ExemptFromIdempotencyKey("A DELETE is idempotent by nature: removing a stock twice leaves the same state, and the second answer is the same 204.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
    }

    private static async Task<IResult> ListAsync(ISender sender, [AsParameters] WatchlistListParameters query, CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new GetWatchlistQuery(query.Sort, query.Order, query.Market, query.AssetType, query.Q, query.PriceStatus, query.Limit, query.Offset, query.Portfolio),
            cancellationToken);
        return result.ToHttpResult(page => ConditionalResults.Json(new WatchlistPageResponse(
            [.. page.Items.Select(ToResponse)], page.Total, page.Limit, page.Offset)));
    }

    private static WatchlistEntryResponse ToResponse(WatchlistEntry entry) =>
        new(
            entry.Id,
            new WatchlistStockResponse(entry.Stock.Id, entry.Stock.Market, entry.Stock.Ticker, entry.Stock.Name, entry.Stock.Currency, entry.Stock.AssetType),
            entry.Notes,
            entry.TargetPrice,
            entry.CreatedAt,
            entry.UpdatedAt,
            entry.PriceStatus,
            entry.Price is null ? null : new PriceResponse(entry.Price.Value, entry.Price.Date, entry.Price.IsFinal),
            entry.StaleSince,
            [.. entry.Windows.Select(w => new WindowResponse(w.Window, w.Change, w.BaselineDate, w.Reason))]);

    private static async Task<IResult> PutAsync(string market, string ticker, PutWatchlistItemRequest? body, ISender sender, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new PutWatchlistItemCommand(market, ticker, body?.Notes, body?.TargetPrice), cancellationToken);
        return result.ToHttpResult(put =>
        {
            WatchlistItemView item = put.Item;
            var response = new WatchlistItemResponse(
                item.Id,
                new WatchlistStockResponse(item.Stock.Id, item.Stock.Market, item.Stock.Ticker, item.Stock.Name, item.Stock.Currency, item.Stock.AssetType),
                item.Notes,
                item.TargetPrice,
                item.CreatedAt,
                item.UpdatedAt);
            return put.Created ? TypedResults.Json(response, statusCode: StatusCodes.Status201Created) : TypedResults.Ok(response);
        });
    }

    private static async Task<IResult> RemoveAsync(string market, string ticker, ISender sender, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new RemoveWatchlistItemCommand(market, ticker), cancellationToken);
        return result.ToHttpResult();
    }
}
