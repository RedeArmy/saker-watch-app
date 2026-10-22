using SakerWatch.Watchlist.Domain.Portfolios;

namespace SakerWatch.Watchlist.Application;

/// <summary>A portfolio with how many stocks it holds.</summary>
/// <param name="Portfolio">The portfolio.</param>
/// <param name="ItemCount">Stocks in it.</param>
public sealed record PortfolioSummary(Portfolio Portfolio, int ItemCount);

/// <summary>A stock in a portfolio.</summary>
/// <param name="StockId">The catalog id of the stock.</param>
/// <param name="Position">Where it sits in the portfolio, from zero.</param>
/// <param name="AddedAt">When it was put in the portfolio.</param>
public sealed record PortfolioMember(Guid StockId, int Position, DateTimeOffset AddedAt);

/// <summary>What deleting a portfolio would do to the watchlist (ADR-0026).</summary>
/// <param name="StocksToRemove">The catalog ids of the stocks that are in this portfolio and in no other, so they would leave the watchlist.</param>
/// <param name="StocksKept">How many stocks of the portfolio would stay, because another portfolio holds them too.</param>
public sealed record DeletionPreview(IReadOnlyList<Guid> StocksToRemove, int StocksKept);

/// <summary>The result of storing a portfolio.</summary>
public enum PortfolioWrite
{
    /// <summary>Stored.</summary>
    Done,

    /// <summary>Another portfolio of the user has the same name, ignoring letter case.</summary>
    NameTaken,

    /// <summary>The user already has the most portfolios allowed.</summary>
    LimitReached,

    /// <summary>The portfolio does not exist (for the user).</summary>
    NotFound,
}

/// <summary>The result of putting a stock in a portfolio.</summary>
public enum MemberAdd
{
    /// <summary>The stock was put in the portfolio.</summary>
    Added,

    /// <summary>It was there already.</summary>
    AlreadyThere,

    /// <summary>The stock is not on the user's watchlist, so it cannot be in a portfolio.</summary>
    ItemNotOnWatchlist,

    /// <summary>The portfolio already holds the most stocks allowed.</summary>
    LimitReached,

    /// <summary>The portfolio does not exist (for the user).</summary>
    PortfolioNotFound,
}

/// <summary>
/// Storage of a user's portfolios and their members. Every call is scoped to the user it is given and row-level security repeats the scope. A
/// result that the transaction cannot go on after (a ceiling reached, a name taken by a race) is returned and the caller stops.
/// </summary>
public interface IPortfolioRepository
{
    /// <summary>The user's portfolios by position, with their stock counts.</summary>
    Task<IReadOnlyList<PortfolioSummary>> ListAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>One portfolio of the user, or <see langword="null"/>.</summary>
    Task<PortfolioSummary?> FindAsync(Guid userId, PortfolioId id, CancellationToken cancellationToken);

    /// <summary>
    /// Takes the lock that orders the writers of the user's portfolio positions, held until the transaction ends. A use case that reads the
    /// positions to choose the next one calls this first, so two parallel creations cannot both read the same last position.
    /// </summary>
    Task LockOrderAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Stores a new portfolio.</summary>
    Task<PortfolioWrite> AddAsync(Portfolio portfolio, CancellationToken cancellationToken);

    /// <summary>Stores the name and the description of an existing portfolio.</summary>
    Task<PortfolioWrite> UpdateAsync(Portfolio portfolio, CancellationToken cancellationToken);

    /// <summary>Puts the portfolios in this order (positions from zero). The ids must be exactly the user's portfolios; returns false when they are not.</summary>
    Task<bool> ReorderAsync(Guid userId, IReadOnlyList<PortfolioId> ordered, CancellationToken cancellationToken);

    /// <summary>The stocks of a portfolio by position.</summary>
    Task<IReadOnlyList<PortfolioMember>> MembersAsync(Guid userId, PortfolioId id, CancellationToken cancellationToken);

    /// <summary>
    /// Puts the user's watchlist stock in the portfolio, at the end, unless the portfolio already holds <paramref name="maxMembers"/> stocks (the configured
    /// limit, checked under the portfolio's lock so that parallel adds cannot both pass it; the database ceiling is the backstop behind it).
    /// </summary>
    Task<MemberAdd> AddMemberAsync(Guid userId, PortfolioId id, Guid stockId, int maxMembers, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Takes the stock out of the portfolio (it stays on the watchlist). Returns whether it was there.</summary>
    Task<bool> RemoveMemberAsync(Guid userId, PortfolioId id, Guid stockId, CancellationToken cancellationToken);

    /// <summary>Puts the stocks of the portfolio in this order. They must be exactly the portfolio's stocks; returns false when they are not.</summary>
    Task<bool> ReorderMembersAsync(Guid userId, PortfolioId id, IReadOnlyList<Guid> orderedStocks, CancellationToken cancellationToken);

    /// <summary>Which stocks would leave the watchlist if the portfolio were deleted.</summary>
    Task<DeletionPreview> PreviewDeletionAsync(Guid userId, PortfolioId id, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the portfolio and, when <paramref name="removeOrphans"/>, the watchlist items that no other portfolio holds, in the caller's
    /// transaction. Returns whether there was a portfolio.
    /// </summary>
    Task<bool> DeleteAsync(Guid userId, PortfolioId id, bool removeOrphans, CancellationToken cancellationToken);
}
