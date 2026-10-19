using System.Globalization;
using AwesomeAssertions;
using SakerWatch.BuildingBlocks.Kernel.Monetary;

namespace SakerWatch.Application.Tests.Kernel;

/// <summary>Hand-computed cases of the money kit (ADR-0006, ADR-0037): what each type allows, refuses and rounds.</summary>
public sealed class MoneyKitTests
{
    private static readonly CurrencyCode _usd = CurrencyCode.Parse("USD");
    private static readonly CurrencyCode _gtq = CurrencyCode.Parse("GTQ");
    private static readonly CurrencyCode _jpy = CurrencyCode.Parse("JPY");

    private static decimal D(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);

    private static Money M(string amount, CurrencyCode currency) => Money.Create(D(amount), currency).Value;

    [Theory]
    [InlineData("usd", "USD")]
    [InlineData(" gtq ", "GTQ")]
    public void ACurrencyCode_IsTrimmedAndUpperCased(string input, string expected) =>
        CurrencyCode.Create(input).Value.Value.Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("US")]
    [InlineData("USDD")]
    [InlineData("U5D")]
    [InlineData(null)]
    public void ABadCurrencyCode_IsRefused(string? input) =>
        CurrencyCode.Create(input).Error!.Code.Should().Be(MoneyErrors.CurrencyInvalid);

    [Theory]
    [InlineData("JPY", 0)]
    [InlineData("KRW", 0)]
    [InlineData("USD", 2)]
    [InlineData("GTQ", 2)]
    [InlineData("KWD", 3)]
    public void MinorUnits_FollowTheCurrency(string code, int decimals) =>
        CurrencyCode.Parse(code).MinorUnits.Should().Be(decimals);

    [Fact]
    public void Money_KeepsEveryDigit_AndAddsAndSubtractsInTheSameCurrency()
    {
        Money sum = M("0.1", _usd).Add(M("0.2", _usd)).Value;
        Money back = sum.Subtract(M("0.2", _usd)).Value;

        sum.Amount.Should().Be(0.3m, "decimal, unlike double, adds tenths exactly");
        back.Should().Be(M("0.1", _usd));
    }

    [Fact]
    public void Money_OfTwoCurrencies_CannotBeAddedOrSubtracted()
    {
        M("1", _usd).Add(M("1", _gtq)).Error!.Code.Should().Be(MoneyErrors.CurrencyMismatch);
        M("1", _usd).Subtract(M("1", _jpy)).Error!.Code.Should().Be(MoneyErrors.CurrencyMismatch);
    }

    [Fact]
    public void Money_BeyondTheLimit_IsRefusedInEveryWay_AndNothingThrows()
    {
        Money.Create(Money.MaxMagnitude + 0.01m, _usd).Error!.Code.Should().Be(MoneyErrors.AmountOutOfRange);
        Money.Create(decimal.MaxValue, _usd).Error!.Code.Should().Be(MoneyErrors.AmountOutOfRange);
        Money.Create(decimal.MinValue, _usd).Error!.Code.Should().Be(MoneyErrors.AmountOutOfRange);
        M("900000000000000", _usd).Add(M("900000000000000", _usd)).Error!.Code.Should().Be(MoneyErrors.AmountOutOfRange);
        M("900000000000000", _usd).Times(2m).Error!.Code.Should().Be(MoneyErrors.AmountOutOfRange);
        M("1", _usd).Times(decimal.MaxValue).Error!.Code.Should().Be(MoneyErrors.AmountOutOfRange);
        M("1", _usd).Times(decimal.MinValue).Error!.Code.Should().Be(MoneyErrors.AmountOutOfRange);
        M("1", _usd).DividedBy(0.0000000000000000000000000001m).Error!.Code.Should().Be(MoneyErrors.AmountOutOfRange);
    }

    [Fact]
    public void ADivisionByZero_IsAnOutcome() =>
        M("1", _usd).DividedBy(0m).Error!.Code.Should().Be(MoneyErrors.DivisionByZero);

    [Theory]
    [InlineData("0.125", "0.12")]
    [InlineData("0.135", "0.14")]
    [InlineData("0.145", "0.14")]
    [InlineData("2.675", "2.68")]
    [InlineData("-0.125", "-0.12")]
    [InlineData("-2.675", "-2.68")]
    public void ToDisplay_RoundsHalfToEven_ToTheMinorUnits(string exact, string expected)
    {
        DisplayMoney shown = M(exact, _usd).ToDisplay();

        shown.Amount.Should().Be(D(expected));
        shown.Currency.Should().Be(_usd);
    }

    [Fact]
    public void ToDisplay_UsesTheMinorUnitsOfTheCurrency()
    {
        M("4481.25", _jpy).ToDisplay().Amount.Should().Be(4481m);
        M("4481.5", _jpy).ToDisplay().Amount.Should().Be(4482m, "half to even: 4481 is odd, so .5 goes up");
        M("4482.5", _jpy).ToDisplay().Amount.Should().Be(4482m);
        M("1.2345", CurrencyCode.Parse("KWD")).ToDisplay().Amount.Should().Be(1.234m);
    }

    [Fact]
    public void ToDisplay_DoesNotChangeTheExactAmount()
    {
        Money exact = M("1.23456789", _usd);

        _ = exact.ToDisplay();

        exact.Amount.Should().Be(1.23456789m);
    }

    [Fact]
    public void AnExchangeRate_ConvertsItsOwnCurrencyExactly_AndRefusesAnyOther()
    {
        ExchangeRate rate = ExchangeRate.Create(_usd, _gtq, 7.7821m).Value;

        Money converted = rate.Convert(M("50", _usd)).Value;

        converted.Should().Be(M("389.105", _gtq));
        rate.Convert(M("50", _gtq)).Error!.Code.Should().Be(MoneyErrors.CurrencyMismatch, "a USD to GTQ rate is never applied to quetzales");
    }

    [Fact]
    public void AnExchangeRate_InvertsAndChainsWithoutRounding()
    {
        ExchangeRate usdGtq = ExchangeRate.Create(_usd, _gtq, 8m).Value;
        ExchangeRate jpyUsd = ExchangeRate.Create(_jpy, _usd, 0.0064m).Value;

        ExchangeRate inverse = usdGtq.Invert().Value;
        ExchangeRate chained = jpyUsd.Then(usdGtq).Value;

        (inverse.From, inverse.To, inverse.Rate).Should().Be((_gtq, _usd, 0.125m));
        (chained.From, chained.To, chained.Rate).Should().Be((_jpy, _gtq, 0.0512m));
        usdGtq.Then(jpyUsd).Error!.Code.Should().Be(MoneyErrors.CurrencyMismatch, "GTQ does not meet JPY");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.0000000000001")]
    [InlineData("1000000000001")]
    public void AnExchangeRateOutsideTheRange_IsRefused(string rate) =>
        ExchangeRate.Create(_usd, _gtq, D(rate)).Error!.Code.Should().Be(MoneyErrors.RateInvalid);

    [Fact]
    public void AnExchangeRate_BetweenACurrencyAndItself_IsOnlyTheIdentity()
    {
        ExchangeRate.Create(_usd, _usd, 1m).Error!.Code.Should().Be(MoneyErrors.RateInvalid);

        ExchangeRate identity = ExchangeRate.Identity(_usd);

        identity.Convert(M("12.34", _usd)).Value.Should().Be(M("12.34", _usd));
        identity.Invert().Value.Should().Be(identity);
    }

    [Fact]
    public void Shares_AreBoughtWithAnAmountOfTheSameCurrency_AndCostWhatTheyCost()
    {
        Shares bought = Shares.Buying(M("50", _usd), M("600", _usd)).Value;

        bought.Value.Should().Be(50m / 600m);
        bought.ToDisplay().Value.Should().Be(0.083333m);
        bought.Cost(M("600", _usd)).Value.Amount.Should().BeApproximately(50m, 0.000000000000000000000001m);
        Shares.Buying(M("50", _gtq), M("600", _usd)).Error!.Code.Should().Be(MoneyErrors.CurrencyMismatch);
        Shares.Buying(M("50", _usd), M("0", _usd)).Error!.Code.Should().Be(MoneyErrors.PriceInvalid);
        Shares.Buying(M("-50", _usd), M("600", _usd)).Error!.Code.Should().Be(MoneyErrors.SharesInvalid);
    }

    [Theory]
    [InlineData("0.0833333", "0.083333")]
    [InlineData("0.0833339", "0.083333")]
    [InlineData("0.9999999", "0.999999")]
    [InlineData("12.5", "12.5")]
    public void Shares_AreShownCutTowardsZero_NeverRoundedUp(string exact, string expected) =>
        Shares.Create(D(exact)).Value.ToDisplay().Value.Should().Be(D(expected));

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1000000000000.000001")]
    public void ShareQuantitiesOutsideTheBounds_AreRefused(string quantity) =>
        Shares.Create(D(quantity)).Error!.Code.Should().Be(MoneyErrors.SharesInvalid);

    [Fact]
    public void ASharesRequest_WithMoreThanSixDecimals_IsRefusedInsteadOfBeingCut()
    {
        Shares.FromRequest(1.234567m).IsSuccess.Should().BeTrue();
        Shares.FromRequest(1.2345678m).Error!.Code.Should().Be(MoneyErrors.SharesInvalid);
    }

    [Fact]
    public void Shares_CostAtANonPositivePrice_IsRefused() =>
        Shares.Create(2m).Value.Cost(M("-1", _usd)).Error!.Code.Should().Be(MoneyErrors.PriceInvalid);

    [Theory]
    [InlineData("100", "110", "0.1")]
    [InlineData("100", "90", "-0.1")]
    [InlineData("100", "100", "0")]
    [InlineData("50", "100", "1")]
    [InlineData("3", "1", "-0.6666666666666666666666666667")]
    public void ARelativeChange_IsLatestOverBaselineMinusOne_Exactly(string baseline, string latest, string expected) =>
        RelativeChange.Between(D(baseline), D(latest)).Value.Fraction.Should().Be(D(expected));

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void ARelativeChange_NeedsAPositiveBaseline(string baseline) =>
        RelativeChange.Between(D(baseline), 10m).Error!.Code.Should().Be(MoneyErrors.BaselineInvalid);

    [Fact]
    public void ARelativeChange_RefusesANegativeLatestPrice_AndAnImpossibleDistance()
    {
        RelativeChange.Between(10m, -1m).Error!.Code.Should().Be(MoneyErrors.PriceInvalid);
        RelativeChange.Between(0.0000000000000000000000000001m, decimal.MaxValue).Error!.Code.Should().Be(MoneyErrors.AmountOutOfRange);
    }

    [Fact]
    public void TheTypes_CompareByValue()
    {
        M("1.0", _usd).Should().Be(M("1.00", _usd));
        M("1", _usd).Should().NotBe(M("1", _gtq));
        ExchangeRate.Create(_usd, _gtq, 7.5m).Value.Should().Be(ExchangeRate.Create(_usd, _gtq, 7.50m).Value);
        Shares.Create(1.5m).Value.Should().Be(Shares.Create(1.50m).Value);
    }
}
