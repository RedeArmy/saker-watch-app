using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.Calculator.Domain.Purchases;

namespace SakerWatch.Calculator.Tests;

/// <summary>A price inside the calculator's bounds, in dollars.</summary>
public readonly record struct PriceValue(decimal Value);

/// <summary>An amount to invest in dollars, a cent or more.</summary>
public readonly record struct AmountValue(decimal Value);

/// <summary>A number of shares with at most six decimals.</summary>
public readonly record struct SharesValue(decimal Value);

/// <summary>An exchange rate from dollars to quetzales-like values, inside the kit's range.</summary>
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

    /// <summary>Amounts from a cent to a thousand million.</summary>
    public static Arbitrary<AmountValue> Amounts() =>
        Scaled(2, 0.01m, 1_000_000_000m).Select(value => new AmountValue(value)).ToArbitrary();

    /// <summary>Shares from a millionth with up to six decimals.</summary>
    public static Arbitrary<SharesValue> Shares() =>
        Scaled(6, 0.000001m, 1_000_000m).Select(value => new SharesValue(value)).ToArbitrary();

    /// <summary>Rates from a hundred-millionth to a million.</summary>
    public static Arbitrary<RateValue> Rates() =>
        Scaled(8, 0.00000001m, 1_000_000m).Select(value => new RateValue(value)).ToArbitrary();
}

/// <summary>
/// The invariants of the calculator (ADR-0006, ADR-0037): a purchase is never shown as buying more than the money pays for, the two modes agree,
/// more money never buys less, a dearer share never buys more, the rate converts back, and currencies that do not match are always refused. Each
/// runs on many generated inputs across every scale.
/// </summary>
public sealed class PartialPurchasePropertyTests
{
    private const decimal _tolerance = 0.000000000000000001m; // 1e-18 of the value: the 28 digits of a decimal minus those a quotient may lose

    private static readonly CurrencyCode _usd = CurrencyCode.Parse("USD");
    private static readonly CurrencyCode _gtq = CurrencyCode.Parse("GTQ");
    private static readonly CurrencyCode _eur = CurrencyCode.Parse("EUR");

    private static decimal Gap(decimal a, decimal b) => Math.Abs(a - b);

    private static Money Usd(decimal amount) => Money.Create(amount, _usd).Value;

    private static ExchangeRate ToGtq(RateValue rate) => ExchangeRate.Create(_usd, _gtq, rate.Value).Value;

    [Property(Arbitrary = [typeof(CalculatorArbitraries)])]
    public void TheSharesShownNeverCostMoreThanTheAmount_AndLeaveLessThanAMillionthOfAShareUnspent(PriceValue price, AmountValue amount, RateValue rate)
    {
        Result<PartialPurchase> result = PartialPurchaseCalculator.ByAmount(Usd(price.Value), Usd(amount.Value), ToGtq(rate));
        if (!result.IsSuccess)
        {
            result.Error!.Code.Should().Be(MoneyErrors.SharesInvalid, "the only way an amount in these ranges fails is a quantity beyond the limit");
            return;
        }

        decimal cost = result.Value.ForDisplay().Shares.Value * price.Value;
        cost.Should().BeLessThanOrEqualTo(amount.Value + (amount.Value * _tolerance));
        (amount.Value - cost).Should().BeLessThan((price.Value * 0.000001m) + (amount.Value * _tolerance));
    }

    [Property(Arbitrary = [typeof(CalculatorArbitraries)])]
    public void TheExactSharesTimesThePrice_AreTheAmount(PriceValue price, AmountValue amount, RateValue rate)
    {
        Result<PartialPurchase> result = PartialPurchaseCalculator.ByAmount(Usd(price.Value), Usd(amount.Value), ToGtq(rate));
        if (!result.IsSuccess)
        {
            return;
        }

        Money cost = result.Value.Shares.Cost(result.Value.Price).Value;

        Gap(cost.Amount, amount.Value).Should().BeLessThanOrEqualTo(amount.Value * _tolerance);
    }

