using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Domain.Markets;

namespace SakerWatch.MarketData.Application.Providers;

/// <summary>
/// Which provider serves which market (ADR-0027). Bound from <c>MarketData:Providers</c>. A market that is not listed is disabled: nothing
/// is fetched for it. One market has one provider, so the adjusted closes of a listing never mix two vendors.
/// </summary>
public sealed class MarketDataProvidersOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "MarketData:Providers";

    /// <summary>Provider name by canonical market code, for example <c>XNAS</c> to <c>twelvedata</c>.</summary>
    [Required]
    public Dictionary<string, string> Markets { get; set; } = [];
}

/// <summary>Rejects a routing table with a market code that is not a MIC or an empty provider name, so a typo fails at startup.</summary>
public sealed class MarketDataProvidersOptionsValidator : IValidateOptions<MarketDataProvidersOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, MarketDataProvidersOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string[] problems =
        [
            .. options.Markets.Keys.Where(market => !Mic.Create(market).IsSuccess).Select(market => $"'{market}' is not a market code (four letters or digits)."),
            .. options.Markets.Where(pair => string.IsNullOrWhiteSpace(pair.Value)).Select(pair => $"The market {pair.Key} has no provider name."),
        ];
        return problems.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}

/// <summary>Finds the provider for a market.</summary>
public interface IMarketDataProviderRouter
{
    /// <summary>The provider that serves <paramref name="market"/>, or a failure when the market is disabled or its provider is not registered.</summary>
    Result<IMarketDataProvider> For(Mic market);
}

/// <summary>Routes by the configured table; provider names are compared ignoring case.</summary>
/// <param name="providers">Every registered provider.</param>
/// <param name="options">The routing table.</param>
public sealed class MarketDataProviderRouter(IEnumerable<IMarketDataProvider> providers, IOptions<MarketDataProvidersOptions> options) : IMarketDataProviderRouter
{
    /// <summary>Code returned for a market that has no provider configured.</summary>
    public const string MarketDisabledCode = "marketdata.provider.market_disabled";

    /// <summary>Code returned when the configured provider is not registered.</summary>
    public const string NotRegisteredCode = "marketdata.provider.not_registered";

    private readonly Dictionary<string, IMarketDataProvider> _byName =
        providers.ToDictionary(provider => provider.Name, StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<Mic, string> _byMarket = options.Value.Markets
        .ToDictionary(pair => Mic.Parse(pair.Key), pair => pair.Value.Trim());

    /// <inheritdoc />
    public Result<IMarketDataProvider> For(Mic market)
    {
        ArgumentNullException.ThrowIfNull(market);
        if (!_byMarket.TryGetValue(market, out string? name))
        {
            return Error.NotFound(MarketDisabledCode, $"No provider is configured for the market {market}, so it is disabled.");
        }

        return _byName.TryGetValue(name, out IMarketDataProvider? provider)
            ? Result.Success(provider)
            : Error.Failure(NotRegisteredCode, $"The provider '{name}' configured for the market {market} is not registered.");
    }
}
