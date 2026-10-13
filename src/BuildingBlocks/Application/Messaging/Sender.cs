using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.BuildingBlocks.Application.Messaging;

/// <summary>Default <see cref="ISender"/>: resolves the handler and the behaviors from DI and chains them.</summary>
/// <param name="services">Scope that resolves handlers and behaviors.</param>
internal sealed class Sender(IServiceProvider services) : ISender
{
    private static readonly ConcurrentDictionary<Type, object> _invokers = new();

    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        where TResponse : IResult<TResponse>
    {
        ArgumentNullException.ThrowIfNull(request);

        var invoker = (IInvoker<TResponse>)_invokers.GetOrAdd(request.GetType(), CreateInvoker<TResponse>);
        return invoker.Invoke(request, services, cancellationToken);
    }

    private static object CreateInvoker<TResponse>(Type requestType)
        where TResponse : IResult<TResponse> =>
        Activator.CreateInstance(typeof(Invoker<,>).MakeGenericType(requestType, typeof(TResponse)))!;

    private interface IInvoker<TResponse>
        where TResponse : IResult<TResponse>
    {
        ValueTask<TResponse> Invoke(IRequest<TResponse> request, IServiceProvider services, CancellationToken cancellationToken);
    }

    private sealed class Invoker<TRequest, TResponse> : IInvoker<TResponse>
        where TRequest : IRequest<TResponse>
        where TResponse : IResult<TResponse>
    {
        public ValueTask<TResponse> Invoke(IRequest<TResponse> request, IServiceProvider services, CancellationToken cancellationToken)
        {
            var typed = (TRequest)request;
            var handler = services.GetRequiredService<IRequestHandler<TRequest, TResponse>>();

            PipelineNext<TResponse> pipeline = () => handler.Handle(typed, cancellationToken);

            // Behaviors are registered outermost-first; wrap from the innermost outwards.
            foreach (IPipelineBehavior<TRequest, TResponse> behavior in services.GetServices<IPipelineBehavior<TRequest, TResponse>>().Reverse())
            {
                PipelineNext<TResponse> next = pipeline;
                pipeline = () => behavior.Handle(typed, next, cancellationToken);
            }

            return pipeline();
        }
    }
}
