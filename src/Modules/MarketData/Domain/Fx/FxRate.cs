using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.MarketData.Domain.Fx;

/// <summary>
/// The value of one unit of <see cref="Base"/> in <see cref="Quote"/> on <see cref="AsOfDate"/>, as published by <see cref="Source"/>
/// (ADR-0028). A day without a publication has no rate: the latest earlier one is used and keeps its own date, so a rate is never invented.
/// </summary>
public sealed record FxRate
{
    /// <summary>The longest source name.</summary>
    public const int MaxSourceLength = 32;

    private FxRate(ExchangeRate exchange, DateOnly asOfDate, string source)
    {
        Exchange = exchange;
        AsOfDate = asOfDate;
        Source = source;
    }

    /// <summary>The rate with its direction: <see cref="Base"/> to <see cref="Quote"/>.</summary>
    public ExchangeRate Exchange { get; }

    /// <summary>The currency being priced (the "1 unit of" side).</summary>
    public CurrencyCode Base => Exchange.From;

    /// <summary>The currency the price is expressed in.</summary>
    public CurrencyCode Quote => Exchange.To;

    /// <summary>The day the rate applies to, in the publisher's calendar.</summary>
    public DateOnly AsOfDate { get; }

    /// <summary>Units of <see cref="Quote"/> for one unit of <see cref="Base"/>. Always positive.</summary>
    public decimal Rate => Exchange.Rate;

    /// <summary>Who published it: lower-case letters, digits and underscores (<c>banguat</c>).</summary>
    public string Source { get; }

    /// <summary>Validates and builds a rate: two different currencies, a positive rate inside the range of rates and a well-formed source.</summary>
    public static Result<FxRate> Create(CurrencyCode baseCurrency, CurrencyCode quote, DateOnly asOfDate, decimal rate, string? source)
    {
        ArgumentNullException.ThrowIfNull(baseCurrency);
        ArgumentNullException.ThrowIfNull(quote);

        if (baseCurrency == quote)
        {
            return Error.Validation("marketdata.fx.same_currency", "A rate relates two different currencies.");
        }

        if (rate <= 0)
        {
            return Error.Validation("marketdata.fx.rate_not_positive", "A rate is greater than zero.");
        }

        Result<ExchangeRate> exchange = ExchangeRate.Create(baseCurrency, quote, rate);
        if (!exchange.IsSuccess)
        {
            return exchange.Error!;
        }

        string normalized = (source ?? string.Empty).Trim();
        return normalized.Length is >= 1 and <= MaxSourceLength && normalized.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_')
            ? new FxRate(exchange.Value, asOfDate, normalized)
            : Error.Validation("marketdata.fx.source_invalid", "A source is one to 32 lower-case letters, digits or underscores.");
    }
}
