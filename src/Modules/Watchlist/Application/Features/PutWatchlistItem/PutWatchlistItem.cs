using FluentValidation;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Kernel.Time;
using SakerWatch.BuildingBlocks.Kernel.Transactions;
using SakerWatch.MarketData.Contracts;
using SakerWatch.Watchlist.Domain.Items;

namespace SakerWatch.Watchlist.Application.Features.PutWatchlistItem;

/// <summary>
/// Puts a stock on the caller's watchlist with these notes and this target, or replaces the notes and the target of the item already there
/// (a <c>PUT</c>: the same request twice is the same state, so it needs no idempotency key). A stock the catalog does not have yet is registered
/// first, from the provider's answer for the exact market and ticker (ADR-0032).
/// </summary>
/// <param name="Market">The canonical market code (<c>XNAS</c>).</param>
/// <param name="Ticker">The ticker on that market.</param>
/// <param name="Notes">The user's notes, or <see langword="null"/>.</param>
/// <param name="TargetPrice">The target price in the stock's currency, or <see langword="null"/>.</param>
public sealed record PutWatchlistItemCommand(string Market, string Ticker, string? Notes, decimal? TargetPrice) : ISelfTransactingCommand<PutWatchlistItemResult>;

/// <summary>A watchlist item with the stock it points to.</summary>
/// <param name="Id">The item id.</param>
/// <param name="Stock">The catalog stock.</param>
/// <param name="Notes">The user's notes, or <see langword="null"/>.</param>
/// <param name="TargetPrice">The target price, or <see langword="null"/>.</param>
/// <param name="CreatedAt">When the stock was added.</param>
/// <param name="UpdatedAt">When the item last changed.</param>
public sealed record WatchlistItemView(Guid Id, StockSummary Stock, string? Notes, decimal? TargetPrice, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>The item after a put and whether this call created it.</summary>
/// <param name="Item">The item.</param>
/// <param name="Created">True when the stock was not on the watchlist before.</param>
public sealed record PutWatchlistItemResult(WatchlistItemView Item, bool Created);

/// <summary>Errors of the watchlist use cases.</summary>
public static class WatchlistErrors
{
    /// <summary>The caller is not authenticated.</summary>
    public const string NotAuthenticated = "watchlist.not_authenticated";

    /// <summary>The watchlist already holds the most stocks a user may keep.</summary>
    public const string LimitReached = "watchlist.limit_reached";
}

/// <summary>Reports every problem of the request at once, by field, before anything is looked up or stored.</summary>
public sealed class PutWatchlistItemValidator : AbstractValidator<PutWatchlistItemCommand>
{
    /// <summary>Creates the validator.</summary>
    public PutWatchlistItemValidator()
    {
        RuleFor(x => x.Market).NotEmpty().MaximumLength(4);
        RuleFor(x => x.Ticker).NotEmpty().MaximumLength(16);
        // The length of what is stored: the note is trimmed first, so spaces around a note that fits do not make it too long.
        RuleFor(x => x.Notes).Must(notes => notes is null || notes.Trim().Length <= WatchlistItem.MaxNotesLength)
            .WithMessage($"Notes have at most {WatchlistItem.MaxNotesLength} characters.");
        RuleFor(x => x.TargetPrice)
            .GreaterThan(0m).LessThanOrEqualTo(WatchlistItem.MaxTargetPrice)
            .When(x => x.TargetPrice is not null);
    }
}

/// <summary>
/// Registers the stock if needed, then adds or updates the item in a unit of work of its own. Registering a stock asks a provider, which can
/// take as long as the client's timeout, so the pipeline does not hold a connection and a transaction around it (ADR-0044): the stock is
/// found or registered first, in short units of work of its own, and the item is written in one that opens after. If that write then fails, the
/// registered stock stays in the catalog: it is valid and shared, it is bounded by the daily cap of the user and the next user finds it ready (ADR-0047).
/// </summary>
public sealed class PutWatchlistItemHandler(
    ICurrentUser currentUser,
    IStockDirectory stocks,
    IWatchlistRepository items,
    IIdGenerator ids,
    IOptions<WatchlistOptions> options,
    IUnitOfWork unit,
    TimeProvider clock) : IRequestHandler<PutWatchlistItemCommand, Result<PutWatchlistItemResult>>
{
    /// <inheritdoc />
    public async ValueTask<Result<PutWatchlistItemResult>> Handle(PutWatchlistItemCommand request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Unauthorized(WatchlistErrors.NotAuthenticated, "Authentication is required.");
        }

        // Both are checked before the stock is looked up, so a bad note never costs a provider search or a catalog row.
        Result<string?> notes = WatchlistItem.NormalizeNotes(request.Notes);
        if (!notes.IsSuccess)
        {
            return notes.Error!;
        }

        Result<decimal?> target = WatchlistItem.ValidateTargetPrice(request.TargetPrice);
        if (!target.IsSuccess)
        {
            return target.Error!;
        }

        Result<StockSummary> stock = await ResolveStockAsync(request, userId, cancellationToken);
        if (!stock.IsSuccess)
        {
            return stock.Error!;
        }

        await unit.BeginAsync(cancellationToken);
        try
        {
            Result<PutWatchlistItemResult> written = await WriteAsync(userId, stock.Value, notes.Value, target.Value, cancellationToken);
            if (written.IsSuccess)
            {
                await unit.CommitAsync(cancellationToken);
            }
            else
            {
                await unit.RollbackAsync(cancellationToken);
            }

            return written;
        }
        catch
        {
            await unit.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    // A stock the catalog has is found with one read and needs no provider, so the common request opens two units of work in all (this one and the write). Only a stock
    // the catalog does not have goes on: a full watchlist can still edit a stock it holds, but cannot take a new one, so it is refused here, before a provider is asked and
    // a catalog row is made for a stock that cannot be added.
    private async Task<Result<StockSummary>> ResolveStockAsync(PutWatchlistItemCommand request, Guid userId, CancellationToken cancellationToken)
    {
        Result<StockSummary> found = await stocks.FindAsync(request.Market, request.Ticker, cancellationToken);
        if (found.IsSuccess || found.Error is not { Type: ErrorType.NotFound })
        {
            return found;
        }

        return await IsFullAsync(userId, cancellationToken)
            ? LimitReached()
            : await stocks.EnsureAsync(request.Market, request.Ticker, cancellationToken);
    }

    private async Task<bool> IsFullAsync(Guid userId, CancellationToken cancellationToken)
    {
        await unit.BeginAsync(cancellationToken);
        try
        {
            bool full = await items.CountAsync(userId, cancellationToken) >= options.Value.MaxItems;
            await unit.CommitAsync(cancellationToken);
            return full;
        }
        catch
        {
            await unit.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<Result<PutWatchlistItemResult>> WriteAsync(Guid userId, StockSummary stock, string? notesText, decimal? targetPrice, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        if (await items.FindByStockAsync(userId, stock.Id, cancellationToken) is { } existing)
        {
            return await UpdateAsync(existing, stock, notesText, targetPrice, now, cancellationToken);
        }

        if (await items.CountAsync(userId, cancellationToken) >= options.Value.MaxItems)
        {
            return LimitReached();
        }

        Result<WatchlistItem> created = WatchlistItem.Add(ids.New<WatchlistItemId>(), userId, stock.Id, now);
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        Result applied = created.Value.Update(notesText, targetPrice, now);
        if (!applied.IsSuccess)
        {
            return applied.Error!;
        }

        switch (await items.AddAsync(created.Value, cancellationToken))
        {
            case AddOutcome.LimitReached:
                return LimitReached();
            case AddOutcome.AlreadyThere:
                // A parallel request put the same stock there between our read and our insert: the end state is the same, so update that one.
                WatchlistItem? winner = await items.FindByStockAsync(userId, stock.Id, cancellationToken);
                return winner is null
                    ? Error.Conflict("watchlist.item_changed", "The watchlist changed while the request ran. Try again.")
                    : await UpdateAsync(winner, stock, notesText, targetPrice, now, cancellationToken);
            default:
                return new PutWatchlistItemResult(ToView(created.Value, stock), Created: true);
        }
    }

    private static Error LimitReached() =>
        Error.Conflict(WatchlistErrors.LimitReached, "The watchlist is full. Remove a stock before adding another.");

    private static WatchlistItemView ToView(WatchlistItem item, StockSummary stock) =>
        new(item.Id.Value, stock, item.Notes, item.TargetPrice, item.CreatedAt, item.UpdatedAt);

    private async Task<Result<PutWatchlistItemResult>> UpdateAsync(
        WatchlistItem item, StockSummary stock, string? notes, decimal? target, DateTimeOffset now, CancellationToken cancellationToken)
    {
        (string? notesBefore, decimal? targetBefore) = (item.Notes, item.TargetPrice);
        Result applied = item.Update(notes, target, now);
        if (!applied.IsSuccess)
        {
            return applied.Error!;
        }

        // Compared by value and not by the update date: the clock may not have moved since the item was last written.
        if (item.Notes != notesBefore || item.TargetPrice != targetBefore)
        {
            await items.UpdateAsync(item, cancellationToken);
        }

        return new PutWatchlistItemResult(ToView(item, stock), Created: false);
    }
}
