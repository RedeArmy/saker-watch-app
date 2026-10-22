using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.MarketData.Application.Fx;
using SakerWatch.MarketData.Domain.Fx;

namespace SakerWatch.MarketData.Tests;

public sealed class FxRateGuardTests
{
    private static readonly CurrencyCode _usd = CurrencyCode.Parse("USD");
    private static readonly CurrencyCode _gtq = CurrencyCode.Parse("GTQ");
    private static readonly CurrencyCode _eur = CurrencyCode.Parse("EUR");

    private static readonly FxGuardSettings _settings = new(new Dictionary<string, FxBand> { ["USDGTQ"] = new(5m, 12m) }, 0.03m);

    private static FxRate Rate(int day, decimal rate, CurrencyCode? baseCurrency = null, CurrencyCode? quote = null) =>
        FxRate.Create(baseCurrency ?? _usd, quote ?? _gtq, new DateOnly(2026, 10, day), rate, "banguat").Value;

    private static string[] Days(IEnumerable<FxRate> rates) => [.. rates.Select(rate => $"{rate.AsOfDate.Day}:{rate.Rate}")];

    [Theory]
    [InlineData(77.5)]
    [InlineData(0.775)]
    [InlineData(4.99)]
    [InlineData(12.01)]
    public void Screen_ARateOutsideTheBand_IsRefused(double rate)
    {
        FxScreening screening = FxRateGuard.Screen([Rate(16, (decimal)rate)], [], _settings);

        screening.Accepted.Should().BeEmpty();
        screening.Rejected.Should().ContainSingle().Which.Reason.Should().Be(FxRejectionReasons.OutOfBand);
    }

    [Theory]
    [InlineData(5.0)]
    [InlineData(12.0)]
    public void Screen_TheEdgesOfTheBand_AreInside(double rate) =>
        FxRateGuard.Screen([Rate(16, (decimal)rate)], [], _settings).Accepted.Should().ContainSingle();

    [Fact]
    public void Screen_APairWithoutABand_OnlyHasTheDailyLimit()
    {
        FxScreening screening = FxRateGuard.Screen([Rate(15, 0.92m, _eur, _usd), Rate(16, 920m, _eur, _usd), Rate(17, 0.93m, _eur, _usd)], [], _settings);

        Days(screening.Accepted).Should().Equal("15:0.92", "17:0.93");
        screening.Rejected.Should().ContainSingle().Which.Reason.Should().Be(FxRejectionReasons.Jump);
    }

    [Fact]
    public void Screen_AJumpAgainstTheLastStoredRate_IsRefused_AndASmallMoveIsNot()
    {
        FxRate[] stored = [Rate(14, 7.6m)];

        FxScreening small = FxRateGuard.Screen([Rate(15, 7.6m * 1.029m)], stored, _settings);
        FxScreening big = FxRateGuard.Screen([Rate(15, 7.6m * 1.031m)], stored, _settings);

        small.Accepted.Should().ContainSingle();
        big.Rejected.Should().ContainSingle().Which.Reason.Should().Be(FxRejectionReasons.Jump);
    }

    [Fact]
    public void Screen_ComparesEachRateWithTheOneBeforeIt_SoAGradualDriftPasses()
    {
        // 1 % a day for six days is 6 %: no day moves more than the limit, and the guard does not compare with the first one.
        FxRate[] drift = [.. Enumerable.Range(0, 6).Select(i => Rate(10 + i, Math.Round(7.0m * (decimal)Math.Pow(1.01, i), 4)))];

        FxRateGuard.Screen(drift, [], _settings).Rejected.Should().BeEmpty();
    }

    [Fact]
    public void Screen_AfterARefusedRate_TheNextOnesAreComparedWithTheLastBelievedOne()
    {
        // The 16th is a misplaced decimal inside the band's neighbourhood; the 17th is back to normal and passes, the 18th is also high and fails.
        FxScreening screening = FxRateGuard.Screen([Rate(15, 7.6m), Rate(16, 9.9m), Rate(17, 7.61m), Rate(18, 9.9m)], [], _settings);

        Days(screening.Accepted).Should().Equal("15:7.6", "17:7.61");
        screening.Rejected.Select(r => r.Rate.AsOfDate.Day).Should().Equal(16, 18);
    }

