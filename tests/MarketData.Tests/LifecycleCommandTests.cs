using AwesomeAssertions;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Application.Lifecycle;

namespace SakerWatch.MarketData.Tests;

/// <summary>What an operator may type to change the life of a listing, and what is refused (ADR-0039).</summary>
public sealed class LifecycleCommandTests
{
    [Fact]
    public void Delist_TakesAMarketAndATicker_AndOptionallyTheDay()
    {
        LifecycleCommand plain = LifecycleCommand.Parse(["delist", "XNAS", "ABC"]).Value;
        LifecycleCommand dated = LifecycleCommand.Parse(["DELIST", "XNAS", "ABC", "--on", "2026-10-01"]).Value;

        plain.Should().Be(new LifecycleCommand(LifecycleOperation.Delist, "XNAS", "ABC", null, null, null));
        dated.Should().Be(new LifecycleCommand(LifecycleOperation.Delist, "XNAS", "ABC", null, new DateOnly(2026, 10, 1), null));
    }

    [Fact]
    public void Relist_TakesAMarketAndATicker() =>
        LifecycleCommand.Parse(["relist", "XNAS", "ABC"]).Value.Should().Be(new LifecycleCommand(LifecycleOperation.Relist, "XNAS", "ABC", null, null, null));

    [Fact]
    public void Rename_TakesTheOldAndTheNewTicker_AndOptionallyTheDayAndTheProviderSymbol()
    {
        LifecycleCommand plain = LifecycleCommand.Parse(["rename", "XNAS", "FB", "META"]).Value;
        LifecycleCommand full = LifecycleCommand.Parse(["rename", "XNAS", "FB", "META", "--provider-symbol", "META", "--on", "2022-06-09"]).Value;

        plain.Should().Be(new LifecycleCommand(LifecycleOperation.Rename, "XNAS", "FB", "META", null, null));
        full.Should().Be(new LifecycleCommand(LifecycleOperation.Rename, "XNAS", "FB", "META", new DateOnly(2022, 6, 9), "META"));
    }

    [Fact]
    public void TheOptions_MayComeBeforeTheValues() =>
        LifecycleCommand.Parse(["--on", "2026-10-01", "delist", "XNAS", "ABC"]).Value.On.Should().Be(new DateOnly(2026, 10, 1));

    [Theory]
    [InlineData]
    [InlineData("explode", "XNAS", "ABC")]
    [InlineData("delist")]
    [InlineData("delist", "XNAS")]
    [InlineData("delist", "XNAS", "ABC", "EXTRA")]
    [InlineData("rename", "XNAS", "FB")]
    [InlineData("rename", "XNAS", "FB", "META", "EXTRA")]
    [InlineData("delist", "XNAS", "ABC", "--on")]
    [InlineData("delist", "XNAS", "ABC", "--on", "yesterday")]
    [InlineData("delist", "XNAS", "ABC", "--on", "2026-13-40")]
    [InlineData("delist", "XNAS", "ABC", "--force")]
    [InlineData("delist", "XNAS", "ABC", "--provider-symbol", "X")]
    [InlineData("relist", "XNAS", "ABC", "--on", "2026-10-01")]
    [InlineData("rename", "XNAS", "FB", "META", "--provider-symbol")]
    [InlineData("rename", "XNAS", "FB", "META", "--provider-symbol", "--on")]
    [InlineData("rename", "XNAS", "FB", "META", "--provider-symbol", "ME TA")]
    [InlineData("rename", "XNAS", "FB", "META", "--provider-symbol", "META;DROP")]
    [InlineData("rename", "XNAS", "FB", "META", "--provider-symbol", ".META")]
    [InlineData("rename", "XNAS", "FB", "META", "--provider-symbol", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void ARequestThatCannotBeRead_IsRefusedWithTheUsage(params string[] arguments)
    {
        Result<LifecycleCommand> result = LifecycleCommand.Parse(arguments);

        (result.Error!.Type, result.Error.Code).Should().Be((ErrorType.Validation, LifecycleCommand.UsageCode));
        result.Error.Message.Should().Contain("marketdata rename MIC OLD NEW");
    }
}
