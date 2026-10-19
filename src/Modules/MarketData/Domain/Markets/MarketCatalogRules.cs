using SakerWatch.BuildingBlocks.Kernel.Monetary;
namespace SakerWatch.MarketData.Domain.Markets;

/// <summary>
/// What the catalog accepts about a market, whatever a provider says (ADR-0027, ADR-0032): the canonical market a provider's segment code
/// belongs to, and the currency every listing of a market trades in. A provider's currency that disagrees (a cross-listing, a depositary
/// receipt) is a different listing and is not registered as this one.
/// </summary>
public static class MarketCatalogRules
{
    // Nasdaq reports its tiers as separate operating MICs (Global Select, Global Market, Capital Market); the catalog keeps one market.
    private static readonly Dictionary<string, string> _canonicalByProviderMic = new(StringComparer.OrdinalIgnoreCase)
    {
        ["XNGS"] = "XNAS",
        ["XNMS"] = "XNAS",
        ["XNCM"] = "XNAS",
    };

    private static readonly HashSet<string> _usMarkets = new(StringComparer.OrdinalIgnoreCase) { "XNYS", "XNAS", "XASE", "ARCX", "BATS" };

    /// <summary>The canonical market code for a provider's code: the same code, upper case, unless it is a segment of a larger market.</summary>
    public static string CanonicalMarket(string providerMic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerMic);
        string trimmed = providerMic.Trim();
        return _canonicalByProviderMic.TryGetValue(trimmed, out string? canonical) ? canonical : trimmed.ToUpperInvariant();
    }

    /// <summary>The currency every listing of <paramref name="market"/> trades in, or <see langword="null"/> when the market is not known yet.</summary>
    public static CurrencyCode? ExpectedCurrency(Mic market)
    {
        ArgumentNullException.ThrowIfNull(market);
        return _usMarkets.Contains(market.Value) ? CurrencyCode.Parse("USD") : null;
    }
}
