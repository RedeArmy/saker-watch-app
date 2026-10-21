using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Web.Idempotency;
using SakerWatch.BuildingBlocks.Web.Problems;
using SakerWatch.Calculator.Application.Features.CalculatePartialPurchase;
using SakerWatch.MarketData.Contracts;

namespace SakerWatch.Hosts.Api.Api;

/// <summary>
/// Body of <c>POST /v1/calculations/partial-purchase</c>. Give a stock (<c>market</c> and <c>ticker</c>) or a <c>price</c> with its <c>currency</c>,
/// and exactly one of <c>amount</c> and <c>shares</c>. Decimals are strings.
/// </summary>
/// <param name="Market">Canonical market code of a stock in the catalog (<c>XNAS</c>).</param>
/// <param name="Ticker">Ticker of that stock.</param>
/// <param name="Price">Price of one share, instead of a stock (a decimal string).</param>
/// <param name="Currency">Currency of the price (ISO 4217); only with <c>price</c>.</param>
/// <param name="Amount">Money to invest, in the currency of the price (a decimal string).</param>
/// <param name="Shares">Number of shares to buy, up to six decimals (a decimal string).</param>
/// <param name="FeePercent">The broker's commission as a percentage of the value of the shares, 0 to 10 (a decimal string; <c>"1"</c> is 1 %). Optional: without it the commission is 0. When given with <c>amount</c>, the amount is the total paid, commission included.</param>
public sealed record PartialPurchaseRequest(string? Market, string? Ticker, decimal? Price, string? Currency, decimal? Amount, decimal? Shares, decimal? FeePercent = null);

/// <summary>One step of the exchange path.</summary>
/// <param name="From">The currency the step starts from.</param>
/// <param name="To">The currency the step ends in.</param>
/// <param name="Rate">Units of <c>to</c> for one of <c>from</c>, as applied (a decimal string).</param>
/// <param name="AsOf">The day the stored rate was published for (ISO-8601 date).</param>
/// <param name="Source">Who published it.</param>
/// <param name="Inverted">Whether the stored rate was the other way round and was inverted.</param>
public sealed record FxLegResponse(string From, string To, decimal Rate, DateOnly AsOf, string Source, bool Inverted);

/// <summary>The result of a partial-purchase calculation. A reference, not a quote.</summary>
/// <param name="Mode"><c>amount</c> (an amount was invested) or <c>shares</c> (a number of shares was bought).</param>
/// <param name="PriceSource"><c>catalog</c> (the newest close of the stock) or <c>manual</c> (the price given).</param>
/// <param name="Stock">The catalog stock, when the price came from one.</param>
/// <param name="PriceDate">The trading day of the close, when the price came from the catalog.</param>
/// <param name="PriceStatus"><c>current</c>, <c>stale</c> (the last known close is used) or <c>delisted</c> (the stock no longer trades; its last close is used), when the price came from the catalog.</param>
/// <param name="StaleSince">When the close went stale (ISO-8601 UTC), when it did.</param>
/// <param name="QuoteCurrency">The currency of the price and of <c>amountNative</c>.</param>
/// <param name="PriceNative">The price of one share, as given or as traded (a decimal string).</param>
/// <param name="FxPath">The currencies the rate went through, for example <c>["CAD","USD","GTQ"]</c>; just <c>["GTQ"]</c> when no conversion was needed.</param>
/// <param name="FxRate">The combined rate to quetzales, exact (a decimal string).</param>
/// <param name="FxAsOf">The oldest day among the steps of the rate (ISO-8601 date).</param>
/// <param name="FxSource">Who published the rate; several sources are joined with a comma; null when no conversion was needed.</param>
/// <param name="FxStale">Whether the rate is older than the allowed age.</param>
/// <param name="FxLegs">The steps of the exchange path.</param>
/// <param name="CostOneShareGtq">The price of one share in quetzales, rounded to cents.</param>
/// <param name="SharesFraction">The shares, cut at six decimals towards zero (never rounded up).</param>
/// <param name="AmountNative">The total paid in the stock's currency, commission included, rounded to its minor units.</param>
/// <param name="AmountGtq">The total paid in quetzales, commission included, rounded to cents.</param>
/// <param name="FeePercent">The commission assumed, as given; <c>"0"</c> when none was.</param>
/// <param name="FeeNative">The commission within <c>amountNative</c>, rounded to the minor units of the stock's currency.</param>
/// <param name="FeeGtq">The commission within <c>amountGtq</c>, rounded to cents.</param>
/// <param name="DisclaimerKey">Key of the notice to show: the rate is a reference and a broker's rate, fees and minimum order sizes differ.</param>
public sealed record PartialPurchaseResponse(
    string Mode,
    string PriceSource,
    WatchlistStockResponse? Stock,
    DateOnly? PriceDate,
    string? PriceStatus,
    DateTimeOffset? StaleSince,
    string QuoteCurrency,
    decimal PriceNative,
    IReadOnlyList<string> FxPath,
    decimal FxRate,
    DateOnly FxAsOf,
    string? FxSource,
    bool FxStale,
    IReadOnlyList<FxLegResponse> FxLegs,
    decimal CostOneShareGtq,
    decimal SharesFraction,
    decimal AmountNative,
    decimal AmountGtq,
    decimal FeePercent,
    decimal FeeNative,
    decimal FeeGtq,
    string DisclaimerKey);

