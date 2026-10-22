using FluentValidation;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.MarketData.Contracts;
using SakerWatch.Watchlist.Application.Features.PutWatchlistItem;
using SakerWatch.Watchlist.Domain.Portfolios;

namespace SakerWatch.Watchlist.Application.Features.Portfolios;

/// <summary>
/// Puts a stock of the caller's watchlist in a portfolio, at the end (a <c>PUT</c>: putting it again changes nothing). The stock must already be on
/// the watchlist; the portfolio only groups what the caller follows.
/// </summary>
/// <param name="PortfolioId">The portfolio.</param>
/// <param name="Market">The canonical market code of the stock.</param>
/// <param name="Ticker">The ticker on that market.</param>
public sealed record PutPortfolioItemCommand(Guid PortfolioId, string Market, string Ticker) : ICommand<PutPortfolioItemResult>;

/// <summary>The stock in the portfolio after a put and whether this call put it there.</summary>
/// <param name="Item">The stock in the portfolio.</param>
/// <param name="Created">True when the stock was not in the portfolio before.</param>
public sealed record PutPortfolioItemResult(PortfolioItemView Item, bool Created);

/// <summary>Takes a stock out of a portfolio; it stays on the watchlist. A stock that is not there is not an error.</summary>
/// <param name="PortfolioId">The portfolio.</param>
/// <param name="Market">The canonical market code of the stock.</param>
/// <param name="Ticker">The ticker on that market.</param>
public sealed record RemovePortfolioItemCommand(Guid PortfolioId, string Market, string Ticker) : ICommand;

/// <summary>One stock in an order.</summary>
/// <param name="Market">The canonical market code.</param>
/// <param name="Ticker">The ticker.</param>
public sealed record StockKey(string Market, string Ticker);

/// <summary>Puts the stocks of a portfolio in this order; they must be exactly the portfolio's stocks.</summary>
/// <param name="PortfolioId">The portfolio.</param>
/// <param name="Items">The stocks in the order wanted.</param>
public sealed record ReorderPortfolioItemsCommand(Guid PortfolioId, IReadOnlyList<StockKey> Items) : ICommand;

/// <summary>Reports a market or ticker that is empty or too long.</summary>
public sealed class PutPortfolioItemValidator : AbstractValidator<PutPortfolioItemCommand>
{
    /// <summary>Creates the validator.</summary>
    public PutPortfolioItemValidator()
    {
        RuleFor(x => x.PortfolioId).NotEmpty();
        RuleFor(x => x.Market).NotEmpty().MaximumLength(4);
        RuleFor(x => x.Ticker).NotEmpty().MaximumLength(16);
    }
}

/// <summary>Finds the stock in the catalog and puts it in the portfolio, when the caller's watchlist holds it.</summary>
public sealed class PutPortfolioItemHandler(ICurrentUser currentUser, IStockDirectory stocks, IPortfolioRepository portfolios, IOptions<WatchlistOptions> options, TimeProvider clock)
    : IRequestHandler<PutPortfolioItemCommand, Result<PutPortfolioItemResult>>
{
    /// <inheritdoc />
    public async ValueTask<Result<PutPortfolioItemResult>> Handle(PutPortfolioItemCommand request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Unauthorized(WatchlistErrors.NotAuthenticated, "Authentication is required.");
        }

        var id = PortfolioId.Create(request.PortfolioId);
        if (await portfolios.FindAsync(userId, id, cancellationToken) is null)
        {
            return PortfolioErrors.PortfolioNotFound();
        }

        Result<StockSummary> stock = await stocks.FindAsync(request.Market, request.Ticker, cancellationToken);
        if (!stock.IsSuccess)
        {
            // A stock the catalog does not have cannot be on a watchlist; a malformed key is still a validation failure.
            return stock.Error!.Type == ErrorType.NotFound ? NotOnWatchlist() : stock.Error;
        }

        MemberAdd added = await portfolios.AddMemberAsync(userId, id, stock.Value.Id, options.Value.MaxItemsPerPortfolio, clock.GetUtcNow(), cancellationToken);
        switch (added)
        {
            case MemberAdd.ItemNotOnWatchlist:
                return NotOnWatchlist();
            case MemberAdd.PortfolioNotFound:
                return PortfolioErrors.PortfolioNotFound();
            case MemberAdd.LimitReached:
                return Error.Conflict(PortfolioErrors.ItemLimitReached, "The portfolio holds the most stocks allowed. Remove one before adding another.");
            default:
                IReadOnlyList<PortfolioMember> members = await portfolios.MembersAsync(userId, id, cancellationToken);
                PortfolioMember member = members.First(m => m.StockId == stock.Value.Id);
                return new PutPortfolioItemResult(new PortfolioItemView(stock.Value, member.Position, member.AddedAt), added == MemberAdd.Added);
        }
    }

    private static Error NotOnWatchlist() =>
        Error.NotFound(PortfolioErrors.ItemNotOnWatchlist, "That stock is not on your watchlist. Add it to the watchlist first.");
}

