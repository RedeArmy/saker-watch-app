using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using SakerWatch.Calculator.Domain.Purchases;

namespace SakerWatch.Calculator.Tests;

/// <summary>A price inside the bounds of the calculator.</summary>
public readonly record struct PriceValue(decimal Value);

/// <summary>An amount to invest inside the bounds.</summary>
public readonly record struct AmountValue(decimal Value);

/// <summary>A number of shares inside the bounds, with at most six decimals.</summary>
public readonly record struct SharesValue(decimal Value);

/// <summary>An exchange rate inside the bounds.</summary>
public readonly record struct RateValue(decimal Value);

/// <summary>Generators of values that span the scales real inputs have, from fractions of a cent to millions.</summary>
public static class CalculatorArbitraries
{
    // A whole number divided by a power of ten: every scale from 0 to maxScale decimals, with a mantissa up to a billion.
    private static Gen<decimal> Scaled(int maxScale, decimal min, decimal max) =>
        Gen.Choose(1, 999_999_999).SelectMany(mantissa =>
            Gen.Choose(0, maxScale).Select(scale => Math.Clamp(mantissa / (decimal)Math.Pow(10, scale), min, max)));

    /// <summary>Prices from one millionth to a thousand million.</summary>
    public static Arbitrary<PriceValue> Prices() =>
        Scaled(6, PartialPurchaseCalculator.MinPrice, PartialPurchaseCalculator.MaxPrice).Select(value => new PriceValue(value)).ToArbitrary();

    /// <summary>Amounts from a cent to a million million.</summary>
    public static Arbitrary<AmountValue> Amounts() =>
        Scaled(2, 0.01m, PartialPurchaseCalculator.MaxAmount).Select(value => new AmountValue(value)).ToArbitrary();

    /// <summary>Shares from a millionth with up to six decimals.</summary>
    public static Arbitrary<SharesValue> Shares() =>
        Scaled(6, 0.000001m, PartialPurchaseCalculator.MaxShares).Select(value => new SharesValue(value)).ToArbitrary();

    /// <summary>Rates from a hundred-millionth to a million.</summary>
    public static Arbitrary<RateValue> Rates() =>
        Scaled(8, PartialPurchaseCalculator.MinRate, PartialPurchaseCalculator.MaxRate).Select(value => new RateValue(value)).ToArbitrary();
}

/// <summary>
/// The invariants of the calculator (ADR-0006): a purchase is never shown as buying more than the money pays for, the two modes agree, more money
/// never buys less, a dearer share never buys more, and a rate converts back. Each runs on many generated inputs across every scale.
/// </summary>
public sealed class PartialPurchasePropertyTests
{
    private const decimal _tolerance = 0.000000000000000001m; // 1e-18 of the value: the 28 digits of a decimal minus those a quotient may lose

    private static decimal Gap(decimal a, decimal b) => Math.Abs(a - b);

    [Property(Arbitrary = [typeof(CalculatorArbitraries)])]
    public void TheSharesShownNeverCostMoreThanTheAmount_AndLeaveLessThanAMillionthOfAShareUnspent(PriceValue price, AmountValue amount, RateValue rate)
    {
        PartialPurchase shown = PartialPurchaseCalculator.ByAmount("USD", price.Value, amount.Value, rate.Value).Value.ForDisplay();

        decimal cost = shown.Shares * price.Value;
        cost.Should().BeLessThanOrEqualTo(amount.Value + (amount.Value * _tolerance));
        (amount.Value - cost).Should().BeLessThan((price.Value * 0.000001m) + (amount.Value * _tolerance));
    }

    [Property(Arbitrary = [typeof(CalculatorArbitraries)])]
    public void TheExactSharesTimesThePrice_AreTheAmount(PriceValue price, AmountValue amount, RateValue rate)
    {
        PartialPurchase exact = PartialPurchaseCalculator.ByAmount("USD", price.Value, amount.Value, rate.Value).Value;

        Gap(exact.Shares * price.Value, amount.Value).Should().BeLessThanOrEqualTo(amount.Value * _tolerance);
    }

    [Property(Arbitrary = [typeof(CalculatorArbitraries)])]
    public void BothModesAgree_SharesBoughtWithTheAmountOfTheOtherModeAreTheSame(PriceValue price, SharesValue shares, RateValue rate)
    {
        PartialPurchase asked = PartialPurchaseCalculator.ByShares("USD", price.Value, shares.Value, rate.Value).Value;
        if (asked.AmountNative > PartialPurchaseCalculator.MaxAmount)
        {
            return; // the cost of that many shares is more than an amount the first mode accepts
        }

        PartialPurchase back = PartialPurchaseCalculator.ByAmount("USD", price.Value, asked.AmountNative, rate.Value).Value;

        asked.Shares.Should().Be(shares.Value, "the shares of the second mode are exactly the ones asked for");
        Gap(back.Shares, shares.Value).Should().BeLessThanOrEqualTo(shares.Value * _tolerance);
        Gap(back.AmountGtq, asked.AmountGtq).Should().BeLessThanOrEqualTo(asked.AmountGtq * _tolerance);
    }