/// <summary>Endpoints under <c>/v1/calculations</c>.</summary>
internal static class CalculationsEndpoints
{
    private const string _tag = "Calculator";
    private const string _disclaimerKey = "marketdata.fx.reference_only";

    /// <summary>Maps the endpoints on the version group.</summary>
    public static void MapCalculations(this RouteGroupBuilder v1)
    {
        v1.MapPost("/calculations/partial-purchase", PartialPurchaseAsync)
            .WithName("CalculatePartialPurchase")
            .WithSummary("Calculate a partial purchase")
            .WithDescription("How many shares an amount buys, or what a number of shares costs, in the stock's currency and in quetzales. Give a stock of the catalog (market and ticker; its newest close is the price, flagged when it is stale or the stock no longer trades) or a price with its currency, and exactly one of amount (money to invest) or shares (up to six decimals). The rate is the Banco de Guatemala reference rate of the latest day published, directly or through USD, returned with its path, day and source. An optional feePercent (0 to 10) is the broker's commission as a percentage of the value of the shares: with amount, the amount is the total paid and the shares are bought with what is left after the commission; with shares, the commission is added to their cost; without it the commission is 0 and nothing changes. Money is rounded to the minor units of its currency (half to even) and shares are cut at six decimals, never rounded up. A calculation changes nothing: the request needs no Idempotency-Key. A reference only: a broker's rate, fees and minimum order sizes differ, and nothing here is financial advice.")
            .WithTags(_tag)
            .ExemptFromIdempotencyKey("A calculation is a pure computation over stored data: the same body always gives the same answer and nothing is changed.")
            .Accepts<PartialPurchaseRequest>("application/json")
            .Produces<PartialPurchaseResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
    }

    private static async Task<IResult> PartialPurchaseAsync(PartialPurchaseRequest? body, ISender sender, CancellationToken cancellationToken)
    {
        var query = new CalculatePartialPurchaseQuery(body?.Market, body?.Ticker, body?.Price, body?.Currency, body?.Amount, body?.Shares, body?.FeePercent);
        var result = await sender.Send(query, cancellationToken);
        return result.ToHttpResult(view => TypedResults.Ok(ToResponse(view)));
    }

    private static PartialPurchaseResponse ToResponse(PartialPurchaseView view)
    {
        var purchase = view.Purchase;
        FxQuote fx = view.Fx;
        string[] path = fx.Legs.Count == 0
            ? [fx.Rate.To.Value]
            : [fx.Legs[0].Rate.From.Value, .. fx.Legs.Select(leg => leg.Rate.To.Value)];
        string? source = fx.Legs.Count == 0 ? null : string.Join(',', fx.Legs.Select(leg => leg.Source).Distinct(StringComparer.Ordinal));
        StockSummary? stock = view.Stock;

        return new PartialPurchaseResponse(
            view.Mode,
            view.PriceSource,
            stock is null ? null : new WatchlistStockResponse(stock.Id, stock.Market, stock.Ticker, stock.Name, stock.Currency, stock.AssetType),
            view.PriceDate,
            view.PriceStatus,
            view.StaleSince,
            purchase.Price.Currency.Value,
            purchase.Price.Amount,
            path,
            fx.Rate.Rate,
            fx.AsOfDate,
            source,
            fx.IsStale,
            [.. fx.Legs.Select(leg => new FxLegResponse(leg.Rate.From.Value, leg.Rate.To.Value, leg.Rate.Rate, leg.AsOfDate, leg.Source, leg.Inverted))],
            purchase.PriceConverted.Amount,
            purchase.Shares.Value,
            purchase.Amount.Amount,
            purchase.AmountConverted.Amount,
            purchase.FeePercent.Value,
            purchase.Fee.Amount,
            purchase.FeeConverted.Amount,
            _disclaimerKey);
    }
}
