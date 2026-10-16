using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using SakerWatch.BuildingBlocks.Application.Events;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.Identity.Application.Features.GetMe;

namespace SakerWatch.Contract.Tests;

/// <summary>What the API host composes from its module list: handlers, and the registries that jobs and events are resolved through.</summary>
public sealed class HostCompositionTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public HostCompositionTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task TheModuleListFeedsTheHandlersAndBothRegistries()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IServiceProvider services = scope.ServiceProvider;

        services.GetService<IRequestHandler<GetMeQuery, Result<MeProfile>>>().Should().NotBeNull("the Identity handlers are scanned");
        services.GetService<JobRegistry>().Should().NotBeNull("the API can enqueue jobs, so it must know their types");
        services.GetService<IntegrationEventRegistry>().Should().NotBeNull("the API writes integration events to the outbox");
    }

    [Fact]
    public async Task EveryRequestHandlerOfEveryModule_IsReachableFromTheApi()
    {
        string[] modules = ["Identity", "MarketData", "Watchlist", "Analysis", "Signals", "Calculator", "Retention"];
        var missing = new List<string>();
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();

        foreach (string module in modules)
        {
            var assembly = System.Reflection.Assembly.Load($"SakerWatch.{module}.Application");
            foreach (Type handler in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false }))
            {
                foreach (Type contract in handler.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)))
                {
                    if (scope.ServiceProvider.GetService(contract) is null)
                    {
                        missing.Add($"{handler.FullName} ({module})");
                    }
                }
            }
        }

        missing.Should().BeEmpty("a handler a module defines must be in the module list of Host.Api, or its endpoint fails the first time it is called");
    }
}
