using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Domain.Fx;
using SakerWatch.MarketData.Domain.Markets;

namespace SakerWatch.MarketData.Application.Fx;

/// <summary>Converts an amount between two currencies with the stored daily rates (ADR-0028).</summary>
public interface IFxConverter
{
    /// <summary>
    /// Converts <paramref name="amount"/> from <paramref name="from"/> to <paramref name="to"/> with the rates in force on <paramref name="date"/>
    /// (today in Guatemala when not given): the newest rate published on or before it, never a day it was not published for. A pair that is
    /// not stored directly is tried the other way round and then through USD (ADR-0018); every step is returned with its day and source.
    /// </summary>
    Task<Result<FxConversion>> ConvertAsync(decimal amount, CurrencyCode from, CurrencyCode to, DateOnly? date, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IFxConverter"/> over <see cref="IFxRateStore"/>. The math is plain <see cref="decimal"/> and nothing is rounded: rounding belongs
/// to the presentation boundary (ADR-0006).
/// </summary>
public sealed class FxConverter(IFxRateStore store, IOptions<IngestionOptions> options, TimeProvider clock) : IFxConverter
{
    /// <summary>Code of the failure when no rate is stored for a step of the conversion.</summary>
    public const string RateUnavailableCode = "marketdata.fx.rate_unavailable";

    /// <summary>Code of the failure when the amount is negative or too large to convert.</summary>
    public const string AmountInvalidCode = "marketdata.fx.amount_invalid";

    private static readonly CurrencyCode _pivot = CurrencyCode.Parse("USD");

    /// <inheritdoc />
    public async Task<Result<FxConversion>> ConvertAsync(decimal amount, CurrencyCode from, CurrencyCode to, DateOnly? date, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        if (amount < 0)
        {
            return Error.Validation(AmountInvalidCode, "An amount to convert is zero or more.");
        }

        DateOnly day = date ?? FxFreshness.TodayInGuatemala(clock.GetUtcNow());
        if (from == to)
        {
            return new FxConversion(amount, from, to, amount, [], 1m, day, false);
        }

        var legs = new List<FxLeg>(2);
        foreach ((CurrencyCode legFrom, CurrencyCode legTo) in await PathAsync(from, to, day, cancellationToken))
        {
            FxLeg? leg = await ResolveAsync(legFrom, legTo, day, cancellationToken);
            if (leg is null)
            {
                return Error.NotFound(RateUnavailableCode, $"There is no {legFrom} to {legTo} rate on or before {day:yyyy-MM-dd}.");
            }

            legs.Add(leg);
        }

        return Combine(amount, from, to, day, legs);
    }

    // One step when the pair is stored (in either direction) or one side is the pivot; otherwise through the pivot (ADR-0018).
    private async Task<(CurrencyCode From, CurrencyCode To)[]> PathAsync(CurrencyCode from, CurrencyCode to, DateOnly day, CancellationToken cancellationToken)
    {
        if (from == _pivot || to == _pivot || await ResolveAsync(from, to, day, cancellationToken) is not null)
        {
            return [(from, to)];
        }

        return [(from, _pivot), (_pivot, to)];
    }

    // The stored rate in the asked direction or the reciprocal of the opposite one, whichever is newer (the asked direction on a tie).
    private async Task<FxLeg?> ResolveAsync(CurrencyCode from, CurrencyCode to, DateOnly day, CancellationToken cancellationToken)
    {
        FxRate? direct = await store.OnOrBeforeAsync(from, to, day, cancellationToken);
        FxRate? inverse = await store.OnOrBeforeAsync(to, from, day, cancellationToken);
        if (direct is not null && (inverse is null || direct.AsOfDate >= inverse.AsOfDate))
        {
            return new FxLeg(from, to, direct.Rate, direct.AsOfDate, direct.Source, Inverted: false);
        }

        return inverse is null ? null : new FxLeg(from, to, 1m / inverse.Rate, inverse.AsOfDate, inverse.Source, Inverted: true);
    }

    private Result<FxConversion> Combine(decimal amount, CurrencyCode from, CurrencyCode to, DateOnly day, List<FxLeg> legs)
    {
        try
        {
            decimal converted = amount;
            decimal rate = 1m;
            foreach (decimal legRate in legs.Select(leg => leg.Rate))
            {
                converted *= legRate;
                rate *= legRate;
            }

            DateOnly oldest = legs.Min(leg => leg.AsOfDate);
            int maxAge = options.Value.FxMaxAgeDays;
            bool stale = legs.Exists(leg => FxFreshness.IsStale(leg.AsOfDate, day, maxAge));
            return new FxConversion(amount, from, to, converted, legs, rate, oldest, stale);
        }
        catch (OverflowException)
        {
            return Error.Validation(AmountInvalidCode, "The amount is too large to convert.");
        }
    }
}
