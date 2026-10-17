using Microsoft.EntityFrameworkCore;
using SakerWatch.BuildingBlocks.Persistence.Conventions;

namespace SakerWatch.MarketData.Infrastructure.Persistence;

/// <summary>Owns the <c>marketdata</c> schema: the shared stock catalog and the facts about each listing (ADR-0018, ADR-0029).</summary>
/// <param name="options">Context options.</param>
public sealed class MarketDataDbContext(DbContextOptions<MarketDataDbContext> options) : DbContext(options)
{
    /// <summary>Schema that holds the market data tables and the migrations history.</summary>
    public const string Schema = "marketdata";

    /// <summary>The stock catalog.</summary>
    public DbSet<StockRow> Stocks => Set<StockRow>();

    /// <summary>Provider symbol aliases.</summary>
    public DbSet<SymbolAliasRow> SymbolAliases => Set<SymbolAliasRow>();

    /// <summary>Splits, dividends, symbol changes and delistings.</summary>
    public DbSet<CorporateActionRow> CorporateActions => Set<CorporateActionRow>();

    /// <summary>Daily prices (a hypertable).</summary>
    public DbSet<PriceDailyRow> PricesDaily => Set<PriceDailyRow>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<StockRow>(e =>
        {
            e.ToTable("stocks", t =>
            {
                t.HasCheckConstraint("ck_stocks_market_mic", "market_mic ~ '^[A-Z0-9]{4}$'");
                t.HasCheckConstraint("ck_stocks_ticker", "ticker ~ '^[A-Z0-9][A-Z0-9._-]{0,15}$'");
                t.HasCheckConstraint("ck_stocks_currency", "currency ~ '^[A-Z]{3}$'");
                t.HasCheckConstraint("ck_stocks_name", "char_length(name) BETWEEN 1 AND 200");
                t.HasCheckConstraint("ck_stocks_asset_type", "asset_type IN ('equity', 'etf', 'other')");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.MarketMic).HasMaxLength(4).IsFixedLength();
            e.Property(x => x.Ticker).HasMaxLength(16);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            e.Property(x => x.TimeZone).HasMaxLength(64);
            e.Property(x => x.AssetType).HasMaxLength(16);
            e.HasIndex(x => new { x.MarketMic, x.Ticker }).IsUnique();
        });

        modelBuilder.Entity<SymbolAliasRow>(e =>
        {
            e.ToTable("symbol_aliases");
            e.HasKey(x => new { x.Provider, x.ProviderMic, x.ProviderSymbol });
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.ProviderMic).HasMaxLength(16);
            e.Property(x => x.ProviderSymbol).HasMaxLength(32);
            e.HasOne<StockRow>().WithMany().HasForeignKey(x => x.StockId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.StockId);
        });

        modelBuilder.Entity<CorporateActionRow>(e =>
        {
            e.ToTable("corporate_actions", t =>
            {
                t.HasCheckConstraint("ck_corporate_actions_kind", "kind IN ('split', 'dividend', 'symbol_change', 'delisting')");
                t.HasCheckConstraint("ck_corporate_actions_split", "kind <> 'split' OR (split_ratio IS NOT NULL AND split_ratio > 0)");
                t.HasCheckConstraint("ck_corporate_actions_dividend", "kind <> 'dividend' OR (cash_amount IS NOT NULL AND cash_amount >= 0 AND currency IS NOT NULL)");
                t.HasCheckConstraint("ck_corporate_actions_currency", "currency IS NULL OR currency ~ '^[A-Z]{3}$'");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.SplitRatio).HasPrecision(28, 12);
            e.Property(x => x.CashAmount).HasPrecision(28, 12);
            e.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            e.Property(x => x.Source).HasMaxLength(32);
            e.HasOne<StockRow>().WithMany().HasForeignKey(x => x.StockId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.StockId, x.Kind, x.EffectiveDate }).IsUnique();
        });

        modelBuilder.Entity<PriceDailyRow>(e =>
        {
            e.ToTable("price_daily", t =>
            {
                t.HasCheckConstraint("ck_price_daily_prices", "open > 0 AND high > 0 AND low > 0 AND close > 0 AND adjusted_close > 0");
                t.HasCheckConstraint("ck_price_daily_range", "high >= greatest(open, close, low) AND low <= least(open, close, high)");
                t.HasCheckConstraint("ck_price_daily_volume", "volume >= 0");
                t.HasCheckConstraint("ck_price_daily_source", "source ~ '^[a-z0-9_]{1,32}$'");
            });
            e.HasKey(x => new { x.StockId, x.TradeDate });
            e.Property(x => x.Open).HasPrecision(20, 8);
            e.Property(x => x.High).HasPrecision(20, 8);
            e.Property(x => x.Low).HasPrecision(20, 8);
            e.Property(x => x.Close).HasPrecision(20, 8);
            e.Property(x => x.AdjustedClose).HasPrecision(20, 8);
            e.Property(x => x.Source).HasMaxLength(32);
            e.HasOne<StockRow>().WithMany().HasForeignKey(x => x.StockId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.UseSnakeCaseNames();
    }
}
