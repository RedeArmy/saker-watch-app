using SakerWatch.BuildingBlocks.Kernel.Domain;
using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.Watchlist.Domain.Items;

/// <summary>Identifier of a <see cref="WatchlistItem"/>: a UUIDv7 from the injected clock.</summary>
/// <param name="Value">The underlying UUID.</param>
public readonly record struct WatchlistItemId(Guid Value) : IStronglyTypedId<WatchlistItemId>
{
    /// <inheritdoc />
    public static WatchlistItemId Create(Guid value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// A stock on one user's watchlist with the user's own notes and an optional target price. The stock is a reference by id to the shared
/// catalog; there is at most one item per user and stock. A target price is a note the user keeps in the stock's currency, never an order.
/// </summary>
public sealed class WatchlistItem : AggregateRoot<WatchlistItemId>
{
    /// <summary>Longest accepted note.</summary>
    public const int MaxNotesLength = 2000;

    /// <summary>Decimal places a target price keeps (the column holds eight).</summary>
    public const int TargetPriceScale = 8;

    /// <summary>Largest target price, so the column (twenty digits, eight decimals) can always hold it.</summary>
    public const decimal MaxTargetPrice = 999_999_999_999m;

    private WatchlistItem(WatchlistItemId id, Guid userId, Guid stockId, DateTimeOffset createdAt)
        : base(id)
    {
        UserId = userId;
        StockId = stockId;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    /// <summary>The owner.</summary>
    public Guid UserId { get; }

    /// <summary>The catalog id of the stock (<c>marketdata.stocks</c>).</summary>
    public Guid StockId { get; }

    /// <summary>The user's notes, or <see langword="null"/>.</summary>
    public string? Notes { get; private set; }

    /// <summary>The price the user is waiting for, in the stock's currency, or <see langword="null"/>.</summary>
    public decimal? TargetPrice { get; private set; }

    /// <summary>When the stock was added.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>When the item last changed.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Puts a stock on a user's watchlist, with no notes and no target.</summary>
    public static Result<WatchlistItem> Add(WatchlistItemId id, Guid userId, Guid stockId, DateTimeOffset now)
    {
        if (userId == Guid.Empty || stockId == Guid.Empty)
        {
            return Error.Validation("watchlist.item.ids_required", "An item needs a user and a stock.");
        }

        return new WatchlistItem(id, userId, stockId, now);
    }

    /// <summary>
    /// Rebuilds an item from storage. The values were validated when they were written and the database repeats the checks, so nothing is
    /// validated again.
    /// </summary>
    public static WatchlistItem Restore(WatchlistItemId id, Guid userId, Guid stockId, string? notes, decimal? targetPrice, DateTimeOffset createdAt, DateTimeOffset updatedAt) =>
        new(id, userId, stockId, createdAt) { Notes = notes, TargetPrice = targetPrice, UpdatedAt = updatedAt };

    /// <summary>Normalizes the text of a note: trimmed, and <see langword="null"/> when nothing is left.</summary>
    public static Result<string?> NormalizeNotes(string? notes)
    {
        string? trimmed = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        return trimmed is { Length: > MaxNotesLength }
            ? Error.Validation("watchlist.item.notes_too_long", $"Notes have at most {MaxNotesLength} characters.")
            : Result.Success(trimmed);
    }

    /// <summary>Checks a target price: positive, within the column and with at most eight decimals. <see langword="null"/> clears it.</summary>
    public static Result<decimal?> ValidateTargetPrice(decimal? price)
    {
        if (price is null)
        {
            return Result.Success<decimal?>(null);
        }

        decimal value = price.Value;
        if (value <= 0 || value > MaxTargetPrice)
        {
            return Error.Validation("watchlist.item.target_price_invalid", "A target price is greater than zero and below one trillion.");
        }

        return decimal.Round(value, TargetPriceScale) == value
            ? Result.Success<decimal?>(value)
            : Error.Validation("watchlist.item.target_price_precision", $"A target price has at most {TargetPriceScale} decimals.");
    }

    /// <summary>Replaces the notes and the target price. Both are validated first and nothing changes when either is refused.</summary>
    public Result Update(string? notes, decimal? targetPrice, DateTimeOffset now)
    {
        Result<string?> normalizedNotes = NormalizeNotes(notes);
        if (!normalizedNotes.IsSuccess)
        {
            return normalizedNotes.Error!;
        }

        Result<decimal?> validatedPrice = ValidateTargetPrice(targetPrice);
        if (!validatedPrice.IsSuccess)
        {
            return validatedPrice.Error!;
        }

        if (normalizedNotes.Value == Notes && validatedPrice.Value == TargetPrice)
        {
            return Result.Success();
        }

        Notes = normalizedNotes.Value;
        TargetPrice = validatedPrice.Value;
        UpdatedAt = now;
        return Result.Success();
    }
}
