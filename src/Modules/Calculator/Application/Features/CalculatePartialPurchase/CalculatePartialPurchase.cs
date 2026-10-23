using FluentValidation;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.Calculator.Domain.Purchases;
using SakerWatch.MarketData.Contracts;

namespace SakerWatch.Calculator.Application.Features.CalculatePartialPurchase;

/// <summary>
/// How many shares an amount buys, or what a number of shares costs, in the stock's currency and in quetzales (ADR-0036). The price comes from a
/// stock of the catalog (<paramref name="Market"/> and <paramref name="Ticker"/>) or is given (<paramref name="Price"/> and
/// <paramref name="Currency"/>); exactly one of <paramref name="Amount"/> and <paramref name="Shares"/> is given. A research aid: nothing here
/// is a recommendation, and broker rates, fees and minimum order sizes differ.
/// </summary>
/// <param name="Market">The canonical market code of a catalog stock, or <see langword="null"/>.</param>
/// <param name="Ticker">The ticker of a catalog stock, or <see langword="null"/>.</param>
/// <param name="Price">A price of one share, or <see langword="null"/> when a catalog stock gives it.</param>
/// <param name="Currency">The currency of <paramref name="Price"/> (ISO 4217), or <see langword="null"/> for a catalog stock.</param>
/// <param name="Amount">The money to invest, in the currency of the price, or <see langword="null"/>.</param>
/// <param name="Shares">The number of shares to buy, with at most six decimals, or <see langword="null"/>.</param>
/// <param name="Fee">The broker's commission as a percentage of the value of the shares (<c>1</c> is 1 %), or <see langword="null"/> for none. The amount, when given, is the total paid with the commission included.</param>
public sealed record CalculatePartialPurchaseQuery(string? Market, string? Ticker, decimal? Price, string? Currency, decimal? Amount, decimal? Shares, decimal? Fee = null)
    : IQuery<PartialPurchaseView>, IReadsDatabase;

/// <summary>What was calculated and what it was calculated from.</summary>
/// <param name="Mode"><c>amount</c> (an amount was invested) or <c>shares</c> (a number of shares was bought).</param>
/// <param name="PriceSource"><c>catalog</c> (the newest close of a stock) or <c>manual</c> (the price given).</param>
/// <param name="Stock">The catalog stock, when the price came from one.</param>
/// <param name="PriceDate">The trading day of the close, when the price came from the catalog.</param>
/// <param name="PriceStatus"><c>current</c>, <c>stale</c> (the last known close is used) or <c>delisted</c> (the stock no longer trades, ADR-0039: its last close is used and the caller is told), when the price came from the catalog. A stock with no close at all is refused (<c>price_unavailable</c>), so <c>pending</c> and <c>unavailable</c> never reach here.</param>
/// <param name="StaleSince">When the close went stale, when it did.</param>
/// <param name="Purchase">The figures, rounded for display.</param>
/// <param name="Fx">The exchange rate used, with its path, days and sources.</param>
public sealed record PartialPurchaseView(
    string Mode,
    string PriceSource,
    StockSummary? Stock,
    DateOnly? PriceDate,
    string? PriceStatus,
    DateTimeOffset? StaleSince,
    PartialPurchaseDisplay Purchase,
    FxQuote Fx);

/// <summary>Errors of the calculation.</summary>
public static class PartialPurchaseErrors
{
    /// <summary>The catalog has no price for the stock yet, or loading it failed for good.</summary>
    public const string PriceUnavailable = "calculator.price_unavailable";
}

/// <summary>Reports every problem of the request at once, by field, before anything is read.</summary>
public sealed class CalculatePartialPurchaseValidator : AbstractValidator<CalculatePartialPurchaseQuery>
{
    /// <summary>Creates the validator.</summary>
    public CalculatePartialPurchaseValidator()
    {
        RuleFor(x => x).Must(HasExactlyOneSource)
            .WithName("Price").WithMessage("Give a stock (market and ticker) or a price with its currency, not both and not neither.");
        RuleFor(x => x).Must(x => (x.Amount is null) != (x.Shares is null))
            .WithName("Amount").WithMessage("Give an amount to invest or a number of shares, not both and not neither.");
        RuleFor(x => x.Market).NotEmpty().MaximumLength(4).When(x => x.Ticker is not null || x.Market is not null);
        RuleFor(x => x.Ticker).NotEmpty().MaximumLength(16).When(x => x.Ticker is not null || x.Market is not null);
        RuleFor(x => x.Currency).Length(CurrencyCode.Length).When(x => x.Currency is not null).WithMessage("A currency code has three letters.");
        RuleFor(x => x.Price).GreaterThan(0m).When(x => x.Price is not null);
        RuleFor(x => x.Amount).GreaterThan(0m).When(x => x.Amount is not null);
        RuleFor(x => x.Shares).GreaterThan(0m).When(x => x.Shares is not null);
        RuleFor(x => x.Fee).InclusiveBetween(0m, FeePercent.Max).When(x => x.Fee is not null)
            .WithMessage($"The commission is between 0 and {FeePercent.Max:0} percent.");
    }

