using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Contracts;
using SakerWatch.MarketData.Domain.Fx;

namespace SakerWatch.MarketData.Application.Fx;

/// <summary><see cref="IFxRates"/> over the converter of ADR-0028: the rate of one unit, with the path it was reached by.</summary>
public sealed class FxRates(IFxConverter converter) : IFxRates
{
    /// <inheritdoc />
    public async Task<Result<FxQuote>> QuoteAsync(CurrencyCode from, CurrencyCode to, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        Result<FxConversion> converted = await converter.ConvertAsync(Money.Create(1m, from).Value, to, null, cancellationToken);
        if (!converted.IsSuccess)
        {
            return converted.Error!;
        }

        FxConversion conversion = converted.Value;
        return new FxQuote(
            conversion.Rate,
            [.. conversion.Legs.Select(leg => new FxQuoteLeg(leg.Rate, leg.AsOfDate, leg.Source, leg.Inverted))],
            conversion.AsOfDate,
            conversion.IsStale);
    }
}
