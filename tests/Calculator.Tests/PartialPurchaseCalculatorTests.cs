using System.Globalization;
using AwesomeAssertions;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.Calculator.Domain.Purchases;

namespace SakerWatch.Calculator.Tests;

/// <summary>Hand-computed cases of the partial-purchase arithmetic on the money kit, and of what it refuses (ADR-0006, ADR-0036, ADR-0037).</summary>
public sealed class PartialPurchaseCalculatorTests
{
    private static readonly CurrencyCode _usd = CurrencyCode.Parse("USD");
    private static readonly CurrencyCode _gtq = CurrencyCode.Parse("GTQ");
    private static readonly CurrencyCode _jpy = CurrencyCode.Parse("JPY");

    private static decimal D(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);

    private static Money M(string amount, CurrencyCode currency) => Money.Create(D(amount), currency).Value;

    private static ExchangeRate Rate(CurrencyCode from, CurrencyCode to, string rate) => ExchangeRate.Create(from, to, D(rate)).Value;

    private static Shares S(string shares) => Shares.FromRequest(D(shares)).Value;

    [Fact]
    public void ByAmount_FiftyDollarsOnASixHundredDollarShare_BuysAboutATwelfth_AtTheRateOfTheDay()
    {
        PartialPurchase purchase = PartialPurchaseCalculator.ByAmount(M("600", _usd), M("50", _usd), Rate(_usd, _gtq, "7.75")).Value;

        purchase.Shares.Value.Should().Be(50m / 600m, "kept exact until the presentation boundary");
        purchase.Amount.Should().Be(M("50", _usd));
        (purchase.PriceConverted, purchase.AmountConverted).Should().Be((M("4650", _gtq), M("387.5", _gtq)));
        PartialPurchaseDisplay shown = purchase.ForDisplay();
        (shown.Shares.Value, shown.Amount.Amount, shown.PriceConverted.Amount, shown.AmountConverted.Amount).Should().Be((0.083333m, 50m, 4650m, 387.5m));
        (shown.PriceConverted.Currency, shown.AmountConverted.Currency).Should().Be((_gtq, _gtq));
    }

    [Fact]
    public void ByShares_ThreeAndAHalfShares_CostTheirProductInBothCurrencies()
    {
        PartialPurchase purchase = PartialPurchaseCalculator.ByShares(M("221.37", _usd), S("3.5"), Rate(_usd, _gtq, "7.7821")).Value;

        purchase.Shares.Value.Should().Be(3.5m, "the shares are the ones asked for");
        purchase.Amount.Amount.Should().Be(774.795m);
        purchase.AmountConverted.Amount.Should().Be(774.795m * 7.7821m);
        PartialPurchaseDisplay shown = purchase.ForDisplay();
        (shown.Shares.Value, shown.Amount.Amount, shown.AmountConverted.Amount, shown.PriceConverted.Amount).Should().Be((3.5m, 774.80m, 6029.53m, 1722.72m));
    }

    [Fact]
    public void ForDisplay_ShowsEachAmountInTheMinorUnitsOfItsOwnCurrency()
    {
        PartialPurchase yen = PartialPurchaseCalculator.ByShares(M("2987.5", _jpy), S("1.5"), Rate(_jpy, _gtq, "0.0518")).Value;

        PartialPurchaseDisplay shown = yen.ForDisplay();

        shown.Amount.Amount.Should().Be(4481m, "4481.25 yen, shown without decimals");
        shown.AmountConverted.Amount.Should().Be(232.13m, "4481.25 x 0.0518 = 232.12875");
        shown.PriceConverted.Amount.Should().Be(154.75m, "2987.5 x 0.0518 = 154.7525");
    }

