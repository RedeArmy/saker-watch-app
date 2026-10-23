using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Domain.Fx;

namespace SakerWatch.MarketData.Application.Fx;

/// <summary>Converts an amount between two currencies with the stored daily rates (ADR-0028).</summary>
public interface IFxConverter
{
    /// <summary>
    /// Converts <paramref name="amount"/> to <paramref name="to"/> with the rates in force on <paramref name="date"/>
    /// (today in Guatemala when not given): the newest rate published on or before it, never a day it was not published for. A pair that is
    /// not stored directly is tried the other way round and then through USD (ADR-0018); every step is returned with its day and source.
    /// </summary>
    Task<Result<FxConversion>> ConvertAsync(Money amount, CurrencyCode to, DateOnly? date, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IFxConverter"/> over <see cref="IFxRateStore"/>. The math is done by the money kit (<see cref="ExchangeRate"/>, <see cref="Money"/>) and
/// nothing is rounded: rounding belongs to the presentation boundary (ADR-0006, ADR-0037).
/// </summary>
public sealed class FxConverter(IFxRateStore store, IOptions<IngestionOptions> options, TimeProvider clock) : IFxConverter
{
    /// <summary>Code of the failure when no rate is stored for a step of the conversion.</summary>
    public const string RateUnavailableCode = "marketdata.fx.rate_unavailable";

    /// <summary>Code of the failure when the amount is negative or too large to convert.</summary>
    public const string AmountInvalidCode = "marketdata.fx.amount_invalid";

    private static readonly CurrencyCode _pivot = CurrencyCode.Parse("USD");

    /// <inheritdoc />
    public async Task<Result<FxConversion>> ConvertAsync(Money amount, CurrencyCode to, DateOnly? date, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentNullException.ThrowIfNull(to);

        if (amount.Amount < 0)
        {
            return Error.Validation(AmountInvalidCode, "An amount to convert is zero or more.");
        }

        DateOnly day = date ?? FxFreshness.TodayInGuatemala(clock.GetUtcNow());
        CurrencyCode from = amount.Currency;
        if (from == to)
        {
            return new FxConversion(amount, amount, [], ExchangeRate.Identity(from), day, false);
        }

        (List<FxLeg> legs, (CurrencyCode From, CurrencyCode To)? missing) = await PathAsync(from, to, day, cancellationToken);
        return missing is { } gap
            ? Error.NotFound(RateUnavailableCode, $"There is no {gap.From} to {gap.To} rate on or before {day:yyyy-MM-dd}.")
            : Combine(amount, day, legs);
    }

    // One step when the pair is stored (in either direction) or one side is the pivot; otherwise through the pivot (ADR-0018). Each pair is resolved once, and the legs
    // that were resolved to find the path are the ones used: the step that has no rate is the one named when there is none.
    private async Task<(List<FxLeg> Legs, (CurrencyCode From, CurrencyCode To)? Missing)> PathAsync(CurrencyCode from, CurrencyCode to, DateOnly day, CancellationToken cancellationToken)
    {
        FxLeg? direct = await ResolveAsync(from, to, day, cancellationToken);
        if (direct is not null)
        {
            return ([direct], null);
        }

        if (from == _pivot || to == _pivot)
        {
            return ([], (from, to));
        }

        FxLeg? toPivot = await ResolveAsync(from, _pivot, day, cancellationToken);
        if (toPivot is null)
        {
            return ([], (from, _pivot));
        }

        FxLeg? fromPivot = await ResolveAsync(_pivot, to, day, cancellationToken);
        return fromPivot is null ? ([], (_pivot, to)) : ([toPivot, fromPivot], null);
    }

    // The stored rate in the asked direction or the reciprocal of the opposite one, whichever is newer (the asked direction on a tie).
    private async Task<FxLeg?> ResolveAsync(CurrencyCode from, CurrencyCode to, DateOnly day, CancellationToken cancellationToken)
    {
        (FxRate? direct, FxRate? inverse) = await store.OnOrBeforeBothWaysAsync(from, to, day, cancellationToken);
        if (direct is not null && (inverse is null || direct.AsOfDate >= inverse.AsOfDate))
        {
            return new FxLeg(direct.Exchange, direct.AsOfDate, direct.Source, Inverted: false);
        }

        // The reciprocal of a stored rate is inside the range of rates because the range is symmetric, so this cannot fail; a rate that did not
        // invert would be treated as missing, never as a crash.
        return inverse is not null && inverse.Exchange.Invert() is { IsSuccess: true } inverted
            ? new FxLeg(inverted.Value, inverse.AsOfDate, inverse.Source, Inverted: true)
            : null;
    }

    private Result<FxConversion> Combine(Money amount, DateOnly day, List<FxLeg> legs)
    {
        Money converted = amount;
        ExchangeRate rate = legs[0].Rate;
        foreach (FxLeg leg in legs)
        {
            Result<Money> step = leg.Rate.Convert(converted);
            if (!step.IsSuccess)
            {
                return Error.Validation(AmountInvalidCode, "The amount is too large to convert.");
            }

            converted = step.Value;
        }

        foreach (FxLeg leg in legs.Skip(1))
        {
            Result<ExchangeRate> chained = rate.Then(leg.Rate);
            if (!chained.IsSuccess)
            {
                return Error.Validation(RateUnavailableCode, "The rates of the path cannot be combined.");
            }

            rate = chained.Value;
        }

        DateOnly oldest = legs.Min(leg => leg.AsOfDate);
        int maxAge = options.Value.FxMaxAgeDays;
        bool stale = legs.Exists(leg => FxFreshness.IsStale(leg.AsOfDate, day, maxAge));
        return new FxConversion(amount, converted, legs, rate, oldest, stale);
    }
}
