using AwesomeAssertions;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Markets;

namespace SakerWatch.MarketData.Tests;

/// <summary>The US equity calendar against dates checked by hand from the NYSE holiday schedule.</summary>
public sealed class UsEquityCalendarTests
{
    private readonly UsEquityCalendar _calendar = new(Mic.Parse("XNYS"));

    [Theory]
    // 2024
    [InlineData(2024, 1, 1)]
    [InlineData(2024, 1, 15)]
    [InlineData(2024, 2, 19)]
    [InlineData(2024, 3, 29)]
    [InlineData(2024, 5, 27)]
    [InlineData(2024, 6, 19)]
    [InlineData(2024, 7, 4)]
    [InlineData(2024, 9, 2)]
    [InlineData(2024, 11, 28)]
    [InlineData(2024, 12, 25)]
    // 2025
    [InlineData(2025, 1, 1)]
    [InlineData(2025, 1, 20)]
    [InlineData(2025, 2, 17)]
    [InlineData(2025, 4, 18)]
    [InlineData(2025, 5, 26)]
    [InlineData(2025, 6, 19)]
    [InlineData(2025, 7, 4)]
    [InlineData(2025, 9, 1)]
    [InlineData(2025, 11, 27)]
    [InlineData(2025, 12, 25)]
    // 2026, with July 4 on a Saturday observed on Friday July 3
    [InlineData(2026, 1, 1)]
    [InlineData(2026, 1, 19)]
    [InlineData(2026, 2, 16)]
    [InlineData(2026, 4, 3)]
    [InlineData(2026, 5, 25)]
    [InlineData(2026, 6, 19)]
    [InlineData(2026, 7, 3)]
    [InlineData(2026, 9, 7)]
    [InlineData(2026, 11, 26)]
    [InlineData(2026, 12, 25)]
    public void Holiday_IsNotATradingDay(int year, int month, int day) =>
        _calendar.IsTradingDay(new DateOnly(year, month, day)).Should().BeFalse();

    [Theory]
    [InlineData(2021, 12, 24)] // Christmas on a Saturday is observed on Friday
    [InlineData(2023, 1, 2)] // New Year's Day on a Sunday is observed on Monday
    [InlineData(2022, 6, 20)] // Juneteenth on a Sunday is observed on Monday, from 2022
    [InlineData(2022, 12, 26)] // Christmas on a Sunday is observed on Monday
    [InlineData(2022, 1, 17)] // Martin Luther King Jr. Day
    public void ObservedHoliday_IsNotATradingDay(int year, int month, int day) =>
        _calendar.IsTradingDay(new DateOnly(year, month, day)).Should().BeFalse();

    [Theory]
    [InlineData(2021, 12, 31)] // New Year's Day 2022 is a Saturday and is not observed on the Friday before
    [InlineData(2021, 6, 18)] // Juneteenth was not a market holiday before 2022
    [InlineData(2022, 7, 1)] // July 4 is a Monday, so the Friday before is a normal day
    [InlineData(2024, 7, 3)]
    [InlineData(2024, 11, 29)]
    [InlineData(2024, 12, 24)]
    [InlineData(2025, 12, 24)]
    [InlineData(2026, 11, 27)]
    [InlineData(2024, 3, 8)]
    public void OrdinaryOrEarlyCloseDay_IsATradingDay(int year, int month, int day) =>
        _calendar.IsTradingDay(new DateOnly(year, month, day)).Should().BeTrue();

    [Theory]
    [InlineData(2025, 1, 9)] // national day of mourning
    [InlineData(2012, 10, 29)] // Hurricane Sandy
    [InlineData(2012, 10, 30)]
    [InlineData(2001, 9, 11)]
    [InlineData(2001, 9, 14)]
    [InlineData(2018, 12, 5)]
    [InlineData(2007, 1, 2)]
    [InlineData(2004, 6, 11)]
    public void ExtraordinaryClosure_IsNotATradingDay(int year, int month, int day) =>
        _calendar.IsTradingDay(new DateOnly(year, month, day)).Should().BeFalse();

    [Theory]
    [InlineData(2024, 3, 9)]
    [InlineData(2024, 3, 10)]
    public void Weekend_IsNotATradingDay(int year, int month, int day) =>
        _calendar.IsTradingDay(new DateOnly(year, month, day)).Should().BeFalse();

