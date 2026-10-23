using FluentValidation;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Application.Idempotency;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Kernel.Time;
using SakerWatch.MarketData.Contracts;
using SakerWatch.Watchlist.Application.Features.PutWatchlistItem;
using SakerWatch.Watchlist.Domain;
using SakerWatch.Watchlist.Domain.Portfolios;

namespace SakerWatch.Watchlist.Application.Features.Portfolios;

/// <summary>Creates a portfolio at the end of the caller's list. A retried request (same <c>Idempotency-Key</c>) returns the first result.</summary>
/// <param name="Name">The name; spaces at both ends are removed, 1 to 60 characters, unique ignoring letter case.</param>
/// <param name="Description">An optional description, up to 500 characters.</param>
/// <param name="IdempotencyKey">Key and scope of this attempt.</param>
public sealed record CreatePortfolioCommand(string? Name, string? Description, IdempotencyKeyInfo IdempotencyKey) : ICommand<PortfolioView>, IIdempotentRequest;

/// <summary>Renames and describes a portfolio (a <c>PUT</c>: both fields replace what is stored).</summary>
/// <param name="Id">The portfolio.</param>
/// <param name="Name">The new name.</param>
/// <param name="Description">The new description, or <see langword="null"/> to clear it.</param>
public sealed record UpdatePortfolioCommand(Guid Id, string? Name, string? Description) : ICommand<PortfolioView>;

/// <summary>Puts the caller's portfolios in this order; the ids must be exactly the caller's portfolios.</summary>
/// <param name="PortfolioIds">The portfolio ids in the order wanted.</param>
public sealed record ReorderPortfoliosCommand(IReadOnlyList<Guid> PortfolioIds) : ICommand;

/// <summary>The caller's portfolios by position.</summary>
public sealed record ListPortfoliosQuery : IQuery<IReadOnlyList<PortfolioView>>, IReadsDatabase;

/// <summary>One portfolio with its stocks.</summary>
/// <param name="Id">The portfolio.</param>
public sealed record GetPortfolioQuery(Guid Id) : IQuery<PortfolioDetail>, IReadsDatabase;

/// <summary>What deleting a portfolio would take off the watchlist.</summary>
/// <param name="Portfolio">The portfolio.</param>
/// <param name="StocksToRemove">The stocks that are in it and in no other portfolio: they would leave the watchlist, with their notes and targets.</param>
/// <param name="StocksKept">How many of its stocks would stay on the watchlist because another portfolio holds them too.</param>
/// <param name="RemovalToken">A token of exactly these stocks: the delete that confirms them sends it back, and is refused if the stocks changed since.</param>
public sealed record PortfolioDeletionPreview(PortfolioView Portfolio, IReadOnlyList<StockSummary> StocksToRemove, int StocksKept, string RemovalToken);

/// <summary>Lists what a deletion would remove, before the caller confirms it.</summary>
/// <param name="Id">The portfolio.</param>
public sealed record PreviewPortfolioDeletionQuery(Guid Id) : IQuery<PortfolioDeletionPreview>, IReadsDatabase;

/// <summary>
/// Deletes a portfolio. The stocks that would be left in no portfolio leave the watchlist too (ADR-0026), so the caller must confirm when there are
/// any; a portfolio whose stocks are all held by others, or empty, is deleted without it. Deleting one that is not there is not an error.
/// </summary>
/// <param name="Id">The portfolio.</param>
/// <param name="Confirm">True to accept that the stocks listed by the preview leave the watchlist.</param>
/// <param name="RemovalToken">The token of the preview that was shown: what is confirmed is exactly those stocks. Needed with <paramref name="Confirm"/> when any would leave.</param>
public sealed record DeletePortfolioCommand(Guid Id, bool Confirm, string? RemovalToken = null) : ICommand;

/// <summary>Reports the fields of a create that are wrong, by name.</summary>
public sealed class CreatePortfolioValidator : AbstractValidator<CreatePortfolioCommand>
{
    /// <summary>Creates the validator.</summary>
    public CreatePortfolioValidator()
    {
        RuleFor(x => x.Name).NotEmpty().Must(name => (name ?? string.Empty).Trim().Length is >= 1 and <= PortfolioName.MaxLength).WithMessage($"A name has 1 to {PortfolioName.MaxLength} characters.");
    }
}