    private static bool HasExactlyOneSource(CalculatePartialPurchaseQuery request)
    {
        bool stock = request.Market is not null || request.Ticker is not null;
        bool manual = request.Price is not null || request.Currency is not null;
        return stock != manual && (!manual || (request.Price is not null && request.Currency is not null));
    }
}

/// <summary>Finds the price, the exchange rate and does the arithmetic of the domain.</summary>
public sealed class CalculatePartialPurchaseHandler(IStockDirectory stocks, IMarketSnapshots market, IFxRates fx)
    : IRequestHandler<CalculatePartialPurchaseQuery, Result<PartialPurchaseView>>
{
    private static readonly CurrencyCode _quetzal = CurrencyCode.Parse("GTQ");

    /// <inheritdoc />
    public async ValueTask<Result<PartialPurchaseView>> Handle(CalculatePartialPurchaseQuery request, CancellationToken cancellationToken)
    {
        // The request is read first: a shares request with too many decimals never costs a database read.
        Shares? shares = null;
        if (request.Shares is { } asked)
        {
            Result<Shares> parsed = Shares.FromRequest(asked);
            if (!parsed.IsSuccess)
            {
                return parsed.Error!;
            }

            shares = parsed.Value;
        }

        FeePercent? fee = null;
        if (request.Fee is { } commission)
        {
            Result<FeePercent> parsedFee = FeePercent.Create(commission);
            if (!parsedFee.IsSuccess)
            {
                return parsedFee.Error!;
            }

            fee = parsedFee.Value;
        }

        Result<PriceFound> price = await FindPriceAsync(request, cancellationToken);
        if (!price.IsSuccess)
        {
            return price.Error!;
        }

        Money unitPrice = price.Value.Price;
        Result<FxQuote> quote = await fx.QuoteAsync(unitPrice.Currency, _quetzal, cancellationToken);
        if (!quote.IsSuccess)
        {
            return quote.Error!;
        }

        Result<PartialPurchase> purchase;
        if (shares is not null)
        {
            purchase = PartialPurchaseCalculator.ByShares(unitPrice, shares, quote.Value.Rate, fee);
        }
        else
        {
            Result<Money> amount = Money.Create(request.Amount!.Value, unitPrice.Currency);
            purchase = amount.IsSuccess ? PartialPurchaseCalculator.ByAmount(unitPrice, amount.Value, quote.Value.Rate, fee) : amount.Error!;
        }

        if (!purchase.IsSuccess)
        {
            return purchase.Error!;
        }

        PriceFound found = price.Value;
        return new PartialPurchaseView(
            shares is null ? "amount" : "shares",
            found.Stock is null ? "manual" : "catalog",
            found.Stock,
            found.Date,
            found.Status,
            found.StaleSince,
            purchase.Value.ForDisplay(),
            quote.Value);
    }

    private async Task<Result<PriceFound>> FindPriceAsync(CalculatePartialPurchaseQuery request, CancellationToken cancellationToken)
    {
        if (request.Price is { } given)
        {
            Result<CurrencyCode> currency = CurrencyCode.Create(request.Currency);
            if (!currency.IsSuccess)
            {
                return currency.Error!;
            }

            Result<Money> manual = Money.Create(given, currency.Value);
            return manual.IsSuccess ? new PriceFound(manual.Value, null, null, null, null) : manual.Error!;
        }

        Result<StockSummary> stock = await stocks.FindAsync(request.Market!, request.Ticker!, cancellationToken);
        if (!stock.IsSuccess)
        {
            return stock.Error!;
        }

        IReadOnlyDictionary<Guid, StockPrice> prices = await market.PricesAsync([stock.Value.Id], cancellationToken);
        if (!prices.TryGetValue(stock.Value.Id, out StockPrice? found) || found.Price is null)
        {
            return Error.Unprocessable(PartialPurchaseErrors.PriceUnavailable, $"{stock.Value.Ticker} has no price yet. Try again later, or give a price.");
        }

        Result<CurrencyCode> currencyOfStock = CurrencyCode.Create(stock.Value.Currency);
        if (!currencyOfStock.IsSuccess)
        {
            return currencyOfStock.Error!;
        }

        // The raw close, in the stock's own currency: the price a share trades at, and not the adjusted close the windows use.
        Result<Money> closing = Money.Create(found.Price.Value, currencyOfStock.Value);
        return closing.IsSuccess
            ? new PriceFound(closing.Value, stock.Value, found.Price.Date, found.PriceStatus, found.StaleSince)
            : closing.Error!;
    }

    private sealed record PriceFound(Money Price, StockSummary? Stock, DateOnly? Date, string? Status, DateTimeOffset? StaleSince);
}
