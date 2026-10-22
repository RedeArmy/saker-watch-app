using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.MarketData.Domain.Fx;

namespace SakerWatch.MarketData.Application.Fx;

/// <summary>How the exchange rates are screened before they are stored (ADR-0042). Bound from <c>MarketData:Fx:Guard</c>.</summary>
public sealed class FxGuardOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "MarketData:Fx:Guard";

    /// <summary>The largest relative change in a day, as a fraction (<c>0.03</c> is 3 %). The quetzal has never moved that much in a day.</summary>
    [Range(typeof(decimal), "0.001", "0.5")]
    public decimal MaxDailyChange { get; set; } = 0.03m;

    /// <summary>The band of each pair that has one, by the two codes joined (<c>USDGTQ</c>). A pair without a band has only the daily limit.</summary>
    public Dictionary<string, FxBandOptions> Bands { get; set; } = new() { ["USDGTQ"] = new FxBandOptions { Min = 5m, Max = 12m } };

    /// <summary>The settings the domain guard takes.</summary>
    public FxGuardSettings ToSettings() => new(
        Bands.ToDictionary(pair => pair.Key, pair => new FxBand(pair.Value.Min, pair.Value.Max), StringComparer.Ordinal),
        MaxDailyChange);
}

/// <summary>The lowest and highest rate believed for a pair.</summary>
public sealed class FxBandOptions
{
    /// <summary>The lowest rate accepted.</summary>
    public decimal Min { get; set; }

    /// <summary>The highest rate accepted.</summary>
    public decimal Max { get; set; }
}

/// <summary>Cross-field validation of <see cref="FxGuardOptions"/>: a band is a pair of codes with a positive minimum below its maximum.</summary>
public sealed partial class FxGuardOptionsValidator : IValidateOptions<FxGuardOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, FxGuardOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];
        foreach ((string pair, FxBandOptions band) in options.Bands)
        {
            if (!Pair().IsMatch(pair))
            {
                failures.Add($"MarketData:Fx:Guard:Bands:{pair}: the name is the two currency codes joined, for example USDGTQ.");
            }
            else if (band.Min <= 0 || band.Max <= band.Min)
            {
                failures.Add($"MarketData:Fx:Guard:Bands:{pair}: Min must be greater than zero and Max greater than Min.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    [GeneratedRegex(@"^[A-Z]{6}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Pair();
}

/// <summary>What the refresh needs to screen rates: the rates already stored for a pair and source, and a place to keep the refused ones apart.</summary>
public interface IFxRateScreening
{
    /// <summary>The rates stored for the pair and source from <paramref name="from"/> to <paramref name="to"/> inclusive, in date order.</summary>
    Task<IReadOnlyList<FxRate>> StoredAsync(CurrencyCode baseCurrency, CurrencyCode quote, string source, DateOnly from, DateOnly to, CancellationToken cancellationToken);

    /// <summary>Keeps the refused rates apart with the reason (one row per pair, day and source: the last refusal wins). They are never served.</summary>
    Task RejectAsync(IReadOnlyList<RejectedFxRate> rejected, DateTimeOffset seenAt, CancellationToken cancellationToken);
}
