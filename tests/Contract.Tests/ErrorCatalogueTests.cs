using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace SakerWatch.Contract.Tests;

/// <summary>
/// <c>docs/api/errors.md</c> is what a screen switches on, so it must say exactly the codes the server can send: a code of the source that a client
/// is meant to see and the page does not list is a surprise for the frontend, and a code on the page that the source no longer has is a promise
/// nobody keeps.
/// </summary>
public sealed partial class ErrorCatalogueTests
{
    // The prefixes of the codes that reach a response. The ingestion, provider and lifecycle codes (marketdata.provider, .ingestion, .lifecycle, .filings)
    // are for the operator and the logs and are not in this list on purpose.
    [GeneratedRegex("\"((?:watchlist|calculator|identity|idempotency|money|validation|resource|auth)(?:\\.[a-z_]+)+|rate_limited|busy|internal_error|marketdata\\.(?:stock|search|prices|fx)\\.[a-z_]+)\"")]
    private static partial Regex SourceCode();

    // Literals with the shape of a code that are not an error a client sees: a disclaimer key, an internal invariant that means a bug of the server,
    // and the name of a setting or a claim that happens to start with the same word.
    private static readonly HashSet<string> _notShown =
    [
        "marketdata.fx.reference_only",
        "watchlist.item.ids_required",
        "watchlist.portfolio.user_required",
        "watchlist.portfolio.position_invalid",

        // The exchange rate the ingestion refuses to store (FxRate): it never reaches a response, the calculator reads rates through the money kit.
        "marketdata.fx.rate_not_positive",
        "marketdata.fx.same_currency",
        "marketdata.fx.source_invalid",

        // A listing the provider described badly: registration answers it as "not found" (StockDirectory), so the client sees marketdata.stock.not_found.
        "marketdata.stock.name_invalid",
        "marketdata.stock.time_zone_invalid",
    ];

    [GeneratedRegex("`([^`]+)`")]
    private static partial Regex InBackticks();

    [GeneratedRegex("^\\| ((?:`[^`]+`(?:, )?)+) \\|", RegexOptions.Multiline)]
    private static partial Regex CataloguedRow();

    private static string Root()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "SakerWatch.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static HashSet<string> Catalogued()
    {
        // The tables from "Any endpoint" on; the table of the members of a problem above them names `code` itself.
        string whole = File.ReadAllText(Path.Combine(Root(), "docs", "api", "errors.md"));
        string page = whole[whole.IndexOf("## Any endpoint", StringComparison.Ordinal)..];
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match row in CataloguedRow().Matches(page))
        {
            foreach (Match code in InBackticks().Matches(row.Groups[1].Value))
            {
                codes.Add(code.Groups[1].Value);
            }
        }

        return codes;
    }

    private static HashSet<string> InSource()
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Providers{Path.DirectorySeparatorChar}", StringComparison.Ordinal)   // adapters of providers: ingestion only
                                    && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                    && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            foreach (Match match in SourceCode().Matches(File.ReadAllText(file)))
            {
                codes.Add(match.Groups[1].Value);
            }
        }

        return codes;
    }

    [Fact]
    public void EveryCodeAClientCanSee_IsOnThePage()
    {
        HashSet<string> catalogued = Catalogued();

        string[] missing = [.. InSource().Where(code => !catalogued.Contains(code) && !_notShown.Contains(code)).Order(StringComparer.Ordinal)];

        missing.Should().BeEmpty("add them to docs/api/errors.md (or, if a client never sees them, to _notShown with the reason)");
    }

    [Fact]
    public void EveryCodeOnThePage_IsStillInTheSource()
    {
        HashSet<string> inSource = InSource();

        // The codes the framework writes are made from the status, so they have no literal.
        string[] gone = [.. Catalogued().Where(code => !code.StartsWith("http.", StringComparison.Ordinal) && !inSource.Contains(code)).Order(StringComparer.Ordinal)];

        gone.Should().BeEmpty("a code the server no longer sends must leave docs/api/errors.md");
    }

    [Fact]
    public void ThePage_DoesNotPromiseACodeItSaysIsNotShown()
    {
        Catalogued().Intersect(_notShown).Should().BeEmpty("a code that means a bug of the server is not part of the contract");
    }
}
