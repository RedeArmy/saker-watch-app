namespace SakerWatch.MarketData.Domain.Fx;

/// <summary>The range a pair's rate must stay in to be believed at all.</summary>
/// <param name="Min">The lowest rate accepted.</param>
/// <param name="Max">The highest rate accepted.</param>
public sealed record FxBand(decimal Min, decimal Max);

/// <summary>What the guard holds a rate to (ADR-0042).</summary>
/// <param name="Bands">The band of each pair that has one, by the base and quote codes joined (<c>USDGTQ</c>); a pair without one has no band.</param>
/// <param name="MaxDailyChange">The largest relative change, as a fraction, between a rate and the one before it, or between a rate and the one stored for the same day.</param>
public sealed record FxGuardSettings(IReadOnlyDictionary<string, FxBand> Bands, decimal MaxDailyChange);

/// <summary>Why a rate was kept apart.</summary>
public static class FxRejectionReasons
{
    /// <summary>The rate is outside the band of its pair.</summary>
    public const string OutOfBand = "out_of_band";

    /// <summary>The rate moved more in a day than the pair is allowed to.</summary>
    public const string Jump = "jump";

    /// <summary>The rate would replace the one stored for the same day with a very different one.</summary>
    public const string Rewrite = "rewrite";
}

/// <summary>A rate that was not believed, with the reason.</summary>
/// <param name="Rate">The rate as the provider gave it.</param>
/// <param name="Reason">One of <see cref="FxRejectionReasons"/>.</param>
public sealed record RejectedFxRate(FxRate Rate, string Reason);

/// <summary>The outcome of screening a batch of rates.</summary>
/// <param name="Accepted">The rates to store, in date order.</param>
/// <param name="Rejected">The rates to keep apart, in date order.</param>
public sealed record FxScreening(IReadOnlyList<FxRate> Accepted, IReadOnlyList<RejectedFxRate> Rejected);

/// <summary>
/// Decides which of the rates a provider returned are believed before they are stored (ADR-0042). The bank is a public service that calls itself
/// "not an official source", and every quetzal figure of the product comes from one number: a misplaced decimal must not become a price. A rate
/// is refused when it is outside the band of its pair, when it differs by more than the daily limit from the rate before it (the newest earlier
/// one that is stored or was just accepted), or when it would replace the stored rate of the same day with one that differs by more than the limit.
/// A small correction of a day is allowed. Pure: no I/O.
/// </summary>
public static class FxRateGuard
{
    /// <summary>Screens <paramref name="incoming"/> against <paramref name="stored"/> (the rates already held for the same pair and source).</summary>
    public static FxScreening Screen(IReadOnlyList<FxRate> incoming, IReadOnlyList<FxRate> stored, FxGuardSettings settings)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(settings);

        var known = new SortedList<DateOnly, decimal>();
        foreach (FxRate rate in stored)
        {
            known[rate.AsOfDate] = rate.Rate;
        }

        var accepted = new List<FxRate>(incoming.Count);
        var rejected = new List<RejectedFxRate>();
        foreach (FxRate rate in incoming.OrderBy(r => r.AsOfDate))
        {
            string? reason = ReasonToRefuse(rate, known, settings);
            if (reason is null)
            {
                accepted.Add(rate);
                known[rate.AsOfDate] = rate.Rate;
            }
            else
            {
                rejected.Add(new RejectedFxRate(rate, reason));
            }
        }

        return new FxScreening(accepted, rejected);
    }

    private static string? ReasonToRefuse(FxRate rate, SortedList<DateOnly, decimal> known, FxGuardSettings settings)
    {
        if (settings.Bands.TryGetValue(rate.Base.Value + rate.Quote.Value, out FxBand? band) && (rate.Rate < band.Min || rate.Rate > band.Max))
        {
            return FxRejectionReasons.OutOfBand;
        }

        if (known.TryGetValue(rate.AsOfDate, out decimal sameDay) && Moved(sameDay, rate.Rate) > settings.MaxDailyChange)
        {
            return FxRejectionReasons.Rewrite;
        }

        decimal? before = Before(known, rate.AsOfDate);
        return before is { } previous && Moved(previous, rate.Rate) > settings.MaxDailyChange ? FxRejectionReasons.Jump : null;
    }

    private static decimal? Before(SortedList<DateOnly, decimal> known, DateOnly date)
    {
        IList<DateOnly> days = known.Keys;
        int low = 0, high = days.Count - 1, found = -1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (days[middle] < date)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found < 0 ? null : known.Values[found];
    }

    private static decimal Moved(decimal from, decimal to) => Math.Abs((to / from) - 1m);
}
