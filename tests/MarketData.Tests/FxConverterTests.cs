using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Application.Fx;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Domain.Fx;
using SakerWatch.MarketData.Domain.Markets;

namespace SakerWatch.MarketData.Tests;

public sealed class FxConverterTests
{
    private static readonly CurrencyCode _usd = CurrencyCode.Parse("USD");
    private static readonly CurrencyCode _gtq = CurrencyCode.Parse("GTQ");
    private static readonly CurrencyCode _cad = CurrencyCode.Parse("CAD");
    private static readonly CurrencyCode _jpy = CurrencyCode.Parse("JPY");

    // 2026-10-17 12:00 UTC is Saturday the 17th in Guatemala.
    private static readonly DateTimeOffset _now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private sealed class Rates : IFxRateStore
    {
        private readonly List<FxRate> _all = [];

        public Rates With(string from, string to, int day, decimal rate, string source = "banguat")
        {
            _all.Add(FxRate.Create(CurrencyCode.Parse(from), CurrencyCode.Parse(to), new DateOnly(2026, 10, day), rate, source).Value);
            return this;
        }

        public Task<int> UpsertAsync(IReadOnlyList<FxRate> rates, DateTimeOffset ingestedAt, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FxRate?> OnOrBeforeAsync(CurrencyCode baseCurrency, CurrencyCode quote, DateOnly date, CancellationToken cancellationToken) =>
            Task.FromResult(_all.Where(r => r.Base == baseCurrency && r.Quote == quote && r.AsOfDate <= date).OrderByDescending(r => r.AsOfDate).FirstOrDefault());

        public Task<DateOnly?> LatestDateAsync(CurrencyCode baseCurrency, CurrencyCode quote, string source, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static FxConverter Converter(Rates rates, int maxAgeDays = 3) =>
        new(rates, Options.Create(new IngestionOptions { FxMaxAgeDays = maxAgeDays }), new FakeTimeProvider(_now));

    private static Rates Bank() => new Rates().With("USD", "GTQ", 16, 7.64572m).With("USD", "GTQ", 17, 7.64572m);

    private static Task<Result<FxConversion>> ConvertAsync(FxConverter converter, decimal amount, CurrencyCode from, CurrencyCode to, DateOnly? date = null) =>
        converter.ConvertAsync(amount, from, to, date, CancellationToken.None);

    [Fact]
    public async Task Convert_UsdToGtq_AppliesTheStoredRateExactly()
    {
        Result<FxConversion> result = await ConvertAsync(Converter(Bank()), 100m, _usd, _gtq);

        FxConversion conversion = result.Value;
        conversion.Converted.Should().Be(764.572m, "100 x 7.64572, hand-computed, with no rounding");
        conversion.Rate.Should().Be(7.64572m);
        conversion.Legs.Should().ContainSingle().Which.Should().Be(new FxLeg(_usd, _gtq, 7.64572m, new DateOnly(2026, 10, 17), "banguat", Inverted: false));
        (conversion.AsOfDate, conversion.IsStale).Should().Be((new DateOnly(2026, 10, 17), false));
    }

    [Fact]
    public async Task Convert_GtqToUsd_UsesTheReciprocalOfTheStoredRate_AndSaysSo()
    {
        FxConversion conversion = (await ConvertAsync(Converter(Bank()), 764.572m, _gtq, _usd)).Value;

        conversion.Converted.Should().BeApproximately(100m, 0.0000000001m);
        FxLeg leg = conversion.Legs.Should().ContainSingle().Subject;
        (leg.Inverted, leg.From, leg.To, leg.Source).Should().Be((true, _gtq, _usd, "banguat"));
        leg.Rate.Should().BeApproximately(1m / 7.64572m, 0.000000000000000001m);
    }

    [Fact]
    public async Task Convert_AnotherCurrencyToGtq_GoesThroughUsd_WithBothLegsReturned()
    {
        // USD to CAD is quoted 1.4283: 100 CAD = 70.0133 USD = 535.3021 GTQ.
        Rates rates = Bank().With("USD", "CAD", 16, 1.4283m, "other");

        FxConversion conversion = (await ConvertAsync(Converter(rates), 100m, _cad, _gtq)).Value;

        conversion.Converted.Should().BeApproximately(535.3021074m, 0.0000001m);
        conversion.Legs.Select(l => (l.From.Value, l.To.Value, l.Inverted, l.Source)).Should().Equal(("CAD", "USD", true, "other"), ("USD", "GTQ", false, "banguat"));
        conversion.Rate.Should().BeApproximately(conversion.Converted / 100m, 0.000000000000001m);
        conversion.AsOfDate.Should().Be(new DateOnly(2026, 10, 16), "the oldest day among the legs");
    }

    [Fact]
    public async Task Convert_BetweenTwoNonUsdCurrencies_NeedsBothRatesAgainstUsd()
    {
        Rates rates = Bank().With("USD", "CAD", 17, 1.4283m).With("USD", "JPY", 17, 150m);

        FxConversion conversion = (await ConvertAsync(Converter(rates), 1000m, _cad, _jpy)).Value;

        conversion.Converted.Should().BeApproximately(1000m / 1.4283m * 150m, 0.0000001m);
        conversion.Legs.Should().HaveCount(2);
    }

    [Fact]
    public async Task Convert_TheSameCurrency_IsTheSameAmount_WithNoLegs()
    {
        FxConversion conversion = (await ConvertAsync(Converter(new Rates()), 42.5m, _usd, _usd)).Value;

        (conversion.Converted, conversion.Rate, conversion.IsStale).Should().Be((42.5m, 1m, false));
        conversion.Legs.Should().BeEmpty();
    }

    [Fact]
    public async Task Convert_OnADayWithoutARate_UsesTheEarlierOneAndReturnsItsOwnDate()
    {
        Rates rates = new Rates().With("USD", "GTQ", 9, 7.6m).With("USD", "GTQ", 14, 7.7m);

        FxConversion conversion = (await ConvertAsync(Converter(rates), 10m, _usd, _gtq, new DateOnly(2026, 10, 12))).Value;

        conversion.Legs.Single().AsOfDate.Should().Be(new DateOnly(2026, 10, 9), "never a day the rate was not published for");
        conversion.Converted.Should().Be(76m);
    }

    [Fact]
    public async Task Convert_WithNoDate_UsesTodayInGuatemala()
    {
        // At 04:00 UTC on the 17th it is still the 16th in Guatemala: the rate of the 17th must not be used.
        Rates rates = new Rates().With("USD", "GTQ", 16, 7.6m).With("USD", "GTQ", 17, 7.7m);
        var converter = new FxConverter(rates, Options.Create(new IngestionOptions()), new FakeTimeProvider(new DateTimeOffset(2026, 10, 17, 4, 0, 0, TimeSpan.Zero)));

        FxConversion conversion = (await ConvertAsync(converter, 10m, _usd, _gtq)).Value;

        conversion.Legs.Single().AsOfDate.Should().Be(new DateOnly(2026, 10, 16));
    }

    [Theory]
    [InlineData(17, 3, false)]
    [InlineData(14, 3, false)]
    [InlineData(13, 3, true)]
    [InlineData(13, 5, false)]
    public async Task Convert_FlagsARateOlderThanTheMaximumAge(int rateDay, int maxAge, bool stale)
    {
        Rates rates = new Rates().With("USD", "GTQ", rateDay, 7.6m);

        FxConversion conversion = (await ConvertAsync(Converter(rates, maxAge), 10m, _usd, _gtq)).Value;

        conversion.IsStale.Should().Be(stale);
    }

    [Fact]
    public async Task Convert_WhenOneLegIsOldAndTheOtherIsNot_IsStale()
    {
        Rates rates = new Rates().With("USD", "GTQ", 17, 7.6m).With("USD", "CAD", 1, 1.4m);

        (await ConvertAsync(Converter(rates), 10m, _cad, _gtq)).Value.IsStale.Should().BeTrue();
    }

    [Theory]
    [InlineData("USD", "GTQ", 17)]
    [InlineData("CAD", "GTQ", 17)]
    [InlineData("GTQ", "CAD", 17)]
    public async Task Convert_WithoutAStoredRate_SaysWhichStepIsMissing(string from, string to, int rateDay)
    {
        // Only a rate dated after the asked day exists for USD/GTQ, and nothing for CAD.
        Rates rates = new Rates().With("USD", "GTQ", rateDay, 7.6m);

        Result<FxConversion> result = await ConvertAsync(Converter(rates), 10m, CurrencyCode.Parse(from), CurrencyCode.Parse(to), new DateOnly(2026, 10, 10));

        result.Error!.Code.Should().Be(FxConverter.RateUnavailableCode);
    }

    [Fact]
    public async Task Convert_ANegativeAmount_IsRefused()
    {
        Result<FxConversion> result = await ConvertAsync(Converter(Bank()), -1m, _usd, _gtq);

        result.Error!.Code.Should().Be(FxConverter.AmountInvalidCode);
    }

    [Fact]
    public async Task Convert_Zero_IsZero() =>
        (await ConvertAsync(Converter(Bank()), 0m, _usd, _gtq)).Value.Converted.Should().Be(0m);

    [Fact]
    public async Task Convert_AnAmountThatOverflows_IsRefusedInsteadOfThrowing()
    {
        Result<FxConversion> result = await ConvertAsync(Converter(Bank()), decimal.MaxValue, _usd, _gtq);

        result.Error!.Code.Should().Be(FxConverter.AmountInvalidCode);
    }

    [Theory]
    [InlineData("0.01")]
    [InlineData("1")]
    [InlineData("123456.78")]
    [InlineData("99999999999.99")]
    public async Task Convert_ThereAndBack_ReturnsTheAmountWithinTheRoundingOfTheDecimalType(string text)
    {
        decimal amount = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        FxConverter converter = Converter(Bank().With("USD", "CAD", 17, 1.4283m));

        decimal there = (await ConvertAsync(converter, amount, _cad, _gtq)).Value.Converted;
        decimal back = (await ConvertAsync(converter, there, _gtq, _cad)).Value.Converted;

        back.Should().BeApproximately(amount, amount * 0.000000000000001m);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(10, 11)]
    public async Task Convert_ALargerAmountNeverGivesLess(int small, int large)
    {
        FxConverter converter = Converter(Bank());

        decimal a = (await ConvertAsync(converter, small, _usd, _gtq)).Value.Converted;
        decimal b = (await ConvertAsync(converter, large, _usd, _gtq)).Value.Converted;

        b.Should().BeGreaterThan(a);
    }

    [Fact]
    public async Task Convert_TheCombinedRateTimesTheAmount_IsTheConvertedAmount()
    {
        FxConversion conversion = (await ConvertAsync(Converter(Bank().With("USD", "CAD", 17, 1.4283m)), 250m, _cad, _gtq)).Value;

        (conversion.Rate * 250m).Should().BeApproximately(conversion.Converted, 0.0000000000001m);
    }

    [Fact]
    public void EveryResult_CarriesTheReferenceOnlyNotice() =>
        FxConversion.DisclaimerKey.Should().Be("marketdata.fx.reference_only");
}
