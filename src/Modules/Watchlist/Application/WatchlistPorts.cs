using SakerWatch.Watchlist.Domain.Items;

namespace SakerWatch.Watchlist.Application;

/// <summary>Why an item could not be added.</summary>
public enum AddOutcome
{
    /// <summary>The item was stored.</summary>
    Added,

    /// <summary>The user already has that stock on the watchlist, for example because a parallel request added it first.</summary>
    AlreadyThere,

    /// <summary>The watchlist is full.</summary>
    LimitReached,
}

/// <summary>Storage of a user's watchlist items. Every call is scoped to the user it is given, and row-level security repeats the scope.</summary>
public interface IWatchlistRepository
{
    /// <summary>The user's item for a catalog stock, or <see langword="null"/>.</summary>
    Task<WatchlistItem?> FindByStockAsync(Guid userId, Guid stockId, CancellationToken cancellationToken);

    /// <summary>How many stocks the user has on the watchlist.</summary>
    Task<int> CountAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Stores a new item; a stock the user already has, or a full watchlist, is an outcome and not an exception.</summary>
    Task<AddOutcome> AddAsync(WatchlistItem item, CancellationToken cancellationToken);

    /// <summary>Stores the notes, the target price and the update date of an existing item.</summary>
    Task UpdateAsync(WatchlistItem item, CancellationToken cancellationToken);

    /// <summary>Removes the user's item for a stock, with its portfolio memberships. Returns whether there was one.</summary>
    Task<bool> RemoveByStockAsync(Guid userId, Guid stockId, CancellationToken cancellationToken);
}
