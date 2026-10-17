using SakerWatch.MarketData.Domain.Markets;

namespace SakerWatch.MarketData.Application.Fx;

/// <summary>
/// The one rule for how current an exchange rate is (ADR-0028). The anchor pair is USD to GTQ, published by the Banco de Guatemala, whose
/// calendar is the Guatemalan one: a day starts and ends at the offset of <c>America/Guatemala</c> (UTC-6, no daylight saving).
/// </summary>
public static class FxFreshness
{
    /// <summary>The pair the product anchors on: US dollars to Guatemalan quetzales.</summary>
    public static readonly (CurrencyCode Base, CurrencyCode Quote) Anchor = (CurrencyCode.Parse("USD"), CurrencyCode.Parse("GTQ"));

    private const string _zone = "America/Guatemala";

    /// <summary>The calendar date in Guatemala at <paramref name="now"/>.</summary>
    public static DateOnly TodayInGuatemala(DateTimeOffset now) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(_zone)).DateTime);

    /// <summary>Whole days between the newest rate and <paramref name="today"/>; a rate dated in the future (the bank publishes ahead) is zero days old.</summary>
    public static int AgeDays(DateOnly latest, DateOnly today) => Math.Max(0, today.DayNumber - latest.DayNumber);

    /// <summary>
    /// True when the newest rate is older than <paramref name="maxAgeDays"/>. The bank fills weekends and holidays with the previous rate, so an
    /// old rate means the ingestion stopped, not that the market was closed. No rate at all is stale too.
    /// </summary>
    public static bool IsStale(DateOnly? latest, DateOnly today, int maxAgeDays) => latest is null || AgeDays(latest.Value, today) > maxAgeDays;
}
