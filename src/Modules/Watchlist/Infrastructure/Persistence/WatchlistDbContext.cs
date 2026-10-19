using Microsoft.EntityFrameworkCore;
using SakerWatch.BuildingBlocks.Persistence.Conventions;
using SakerWatch.Watchlist.Domain;
using SakerWatch.Watchlist.Domain.Items;
using SakerWatch.Watchlist.Domain.Portfolios;

namespace SakerWatch.Watchlist.Infrastructure.Persistence;

/// <summary>
/// Owns the <c>watchlist</c> schema: the user's stocks and portfolios. Every table is user-owned and protected by row-level security
/// (ADR-0010); the stock is a reference by id to <c>marketdata.stocks</c> with no foreign key across schemas (ADR-0031).
/// </summary>
/// <param name="options">Context options.</param>
public sealed class WatchlistDbContext(DbContextOptions<WatchlistDbContext> options) : DbContext(options)
{
    /// <summary>Schema that holds the watchlist tables and the migrations history.</summary>
    public const string Schema = "watchlist";

    /// <summary>The stocks on each user's watchlist.</summary>
    public DbSet<WatchlistItemRow> Items => Set<WatchlistItemRow>();

    /// <summary>The portfolios.</summary>
    public DbSet<PortfolioRow> Portfolios => Set<PortfolioRow>();

    /// <summary>Which watchlist stock is in which portfolio.</summary>
    public DbSet<PortfolioItemRow> PortfolioItems => Set<PortfolioItemRow>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<WatchlistItemRow>(e =>
        {
            e.ToTable("watchlist_items", t =>
            {
                t.HasCheckConstraint("ck_watchlist_items_target_price", "target_price IS NULL OR target_price > 0");
                t.HasCheckConstraint("ck_watchlist_items_notes", $"notes IS NULL OR char_length(notes) BETWEEN 1 AND {WatchlistItem.MaxNotesLength}");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Notes).HasMaxLength(WatchlistItem.MaxNotesLength);
            e.Property(x => x.TargetPrice).HasPrecision(20, WatchlistItem.TargetPriceScale);
            e.HasIndex(x => new { x.UserId, x.StockId }).IsUnique();
            e.HasAlternateKey(x => new { x.Id, x.UserId });
            e.HasIndex(x => x.StockId);
        });

        modelBuilder.Entity<PortfolioRow>(e =>
        {
            e.ToTable("portfolios", t =>
            {
                t.HasCheckConstraint("ck_portfolios_name", $"name = btrim(name) AND char_length(name) BETWEEN 1 AND {PortfolioName.MaxLength}");
                t.HasCheckConstraint("ck_portfolios_description", $"description IS NULL OR char_length(description) BETWEEN 1 AND {Portfolio.MaxDescriptionLength}");
                t.HasCheckConstraint("ck_portfolios_position", "position >= 0");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(PortfolioName.MaxLength);
            e.Property(x => x.Description).HasMaxLength(Portfolio.MaxDescriptionLength);
            e.HasAlternateKey(x => new { x.Id, x.UserId });
            e.HasIndex(x => new { x.UserId, x.Position });
        });

        modelBuilder.Entity<PortfolioItemRow>(e =>
        {
            e.ToTable("portfolio_items", t => t.HasCheckConstraint("ck_portfolio_items_position", "position >= 0"));
            e.HasKey(x => new { x.PortfolioId, x.WatchlistItemId });
            e.HasOne<PortfolioRow>().WithMany().HasForeignKey(x => new { x.PortfolioId, x.UserId }).HasPrincipalKey(x => new { x.Id, x.UserId }).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<WatchlistItemRow>().WithMany().HasForeignKey(x => new { x.WatchlistItemId, x.UserId }).HasPrincipalKey(x => new { x.Id, x.UserId }).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.WatchlistItemId);
        });

        modelBuilder.UseSnakeCaseNames();
    }
}