/// <summary>Creates the portfolio at the end of the list.</summary>
public sealed class CreatePortfolioHandler(ICurrentUser currentUser, IPortfolioRepository portfolios, IIdGenerator ids, IOptions<WatchlistOptions> options, TimeProvider clock)
    : IRequestHandler<CreatePortfolioCommand, Result<PortfolioView>>
{
    /// <inheritdoc />
    public async ValueTask<Result<PortfolioView>> Handle(CreatePortfolioCommand request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Unauthorized(WatchlistErrors.NotAuthenticated, "Authentication is required.");
        }

        // The next position is read from the list, so parallel creations of one user are queued before they read it.
        await portfolios.LockOrderAsync(userId, cancellationToken);
        IReadOnlyList<PortfolioSummary> existing = await portfolios.ListAsync(userId, cancellationToken);
        if (existing.Count >= options.Value.MaxPortfolios)
        {
            return PortfolioErrors.PortfolioLimit();
        }

        int position = existing.Count == 0 ? 0 : existing.Max(summary => summary.Portfolio.Position) + 1;
        Result<Portfolio> created = Portfolio.Create(ids.New<PortfolioId>(), userId, request.Name, request.Description, position, clock.GetUtcNow());
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        return await portfolios.AddAsync(created.Value, cancellationToken) switch
        {
            PortfolioWrite.Done => PortfolioView.Of(new PortfolioSummary(created.Value, 0)),
            PortfolioWrite.NameTaken => PortfolioErrors.PortfolioNameTaken(),
            _ => PortfolioErrors.PortfolioLimit(),
        };
    }
}

/// <summary>Reports the fields of an update that are wrong, by name.</summary>
public sealed class UpdatePortfolioValidator : AbstractValidator<UpdatePortfolioCommand>
{
    /// <summary>Creates the validator.</summary>
    public UpdatePortfolioValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().Must(name => (name ?? string.Empty).Trim().Length is >= 1 and <= PortfolioName.MaxLength).WithMessage($"A name has 1 to {PortfolioName.MaxLength} characters.");
    }
}

/// <summary>Renames and describes the portfolio.</summary>
public sealed class UpdatePortfolioHandler(ICurrentUser currentUser, IPortfolioRepository portfolios, TimeProvider clock)
    : IRequestHandler<UpdatePortfolioCommand, Result<PortfolioView>>
{
    /// <inheritdoc />
    public async ValueTask<Result<PortfolioView>> Handle(UpdatePortfolioCommand request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Unauthorized(WatchlistErrors.NotAuthenticated, "Authentication is required.");
        }

        if (await portfolios.FindAsync(userId, PortfolioId.Create(request.Id), cancellationToken) is not { } found)
        {
            return PortfolioErrors.PortfolioNotFound();
        }

        (string beforeName, string? beforeDescription) = (found.Portfolio.Name.Value, found.Portfolio.Description);
        Result edited = found.Portfolio.Edit(request.Name, request.Description, clock.GetUtcNow());
        if (!edited.IsSuccess)
        {
            return edited.Error!;
        }

        // Compared by value and not by date: the clock may not have moved since the portfolio was last written.
        if (found.Portfolio.Name.Value != beforeName || found.Portfolio.Description != beforeDescription)
        {
            PortfolioWrite write = await portfolios.UpdateAsync(found.Portfolio, cancellationToken);
            if (write == PortfolioWrite.NameTaken)
            {
                return PortfolioErrors.PortfolioNameTaken();
            }

            if (write == PortfolioWrite.NotFound)
            {
                return PortfolioErrors.PortfolioNotFound();
            }
        }

        return PortfolioView.Of(found);
    }
}

