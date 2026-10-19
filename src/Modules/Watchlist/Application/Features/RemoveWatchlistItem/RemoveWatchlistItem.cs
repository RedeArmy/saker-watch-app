using FluentValidation;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.MarketData.Contracts;
using SakerWatch.Watchlist.Application.Features.PutWatchlistItem;

namespace SakerWatch.Watchlist.Application.Features.RemoveWatchlistItem;

/// <summary>
/// Takes a stock off the caller's watchlist, and out of every portfolio it was in. Removing a stock that is not there is not an error (a
/// <c>DELETE</c> repeated is the same state), and it never looks at the provider or registers anything.
/// </summary>
/// <param name="Market">The canonical market code.</param>
/// <param name="Ticker">The ticker on that market.</param>
public sealed record RemoveWatchlistItemCommand(string Market, string Ticker) : ICommand;

/// <summary>Reports a market or ticker that is empty or too long before anything is looked up.</summary>
public sealed class RemoveWatchlistItemValidator : AbstractValidator<RemoveWatchlistItemCommand>
{
    /// <summary>Creates the validator.</summary>
    public RemoveWatchlistItemValidator()
    {
        RuleFor(x => x.Market).NotEmpty().MaximumLength(4);
        RuleFor(x => x.Ticker).NotEmpty().MaximumLength(16);
    }
}

/// <summary>Finds the stock in the catalog and removes the caller's item for it.</summary>
public sealed class RemoveWatchlistItemHandler(ICurrentUser currentUser, IStockDirectory stocks, IWatchlistRepository items)
    : IRequestHandler<RemoveWatchlistItemCommand, Result>
{
    /// <inheritdoc />
    public async ValueTask<Result> Handle(RemoveWatchlistItemCommand request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Unauthorized(WatchlistErrors.NotAuthenticated, "Authentication is required.");
        }

        Result<StockSummary> stock = await stocks.FindAsync(request.Market, request.Ticker, cancellationToken);
        if (!stock.IsSuccess)
        {
            // A stock the catalog does not have cannot be on anyone's watchlist: there is nothing to remove. A malformed key is still refused.
            return stock.Error!.Type == ErrorType.NotFound ? Result.Success() : stock.Error;
        }

        await items.RemoveByStockAsync(userId, stock.Value.Id, cancellationToken);
        return Result.Success();
    }
}