/// <summary>Removes the membership.</summary>
public sealed class RemovePortfolioItemHandler(ICurrentUser currentUser, IStockDirectory stocks, IPortfolioRepository portfolios)
    : IRequestHandler<RemovePortfolioItemCommand, Result>
{
    /// <inheritdoc />
    public async ValueTask<Result> Handle(RemovePortfolioItemCommand request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Unauthorized(WatchlistErrors.NotAuthenticated, "Authentication is required.");
        }

        var id = PortfolioId.Create(request.PortfolioId);
        if (await portfolios.FindAsync(userId, id, cancellationToken) is null)
        {
            return PortfolioErrors.PortfolioNotFound();
        }

        Result<StockSummary> stock = await stocks.FindAsync(request.Market, request.Ticker, cancellationToken);
        if (!stock.IsSuccess)
        {
            return stock.Error!.Type == ErrorType.NotFound ? Result.Success() : stock.Error;
        }

        await portfolios.RemoveMemberAsync(userId, id, stock.Value.Id, cancellationToken);
        return Result.Success();
    }
}

/// <summary>Checks the size of the order.</summary>
public sealed class ReorderPortfolioItemsValidator : AbstractValidator<ReorderPortfolioItemsCommand>
{
    /// <summary>Creates the validator.</summary>
    public ReorderPortfolioItemsValidator()
    {
        RuleFor(x => x.PortfolioId).NotEmpty();
        RuleFor(x => x.Items).NotNull().Must(items => items.Count <= 100).WithMessage("A portfolio holds at most 100 stocks.");
    }
}

/// <summary>Reorders the stocks of a portfolio.</summary>
public sealed class ReorderPortfolioItemsHandler(ICurrentUser currentUser, IStockDirectory stocks, IPortfolioRepository portfolios)
    : IRequestHandler<ReorderPortfolioItemsCommand, Result>
{
    /// <inheritdoc />
    public async ValueTask<Result> Handle(ReorderPortfolioItemsCommand request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Unauthorized(WatchlistErrors.NotAuthenticated, "Authentication is required.");
        }

        var id = PortfolioId.Create(request.PortfolioId);
        if (await portfolios.FindAsync(userId, id, cancellationToken) is null)
        {
            return PortfolioErrors.PortfolioNotFound();
        }

        var ordered = new List<Guid>(request.Items.Count);
        foreach (StockKey key in request.Items)
        {
            Result<StockSummary> stock = await stocks.FindAsync(key.Market, key.Ticker, cancellationToken);
            if (!stock.IsSuccess)
            {
                return stock.Error!.Type == ErrorType.NotFound ? Mismatch() : stock.Error;
            }

            ordered.Add(stock.Value.Id);
        }

        return await portfolios.ReorderMembersAsync(userId, id, ordered, cancellationToken) ? Result.Success() : Mismatch();
    }

    private static Error Mismatch() => Error.Validation(PortfolioErrors.OrderMismatch, "The list must contain each stock of the portfolio exactly once.");
}
