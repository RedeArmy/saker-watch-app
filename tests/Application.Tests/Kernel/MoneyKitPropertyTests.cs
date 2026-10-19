using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.Application.Tests.Kernel;

/// <summary>An exact amount of money, up to a thousand million, with up to eight decimals.</summary>
public readonly record struct AmountOf(decimal Value);

/// <summary>A price of a share: from a millionth to a million.</summary>
public readonly record struct PriceOf(decimal Value);

/// <summary>An exchange rate from a hundred-millionth to a million.</summary>
public readonly record struct RateOf(decimal Value);

/// <summary>A currency among a few with different minor units.</summary>
public readonly record struct CurrencyOf(CurrencyCode Value);

/// <summary>Generators that span the scales real money has, from fractions of a cent to thousands of millions.</summary>
public static class MoneyKitArbitraries
{
    private static readonly CurrencyCode[] _currencies = [CurrencyCode.Parse("USD"), CurrencyCode.Parse("GTQ"), CurrencyCode.Parse("JPY"), CurrencyCode.Parse("KWD"), CurrencyCode.Parse("EUR")];

    private static Gen<decimal> Scaled(int maxScale, decimal min, decimal max) =>
        Gen.Choose(1, 999_999_999).SelectMany(mantissa =>
            Gen.Choose(0, maxScale).Select(scale => Math.Clamp(mantissa / (decimal)Math.Pow(10, scale), min, max)));

    /// <summary>Amounts from a hundred-millionth to a thousand million, in either sign.</summary>
    public static Arbitrary<AmountOf> Amounts() =>
        Scaled(8, 0.00000001m, 1_000_000_000m).SelectMany(value => Gen.Elements(1m, -1m).Select(sign => new AmountOf(value * sign))).ToArbitrary();

    /// <summary>Prices of one share.</summary>
    public static Arbitrary<PriceOf> Prices() =>
        Scaled(6, 0.000001m, 1_000_000m).Select(value => new PriceOf(value)).ToArbitrary();

    /// <summary>Exchange rates.</summary>
    public static Arbitrary<RateOf> Rates() =>
        Scaled(8, 0.00000001m, 1_000_000m).Select(value => new RateOf(value)).ToArbitrary();

    /// <summary>Currencies.</summary>
    public static Arbitrary<CurrencyOf> Currencies() =>
        Gen.Elements(_currencies).Select(currency => new CurrencyOf(currency)).ToArbitrary();
}

/// <summary>
/// The guarantees of the money kit (ADR-0037), each checked on many generated inputs across every scale: nothing throws, nothing is rounded
/// before display, currencies and directions cannot be mixed, and what is shown is within half a minor unit of what is exact.
/// </summary>
public sealed class MoneyKitPropertyTests
{
    private const decimal _tolerance = 0.00000000000000000001m; // 1e-20 relative: well inside the 28 digits of a decimal

    private static readonly CurrencyCode _usd = CurrencyCode.Parse("USD");
    private static readonly CurrencyCode _gtq = CurrencyCode.Parse("GTQ");

    private static decimal Gap(decimal a, decimal b) => Math.Abs(a - b);

    [Property(Arbitrary = [typeof(MoneyKitArbitraries)])]
    public void AddingThenSubtracting_GivesBackTheSameMoney_ToTheLastDigit(AmountOf first, AmountOf second, CurrencyOf currency)
    {
        Money a = Money.Create(first.Value, currency.Value).Value;
        Money b = Money.Create(second.Value, currency.Value).Value;

        Money sum = a.Add(b).Value;

        sum.Subtract(b).Value.Should().Be(a);
        a.Add(b).Value.Should().Be(b.Add(a).Value, "addition does not depend on the order");
    }

    [Property(Arbitrary = [typeof(MoneyKitArbitraries)])]
    public void MoneyOfDifferentCurrencies_NeverMixes(AmountOf first, AmountOf second, CurrencyOf one, CurrencyOf other)
    {
        Money a = Money.Create(first.Value, one.Value).Value;
        Money b = Money.Create(second.Value, other.Value).Value;

        Result<Money> sum = a.Add(b);

        if (one.Value == other.Value)
        {
            sum.IsSuccess.Should().BeTrue();
        }
        else
        {
            sum.Error!.Code.Should().Be(MoneyErrors.CurrencyMismatch);
            a.Subtract(b).Error!.Code.Should().Be(MoneyErrors.CurrencyMismatch);
        }
    }

    [Property(Arbitrary = [typeof(MoneyKitArbitraries)])]
    public void TheDisplay_IsWithinHalfAMinorUnitOfTheExactAmount_AndIsItsOwnDisplay(AmountOf amount, CurrencyOf currency)
    {
        Money exact = Money.Create(amount.Value, currency.Value).Value;

        DisplayMoney shown = exact.ToDisplay();

        decimal halfUnit = 0.5m / (decimal)Math.Pow(10, currency.Value.MinorUnits);
        Gap(shown.Amount, exact.Amount).Should().BeLessThanOrEqualTo(halfUnit);
        Money.Create(shown.Amount, currency.Value).Value.ToDisplay().Amount.Should().Be(shown.Amount, "showing a shown amount changes nothing");
        decimal.Round(shown.Amount, currency.Value.MinorUnits).Should().Be(shown.Amount, "no more decimals than the currency has");
    }