    [Fact]
    public void AStockPricedInQuetzales_UsesTheIdentityRate()
    {
        PartialPurchase purchase = PartialPurchaseCalculator.ByAmount(M("40", _gtq), M("100", _gtq), ExchangeRate.Identity(_gtq)).Value;

        purchase.Shares.Value.Should().Be(2.5m);
        purchase.AmountConverted.Should().Be(purchase.Amount);
        purchase.PriceConverted.Should().Be(purchase.Price);
    }

    [Fact]
    public void ForDisplay_EchoesThePriceAndTheRateAsGiven_AndNeverRoundsThemOrTheExactPurchase()
    {
        PartialPurchase purchase = PartialPurchaseCalculator.ByAmount(M("0.123456", _usd), M("10", _usd), Rate(_usd, _gtq, "7.64572")).Value;

        PartialPurchaseDisplay shown = purchase.ForDisplay();

        shown.Price.Amount.Should().Be(0.123456m);
        shown.Rate.Rate.Should().Be(7.64572m);
        purchase.Shares.Value.Should().Be(10m / 0.123456m, "showing it did not change the exact figure");
        shown.Shares.Value.Should().Be(81.000518m, "10 / 0.123456 = 81.00051840..., cut at six decimals");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.0000009")]
    [InlineData("1000000000.01")]
    public void APriceOutsideTheBounds_IsRefused_InBothModes(string text)
    {
        Money price = M(text, _usd);

        PartialPurchaseCalculator.ByAmount(price, M("50", _usd), Rate(_usd, _gtq, "7.75")).Error!.Code.Should().Be(CalculatorErrors.PriceOutOfRange);
        PartialPurchaseCalculator.ByShares(price, S("1"), Rate(_usd, _gtq, "7.75")).Error!.Code.Should().Be(CalculatorErrors.PriceOutOfRange);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-50")]
    public void AnAmountThatIsNotPositive_IsRefused(string text) =>
        PartialPurchaseCalculator.ByAmount(M("600", _usd), M(text, _usd), Rate(_usd, _gtq, "7.75")).Error!.Code.Should().Be(CalculatorErrors.AmountInvalid);

    [Fact]
    public void Currencies_ThatDoNotMatch_AreRefused()
    {
        ExchangeRate usdToGtq = Rate(_usd, _gtq, "7.75");

        PartialPurchaseCalculator.ByAmount(M("600", _usd), M("50", _gtq), usdToGtq).Error!.Code.Should().Be(MoneyErrors.CurrencyMismatch, "quetzales cannot buy a dollar-priced share");
        PartialPurchaseCalculator.ByAmount(M("600", _jpy), M("50", _jpy), usdToGtq).Error!.Code.Should().Be(MoneyErrors.CurrencyMismatch, "a rate from dollars does not convert yen");
        PartialPurchaseCalculator.ByShares(M("600", _jpy), S("1"), usdToGtq).Error!.Code.Should().Be(MoneyErrors.CurrencyMismatch);
    }

    [Fact]
    public void WhatTheKitCannotHold_IsAnOutcome_NotAnException()
    {
        ExchangeRate usdToGtq = Rate(_usd, _gtq, "7.75");

        PartialPurchaseCalculator.ByAmount(M("0.000001", _usd), Money.Create(Money.MaxMagnitude, _usd).Value, usdToGtq).Error!.Code
            .Should().Be(MoneyErrors.SharesInvalid, "a thousand million million at a millionth of a dollar is more shares than one purchase may hold");
        PartialPurchaseCalculator.ByShares(M("1000000000", _usd), Shares.Create(Shares.MaxQuantity).Value, usdToGtq).Error!.Code
            .Should().Be(MoneyErrors.AmountOutOfRange, "that many dear shares cost more than money can be");
        PartialPurchaseCalculator.ByAmount(M("600", _usd), M("500000000000000", _usd), ExchangeRate.Create(_usd, _gtq, ExchangeRate.MaxRate).Value).Error!.Code
            .Should().Be(MoneyErrors.AmountOutOfRange, "converted at the largest rate it is beyond the range of money");
    }
}