/// <summary>Checks that the order names every portfolio once.</summary>
public sealed class ReorderPortfoliosValidator : AbstractValidator<ReorderPortfoliosCommand>
{
    /// <summary>Creates the validator.</summary>
    public ReorderPortfoliosValidator()
    {
        RuleFor(x => x.PortfolioIds).NotNull().Must(ids => ids.Count <= LimitCeilings.Portfolios).WithMessage($"There are at most {LimitCeilings.Portfolios} portfolios to order.");
    }
}

/// <summary>Reorders the portfolios in one statement.</summary>
public sealed class ReorderPortfoliosHandler(ICurrentUser currentUser, IPortfolioRepository portfolios) : IRequestHandler<ReorderPortfoliosCommand, Result>
{
    /// <inheritdoc />
    public async ValueTask<Result> Handle(ReorderPortfoliosCommand request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Unauthorized(WatchlistErrors.NotAuthenticated, "Authentication is required.");
        }

        return await portfolios.ReorderAsync(userId, [.. request.PortfolioIds.Select(PortfolioId.Create)], cancellationToken)
            ? Result.Success()
            : Error.Validation(PortfolioErrors.OrderMismatch, "The list must contain each of your portfolios exactly once.");
    }
}

/// <summary>Lists the portfolios.</summary>
public sealed class ListPortfoliosHandler(ICurrentUser currentUser, IPortfolioRepository portfolios) : IRequestHandler<ListPortfoliosQuery, Result<IReadOnlyList<PortfolioView>>>
{
    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<PortfolioView>>> Handle(ListPortfoliosQuery request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Unauthorized(WatchlistErrors.NotAuthenticated, "Authentication is required.");
        }

        IReadOnlyList<PortfolioSummary> all = await portfolios.ListAsync(userId, cancellationToken);
        return Result.Success<IReadOnlyList<PortfolioView>>([.. all.Select(PortfolioView.Of)]);
    }
}

/// <summary>Reads one portfolio with its stocks.</summary>
public sealed class GetPortfolioHandler(ICurrentUser currentUser, IPortfolioRepository portfolios, IMarketSnapshots market)
    : IRequestHandler<GetPortfolioQuery, Result<PortfolioDetail>>
{
    /// <inheritdoc />
    public async ValueTask<Result<PortfolioDetail>> Handle(GetPortfolioQuery request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Unauthorized(WatchlistErrors.NotAuthenticated, "Authentication is required.");
        }

        var id = PortfolioId.Create(request.Id);
        if (await portfolios.FindAsync(userId, id, cancellationToken) is not { } found)
        {
            return PortfolioErrors.PortfolioNotFound();
        }

        IReadOnlyList<PortfolioMember> members = await portfolios.MembersAsync(userId, id, cancellationToken);
        IReadOnlyDictionary<Guid, StockSummary> stocks = await market.FindManyAsync([.. members.Select(member => member.StockId)], cancellationToken);
        PortfolioItemView[] items = [.. members.Where(member => stocks.ContainsKey(member.StockId)).Select(member => new PortfolioItemView(stocks[member.StockId], member.Position, member.AddedAt))];
        return new PortfolioDetail(PortfolioView.Of(found), items);
    }
}

/// <summary>Lists the stocks a deletion would take off the watchlist.</summary>
public sealed class PreviewPortfolioDeletionHandler(ICurrentUser currentUser, IPortfolioRepository portfolios, IMarketSnapshots market)
    : IRequestHandler<PreviewPortfolioDeletionQuery, Result<PortfolioDeletionPreview>>
{
    /// <inheritdoc />
    public async ValueTask<Result<PortfolioDeletionPreview>> Handle(PreviewPortfolioDeletionQuery request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Unauthorized(WatchlistErrors.NotAuthenticated, "Authentication is required.");
        }

        var id = PortfolioId.Create(request.Id);
        if (await portfolios.FindAsync(userId, id, cancellationToken) is not { } found)
        {
            return PortfolioErrors.PortfolioNotFound();
        }

        DeletionPreview preview = await portfolios.PreviewDeletionAsync(userId, id, cancellationToken);
        IReadOnlyDictionary<Guid, StockSummary> stocks = await market.FindManyAsync(preview.StocksToRemove, cancellationToken);
        StockSummary[] toRemove = [.. preview.StocksToRemove.Where(stocks.ContainsKey).Select(stockId => stocks[stockId]).OrderBy(stock => stock.Ticker, StringComparer.Ordinal)];
        return new PortfolioDeletionPreview(PortfolioView.Of(found), toRemove, preview.StocksKept, preview.Token);
    }
}

