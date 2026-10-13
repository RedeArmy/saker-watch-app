using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using SakerWatch.BuildingBlocks.Application;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.Demo.Features;

namespace SakerWatch.Application.Tests.Pipeline;

/// <summary>Builds a real DI container with the production pipeline plus two ordering probes.</summary>
internal sealed class PipelineTestHost : IDisposable
{
    private readonly ServiceProvider _provider;

    public PipelineTestHost()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddFakeLogging());
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton(Log);
        services.AddApplicationPipeline(typeof(PingCommand).Assembly);

        // Registered after the standard behaviors, so these run closest to the handler (A outer, B inner).
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ProbeA<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ProbeB<,>));
        _provider = services.BuildServiceProvider(validateScopes: true);
    }

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));

    public CallLog Log { get; } = new();

    public FakeLogCollector Logs => _provider.GetRequiredService<FakeLogCollector>();

    public async ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request)
        where TResponse : IResult<TResponse>
    {
        await using AsyncServiceScope scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request, CancellationToken.None);
    }

    public void Dispose() => _provider.Dispose();

    private sealed class ProbeA<TRequest, TResponse>(CallLog log) : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
        where TResponse : IResult<TResponse>
    {
        public async ValueTask<TResponse> Handle(TRequest request, PipelineNext<TResponse> next, CancellationToken cancellationToken)
        {
            log.Add("A:before");
            TResponse response = await next();
            log.Add("A:after");
            return response;
        }
    }

    private sealed class ProbeB<TRequest, TResponse>(CallLog log) : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
        where TResponse : IResult<TResponse>
    {
        public async ValueTask<TResponse> Handle(TRequest request, PipelineNext<TResponse> next, CancellationToken cancellationToken)
        {
            log.Add("B:before");
            TResponse response = await next();
            log.Add("B:after");
            return response;
        }
    }
}
