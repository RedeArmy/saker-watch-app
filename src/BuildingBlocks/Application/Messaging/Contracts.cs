using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.BuildingBlocks.Application.Messaging;

/// <summary>A use-case request. Every request produces a <see cref="Result"/> or <see cref="Result{T}"/>.</summary>
/// <typeparam name="TResponse">The result type returned to the caller.</typeparam>
public interface IRequest<TResponse>
    where TResponse : IResult<TResponse>;

/// <summary>Marks requests that change state: the pipeline runs them in one unit of work (a database transaction).</summary>
public interface ITransactionalRequest;

/// <summary>A state-changing request without a value (CQRS command).</summary>
public interface ICommand : IRequest<Result>, ITransactionalRequest;

/// <summary>A state-changing request that returns <typeparamref name="T"/> (CQRS command).</summary>
/// <typeparam name="T">The success value.</typeparam>
public interface ICommand<T> : IRequest<Result<T>>, ITransactionalRequest;

/// <summary>A read-only request that returns <typeparamref name="T"/> (CQRS query).</summary>
/// <typeparam name="T">The success value.</typeparam>
public interface IQuery<T> : IRequest<Result<T>>;

/// <summary>Handles exactly one request type; lives in the use case's vertical slice.</summary>
/// <typeparam name="TRequest">The request handled.</typeparam>
/// <typeparam name="TResponse">The result type returned.</typeparam>
public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : IResult<TResponse>
{
    /// <summary>Executes the use case. Expected failures are returned, not thrown.</summary>
    ValueTask<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}

/// <summary>The rest of the pipeline after a behavior: the next behavior, or finally the handler.</summary>
/// <typeparam name="TResponse">The result type.</typeparam>
public delegate ValueTask<TResponse> PipelineNext<TResponse>()
    where TResponse : IResult<TResponse>;

/// <summary>Cross-cutting step wrapped around every use case (tracing, logging, validation, idempotency...).</summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The result type.</typeparam>
public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : IResult<TResponse>
{
    /// <summary>Runs this step and calls <paramref name="next"/> to continue the pipeline (or short-circuits).</summary>
    ValueTask<TResponse> Handle(TRequest request, PipelineNext<TResponse> next, CancellationToken cancellationToken);
}

/// <summary>Entry point used by endpoints and jobs to run a use case through the pipeline.</summary>
public interface ISender
{
    /// <summary>Runs <paramref name="request"/> through the behaviors and its handler.</summary>
    ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        where TResponse : IResult<TResponse>;
}
