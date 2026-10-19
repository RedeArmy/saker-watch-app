using AwesomeAssertions;
using SakerWatch.Calculator.Domain.Purchases;

namespace SakerWatch.Calculator.Tests;

/// <summary>Hand-computed cases of the partial-purchase arithmetic and of its presentation rounding (ADR-0006, ADR-0036).</summary>
public sealed class PartialPurchaseCalculatorTests
{
    [Fact]
    public void ByAmount_FiftyDollarsOnASixHundredDollarShare_BuysAboutATwelfth_AtTheRateOfTheDay()
    {
        PartialPurchase purchase = PartialPurchaseCalculator.ByAmount("USD", 600m, 50m, 7.75m).Value;

        purchase.Shares.Should().Be(50m / 600m, "kept exact until the presentation boundary");
        (purchase.AmountNative, purchase.CostOneShareGtq, purchase.AmountGtq).Should().Be((50m, 4650m, 387.5m));
        PartialPurchase shown = purchase.ForDisplay();
        (shown.Shares, shown.AmountNative, shown.CostOneShareGtq, shown.AmountGtq).Should().Be((0.083333m, 50m, 4650m, 387.5m));
    }

    [Fact]
    public void ByShares_ThreeAndAHalfSharesAtTwoHundredAndTwentyCents_CostsTheirProductInBothCurrencies()
    {
        PartialPurchase purchase = PartialPurchaseCalculator.ByShares("USD", 221.37m, 3.5m, 7.7821m).Value;

        purchase.Shares.Should().Be(3.5m, "the shares are the ones asked for");
        purchase.AmountNative.Should().Be(774.795m);
        purchase.AmountGtq.Should().Be(774.795m * 7.7821m);
        PartialPurchase shown = purchase.ForDisplay();
        (shown.Shares, shown.AmountNative, shown.AmountGtq, shown.CostOneShareGtq).Should().Be((3.5m, 774.80m, 6029.53m, 1722.72m));
    }

    [Theory]
    [InlineData("0.125", "0.12")]
    [InlineData("0.135", "0.14")]
    [InlineData("0.145", "0.14")]
    [InlineData("2.675", "2.68")]
    public void ForDisplay_RoundsMoneyToMinorUnitsHalfToEven(string exact, string expected)
    {
        var purchase = new PartialPurchase("USD", 1m, 1m, 1m, decimal.Parse(exact, System.Globalization.CultureInfo.InvariantCulture), 1m, decimal.Parse(exact, System.Globalization.CultureInfo.InvariantCulture));

        PartialPurchase shown = purchase.ForDisplay();

        shown.AmountNative.Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
        shown.AmountGtq.Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("0.0833333", "0.083333")]
    [InlineData("0.0833339", "0.083333")]
    [InlineData("0.9999999", "0.999999")]
    [InlineData("12.5", "12.5")]
    public void ForDisplay_CutsSharesTowardsZero_NeverUpToANeighbour(string exact, string expected)
    {
        var purchase = new PartialPurchase("USD", 1m, 1m, decimal.Parse(exact, System.Globalization.CultureInfo.InvariantCulture), 1m, 1m, 1m);

        purchase.ForDisplay().Shares.Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("JPY", 0)]
    [InlineData("KRW", 0)]
    [InlineData("USD", 2)]
    [InlineData("CAD", 2)]
    [InlineData("EUR", 2)]
    [InlineData("GTQ", 2)]
    [InlineData("KWD", 3)]
    public void MinorUnits_FollowTheCurrency(string currency, int decimals) =>
        CurrencyMinorUnits.Of(currency).Should().Be(decimals);

    [Fact]
    public void ForDisplay_ShowsTheNativeAmountInTheMinorUnitsOfItsCurrency_AndQuetzalesWithTwo()
    {
        PartialPurchase yen = PartialPurchaseCalculator.ByShares("JPY", 2987.5m, 1.5m, 0.0518m).Value;

        PartialPurchase shown = yen.ForDisplay();

        shown.AmountNative.Should().Be(4481m, "4481.25 yen, shown without decimals");
        shown.AmountGtq.Should().Be(232.13m, "4481.25 x 0.0518 = 232.12875");
        shown.CostOneShareGtq.Should().Be(154.75m, "2987.5 x 0.0518 = 154.7525");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.0000009")]
    [InlineData("1000000000.01")]
    public void APriceOutsideTheBounds_IsRefused_InBothModes(string text)
    {
        decimal price = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

        PartialPurchaseCalculator.ByAmount("USD", price, 50m, 7.75m).Error!.Code.Should().Be(CalculatorErrors.PriceInvalid);
        PartialPurchaseCalculator.ByShares("USD", price, 1m, 7.75m).Error!.Code.Should().Be(CalculatorErrors.PriceInvalid);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-50")]
    [InlineData("1000000000000.01")]
    public void AnAmountOutsideTheBounds_IsRefused(string text) =>
        PartialPurchaseCalculator.ByAmount("USD", 600m, decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture), 7.75m).Error!.Code.Should().Be(CalculatorErrors.AmountInvalid);

    [Theory]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("0.0000001")]
    [InlineData("1000000000000.01")]
    public void SharesOutsideTheBounds_OrWithMoreThanSixDecimals_AreRefused(string text) =>
        PartialPurchaseCalculator.ByShares("USD", 600m, decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture), 7.75m).Error!.Code.Should().Be(CalculatorErrors.SharesInvalid);

    [Theory]
    [InlineData("0")]
    [InlineData("-7.75")]
    [InlineData("0.000000001")]
    [InlineData("1000000.01")]
    public void ARateOutsideTheBounds_IsRefused_InBothModes(string text)
    {
        decimal rate = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

        PartialPurchaseCalculator.ByAmount("USD", 600m, 50m, rate).Error!.Code.Should().Be(CalculatorErrors.RateInvalid);
        PartialPurchaseCalculator.ByShares("USD", 600m, 1m, rate).Error!.Code.Should().Be(CalculatorErrors.RateInvalid);
    }

    [Fact]
    public void TheLargestAcceptedInputs_NeitherOverflowNorLoseTheShape()
    {
        PartialPurchase biggest = PartialPurchaseCalculator.ByAmount("USD", PartialPurchaseCalculator.MinPrice, PartialPurchaseCalculator.MaxAmount, PartialPurchaseCalculator.MaxRate).Value;
        PartialPurchase dearest = PartialPurchaseCalculator.ByShares("USD", PartialPurchaseCalculator.MaxPrice, PartialPurchaseCalculator.MaxShares, PartialPurchaseCalculator.MaxRate).Value;

        biggest.Shares.Should().Be(1_000_000_000_000m / 0.000001m);
        dearest.AmountGtq.Should().Be(1_000_000_000m * 1_000_000_000_000m * 1_000_000m);
        biggest.ForDisplay().Shares.Should().BePositive();
    }
}
