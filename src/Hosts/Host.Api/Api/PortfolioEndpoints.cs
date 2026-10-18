using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Web.Http;
using SakerWatch.BuildingBlocks.Web.Idempotency;
using SakerWatch.BuildingBlocks.Web.Problems;
using SakerWatch.Watchlist.Application.Features.Portfolios;

namespace SakerWatch.Hosts.Api.Api;

/// <summary>A portfolio: a named group of the caller's watchlist stocks, kept for observation (never holdings).</summary>
/// <param name="Id">Portfolio id.</param>
/// <param name="Name">Name, unique for the caller ignoring letter case.</param>
/// <param name="Description">Description, or null.</param>
/// <param name="Position">Where it sits among the caller's portfolios, from zero.</param>
/// <param name="ItemCount">How many stocks it holds.</param>
/// <param name="CreatedAt">When it was created (ISO-8601 UTC).</param>
/// <param name="UpdatedAt">When it last changed (ISO-8601 UTC).</param>
public sealed record PortfolioResponse(Guid Id, string Name, string? Description, int Position, int ItemCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>The caller's portfolios by position.</summary>
/// <param name="Items">The portfolios.</param>
public sealed record PortfolioListResponse(IReadOnlyList<PortfolioResponse> Items);

/// <summary>A stock in a portfolio.</summary>
/// <param name="Stock">The stock.</param>
/// <param name="Position">Where it sits in the portfolio, from zero.</param>
/// <param name="AddedAt">When it was put in the portfolio (ISO-8601 UTC).</param>
public sealed record PortfolioItemResponse(WatchlistStockResponse Stock, int Position, DateTimeOffset AddedAt);

/// <summary>A portfolio with its stocks.</summary>
/// <param name="Portfolio">The portfolio.</param>
/// <param name="Items">Its stocks by position.</param>
public sealed record PortfolioDetailResponse(PortfolioResponse Portfolio, IReadOnlyList<PortfolioItemResponse> Items);

/// <summary>What deleting a portfolio would do to the watchlist.</summary>
/// <param name="Portfolio">The portfolio.</param>
/// <param name="StocksToRemove">Stocks in this portfolio and in no other: they would leave the watchlist, with their notes and targets.</param>
/// <param name="StocksKept">How many of its stocks would stay because another portfolio holds them too.</param>
public sealed record PortfolioDeletionPreviewResponse(PortfolioResponse Portfolio, IReadOnlyList<WatchlistStockResponse> StocksToRemove, int StocksKept);

/// <summary>Body of <c>POST /v1/portfolios</c> and <c>PUT /v1/portfolios/{id}</c>.</summary>
/// <param name="Name">Name; spaces at both ends are removed; 1 to 60 characters.</param>
/// <param name="Description">Description up to 500 characters, or null.</param>
public sealed record PortfolioRequest(string? Name, string? Description);

/// <summary>Body of <c>PUT /v1/portfolios/order</c>.</summary>
/// <param name="PortfolioIds">Every one of the caller's portfolio ids, once, in the order wanted.</param>
public sealed record ReorderPortfoliosRequest(IReadOnlyList<Guid>? PortfolioIds);

/// <summary>A stock named by market and ticker.</summary>
/// <param name="Market">Canonical market code.</param>
/// <param name="Ticker">Ticker.</param>
public sealed record StockKeyRequest(string? Market, string? Ticker);

/// <summary>Body of <c>PUT /v1/portfolios/{id}/items/order</c>.</summary>
/// <param name="Items">Every stock of the portfolio, once, in the order wanted.</param>
public sealed record ReorderPortfolioItemsRequest(IReadOnlyList<StockKeyRequest>? Items);

/// <summary>Endpoints under <c>/v1/portfolios</c>.</summary>
internal static class PortfolioEndpoints
{
    private const string _tag = "Portfolios";
    private const string _json = "application/json";

    /// <summary>Maps the endpoints on the version group.</summary>
    public static void MapPortfolios(this RouteGroupBuilder v1)
    {
        v1.MapPost("/portfolios", CreateAsync)
            .WithName("CreatePortfolio")
            .WithSummary("Create a portfolio")
            .WithDescription("Creates a named group of the caller's watchlist stocks, at the end of the list. Names are unique per caller ignoring letter case and spaces at the ends are removed; a caller keeps at most 20 portfolios. Requires an Idempotency-Key: repeating the request with the same key and body replays the first response.")
            .WithTags(_tag)
            .RequireIdempotencyKey()
            .Accepts<PortfolioRequest>(_json)
            .Produces<PortfolioResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        v1.MapGet("/portfolios", ListAsync)
            .WithName("ListPortfolios")
            .WithSummary("List the portfolios")
            .WithDescription("The caller's portfolios by position, each with the number of stocks it holds. Carries an ETag; send it back in If-None-Match to get 304 when nothing changed.")
            .WithTags(_tag)
            .Produces<PortfolioListResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        v1.MapPut("/portfolios/order", ReorderAsync)
            .WithName("ReorderPortfolios")
            .WithSummary("Reorder the portfolios")
            .WithDescription("Puts the caller's portfolios in the order given, which must name each of them exactly once. The same request twice leaves the same order.")
            .WithTags(_tag)
            .ExemptFromIdempotencyKey("A PUT of a complete order is idempotent by nature: the same list always ends in the same order.")
            .Accepts<ReorderPortfoliosRequest>(_json)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        v1.MapGet("/portfolios/{id:guid}", GetAsync)
            .WithName("GetPortfolio")
            .WithSummary("Read a portfolio")
            .WithDescription("The portfolio with its stocks by position. Carries an ETag.")
            .WithTags(_tag)
            .Produces<PortfolioDetailResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        v1.MapPut("/portfolios/{id:guid}", UpdateAsync)
            .WithName("UpdatePortfolio")
            .WithSummary("Rename or describe a portfolio")
            .WithDescription("Replaces the name and the description. A name another portfolio of the caller has (ignoring letter case) is a 409. The same request twice leaves the same state.")
            .WithTags(_tag)
            .ExemptFromIdempotencyKey("A PUT is idempotent by nature: the same body always ends in the same state.")
            .Accepts<PortfolioRequest>(_json)
            .Produces<PortfolioResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        v1.MapGet("/portfolios/{id:guid}/deletion-preview", PreviewAsync)
            .WithName("PreviewPortfolioDeletion")
            .WithSummary("See what deleting a portfolio would remove")
            .WithDescription("Lists the stocks that are in this portfolio and in no other: deleting the portfolio takes them off the watchlist too, with their notes and target prices. Stocks another portfolio also holds stay and are only counted.")
            .WithTags(_tag)
            .Produces<PortfolioDeletionPreviewResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        v1.MapDelete("/portfolios/{id:guid}", DeleteAsync)
            .WithName("DeletePortfolio")
            .WithSummary("Delete a portfolio")
            .WithDescription("Deletes the portfolio. When stocks would be left in no portfolio they leave the watchlist as well, so the request must carry confirm=true; without it the answer is 409 and nothing changes (see the deletion preview). A portfolio that is already gone is not an error: the answer is 204.")
            .WithTags(_tag)
            .ExemptFromIdempotencyKey("A DELETE is idempotent by nature: deleting twice leaves the same state and the same 204.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        v1.MapPut("/portfolios/{id:guid}/items/{market}/{ticker}", PutItemAsync)
            .WithName("PutPortfolioItem")
            .WithSummary("Put a watchlist stock in a portfolio")
            .WithDescription("Puts a stock the caller already follows in the portfolio, at the end (201 when it was added, 200 when it was there). A stock that is not on the watchlist is a 404: add it to the watchlist first. A portfolio holds at most 100 stocks.")
            .WithTags(_tag)
            .ExemptFromIdempotencyKey("A PUT is idempotent by nature: putting the same stock in the same portfolio twice leaves the same state.")
            .Produces<PortfolioItemResponse>(StatusCodes.Status200OK)
            .Produces<PortfolioItemResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        v1.MapDelete("/portfolios/{id:guid}/items/{market}/{ticker}", RemoveItemAsync)
            .WithName("RemovePortfolioItem")
            .WithSummary("Take a stock out of a portfolio")
            .WithDescription("Takes the stock out of the portfolio; it stays on the watchlist. A stock that is not in the portfolio is not an error: the answer is 204.")
            .WithTags(_tag)
            .ExemptFromIdempotencyKey("A DELETE is idempotent by nature: removing twice leaves the same state and the same 204.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        v1.MapPut("/portfolios/{id:guid}/items/order", ReorderItemsAsync)
            .WithName("ReorderPortfolioItems")
            .WithSummary("Reorder the stocks of a portfolio")
            .WithDescription("Puts the stocks of the portfolio in the order given, which must name each of them exactly once. The same request twice leaves the same order.")
            .WithTags(_tag)
            .ExemptFromIdempotencyKey("A PUT of a complete order is idempotent by nature: the same list always ends in the same order.")
            .Accepts<ReorderPortfolioItemsRequest>(_json)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
    }

    private static PortfolioResponse ToResponse(PortfolioView p) => new(p.Id, p.Name, p.Description, p.Position, p.ItemCount, p.CreatedAt, p.UpdatedAt);

    private static WatchlistStockResponse ToResponse(SakerWatch.MarketData.Contracts.StockSummary s) => new(s.Id, s.Market, s.Ticker, s.Name, s.Currency, s.AssetType);

    private static PortfolioItemResponse ToResponse(PortfolioItemView i) => new(ToResponse(i.Stock), i.Position, i.AddedAt);

    private static async Task<IResult> CreateAsync(PortfolioRequest? body, HttpContext http, ISender sender, CancellationToken cancellationToken)
    {
        var key = IdempotencyKeyReader.Require(http);
        if (!key.IsSuccess)
        {
            return key.Error!.ToProblem();
        }

        var result = await sender.Send(new CreatePortfolioCommand(body?.Name, body?.Description, key.Value), cancellationToken);
        return result.ToHttpResult(view =>
        {
            http.Response.Headers.Location = $"/v1/portfolios/{view.Id}";
            return TypedResults.Json(ToResponse(view), statusCode: StatusCodes.Status201Created);
        });
    }

    private static async Task<IResult> ListAsync(ISender sender, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new ListPortfoliosQuery(), cancellationToken);
        return result.ToHttpResult(views => ConditionalResults.Json(new PortfolioListResponse([.. views.Select(ToResponse)])));
    }

    private static async Task<IResult> ReorderAsync(ReorderPortfoliosRequest? body, ISender sender, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new ReorderPortfoliosCommand(body?.PortfolioIds ?? []), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> GetAsync(Guid id, ISender sender, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetPortfolioQuery(id), cancellationToken);
        return result.ToHttpResult(detail => ConditionalResults.Json(new PortfolioDetailResponse(ToResponse(detail.Portfolio), [.. detail.Items.Select(ToResponse)])));
    }

    private static async Task<IResult> UpdateAsync(Guid id, PortfolioRequest? body, ISender sender, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new UpdatePortfolioCommand(id, body?.Name, body?.Description), cancellationToken);
        return result.ToHttpResult(view => TypedResults.Ok(ToResponse(view)));
    }

    private static async Task<IResult> PreviewAsync(Guid id, ISender sender, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new PreviewPortfolioDeletionQuery(id), cancellationToken);
        return result.ToHttpResult(preview => TypedResults.Ok(new PortfolioDeletionPreviewResponse(
            ToResponse(preview.Portfolio), [.. preview.StocksToRemove.Select(ToResponse)], preview.StocksKept)));
    }

    private static async Task<IResult> DeleteAsync(Guid id, ISender sender, CancellationToken cancellationToken, bool confirm = false)
    {
        var result = await sender.Send(new DeletePortfolioCommand(id, confirm), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> PutItemAsync(Guid id, string market, string ticker, ISender sender, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new PutPortfolioItemCommand(id, market, ticker), cancellationToken);
        return result.ToHttpResult(put => put.Created
            ? TypedResults.Json(ToResponse(put.Item), statusCode: StatusCodes.Status201Created)
            : TypedResults.Ok(ToResponse(put.Item)));
    }

    private static async Task<IResult> RemoveItemAsync(Guid id, string market, string ticker, ISender sender, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new RemovePortfolioItemCommand(id, market, ticker), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> ReorderItemsAsync(Guid id, ReorderPortfolioItemsRequest? body, ISender sender, CancellationToken cancellationToken)
    {
        StockKey[] keys = [.. (body?.Items ?? []).Select(item => new StockKey(item.Market ?? string.Empty, item.Ticker ?? string.Empty))];
        var result = await sender.Send(new ReorderPortfolioItemsCommand(id, keys), cancellationToken);
        return result.ToHttpResult();
    }
}
