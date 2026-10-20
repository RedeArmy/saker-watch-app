using AwesomeAssertions;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Tests;

public sealed class StockTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 16, 21, 0, 0, TimeSpan.Zero);

    private static Result<Stock> Create(
        string? market = "XNAS", string? ticker = "AAPL", string? name = "Apple Inc.", string? currency = "USD", string? zone = "America/New_York") =>
        Stock.Create(StockId.Create(Guid.CreateVersion7()), new NewListing(market, ticker, name, currency, zone, AssetType.Equity), _now);

    [Fact]
    public void Create_WithValidParts_ReturnsAnActiveListing()
    {
        Stock stock = Create(" xnas ", "aapl", "  Apple Inc.  ", "usd", " America/New_York ").Value;

        stock.Market.Value.Should().Be("XNAS");
        stock.Ticker.Value.Should().Be("AAPL");
        stock.Name.Should().Be("Apple Inc.");
        stock.Currency.Value.Should().Be("USD");
        stock.TimeZoneId.Should().Be("America/New_York");
        stock.AssetType.Should().Be(AssetType.Equity);
        stock.IsActive.Should().BeTrue();
        stock.ListedAt.Should().Be(_now);
    }

    [Theory]
    [InlineData("XN", "AAPL", "Apple", "USD", "America/New_York", "marketdata.mic.invalid")]
    [InlineData("XNAS", "A PL", "Apple", "USD", "America/New_York", "marketdata.ticker.invalid")]
    [InlineData("XNAS", "AAPL", "Apple", "US", "America/New_York", "money.currency_invalid")]
    [InlineData("XNAS", "AAPL", "  ", "USD", "America/New_York", "marketdata.stock.name_invalid")]
    [InlineData("XNAS", "AAPL", null, "USD", "America/New_York", "marketdata.stock.name_invalid")]
    [InlineData("XNAS", "AAPL", "Apple", "USD", "Nowhere/Land", "marketdata.stock.time_zone_invalid")]
    [InlineData("XNAS", "AAPL", "Apple", "USD", " ", "marketdata.stock.time_zone_invalid")]
    [InlineData("XNAS", "AAPL", "Apple", "USD", null, "marketdata.stock.time_zone_invalid")]
    public void Create_WithAnInvalidPart_NamesTheFailure(string market, string ticker, string? name, string currency, string? zone, string code) =>
        Create(market, ticker, name, currency, zone).Error!.Code.Should().Be(code);

    [Fact]
    public void Create_WithATooLongName_IsRefused() =>
        Create(name: new string('x', Stock.MaxNameLength + 1)).Error!.Code.Should().Be("marketdata.stock.name_invalid");

    [Fact]
    public void Rename_TakesTheTrimmedName_AndRefusesAnEmptyOne()
    {
        Stock stock = Create().Value;

        stock.Rename("  Apple Computer  ").IsSuccess.Should().BeTrue();
        stock.Name.Should().Be("Apple Computer");
        stock.Rename("   ").Error!.Code.Should().Be("marketdata.stock.name_invalid");
        stock.Name.Should().Be("Apple Computer");
    }

    [Fact]
    public void DeactivateAndReactivate_AreIdempotent()
    {
        Stock stock = Create().Value;

        stock.Deactivate();
        stock.Deactivate();
        stock.IsActive.Should().BeFalse();
        stock.Reactivate();
        stock.Reactivate();
        stock.IsActive.Should().BeTrue();
    }

    [Fact]
    public void TwoStocksWithTheSameId_AreTheSameEntity()
    {
        var id = StockId.Create(Guid.CreateVersion7());
        Stock a = Stock.Create(id, new NewListing("XNAS", "AAPL", "Apple", "USD", "America/New_York", AssetType.Equity), _now).Value;
        Stock b = Stock.Create(id, new NewListing("XNAS", "AAPL", "Apple Inc.", "USD", "America/New_York", AssetType.Equity), _now).Value;

        a.Should().Be(b);
    }
}