    [Theory]
    [InlineData(2024, 3, 8, 21)] // standard time, UTC-5
    [InlineData(2024, 3, 11, 20)] // daylight time starts on March 10, UTC-4
    [InlineData(2024, 11, 1, 20)] // still daylight time
    [InlineData(2024, 11, 4, 21)] // standard time returns on November 3
    public void CloseOf_RegularDay_IsFourInTheAfternoonLocalTime(int year, int month, int day, int utcHour) =>
        _calendar.CloseOf(new DateOnly(year, month, day)).Should().Be(new DateTimeOffset(year, month, day, utcHour, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData(2024, 7, 3, 17)] // 13:00 EDT
    [InlineData(2024, 11, 29, 18)] // 13:00 EST
    [InlineData(2024, 12, 24, 18)]
    [InlineData(2025, 7, 3, 17)]
    [InlineData(2025, 11, 28, 18)]
    [InlineData(2025, 12, 24, 18)]
    [InlineData(2026, 11, 27, 18)]
    [InlineData(2026, 12, 24, 18)]
    public void CloseOf_EarlyCloseDay_IsOneInTheAfternoonLocalTime(int year, int month, int day, int utcHour) =>
        _calendar.CloseOf(new DateOnly(year, month, day)).Should().Be(new DateTimeOffset(year, month, day, utcHour, 0, 0, TimeSpan.Zero));

    [Fact]
    public void CloseOf_ANonTradingDay_IsNull()
    {
        _calendar.CloseOf(new DateOnly(2024, 12, 25)).Should().BeNull();
        _calendar.CloseOf(new DateOnly(2024, 3, 9)).Should().BeNull();
    }

    [Fact]
    public void NextTradingDay_SkipsWeekendsAndHolidays()
    {
        _calendar.NextTradingDay(new DateOnly(2024, 3, 28)).Should().Be(new DateOnly(2024, 4, 1)); // Good Friday and the weekend
        _calendar.NextTradingDay(new DateOnly(2024, 12, 24)).Should().Be(new DateOnly(2024, 12, 26));
        _calendar.NextTradingDay(new DateOnly(2024, 3, 8)).Should().Be(new DateOnly(2024, 3, 11));
    }

    [Fact]
    public void PreviousTradingDay_SkipsWeekendsAndHolidays()
    {
        _calendar.PreviousTradingDay(new DateOnly(2024, 1, 2)).Should().Be(new DateOnly(2023, 12, 29));
        _calendar.PreviousTradingDay(new DateOnly(2026, 7, 6)).Should().Be(new DateOnly(2026, 7, 2)); // July 3 is the observed holiday
        _calendar.PreviousTradingDay(new DateOnly(2025, 1, 10)).Should().Be(new DateOnly(2025, 1, 8)); // January 9 was a day of mourning
    }

    [Fact]
    public void ADateBeforeTheSupportedRange_IsRefusedInsteadOfGuessed()
    {
        Action act = () => _calendar.IsTradingDay(new DateOnly(1997, 12, 31));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void EveryYear_HasPlausibleTradingDays_EveryDayWithoutSessionHasNoClose()
    {
        for (int year = 2000; year <= 2035; year++)
        {
            int trading = 0;
            for (var day = new DateOnly(year, 1, 1); day.Year == year; day = day.AddDays(1))
            {
                bool isTrading = _calendar.IsTradingDay(day);
                _calendar.CloseOf(day).HasValue.Should().Be(isTrading, $"{day} close must exist exactly on trading days");
                if (isTrading)
                {
                    day.DayOfWeek.Should().NotBe(DayOfWeek.Saturday).And.NotBe(DayOfWeek.Sunday);
                    trading++;
                }
            }

            trading.Should().BeInRange(245, 253, $"{year} has about 250 sessions");
        }
    }

    // Sessions per year as the NYSE published them: 2012 lost two days to Hurricane Sandy, 2018 and 2025 one each to a day of mourning,
    // 2007 one to the Ford funeral. Early closes still count as a session.
    [Theory]
    [InlineData(2005, 252)]
    [InlineData(2006, 251)]
    [InlineData(2007, 251)]
    [InlineData(2008, 253)]
    [InlineData(2009, 252)]
    [InlineData(2010, 252)]
    [InlineData(2011, 252)]
    [InlineData(2012, 250)]
    [InlineData(2013, 252)]
    [InlineData(2014, 252)]
    [InlineData(2015, 252)]
    [InlineData(2016, 252)]
    [InlineData(2017, 251)]
    [InlineData(2018, 251)]
    [InlineData(2019, 252)]
    [InlineData(2020, 253)]
    [InlineData(2021, 252)]
    [InlineData(2022, 251)]
    [InlineData(2023, 250)]
    [InlineData(2024, 252)]
    [InlineData(2025, 250)]
    [InlineData(2026, 251)]
    public void SessionsPerYear_MatchThePublishedExchangeSchedule(int year, int expected)
    {
        int sessions = 0;
        for (var day = new DateOnly(year, 1, 1); day.Year == year; day = day.AddDays(1))
        {
            if (_calendar.IsTradingDay(day))
            {
                sessions++;
            }
        }

        sessions.Should().Be(expected);
    }

    [Fact]
    public void ExtraordinaryClosures_CannotBeChangedByACaller()
    {
        var closures = (ISet<DateOnly>)UsEquityCalendar.ExtraordinaryClosures;

        Action add = () => closures.Add(new DateOnly(2030, 1, 2));

        add.Should().Throw<NotSupportedException>("the list is shared by every calendar");
        _calendar.IsTradingDay(new DateOnly(2030, 1, 2)).Should().BeTrue();
    }

    [Fact]
    public void TheCalendar_KnowsItsMarketAndZone()
    {
        _calendar.Market.Value.Should().Be("XNYS");
        _calendar.TimeZoneId.Should().Be("America/New_York");
        UsEquityCalendar.ExtraordinaryClosures.Should().Contain(new DateOnly(2025, 1, 9));
    }
}

public sealed class ExchangeCalendarsTests
{
    [Theory]
    [InlineData("XNYS")]
    [InlineData("XNAS")]
    [InlineData("XNGS")]
    [InlineData("ARCX")]
    public void Default_HasACalendarForEveryUsMarket(string code)
    {
        Result<IExchangeCalendar> result = ExchangeCalendars.Default().TryGet(Mic.Parse(code));

        result.IsSuccess.Should().BeTrue();
        result.Value.Market.Value.Should().Be(code);
    }

    [Fact]
    public void TryGet_OfAMarketWithoutACalendar_SaysItIsNotSupportedYet()
    {
        Result<IExchangeCalendar> result = ExchangeCalendars.Default().TryGet(Mic.Parse("XTSE"));

        result.Error!.Code.Should().Be("marketdata.calendar.not_found");
        result.Error.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public void Markets_ListsWhatIsSupported() =>
        ExchangeCalendars.Default().Markets.Select(m => m.Value).Should().Contain(["XNYS", "XNAS"]).And.NotContain("XTSE");
}
