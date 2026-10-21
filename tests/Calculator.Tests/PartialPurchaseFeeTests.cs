using System.Globalization;
using AwesomeAssertions;
using FsCheck.Xunit;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.Calculator.Domain.Purchases;

namespace SakerWatch.Calculator.Tests;

/// <summary>
/// The broker's commission (ADR-0036): a percentage of the value of the shares, part of the total paid. Without one it is zero and nothing changes.
/// </summary>
public sealed class PartialPurchaseFeeTests
{
    private const decimal _tolerance = 0.000000000000000001m;

    private static readonly CurrencyCode _usd = CurrencyCode.Parse("USD");
    private static readonly CurrencyCode _gtq = CurrencyCode.Parse("GTQ");

    private static decimal D(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);

    private static Money M(string amount, CurrencyCode currency) => Money.Create(D(amount), currency).Value;

    private static ExchangeRate Rate() => ExchangeRate.Create(_usd, _gtq, 7.75m).Value;

    private static FeePercent Fee(string percent) => FeePercent.Create(D(percent)).Value;

    private static decimal Gap(decimal a, decimal b) => Math.Abs(a - b);

    [Fact]
    public void ByAmount_TheAmountIsTheTotalPaid_AndTheSharesAreBoughtWithWhatTheCommissionLeaves()
    {
        PartialPurchase purchase = PartialPurchaseCalculator.ByAmount(M("600", _usd), M("101", _usd), Rate(), Fee("1")).Value;

        purchase.Shares.Value.Should().Be(100m / 600m, "101 paid in all, of which 1 is commission, buys 100 worth of shares");
        (purchase.Amount, purchase.Fee).Should().Be((M("101", _usd), M("1", _usd)));
        (purchase.AmountConverted, purchase.FeeConverted).Should().Be((M("782.75", _gtq), M("7.75", _gtq)));
    }

    [Fact]
    public void ByShares_TheCommissionIsAddedToTheCostOfTheShares()
    {
        PartialPurchase purchase = PartialPurchaseCalculator.ByShares(M("50", _usd), Shares.FromRequest(2m).Value, Rate(), Fee("1")).Value;

        (purchase.Amount, purchase.Fee, purchase.Shares.Value).Should().Be((M("101", _usd), M("1", _usd), 2m));
        (purchase.AmountConverted, purchase.FeeConverted).Should().Be((M("782.75", _gtq), M("7.75", _gtq)));
    }

    [Fact]
    public void ACommissionOfAHundredDollarsAtOnePercent_BuysWithNinetyNineAndAFraction()
    {
        PartialPurchase purchase = PartialPurchaseCalculator.ByAmount(M("600", _usd), M("100", _usd), Rate(), Fee("1")).Value;

        purchase.Fee.Amount.Should().BeApproximately(0.990099009900990099m, _tolerance, "the commission is 1 % of the 99.0099 the shares are worth, not 1 % of the 100");
        purchase.Shares.Value.Should().BeApproximately(100m / 1.01m / 600m, _tolerance);
        PartialPurchaseDisplay shown = purchase.ForDisplay();
        (shown.Amount.Amount, shown.Fee.Amount, shown.AmountConverted.Amount, shown.FeeConverted.Amount, shown.Shares.Value).Should().Be((100m, 0.99m, 775m, 7.67m, 0.165016m));
    }

    [Fact]
    public void WithoutACommission_NothingChanges_NoneAndZeroAreTheSame()
    {
        PartialPurchase none = PartialPurchaseCalculator.ByAmount(M("600", _usd), M("50", _usd), Rate()).Value;
        PartialPurchase zero = PartialPurchaseCalculator.ByAmount(M("600", _usd), M("50", _usd), Rate(), Fee("0")).Value;

        zero.Should().Be(none);
        (none.Fee, none.FeeConverted, none.FeePercent).Should().Be((M("0", _usd), M("0", _gtq), FeePercent.None));
        (none.Shares.Value, none.Amount, none.AmountConverted).Should().Be((50m / 600m, M("50", _usd), M("387.5", _gtq)));
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("10.01")]
    [InlineData("100")]
    public void ACommissionOutsideZeroToTenPercent_IsRefused(string percent) =>
        FeePercent.Create(D(percent)).Error!.Code.Should().Be(CalculatorErrors.FeeOutOfRange);

    [Theory]
    [InlineData("0")]
    [InlineData("0.5")]
    [InlineData("10")]
    public void ACommissionBetweenZeroAndTenPercent_IsAccepted(string percent)
    {
        FeePercent fee = FeePercent.Create(D(percent)).Value;

        (fee.Value, fee.Fraction, fee.IsNone).Should().Be((D(percent), D(percent) / 100m, D(percent) == 0m));
    }

    [Property(Arbitrary = [typeof(CalculatorArbitraries)])]
    public void ByShares_TheTotalIsTheValueOfTheSharesPlusTheCommission_AndNeverLess(PriceValue price, SharesValue shares, FeeValue fee, RateValue rate)
    {
        ExchangeRate usdToGtq = ExchangeRate.Create(_usd, _gtq, rate.Value).Value;
        Result<PartialPurchase> result = PartialPurchaseCalculator.ByShares(Money.Create(price.Value, _usd).Value, Shares.Create(shares.Value).Value, usdToGtq, FeePercent.Create(fee.Value).Value);
        if (!result.IsSuccess)
        {
            return;
        }

        PartialPurchase purchase = result.Value;
        Money value = purchase.Shares.Cost(purchase.Price).Value;
        purchase.Amount.Should().Be(value.Add(purchase.Fee).Value, "the total is exactly the value plus the commission");
        Gap(purchase.Fee.Amount, value.Amount * fee.Value / 100m).Should().BeLessThanOrEqualTo((value.Amount * _tolerance) + _tolerance);
        purchase.Amount.Amount.Should().BeGreaterThanOrEqualTo(value.Amount);
        purchase.Fee.Amount.Should().BeGreaterThanOrEqualTo(0m);
    }

