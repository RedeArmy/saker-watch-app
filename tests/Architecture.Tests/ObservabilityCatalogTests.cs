using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace SakerWatch.Architecture.Tests;

/// <summary>
/// Keeps <c>docs/observability/README.md</c> true (the rule of observability: a new log event or metric is registered in the catalog): every log
/// event id and every <c>sakerwatch_</c> metric name in the source is in the catalog, and every event id in the catalog exists in the source.
/// </summary>
public sealed partial class ObservabilityCatalogTests
{
    private static string Root()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "global.json")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new InvalidOperationException("The repository root (global.json) was not found above the test binaries.");
    }

    private static string[] Sources() =>
        [.. Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText)];

    private static string Catalog() => File.ReadAllText(Path.Combine(Root(), "docs", "observability", "README.md"));

    [GeneratedRegex(@"EventId\s*=\s*(\d{4})", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EventIds();

    [GeneratedRegex(@"""(sakerwatch_[a-z0-9_]+)""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Metrics();

    [GeneratedRegex(@"^\| (\d{4}) \|", RegexOptions.CultureInvariant | RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CatalogEvents();

    [Fact]
    public void EveryLogEventOfTheSource_IsInTheCatalog_AndEveryEventOfTheCatalogExists()
    {
        string[] sources = Sources();
        HashSet<string> inCode = [.. sources.SelectMany(text => EventIds().Matches(text).Select(match => match.Groups[1].Value))];
        HashSet<string> inCatalog = [.. CatalogEvents().Matches(Catalog()).Select(match => match.Groups[1].Value)];

        inCode.Should().NotBeEmpty();
        inCode.Except(inCatalog).Should().BeEmpty("a new log event is registered in docs/observability/README.md");
        inCatalog.Except(inCode).Should().BeEmpty("an event of the catalog that no code emits is a stale row");
    }

    [Fact]
    public void EveryMetricOfTheSource_IsInTheCatalog()
    {
        string catalog = Catalog();
        HashSet<string> inCode = [.. Sources().SelectMany(text => Metrics().Matches(text).Select(match => match.Groups[1].Value))];

        inCode.Should().NotBeEmpty();
        inCode.Where(name => !catalog.Contains($"`{name}`", StringComparison.Ordinal)).Should().BeEmpty("a new metric is registered in docs/observability/README.md");
    }

    [Fact]
    public void LogSinks_AreAddedOnlyBehindTheScrubbingSink()
    {
        // A sink added with WriteTo receives the exception of an event as it is: the one place that adds sinks wraps them (ADR-0016).
        string[] offenders =
        [.. Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.EndsWith("SakerLogging.cs", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains(".WriteTo.", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(Root(), path))];

        offenders.Should().BeEmpty("sinks are added with ApplySakerDefaults(additionalSinks) or WriteToScrubbed, so the scrubbing sink is in front of them");
    }
}
