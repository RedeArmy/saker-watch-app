namespace SakerWatch.Watchlist.Infrastructure.Persistence;

// The rows map one to one to the tables; the domain types are rebuilt from them by the repositories. Public setters are the shape EF needs.

/// <summary><c>watchlist.watchlist_items</c>: a stock on one user's watchlist. Unique per user and stock; user-owned, so row-level security applies.</summary>
public sealed class WatchlistItemRow
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The owner; the row-level security policy compares it with the current user.</summary>
    public Guid UserId { get; set; }

    /// <summary>The catalog id of the stock (no foreign key across schemas, ADR-0031).</summary>
    public Guid StockId { get; set; }

    /// <summary>The user's notes, one to 2 000 characters, or null.</summary>
    public string? Notes { get; set; }

    /// <summary>The target price in the stock's currency, positive, or null.</summary>
    public decimal? TargetPrice { get; set; }

    /// <summary>When the stock was added.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the row last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary><c>watchlist.portfolios</c>: a named group of a user's watchlist stocks. The name is unique per user ignoring letter case.</summary>
public sealed class PortfolioRow
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The owner; the row-level security policy compares it with the current user.</summary>
    public Guid UserId { get; set; }

    /// <summary>The trimmed name, one to 60 characters.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>A free description, one to 500 characters, or null.</summary>
    public string? Description { get; set; }

    /// <summary>The place among the user's portfolios, from zero.</summary>
    public int Position { get; set; }

    /// <summary>When the portfolio was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the row last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// <c>watchlist.portfolio_items</c>: a watchlist stock in a portfolio. Both foreign keys carry the user, so a portfolio can never hold another
/// user's item even if a policy were ever wrong.
/// </summary>
public sealed class PortfolioItemRow
{
    /// <summary>The portfolio (part of the primary key).</summary>
    public Guid PortfolioId { get; set; }

    /// <summary>The watchlist item (part of the primary key).</summary>
    public Guid WatchlistItemId { get; set; }

    /// <summary>The owner of both; the composite foreign keys keep it the same.</summary>
    public Guid UserId { get; set; }

    /// <summary>The place of the stock inside the portfolio, from zero.</summary>
    public int Position { get; set; }

    /// <summary>When the stock was added to the portfolio.</summary>
    public DateTimeOffset AddedAt { get; set; }
}