    [Property(Arbitrary = [typeof(MoneyKitArbitraries)])]
    public void DisplayingDoesNotChangeTheExactMoney(AmountOf amount, CurrencyOf currency)
    {
        Money exact = Money.Create(amount.Value, currency.Value).Value;
        decimal before = exact.Amount;

        _ = exact.ToDisplay();

        exact.Amount.Should().Be(before);
    }

    [Property(Arbitrary = [typeof(MoneyKitArbitraries)])]
    public void ARateConvertsBack_ByItsInverse_ToTheLastDigits(AmountOf amount, RateOf rate)
    {
        Money usd = Money.Create(Math.Abs(amount.Value), _usd).Value;
        ExchangeRate usdGtq = ExchangeRate.Create(_usd, _gtq, rate.Value).Value;

        Money gtq = usdGtq.Convert(usd).Value;
        Money back = usdGtq.Invert().Value.Convert(gtq).Value;

        gtq.Currency.Should().Be(_gtq);
        back.Currency.Should().Be(_usd);
        Gap(back.Amount, usd.Amount).Should().BeLessThanOrEqualTo((usd.Amount * _tolerance) + _tolerance);
        usdGtq.Invert().Value.Invert().Value.Rate.Should().BeApproximately(rate.Value, rate.Value * _tolerance);
    }

    [Property(Arbitrary = [typeof(MoneyKitArbitraries)])]
    public void ARateNeverConvertsMoneyOfAnotherCurrency(AmountOf amount, RateOf rate)
    {
        ExchangeRate usdGtq = ExchangeRate.Create(_usd, _gtq, rate.Value).Value;

        usdGtq.Convert(Money.Create(amount.Value, _gtq).Value).Error!.Code.Should().Be(MoneyErrors.CurrencyMismatch);
        usdGtq.Invert().Value.Convert(Money.Create(amount.Value, _usd).Value).Error!.Code.Should().Be(MoneyErrors.CurrencyMismatch);
    }

    [Property(Arbitrary = [typeof(MoneyKitArbitraries)])]
    public void ChainingRates_ConvertsLikeConvertingTwice(AmountOf amount, RateOf first, RateOf second)
    {
        CurrencyCode jpy = CurrencyCode.Parse("JPY");
        Money start = Money.Create(Math.Abs(amount.Value), jpy).Value;
        ExchangeRate jpyUsd = ExchangeRate.Create(jpy, _usd, first.Value).Value;
        ExchangeRate usdGtq = ExchangeRate.Create(_usd, _gtq, second.Value).Value;

        Result<Money> inDollars = jpyUsd.Convert(start);
        Result<ExchangeRate> chained = jpyUsd.Then(usdGtq);
        if (!inDollars.IsSuccess || !chained.IsSuccess)
        {
            return; // a step beyond the range of money or of rates is an outcome, covered by its own test
        }

        Result<Money> viaTwo = usdGtq.Convert(inDollars.Value);
        Result<Money> direct = chained.Value.Convert(start);
        if (!viaTwo.IsSuccess || !direct.IsSuccess)
        {
            return;
        }

        Gap(direct.Value.Amount, viaTwo.Value.Amount).Should().BeLessThanOrEqualTo((viaTwo.Value.Amount * _tolerance) + _tolerance);
    }

    [Property(Arbitrary = [typeof(MoneyKitArbitraries)])]
    public void SharesBoughtAndTheirCost_ReturnTheAmount_AndTheShownSharesNeverCostMore(AmountOf amount, PriceOf price, CurrencyOf currency)
    {
        Money spend = Money.Create(Math.Abs(amount.Value) + 0.01m, currency.Value).Value;
        Money unit = Money.Create(price.Value, currency.Value).Value;

        Result<Shares> bought = Shares.Buying(spend, unit);
        if (!bought.IsSuccess)
        {
            bought.Error!.Code.Should().Be(MoneyErrors.SharesInvalid, "the only reason is a quantity beyond the limit");
            return;
        }

        Money cost = bought.Value.Cost(unit).Value;
        Gap(cost.Amount, spend.Amount).Should().BeLessThanOrEqualTo(spend.Amount * _tolerance);

        decimal shownCost = bought.Value.ToDisplay().Value * unit.Amount;
        shownCost.Should().BeLessThanOrEqualTo(spend.Amount + (spend.Amount * _tolerance), "the shown shares never cost more than the money paid");
        (spend.Amount - shownCost).Should().BeLessThan((unit.Amount * 0.000001m) + (spend.Amount * _tolerance), "and leave less than a millionth of a share unspent");
    }

