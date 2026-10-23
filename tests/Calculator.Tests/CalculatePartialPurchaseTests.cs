using System.Globalization;
using AwesomeAssertions;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.Calculator.Application.Features.CalculatePartialPurchase;
using SakerWatch.MarketData.Contracts;

namespace SakerWatch.Calculator.Tests;

/// <summary>The use case of the calculator over fakes of the catalog, the market data and the exchange rates (ADR-0036).</summary>
public sealed class CalculatePartialPurchaseTests
{
    private static readonly CurrencyCode _usd = CurrencyCode.Parse("USD");
    private static readonly CurrencyCode _gtq = CurrencyCode.Parse("GTQ");
    private static readonly CurrencyCode _cad = CurrencyCode.Parse("CAD");
    private static readonly DateOnly _day = new(2026, 10, 16);
    private static readonly StockSummary _apple = new(Guid.NewGuid(), "XNAS", "AAPL", "Apple Inc.", "USD", "equity");

    private sealed class Directory(StockSummary? stock) : IStockDirectory
    {
        public Task<Result<IReadOnlyList<StockCandidate>>> SearchAsync(string query, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<StockSummary>> FindAsync(string market, string ticker, CancellationToken cancellationToken) =>
            Task.FromResult(stock is not null && stock.Market == market && stock.Ticker == ticker
                ? Result.Success(stock)
                : Result.Failure<StockSummary>(Error.NotFound("marketdata.stock.not_found", "No such stock.")));

        public Task<Result<StockSummary>> EnsureAsync(string market, string ticker, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Market(StockSnapshot? snapshot) : IMarketSnapshots
    {
        public Task<IReadOnlyDictionary<Guid, StockSummary>> FindManyAsync(IReadOnlyCollection<Guid> stockIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        // The calculator needs one close, not the performance windows: asking for the snapshots is a failure of the test.
        public Task<IReadOnlyDictionary<Guid, StockSnapshot>> SnapshotsAsync(IReadOnlyCollection<Guid> stockIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, StockPrice>> PricesAsync(IReadOnlyCollection<Guid> stockIds, CancellationToken cancellationToken)
        {
            StockPrice[] known = snapshot is null ? [] : [new StockPrice(snapshot.Stock, snapshot.PriceStatus, snapshot.Price, snapshot.StaleSince, snapshot.DelistedAt)];
            return Task.FromResult<IReadOnlyDictionary<Guid, StockPrice>>(known.ToDictionary(found => found.Stock.Id));
        }
    }

    private sealed class Rates(params FxQuote[] quotes) : IFxRates
    {
        public int Calls { get; private set; }

        public Task<Result<FxQuote>> QuoteAsync(CurrencyCode from, CurrencyCode to, CancellationToken cancellationToken)
        {
            Calls++;
            FxQuote? found = quotes.FirstOrDefault(q => q.Rate.From == from && q.Rate.To == to);
            return Task.FromResult(found is null
                ? Result.Failure<FxQuote>(Error.NotFound("marketdata.fx.rate_unavailable", $"There is no {from} to {to} rate."))
                : Result.Success(found));
        }
    }

    private static FxQuote Quote(CurrencyCode from, decimal rate, params string[] sources) =>
        new(ExchangeRate.Create(from, _gtq, rate).Value, [.. sources.Select(source => new FxQuoteLeg(ExchangeRate.Create(from, _gtq, rate).Value, _day, source, false))], _day, false);

    private static StockSnapshot Snapshot(decimal? close, string status = PriceStatuses.Current, DateTimeOffset? staleSince = null) =>
        new(_apple, status, close is null ? null : new PriceSnapshot(close.Value, _day, true), staleSince, []);

    private static CalculatePartialPurchaseHandler Handler(Directory? directory = null, Market? market = null, Rates? rates = null) =>
        new(directory ?? new Directory(_apple), market ?? new Market(Snapshot(600m)), rates ?? new Rates(Quote(_usd, 7.75m, "banguat")));

    private static CalculatePartialPurchaseQuery Stock(decimal? amount = null, decimal? shares = null, decimal? fee = null) =>
        new("XNAS", "AAPL", null, null, amount, shares, fee);

    private static CalculatePartialPurchaseQuery Manual(string price, string currency, decimal? amount = null, decimal? shares = null) =>
        new(null, null, decimal.Parse(price, CultureInfo.InvariantCulture), currency, amount, shares);

    private static ValueTask<Result<PartialPurchaseView>> Run(CalculatePartialPurchaseHandler handler, CalculatePartialPurchaseQuery query) =>
        handler.Handle(query, CancellationToken.None);

    [Fact]
    public async Task AnAmountOnACatalogStock_UsesItsNewestClose_AndTheRateOfTheDay()
    {
        PartialPurchaseView view = (await Run(Handler(), Stock(amount: 50m))).Value;

        (view.Mode, view.PriceSource, view.Stock, view.PriceDate, view.PriceStatus).Should().Be(("amount", "catalog", _apple, (DateOnly?)_day, (string?)PriceStatuses.Current));
        (view.Purchase.Shares.Value, view.Purchase.Amount.Amount, view.Purchase.PriceConverted.Amount, view.Purchase.AmountConverted.Amount).Should().Be((0.083333m, 50m, 4650m, 387.5m));
        view.Fx.Rate.Rate.Should().Be(7.75m);
    }

    [Fact]
    public async Task SharesOnACatalogStock_CostTheirProduct()
    {
        PartialPurchaseView view = (await Run(Handler(), Stock(shares: 0.5m))).Value;

        (view.Mode, view.Purchase.Shares.Value, view.Purchase.Amount.Amount, view.Purchase.AmountConverted.Amount).Should().Be(("shares", 0.5m, 300m, 2325m));
    }

    [Fact]
    public async Task ACommission_IsPartOfTheTotalPaid_WhenAnAmountIsInvested()
    {
        PartialPurchaseView view = (await Run(Handler(), Stock(amount: 101m, fee: 1m))).Value;

        (view.Purchase.FeePercent.Value, view.Purchase.Amount.Amount, view.Purchase.Fee.Amount, view.Purchase.AmountConverted.Amount, view.Purchase.FeeConverted.Amount)
            .Should().Be((1m, 101m, 1m, 782.75m, 7.75m));
        view.Purchase.Shares.Value.Should().Be(0.166666m, "101 paid in all buys 100 worth of shares");
    }

    [Fact]
    public async Task ACommission_IsAddedToTheCostOfTheShares()
    {
        PartialPurchaseView view = (await Run(Handler(), Stock(shares: 2m, fee: 1m))).Value;

        (view.Purchase.Amount.Amount, view.Purchase.Fee.Amount, view.Purchase.AmountConverted.Amount).Should().Be((1212m, 12m, 9393m));
    }

    [Fact]
    public async Task WithoutACommission_ItIsZero_AndTheResultIsTheOneWithoutIt()
    {
        PartialPurchaseView without = (await Run(Handler(), Stock(amount: 50m))).Value;
        PartialPurchaseView zero = (await Run(Handler(), Stock(amount: 50m, fee: 0m))).Value;

        (without.Purchase.FeePercent.Value, without.Purchase.Fee.Amount, without.Purchase.FeeConverted.Amount).Should().Be((0m, 0m, 0m));
        zero.Purchase.Should().Be(without.Purchase);
    }

    [Fact]
    public async Task ACommissionOutsideZeroToTenPercent_IsAValidationFailure_BeforeAnythingIsRead()
    {
        var rates = new Rates(Quote(_usd, 7.75m, "banguat"));

        Result<PartialPurchaseView> result = await Run(Handler(rates: rates), Stock(amount: 50m, fee: 10.5m));

        result.Error!.Code.Should().Be("calculator.fee_out_of_range");
        rates.Calls.Should().Be(0);
    }

    [Fact]
    public async Task AGivenPrice_NeedsNoCatalog_AndSaysItIsManual()
    {
        var handler = Handler(new Directory(null), new Market(null));

        PartialPurchaseView view = (await Run(handler, Manual("600", "usd", amount: 50m))).Value;

        (view.PriceSource, view.Stock, view.PriceDate, view.PriceStatus).Should().Be(("manual", (StockSummary?)null, (DateOnly?)null, (string?)null));
        view.Purchase.Shares.Value.Should().Be(0.083333m);
    }

    [Fact]
    public async Task ADelistedStocksLastClose_IsUsed_AndSaidSo_NotPassedOffAsALiveOne()
    {
        PartialPurchaseView view = (await Run(Handler(market: new Market(Snapshot(600m, PriceStatuses.Delisted))), Stock(amount: 50m))).Value;

        view.PriceStatus.Should().Be(PriceStatuses.Delisted);
    }

    [Theory]
    [InlineData(PriceStatuses.Pending)]
    [InlineData(PriceStatuses.Unavailable)]
    public async Task AStockWithNoClose_IsRefused_WhateverItsStatus(string status)
    {
        Result<PartialPurchaseView> result = await Run(Handler(market: new Market(Snapshot(null, status))), Stock(amount: 50m));

        result.Error!.Code.Should().Be(PartialPurchaseErrors.PriceUnavailable);
    }

    [Fact]
    public async Task AStaleClose_IsUsed_AndSaidSo()
    {
        var since = new DateTimeOffset(2026, 10, 15, 21, 0, 0, TimeSpan.Zero);

        PartialPurchaseView view = (await Run(Handler(market: new Market(Snapshot(600m, PriceStatuses.Stale, since))), Stock(amount: 50m))).Value;

        (view.PriceStatus, view.StaleSince).Should().Be((PriceStatuses.Stale, (DateTimeOffset?)since));
    }

    [Theory]
    [InlineData(PriceStatuses.Pending)]
    [InlineData(PriceStatuses.Unavailable)]
    public async Task AStockWithoutAPrice_IsAnUnprocessableRequest_NotAGuess(string status)
    {
        Result<PartialPurchaseView> result = await Run(Handler(market: new Market(Snapshot(null, status))), Stock(amount: 50m));

        (result.Error!.Type, result.Error.Code).Should().Be((ErrorType.Unprocessable, PartialPurchaseErrors.PriceUnavailable));
    }

    [Fact]
    public async Task AStockTheCatalogDoesNotHave_IsNotFound_AndNothingElseIsRead()
    {
        var rates = new Rates(Quote(_usd, 7.75m, "banguat"));

        Result<PartialPurchaseView> result = await Run(Handler(new Directory(null), rates: rates), Stock(amount: 50m));

        result.Error!.Type.Should().Be(ErrorType.NotFound);
        rates.Calls.Should().Be(0, "the rate is looked up only once there is a price");
    }

    [Fact]
    public async Task ACurrencyWithoutARate_IsNotFound()
    {
        Result<PartialPurchaseView> result = await Run(Handler(rates: new Rates()), Manual("10", "EUR", amount: 5m));

        (result.Error!.Type, result.Error.Code).Should().Be((ErrorType.NotFound, "marketdata.fx.rate_unavailable"));
    }

    [Fact]
    public async Task APriceInAnotherCurrency_UsesItsOwnRate_AndTheConvertedFiguresAreInQuetzales()
    {
        var rates = new Rates(Quote(_cad, 5.3530m, "banguat", "banguat"));

        PartialPurchaseView view = (await Run(Handler(rates: rates), Manual("100", "CAD", shares: 2m))).Value;

        view.Purchase.Amount.Currency.Should().Be(_cad);
        (view.Purchase.AmountConverted.Currency, view.Purchase.AmountConverted.Amount, view.Fx.Legs.Count).Should().Be((_gtq, 1070.6m, 2));
    }

    [Fact]
    public async Task SharesWithMoreThanSixDecimals_AreRefused_BeforeAnythingIsRead()
    {
        var rates = new Rates(Quote(_usd, 7.75m, "banguat"));

        Result<PartialPurchaseView> result = await Run(Handler(rates: rates), Stock(shares: 1.2345678m));

        (result.Error!.Type, result.Error.Code).Should().Be((ErrorType.Validation, MoneyErrors.SharesInvalid));
        rates.Calls.Should().Be(0);
    }

    [Fact]
    public async Task AGivenPriceOutsideTheBounds_IsAValidationFailure()
    {
        Result<PartialPurchaseView> result = await Run(Handler(), Manual("0.0000001", "USD", amount: 5m));

        result.Error!.Code.Should().Be("calculator.price_out_of_range");
    }

    private static decimal? Num(string? text) => text is null ? null : decimal.Parse(text, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(null, null, null, null, null, null)] // nothing at all
    [InlineData("XNAS", "AAPL", "600", "USD", "50", null)] // a stock and a price
    [InlineData("XNAS", null, null, null, "50", null)] // half a stock
    [InlineData(null, null, "600", null, "50", null)] // a price without its currency
    [InlineData(null, null, null, "USD", "50", null)] // a currency without its price
    [InlineData("XNAS", "AAPL", null, null, null, null)] // no amount and no shares
    [InlineData("XNAS", "AAPL", null, null, "50", "2")] // both
    [InlineData("XNAS", "AAPL", null, null, "0", null)] // an amount that is not positive
    [InlineData("XNAS", "AAPL", null, null, null, "-1")] // shares that are not positive
    [InlineData(null, null, "0", "USD", "5", null)] // a price that is not positive
    [InlineData(null, null, "600", "US", "5", null)] // a currency that is not a code
    public void TheValidator_RefusesARequestThatIsNotExactlyOneSourceAndOneMode(string? market, string? ticker, string? price, string? currency, string? amount, string? shares)
    {
        var query = new CalculatePartialPurchaseQuery(market, ticker, Num(price), currency, Num(amount), Num(shares));

        new CalculatePartialPurchaseValidator().Validate(query).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("-0.1")]
    [InlineData("10.01")]
    public void TheValidator_RefusesACommissionOutsideZeroToTenPercent(string fee)
    {
        var query = new CalculatePartialPurchaseQuery("XNAS", "AAPL", null, null, 50m, null, Num(fee));

        new CalculatePartialPurchaseValidator().Validate(query).Errors.Should().ContainSingle().Which.PropertyName.Should().Be("Fee");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1.5")]
    [InlineData("10")]
    public void TheValidator_AcceptsACommissionFromZeroToTenPercent(string fee)
    {
        var query = new CalculatePartialPurchaseQuery("XNAS", "AAPL", null, null, 50m, null, Num(fee));

        new CalculatePartialPurchaseValidator().Validate(query).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("XNAS", "AAPL", null, null, "50", null)]
    [InlineData("XNAS", "AAPL", null, null, null, "2")]
    [InlineData(null, null, "600", "USD", "50", null)]
    [InlineData(null, null, "600", "USD", null, "0.5")]
    public void TheValidator_AcceptsOneSourceAndOneMode(string? market, string? ticker, string? price, string? currency, string? amount, string? shares)
    {
        var query = new CalculatePartialPurchaseQuery(market, ticker, Num(price), currency, Num(amount), Num(shares));

        new CalculatePartialPurchaseValidator().Validate(query).IsValid.Should().BeTrue();
    }
}