/// <summary>Checks the form of the token of a confirmation.</summary>
public sealed partial class DeletePortfolioValidator : AbstractValidator<DeletePortfolioCommand>
{
    /// <summary>Creates the validator.</summary>
    public DeletePortfolioValidator() =>
        RuleFor(x => x.RemovalToken).Must(token => token is null || Token().IsMatch(token)).WithMessage("The removal token is the one of the deletion preview: sixteen hex digits.");

    [System.Text.RegularExpressions.GeneratedRegex(@"^[0-9a-f]{16}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial System.Text.RegularExpressions.Regex Token();
}

/// <summary>
/// Deletes the portfolio, and the stocks only it held when the caller confirmed. A delete that cannot go ahead because the caller has not yet named what would
/// leave the watchlist answers with that very list and its token, in the same response, so the confirmation takes one request more and not two (ADR-0049).
/// </summary>
public sealed class DeletePortfolioHandler(ICurrentUser currentUser, IPortfolioRepository portfolios, IMarketSnapshots market) : IRequestHandler<DeletePortfolioCommand, Result>
{
    /// <inheritdoc />
    public async ValueTask<Result> Handle(DeletePortfolioCommand request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Unauthorized(WatchlistErrors.NotAuthenticated, "Authentication is required.");
        }

        var id = PortfolioId.Create(request.Id);
        if (!await portfolios.ExistsAsync(userId, id, cancellationToken))
        {
            return Result.Success();
        }

        DeletionPreview preview = await portfolios.PreviewDeletionAsync(userId, id, cancellationToken);
        if (preview.StocksToRemove.Count > 0 && !request.Confirm)
        {
            return await NamedAsync(
                PortfolioErrors.DeleteNeedsConfirmation,
                $"Deleting this portfolio removes {preview.StocksToRemove.Count} stock(s) from your watchlist. Repeat the request with confirm=true and the removalToken of this answer to accept exactly these.",
                preview,
                cancellationToken);
        }

        if (request.Confirm && request.RemovalToken is null && preview.StocksToRemove.Count > 0)
        {
            return await NamedAsync(
                PortfolioErrors.RemovalTokenRequired,
                "A confirmed delete carries the removalToken of the stocks it removes, so that exactly the stocks you saw are the ones removed. This answer has them and the token.",
                preview,
                cancellationToken);
        }

        // A confirmation is of the set the caller saw. Without a token, and with nothing to remove when it was looked at, it is of the empty set: a stock put in the
        // portfolio since would be removed unseen, so it is refused too.
        string? expected = request.Confirm ? request.RemovalToken ?? DeletionPreview.TokenOf([]) : null;
        PortfolioDelete deleted = await portfolios.DeleteAsync(userId, id, removeOrphans: request.Confirm, expected, cancellationToken);
        return deleted == PortfolioDelete.RemovalChanged
            ? await NamedAsync(
                PortfolioErrors.RemovalChanged,
                "The stocks that would leave your watchlist changed since you looked. This answer has the ones that would leave now and their token.",
                await portfolios.PreviewDeletionAsync(userId, id, cancellationToken),
                cancellationToken)
            : Result.Success();
    }

    // The conflict that carries what would leave (by ticker) and the token that confirms exactly that.
    private async Task<Error> NamedAsync(string code, string message, DeletionPreview preview, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Guid, StockSummary> stocks = await market.FindManyAsync(preview.StocksToRemove, cancellationToken);
        StockSummary[] toRemove = [.. preview.StocksToRemove.Where(stocks.ContainsKey).Select(stockId => stocks[stockId]).OrderBy(stock => stock.Ticker, StringComparer.Ordinal)];
        return Error.Conflict(code, message, new Dictionary<string, object?> { ["stocksToRemove"] = toRemove, ["removalToken"] = preview.Token });
    }
}
