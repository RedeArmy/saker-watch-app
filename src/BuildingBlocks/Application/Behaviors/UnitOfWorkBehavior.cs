using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Transactions;

namespace SakerWatch.BuildingBlocks.Application.Behaviors;

/// <summary>
/// Runs every command (<see cref="ITransactionalRequest"/>) inside one unit of work: everything the handler writes
/// commits together on success and rolls back together on any failure result or exception. A query passes through, unless it is marked
/// <see cref="IReadsDatabase"/>: it then gets the same unit of work, so its raw SQL runs on an open connection with the user set.
/// It runs after validation, so invalid requests never open a transaction.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The result type.</typeparam>
/// <param name="unitOfWork">The scope's unit of work.</param>
public sealed class UnitOfWorkBehavior<TRequest, TResponse>(IUnitOfWork unitOfWork) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : IResult<TResponse>
{
    /// <inheritdoc />
    public async ValueTask<TResponse> Handle(TRequest request, PipelineNext<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not ITransactionalRequest and not IReadsDatabase)
        {
            return await next();
        }

        await unitOfWork.BeginAsync(cancellationToken);
        try
        {
            TResponse response = await next();
            if (response.IsSuccess)
            {
                await unitOfWork.CommitAsync(cancellationToken);
            }
            else
            {
                await unitOfWork.RollbackAsync(cancellationToken);
            }

            return response;
        }
        catch
        {
            // The caller's token may be the reason we are here; the rollback must still happen.
            await unitOfWork.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
