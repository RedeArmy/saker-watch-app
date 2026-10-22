using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using SakerWatch.BuildingBlocks.Persistence.Platform;
using SakerWatch.Identity.Infrastructure.Persistence;
using SakerWatch.MarketData.Infrastructure.Persistence;
using SakerWatch.Watchlist.Infrastructure.Persistence;

namespace SakerWatch.Integration.Tests.Persistence;

/// <summary>Guards against a model change committed without its migration (the snapshot must describe the current model).</summary>
public sealed class ModelSnapshotTests
{
    [Fact]
    public void PlatformModel_HasNoPendingChanges()
    {
        using PlatformDbContext context = new PlatformDbContextFactory().CreateDbContext([]);

        context.Database.HasPendingModelChanges().Should().BeFalse("a model change needs a migration (see the add-migration skill)");
    }

    [Fact]
    public void IdentityModel_HasNoPendingChanges()
    {
        using IdentityDbContext context = new IdentityDbContextFactory().CreateDbContext([]);

        context.Database.HasPendingModelChanges().Should().BeFalse("a model change needs a migration (see the add-migration skill)");
    }

    [Fact]
    public void MarketDataModel_HasNoPendingChanges()
    {
        using MarketDataDbContext context = new MarketDataDbContextFactory().CreateDbContext([]);

        context.Database.HasPendingModelChanges().Should().BeFalse("a model change needs a migration (see the add-migration skill)");
    }

    [Fact]
    public void WatchlistModel_HasNoPendingChanges()
    {
        using WatchlistDbContext context = new WatchlistDbContextFactory().CreateDbContext([]);

        context.Database.HasPendingModelChanges().Should().BeFalse("a model change needs a migration (see the add-migration skill)");
    }
}