    [Property(Arbitrary = [typeof(CalculatorArbitraries)])]
    public void ByAmount_TheCommissionIsPartOfTheAmount_AndTheRestBuysTheShares(PriceValue price, AmountValue amount, FeeValue fee, RateValue rate)
    {
        ExchangeRate usdToGtq = ExchangeRate.Create(_usd, _gtq, rate.Value).Value;
        Result<PartialPurchase> result = PartialPurchaseCalculator.ByAmount(Money.Create(price.Value, _usd).Value, Money.Create(amount.Value, _usd).Value, usdToGtq, FeePercent.Create(fee.Value).Value);
        if (!result.IsSuccess)
        {
            return;
        }

        PartialPurchase purchase = result.Value;
        purchase.Fee.Amount.Should().BeInRange(0m, amount.Value);
        Money value = purchase.Shares.Cost(purchase.Price).Value;
        Gap(value.Amount + purchase.Fee.Amount, amount.Value).Should().BeLessThanOrEqualTo(amount.Value * _tolerance, "shares plus commission are the amount paid");
        purchase.Amount.Amount.Should().Be(amount.Value, "the amount given is the total paid");
        decimal shownCost = purchase.ForDisplay().Shares.Value * price.Value;
        shownCost.Should().BeLessThanOrEqualTo(amount.Value / (1m + (fee.Value / 100m)) * (1m + _tolerance), "the shown shares never cost more than what the commission left");
    }

    [Property(Arbitrary = [typeof(CalculatorArbitraries)])]
    public void ABiggerCommission_NeverBuysMoreShares_AndNeverCostsLess(PriceValue price, AmountValue amount, FeeValue first, FeeValue second, RateValue rate)
    {
        (decimal low, decimal high) = first.Value <= second.Value ? (first.Value, second.Value) : (second.Value, first.Value);
        ExchangeRate usdToGtq = ExchangeRate.Create(_usd, _gtq, rate.Value).Value;
        Money unit = Money.Create(price.Value, _usd).Value;
        Money paid = Money.Create(amount.Value, _usd).Value;

        Result<PartialPurchase> cheap = PartialPurchaseCalculator.ByAmount(unit, paid, usdToGtq, FeePercent.Create(low).Value);
        Result<PartialPurchase> dear = PartialPurchaseCalculator.ByAmount(unit, paid, usdToGtq, FeePercent.Create(high).Value);
        if (cheap.IsSuccess && dear.IsSuccess)
        {
            dear.Value.Shares.Value.Should().BeLessThanOrEqualTo(cheap.Value.Shares.Value);
        }

        Shares quantity = Shares.Create(Math.Min(amount.Value, 1_000m) + 0.000001m).Value;
        Result<PartialPurchase> costsLess = PartialPurchaseCalculator.ByShares(unit, quantity, usdToGtq, FeePercent.Create(low).Value);
        Result<PartialPurchase> costsMore = PartialPurchaseCalculator.ByShares(unit, quantity, usdToGtq, FeePercent.Create(high).Value);
        if (costsLess.IsSuccess && costsMore.IsSuccess)
        {
            costsMore.Value.Amount.Amount.Should().BeGreaterThanOrEqualTo(costsLess.Value.Amount.Amount);
        }
    }

    [Property(Arbitrary = [typeof(CalculatorArbitraries)])]
    public void BothModesAgreeWithACommission_ThePaidTotalBuysTheSameShares(PriceValue price, SharesValue shares, FeeValue fee, RateValue rate)
    {
        ExchangeRate usdToGtq = ExchangeRate.Create(_usd, _gtq, rate.Value).Value;
        Money unit = Money.Create(price.Value, _usd).Value;
        FeePercent commission = FeePercent.Create(fee.Value).Value;
        Result<PartialPurchase> asked = PartialPurchaseCalculator.ByShares(unit, Shares.Create(shares.Value).Value, usdToGtq, commission);
        if (!asked.IsSuccess)
        {
            return;
        }

        Result<PartialPurchase> back = PartialPurchaseCalculator.ByAmount(unit, asked.Value.Amount, usdToGtq, commission);

        back.IsSuccess.Should().BeTrue();
        Gap(back.Value.Shares.Value, shares.Value).Should().BeLessThanOrEqualTo(shares.Value * _tolerance * 10m);
        Gap(back.Value.Fee.Amount, asked.Value.Fee.Amount).Should().BeLessThanOrEqualTo((asked.Value.Amount.Amount * _tolerance) + _tolerance);
    }

    [Property(Arbitrary = [typeof(CalculatorArbitraries)])]
    public void ZeroIsTheSameAsNoCommission(PriceValue price, AmountValue amount, RateValue rate)
    {
        ExchangeRate usdToGtq = ExchangeRate.Create(_usd, _gtq, rate.Value).Value;
        Money unit = Money.Create(price.Value, _usd).Value;
        Money paid = Money.Create(amount.Value, _usd).Value;

        Result<PartialPurchase> none = PartialPurchaseCalculator.ByAmount(unit, paid, usdToGtq);
        Result<PartialPurchase> zero = PartialPurchaseCalculator.ByAmount(unit, paid, usdToGtq, FeePercent.None);

        zero.IsSuccess.Should().Be(none.IsSuccess);
        if (none.IsSuccess)
        {
            zero.Value.Should().Be(none.Value);
        }
    }
}
