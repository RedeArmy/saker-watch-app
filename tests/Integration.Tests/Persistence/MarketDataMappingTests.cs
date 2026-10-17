using AwesomeAssertions;
using SakerWatch.MarketData.Domain.Stocks;
using SakerWatch.MarketData.Infrastructure.Persistence;

namespace SakerWatch.Integration.Tests.Persistence;

/// <summary>The stored vocabulary and the domain vocabulary must not drift apart before a repository maps between them.</summary>
public sealed class MarketDataMappingTests
{
    [Fact]
    public void AssetTypes_StoredInTheDatabase_AreExactlyTheDomainAssetTypes()
    {
        string[] stored = [AssetTypes.Equity, AssetTypes.Etf, AssetTypes.Other];

        string[] domain = [.. Enum.GetNames<AssetType>().Select(name => name.ToLowerInvariant())];

        stored.Should().BeEquivalentTo(domain);
    }
}
