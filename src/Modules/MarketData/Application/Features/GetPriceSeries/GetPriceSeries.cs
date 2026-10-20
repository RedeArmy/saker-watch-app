using FluentValidation;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Contracts;

namespace SakerWatch.MarketData.Application.Features.GetPriceSeries;

/// <summary>The stored prices of a catalog stock over a range, for a chart (ADR-0038). Informational only.</summary>
/// <param name="Market">The canonical market code.</param>
/// <param name="Ticker">The ticker on that market.</param>
/// <param name="Range">One of <see cref="PriceRanges.All"/>; <see langword="null"/> means <c>1Y</c>.</param>
/// <param name="Interval">One of <see cref="PriceIntervals.All"/>; <see langword="null"/> means <c>auto</c>.</param>
public sealed record GetPriceSeriesQuery(string Market, string Ticker, string? Range, string? Interval) : IQuery<PriceSeries>, IReadsDatabase;

/// <summary>Reports a range or an interval that is not one of the known values, and an empty or over-long market or ticker.</summary>
public sealed class GetPriceSeriesValidator : AbstractValidator<GetPriceSeriesQuery>
{
    /// <summary>Creates the validator.</summary>
    public GetPriceSeriesValidator()
    {
        RuleFor(x => x.Market).NotEmpty().MaximumLength(4);
        RuleFor(x => x.Ticker).NotEmpty().MaximumLength(16);
        RuleFor(x => x.Range).Must(range => PriceRanges.All.Contains(range!.Trim().ToUpperInvariant(), StringComparer.Ordinal)).When(x => x.Range is not null)
            .WithMessage($"Range must be one of: {string.Join(", ", PriceRanges.All)}.");
        RuleFor(x => x.Interval).Must(interval => PriceIntervals.All.Contains(interval!.Trim().ToLowerInvariant(), StringComparer.Ordinal)).When(x => x.Interval is not null)
            .WithMessage($"Interval must be one of: {string.Join(", ", PriceIntervals.All)}.");
    }
}

/// <summary>Reads the series through the price history.</summary>
public sealed class GetPriceSeriesHandler(IPriceHistory history) : IRequestHandler<GetPriceSeriesQuery, Result<PriceSeries>>
{
    /// <inheritdoc />
    public async ValueTask<Result<PriceSeries>> Handle(GetPriceSeriesQuery request, CancellationToken cancellationToken) =>
        await history.SeriesAsync(request.Market, request.Ticker, request.Range ?? PriceRanges.OneYear, request.Interval ?? PriceIntervals.Auto, cancellationToken);
}