    [Fact]
    public void Screen_ADayRewrittenWithAVeryDifferentRate_IsRefused_AndASmallCorrectionIsAllowed()
    {
        FxRate[] stored = [Rate(16, 7.6m)];

        FxRateGuard.Screen([Rate(16, 7.62m)], stored, _settings).Accepted.Should().ContainSingle();
        FxScreening rewritten = FxRateGuard.Screen([Rate(16, 8.2m)], stored, _settings);

        rewritten.Rejected.Should().ContainSingle().Which.Reason.Should().Be(FxRejectionReasons.Rewrite);
    }

    [Fact]
    public void Screen_TheFirstRateEver_HasNothingToBeComparedWith_ButStillHasItsBand()
    {
        FxRateGuard.Screen([Rate(1, 7.7m)], [], _settings).Accepted.Should().ContainSingle();
        FxRateGuard.Screen([Rate(1, 70m)], [], _settings).Rejected.Should().ContainSingle();
    }

    [Fact]
    public void Screen_WeekendsFilledWithTheRateOfTheFriday_Pass() =>
        FxRateGuard.Screen([Rate(16, 7.64m), Rate(17, 7.64m), Rate(18, 7.64m)], [], _settings).Rejected.Should().BeEmpty();

    [Fact]
    public void Screen_ARateBetweenTwoStoredDays_IsComparedWithTheEarlierOne()
    {
        FxRate[] stored = [Rate(14, 7.6m), Rate(18, 7.7m)];

        FxScreening screening = FxRateGuard.Screen([Rate(16, 8.4m)], stored, _settings);

        screening.Rejected.Should().ContainSingle().Which.Reason.Should().Be(FxRejectionReasons.Jump);
    }

    [Fact]
    public void Screen_ReturnsTheAcceptedRatesInDateOrder() =>
        Days(FxRateGuard.Screen([Rate(17, 7.62m), Rate(15, 7.6m), Rate(16, 7.61m)], [], _settings).Accepted).Should().Equal("15:7.6", "16:7.61", "17:7.62");

    [Fact]
    public void DefaultOptions_HoldTheQuetzalToFiveToTwelveAndThreePercentADay()
    {
        FxGuardSettings settings = new FxGuardOptions().ToSettings();

        (settings.MaxDailyChange, settings.Bands["USDGTQ"]).Should().Be((0.03m, new FxBand(5m, 12m)));
    }

    [Theory]
    [InlineData("USDGTQ", 5, 12, true)]
    [InlineData("USD-GTQ", 5, 12, false)]
    [InlineData("usdgtq", 5, 12, false)]
    [InlineData("USDGTQ", 0, 12, false)]
    [InlineData("USDGTQ", 12, 5, false)]
    [InlineData("USDGTQ", 5, 5, false)]
    public void Options_ABandIsTwoCodesAndAPositiveRange(string pair, int min, int max, bool valid)
    {
        var options = new FxGuardOptions { Bands = new() { [pair] = new FxBandOptions { Min = min, Max = max } } };

        new FxGuardOptionsValidator().Validate(null, options).Succeeded.Should().Be(valid);
    }

    [Fact]
    public void Options_SettingOnlyTheMaxOfTheDefaultBandFromConfiguration_KeepsTheDefaultMin()
    {
        // What the runbook tells the operator to do: MARKETDATA__FX__GUARD__BANDS__USDGTQ__MAX=15 and nothing else.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MarketData:Fx:Guard:Bands:USDGTQ:Max"] = "15" })
            .Build();
        var options = new FxGuardOptions();

        configuration.GetSection(FxGuardOptions.SectionName).Bind(options);

        options.Bands["USDGTQ"].Should().BeEquivalentTo(new FxBandOptions { Min = 5m, Max = 15m });
        new FxGuardOptionsValidator().Validate(null, options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Options_ABandForAnotherPairFromConfiguration_IsAddedToTheDefault()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MarketData:Fx:Guard:Bands:EURUSD:Min"] = "0.5", ["MarketData:Fx:Guard:Bands:EURUSD:Max"] = "2" })
            .Build();
        var options = new FxGuardOptions();

        configuration.GetSection(FxGuardOptions.SectionName).Bind(options);

        options.ToSettings().Bands.Keys.Should().BeEquivalentTo("USDGTQ", "EURUSD");
    }
}