    [Property(Arbitrary = [typeof(CalculatorArbitraries)])]
    public void MoreMoneyNeverBuysFewerShares_NorCostsFewerQuetzales(PriceValue price, AmountValue first, AmountValue second, RateValue rate)
    {
        (decimal low, decimal high) = first.Value <= second.Value ? (first.Value, second.Value) : (second.Value, first.Value);

        PartialPurchase small = PartialPurchaseCalculator.ByAmount("USD", price.Value, low, rate.Value).Value;
        PartialPurchase large = PartialPurchaseCalculator.ByAmount("USD", price.Value, high, rate.Value).Value;

        small.Shares.Should().BeLessThanOrEqualTo(large.Shares);
        small.AmountGtq.Should().BeLessThanOrEqualTo(large.AmountGtq);
        small.ForDisplay().Shares.Should().BeLessThanOrEqualTo(large.ForDisplay().Shares);
    }

    [Property(Arbitrary = [typeof(CalculatorArbitraries)])]
    public void ADearerShareNeverBuysMoreOfIt(PriceValue first, PriceValue second, AmountValue amount, RateValue rate)
    {
        (decimal cheap, decimal dear) = first.Value <= second.Value ? (first.Value, second.Value) : (second.Value, first.Value);

        PartialPurchase atCheap = PartialPurchaseCalculator.ByAmount("USD", cheap, amount.Value, rate.Value).Value;
        PartialPurchase atDear = PartialPurchaseCalculator.ByAmount("USD", dear, amount.Value, rate.Value).Value;

        atDear.Shares.Should().BeLessThanOrEqualTo(atCheap.Shares);
    }

    [Property(Arbitrary = [typeof(CalculatorArbitraries)])]
    public void TheRateConvertsBack_QuetzalesDividedByTheRateAreTheNativeAmount(PriceValue price, AmountValue amount, RateValue rate)
    {
        PartialPurchase purchase = PartialPurchaseCalculator.ByAmount("USD", price.Value, amount.Value, rate.Value).Value;

        Gap(purchase.AmountGtq / rate.Value, purchase.AmountNative).Should().BeLessThanOrEqualTo(purchase.AmountNative * _tolerance);
        Gap(purchase.CostOneShareGtq / rate.Value, price.Value).Should().BeLessThanOrEqualTo(price.Value * _tolerance);
    }

    [Property(Arbitrary = [typeof(CalculatorArbitraries)])]
    public void DoublingTheAmountDoublesTheSharesAndTheQuetzales(PriceValue price, AmountValue amount, RateValue rate)
    {
        decimal twice = amount.Value * 2m;
        if (twice > PartialPurchaseCalculator.MaxAmount)
        {
            return;
        }

        PartialPurchase one = PartialPurchaseCalculator.ByAmount("USD", price.Value, amount.Value, rate.Value).Value;
        PartialPurchase two = PartialPurchaseCalculator.ByAmount("USD", price.Value, twice, rate.Value).Value;

        Gap(two.Shares, one.Shares * 2m).Should().BeLessThanOrEqualTo(two.Shares * _tolerance);
        two.AmountGtq.Should().Be(one.AmountGtq * 2m);
    }

    [Property(Arbitrary = [typeof(CalculatorArbitraries)])]
    public void TheDisplayIsWithinHalfAMinorUnitOfTheExactFigure_AndSharesAreNeverRoundedUp(PriceValue price, AmountValue amount, RateValue rate)
    {
        PartialPurchase exact = PartialPurchaseCalculator.ByAmount("USD", price.Value, amount.Value, rate.Value).Value;

        PartialPurchase shown = exact.ForDisplay();

        Gap(shown.AmountNative, exact.AmountNative).Should().BeLessThanOrEqualTo(0.005m);
        Gap(shown.AmountGtq, exact.AmountGtq).Should().BeLessThanOrEqualTo(0.005m);
        Gap(shown.CostOneShareGtq, exact.CostOneShareGtq).Should().BeLessThanOrEqualTo(0.005m);
        shown.Shares.Should().BeLessThanOrEqualTo(exact.Shares);
        (exact.Shares - shown.Shares).Should().BeLessThan(0.000001m);
    }

    [Property]
    public void AnythingThatIsNotPositive_IsRefusedWithTheMatchingCode(NegativeInt notPositive)
    {
        decimal value = notPositive.Get;

        PartialPurchaseCalculator.ByAmount("USD", value, 50m, 7.75m).Error!.Code.Should().Be(CalculatorErrors.PriceInvalid);
        PartialPurchaseCalculator.ByAmount("USD", 600m, value, 7.75m).Error!.Code.Should().Be(CalculatorErrors.AmountInvalid);
        PartialPurchaseCalculator.ByShares("USD", 600m, value, 7.75m).Error!.Code.Should().Be(CalculatorErrors.SharesInvalid);
        PartialPurchaseCalculator.ByAmount("USD", 600m, 50m, value).Error!.Code.Should().Be(CalculatorErrors.RateInvalid);
    }
}