    [Property(Arbitrary = [typeof(CalculatorArbitraries)])]
    public void BothModesAgree_TheAmountOfOneModeBuysTheSharesOfTheOther(PriceValue price, SharesValue shares, RateValue rate)
    {
        Result<PartialPurchase> asked = PartialPurchaseCalculator.ByShares(Usd(price.Value), Shares.Create(shares.Value).Value, ToGtq(rate));
        if (!asked.IsSuccess)
        {
            asked.Error!.Code.Should().Be(MoneyErrors.AmountOutOfRange);
            return;
        }

        Result<PartialPurchase> back = PartialPurchaseCalculator.ByAmount(asked.Value.Price, asked.Value.Amount, ToGtq(rate));

        asked.Value.Shares.Value.Should().Be(shares.Value, "the shares of the second mode are exactly the ones asked for");
        back.IsSuccess.Should().BeTrue();
        Gap(back.Value.Shares.Value, shares.Value).Should().BeLessThanOrEqualTo(shares.Value * _tolerance);
        Gap(back.Value.AmountConverted.Amount, asked.Value.AmountConverted.Amount).Should().BeLessThanOrEqualTo(asked.Value.AmountConverted.Amount * _tolerance);
    }

    [Property(Arbitrary = [typeof(CalculatorArbitraries)])]
    public void MoreMoneyNeverBuysFewerShares_NorCostsFewerQuetzales(PriceValue price, AmountValue first, AmountValue second, RateValue rate)
    {
        (decimal low, decimal high) = first.Value <= second.Value ? (first.Value, second.Value) : (second.Value, first.Value);

        Result<PartialPurchase> small = PartialPurchaseCalculator.ByAmount(Usd(price.Value), Usd(low), ToGtq(rate));
        Result<PartialPurchase> large = PartialPurchaseCalculator.ByAmount(Usd(price.Value), Usd(high), ToGtq(rate));
        if (!small.IsSuccess || !large.IsSuccess)
        {
            return;
        }

        small.Value.Shares.Value.Should().BeLessThanOrEqualTo(large.Value.Shares.Value);
        small.Value.AmountConverted.Amount.Should().BeLessThanOrEqualTo(large.Value.AmountConverted.Amount);
        small.Value.ForDisplay().Shares.Value.Should().BeLessThanOrEqualTo(large.Value.ForDisplay().Shares.Value);
    }

    [Property(Arbitrary = [typeof(CalculatorArbitraries)])]
    public void ADearerShareNeverBuysMoreOfIt(PriceValue first, PriceValue second, AmountValue amount, RateValue rate)
    {
        (decimal cheap, decimal dear) = first.Value <= second.Value ? (first.Value, second.Value) : (second.Value, first.Value);

        Result<PartialPurchase> atCheap = PartialPurchaseCalculator.ByAmount(Usd(cheap), Usd(amount.Value), ToGtq(rate));
        Result<PartialPurchase> atDear = PartialPurchaseCalculator.ByAmount(Usd(dear), Usd(amount.Value), ToGtq(rate));
        if (!atCheap.IsSuccess || !atDear.IsSuccess)
        {
            return;
        }

        atDear.Value.Shares.Value.Should().BeLessThanOrEqualTo(atCheap.Value.Shares.Value);
    }

    [Property(Arbitrary = [typeof(CalculatorArbitraries)])]
    public void TheRateConvertsBack_ConvertedMoneyThroughTheInverseIsTheNativeMoney(PriceValue price, AmountValue amount, RateValue rate)
    {
        ExchangeRate usdToGtq = ToGtq(rate);
        Result<PartialPurchase> result = PartialPurchaseCalculator.ByAmount(Usd(price.Value), Usd(amount.Value), usdToGtq);
        if (!result.IsSuccess)
        {
            return;
        }

        Money amountBack = usdToGtq.Invert().Value.Convert(result.Value.AmountConverted).Value;
        Money priceBack = usdToGtq.Invert().Value.Convert(result.Value.PriceConverted).Value;

        Gap(amountBack.Amount, result.Value.Amount.Amount).Should().BeLessThanOrEqualTo(result.Value.Amount.Amount * _tolerance);
        Gap(priceBack.Amount, price.Value).Should().BeLessThanOrEqualTo(price.Value * _tolerance);
    }

