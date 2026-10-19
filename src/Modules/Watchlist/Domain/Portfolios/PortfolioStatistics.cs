namespace SakerWatch.Watchlist.Domain.Portfolios;

/// <summary>The change of one stock over each window, as the market data reports it: a fraction, or null when there is none.</summary>
/// <param name="Stock">An opaque label of the stock, used to name the best and the worst (ties go to the lowest label, ordinally).</param>
/// <param name="Changes">The change by window code; a window missing from the map, or null in it, counts as no data.</param>
public sealed record StockChanges(string Stock, IReadOnlyDictionary<string, decimal?> Changes);

/// <summary>A stock and its change over a window.</summary>
/// <param name="Stock">The label of the stock.</param>
/// <param name="Change">The change as a fraction (<c>0.0123</c> is 1.23 %).</param>
public sealed record WindowExtreme(string Stock, decimal Change);

/// <summary>What a portfolio did over one window, from the stocks that have a number for it.</summary>
/// <param name="Window">The window code.</param>
/// <param name="Count">How many stocks have a change for the window; the average, the median, the best and the worst are over these only.</param>
/// <param name="Average">The equal-weighted average, or <see langword="null"/> when <paramref name="Count"/> is zero.</param>
/// <param name="Median">The median (the mean of the two middle values when the count is even), or <see langword="null"/> when <paramref name="Count"/> is zero.</param>
/// <param name="Best">The stock with the largest change, or <see langword="null"/>.</param>
/// <param name="Worst">The stock with the smallest change, or <see langword="null"/>.</param>
public sealed record WindowStatistics(string Window, int Count, decimal? Average, decimal? Median, WindowExtreme? Best, WindowExtreme? Worst);

/// <summary>
/// The aggregate numbers of a portfolio (ADR-0026, ADR-0035). Every stock weighs the same, because a portfolio holds no quantities, so the average
/// says how the group of stocks moved and not how a sum of money did. All the arithmetic is <see cref="decimal"/> and nothing is rounded here
/// (ADR-0006). A stock with no number for a window is left out of that window's figures and out of its count, never counted as zero.
/// </summary>
public static class PortfolioStatistics
{
    /// <summary>The windows, in the order they are reported (the codes of ADR-0030).</summary>
    public static readonly IReadOnlyList<string> Windows = ["1D", "1W", "1M", "3M", "6M", "YTD", "1Y", "5Y"];

    /// <summary>Aggregates every window over <paramref name="stocks"/>.</summary>
    public static IReadOnlyList<WindowStatistics> Aggregate(IReadOnlyCollection<StockChanges> stocks)
    {
        ArgumentNullException.ThrowIfNull(stocks);
        return [.. Windows.Select(window => ForWindow(window, stocks))];
    }

    private static WindowStatistics ForWindow(string window, IReadOnlyCollection<StockChanges> stocks)
    {
        WindowExtreme[] values =
        [
            .. stocks
                .Where(stock => stock.Changes.TryGetValue(window, out decimal? change) && change.HasValue)
                .Select(stock => new WindowExtreme(stock.Stock, stock.Changes[window]!.Value))
                .OrderBy(extreme => extreme.Change)
                .ThenBy(extreme => extreme.Stock, StringComparer.Ordinal),
        ];
        if (values.Length == 0)
        {
            return new WindowStatistics(window, 0, null, null, null, null);
        }

        decimal sum = 0m;
        foreach (WindowExtreme value in values)
        {
            sum += value.Change;
        }

        // The values are in ascending order, ties by label: the first is the worst, and the best is the last of the largest value, found by its own tie rule.
        decimal largest = values[^1].Change;
        WindowExtreme best = values.Where(value => value.Change == largest).MinBy(value => value.Stock, StringComparer.Ordinal)!;
        return new WindowStatistics(window, values.Length, sum / values.Length, Median(values), best, values[0]);
    }

    private static decimal Median(WindowExtreme[] ascending)
    {
        int middle = ascending.Length / 2;
        return ascending.Length % 2 == 1 ? ascending[middle].Change : (ascending[middle - 1].Change + ascending[middle].Change) / 2m;
    }
}