    [Property(Arbitrary = [typeof(MoneyKitArbitraries)])]
    public void MoreMoney_NeverBuysFewerShares_AndADearerShareNeverBuysMore(AmountOf first, AmountOf second, PriceOf cheap, PriceOf dear, CurrencyOf currency)
    {
        (decimal low, decimal high) = Math.Abs(first.Value) <= Math.Abs(second.Value) ? (Math.Abs(first.Value), Math.Abs(second.Value)) : (Math.Abs(second.Value), Math.Abs(first.Value));
        (decimal cheapPrice, decimal dearPrice) = cheap.Value <= dear.Value ? (cheap.Value, dear.Value) : (dear.Value, cheap.Value);
        Money small = Money.Create(low + 0.01m, currency.Value).Value;
        Money large = Money.Create(high + 0.01m, currency.Value).Value;
        Money atCheap = Money.Create(cheapPrice, currency.Value).Value;
        Money atDear = Money.Create(dearPrice, currency.Value).Value;

        Result<Shares> fewer = Shares.Buying(small, atCheap);
        Result<Shares> more = Shares.Buying(large, atCheap);
        Result<Shares> pricier = Shares.Buying(large, atDear);
        if (fewer.IsSuccess && more.IsSuccess)
        {
            fewer.Value.Value.Should().BeLessThanOrEqualTo(more.Value.Value);
        }

        if (more.IsSuccess && pricier.IsSuccess)
        {
            pricier.Value.Value.Should().BeLessThanOrEqualTo(more.Value.Value);
        }
    }

    [Property(Arbitrary = [typeof(MoneyKitArbitraries)])]
    public void NothingEverThrows_WhateverTheDecimal(decimal amount, decimal factor, decimal rate, decimal price)
    {
        Action everything = () =>
        {
            Result<Money> money = Money.Create(amount, _usd);
            _ = money.IsSuccess ? money.Value.Times(factor) : money;
            _ = money.IsSuccess ? money.Value.DividedBy(factor) : money;
            _ = money.IsSuccess ? money.Value.Add(money.Value) : money;
            _ = money.IsSuccess ? money.Value.ToDisplay() : null;
            Result<ExchangeRate> exchange = ExchangeRate.Create(_usd, _gtq, rate);
            _ = exchange.IsSuccess && money.IsSuccess ? exchange.Value.Convert(money.Value) : money;
            _ = exchange.IsSuccess ? exchange.Value.Invert() : exchange;
            _ = Shares.Create(factor);
            _ = Shares.FromRequest(factor);
            _ = RelativeChange.Between(price, amount);
            if (money.IsSuccess && Money.Create(price, _usd) is { IsSuccess: true } unit)
            {
                _ = Shares.Buying(money.Value, unit.Value);
            }
        };

        everything.Should().NotThrow();
    }

    [Theory]
    [InlineData("79228162514264337593543950335")]
    [InlineData("-79228162514264337593543950335")]
    [InlineData("0.0000000000000000000000000001")]
    [InlineData("1000000000000000")]
    [InlineData("-1000000000000000")]
    [InlineData("0")]
    public void TheExtremesOfADecimal_NeverThrow(string text)
    {
        decimal extreme = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

        Action everything = () =>
        {
            Result<Money> money = Money.Create(extreme, _usd);
            _ = money.IsSuccess ? money.Value.Times(extreme) : money;
            _ = money.IsSuccess ? money.Value.DividedBy(extreme) : money;
            _ = Money.Create(1m, _usd).Value.Times(extreme);
            _ = Money.Create(1m, _usd).Value.DividedBy(extreme);
            _ = ExchangeRate.Create(_usd, _gtq, extreme);
            _ = Shares.Create(extreme);
            _ = RelativeChange.Between(extreme, extreme);
            _ = RelativeChange.Between(1m, extreme);
            _ = RelativeChange.Between(extreme, 1m);
            _ = Shares.Buying(Money.Create(1_000_000_000_000_000m, _usd).Value, Money.Create(0.0000000000000000000000000001m, _usd).Value);
            _ = Money.Create(1_000_000_000_000_000m, _usd).Value.Times(extreme);
            _ = Money.Create(1_000_000_000_000_000m, _usd).Value.DividedBy(extreme);
            _ = Money.Create(-1_000_000_000_000_000m, _usd).Value.Times(extreme);
        };

        everything.Should().NotThrow();
    }

    [Property(Arbitrary = [typeof(MoneyKitArbitraries)])]
    public void ARelativeChange_OfAPriceAgainstItself_IsZero_OfDoubleIsOne_AndGrowsWithThePrice(PriceOf baseline, PriceOf first, PriceOf second)
    {
        RelativeChange.Between(baseline.Value, baseline.Value).Value.Fraction.Should().Be(0m);
        RelativeChange.Between(baseline.Value, baseline.Value * 2m).Value.Fraction.Should().Be(1m);
        (decimal low, decimal high) = first.Value <= second.Value ? (first.Value, second.Value) : (second.Value, first.Value);

        RelativeChange.Between(baseline.Value, low).Value.Fraction.Should().BeLessThanOrEqualTo(RelativeChange.Between(baseline.Value, high).Value.Fraction);
    }
}