    [Property(Arbitrary = [typeof(CalculatorArbitraries)])]
    public void DoublingTheAmountDoublesTheSharesAndTheQuetzales(PriceValue price, AmountValue amount, RateValue rate)
    {
        Result<PartialPurchase> one = PartialPurchaseCalculator.ByAmount(Usd(price.Value), Usd(amount.Value), ToGtq(rate));
        Result<PartialPurchase> two = PartialPurchaseCalculator.ByAmount(Usd(price.Value), Usd(amount.Value * 2m), ToGtq(rate));
        if (!one.IsSuccess || !two.IsSuccess)
        {
            return;
        }

        Gap(two.Value.Shares.Value, one.Value.Shares.Value * 2m).Should().BeLessThanOrEqualTo(two.Value.Shares.Value * _tolerance);
        two.Value.AmountConverted.Amount.Should().Be(one.Value.AmountConverted.Amount * 2m);
    }

    [Property(Arbitrary = [typeof(CalculatorArbitraries)])]
    public void TheDisplayIsWithinHalfAMinorUnitOfTheExactFigure_AndSharesAreNeverRoundedUp(PriceValue price, AmountValue amount, RateValue rate)
    {
        Result<PartialPurchase> result = PartialPurchaseCalculator.ByAmount(Usd(price.Value), Usd(amount.Value), ToGtq(rate));
        if (!result.IsSuccess)
        {
            return;
        }

        PartialPurchase exact = result.Value;
        PartialPurchaseDisplay shown = exact.ForDisplay();

        Gap(shown.Amount.Amount, exact.Amount.Amount).Should().BeLessThanOrEqualTo(0.005m);
        Gap(shown.AmountConverted.Amount, exact.AmountConverted.Amount).Should().BeLessThanOrEqualTo(0.005m);
        Gap(shown.PriceConverted.Amount, exact.PriceConverted.Amount).Should().BeLessThanOrEqualTo(0.005m);
        shown.Shares.Value.Should().BeLessThanOrEqualTo(exact.Shares.Value);
        (exact.Shares.Value - shown.Shares.Value).Should().BeLessThan(0.000001m);
    }

    [Property(Arbitrary = [typeof(CalculatorArbitraries)])]
    public void CurrenciesThatDoNotMatch_AreAlwaysRefused(PriceValue price, AmountValue amount, RateValue rate)
    {
        Money dollars = Usd(price.Value);
        Money euros = Money.Create(amount.Value, _eur).Value;
        ExchangeRate fromEuros = ExchangeRate.Create(_eur, _gtq, rate.Value).Value;

        PartialPurchaseCalculator.ByAmount(dollars, euros, ToGtq(rate)).Error!.Code.Should().Be(MoneyErrors.CurrencyMismatch);
        PartialPurchaseCalculator.ByAmount(dollars, Usd(amount.Value), fromEuros).Error!.Code.Should().Be(MoneyErrors.CurrencyMismatch);
        PartialPurchaseCalculator.ByShares(dollars, Shares.Create(1m).Value, fromEuros).Error!.Code.Should().Be(MoneyErrors.CurrencyMismatch);
    }

    [Property]
    public void AnythingThatIsNotPositive_IsRefusedWithTheMatchingCode(NegativeInt notPositive)
    {
        decimal value = notPositive.Get;
        ExchangeRate rate = ExchangeRate.Create(_usd, _gtq, 7.75m).Value;

        PartialPurchaseCalculator.ByAmount(Usd(value), Usd(50m), rate).Error!.Code.Should().Be(CalculatorErrors.PriceOutOfRange);
        PartialPurchaseCalculator.ByAmount(Usd(600m), Usd(value), rate).Error!.Code.Should().Be(CalculatorErrors.AmountInvalid);
        PartialPurchaseCalculator.ByShares(Usd(value), Shares.Create(1m).Value, rate).Error!.Code.Should().Be(CalculatorErrors.PriceOutOfRange);
    }
}
