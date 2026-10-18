using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Web.Idempotency;
using SakerWatch.BuildingBlocks.Web.Problems;
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

/// <summary>Body of <c>PUT /v1/watchlist/items/{market}/{ticker}</c>. Both fields replace what is stored; leaving one out clears it.</summary>
/// <param name="Notes">Notes, up to 2000 characters, or null.</param>
/// <param name="TargetPrice">Target price greater than zero with at most eight decimals, or null.</param>
public sealed record PutWatchlistItemRequest(string? Notes, decimal? TargetPrice);

/// <summary>Endpoints under <c>/v1/watchlist</c>.</summary>
internal static class WatchlistEndpoints
{
    private const string _tag = "Watchlist";

    /// <summary>Maps the endpoints on the version group.</summary>
    public static void MapWatchlist(this RouteGroupBuilder v1)
    {
        v1.MapPut("/watchlist/items/{market}/{ticker}", PutAsync)
            .WithName("PutWatchlistItem")
            .WithSummary("Put a stock on the watchlist")
            .WithDescription("Adds the stock to the caller's watchlist with these notes and target price, or replaces the notes and target of the item already there: the same request twice leaves the same state, so it needs no Idempotency-Key. A stock the catalog does not have yet is registered from the provider's answer for that exact market and ticker and its history is queued; if the provider cannot confirm it the answer is 503 and nothing is stored. Returns 201 when the stock was added and 200 when an item was updated.")
            .WithTags(_tag)
            .ExemptFromIdempotencyKey("A PUT is idempotent by nature: the same market, ticker and body always end in the same state.")
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
