using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using SakerWatch.BuildingBlocks.Application;
using SakerWatch.BuildingBlocks.Application.Events;
using SakerWatch.BuildingBlocks.Application.Idempotency;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.Demo.Features;

namespace SakerWatch.Application.Tests.Pipeline;

/// <summary>The Add... methods can be called twice (by a host and by a module, or by a test) without duplicating anything.</summary>
public sealed class ServiceRegistrationTests
{
    private static readonly System.Reflection.Assembly _demo = typeof(PingHandler).Assembly;
    private static readonly System.Reflection.Assembly _noJobsOrEvents = typeof(Result).Assembly;

    private static int Descriptors(IServiceCollection services, Type serviceType) => services.Count(d => d.ServiceType == serviceType);

    [Fact]
    public void AddApplicationPipeline_CalledTwice_RegistersEachBehaviorAndHandlerOnce()
    {
        var services = new ServiceCollection();

        services.AddApplicationPipeline(_demo);
        services.AddApplicationPipeline(_demo);
        services.AddIdempotency(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddIdempotency(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        Descriptors(services, typeof(IPipelineBehavior<,>)).Should().Be(5, "tracing, logging, validation, unit of work and idempotency, once each");
        Descriptors(services, typeof(IRequestHandler<PingCommand, Result<string>>)).Should().Be(1);
        Descriptors(services, typeof(ISender)).Should().Be(1);
    }

    [Fact]
    public void AHandlerThatIsItselfGeneric_IsSkippedByTheScan_AndDoesNotStopTheHost()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddApplicationPipeline(_demo);

        services.Should().NotContain(d => d.ImplementationType == typeof(GenericPingHandler<>), "an open generic class has no closed interface to be registered under");
        Action build = () => services.BuildServiceProvider().Dispose();
        build.Should().NotThrow("the provider is built from the scanned registrations, and a generic handler used to make that throw");
    }

    [Fact]
    public void TheBehaviorsKeepTheirOrder_EvenWhenThePipelineIsAddedAgainLater()
    {
        var services = new ServiceCollection();

        services.AddApplicationPipeline(_demo);
        services.AddIdempotency(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddApplicationPipeline(_demo);

        services.Where(d => d.ServiceType == typeof(IPipelineBehavior<,>)).Select(d => d.ImplementationType!.Name)
            .Should().Equal("TracingBehavior`2", "LoggingBehavior`2", "ValidationBehavior`2", "UnitOfWorkBehavior`2", "IdempotencyBehavior`2");
    }

    private sealed class FakeRegistry(IReadOnlyCollection<System.Reflection.Assembly> assemblies)
    {
        public IReadOnlyCollection<System.Reflection.Assembly> Assemblies { get; } = assemblies;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARegistryAddedTwiceWithDifferentAssemblies_IsBuiltOnceFromBoth_InEitherOrder(bool demoFirst)
    {
        var services = new ServiceCollection();
        int built = 0;
        Func<IEnumerable<System.Reflection.Assembly>, FakeRegistry> create = assemblies =>
        {
            built++;
            return new FakeRegistry([.. assemblies]);
        };

        ServiceScanning.AddToRegistry(services, [demoFirst ? _demo : _noJobsOrEvents], create);
        ServiceScanning.AddToRegistry(services, [demoFirst ? _noJobsOrEvents : _demo], create);
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<FakeRegistry>().Assemblies.Should().BeEquivalentTo([_demo, _noJobsOrEvents]);
        provider.GetRequiredService<FakeRegistry>();
        built.Should().Be(1, "the registry is a singleton built once, from everything that was added");
        Descriptors(services, typeof(FakeRegistry)).Should().Be(1);
    }

    [Fact]
    public void RegistriesOfDifferentKinds_KeepTheirAssembliesApart()
    {
        var services = new ServiceCollection();

        ServiceScanning.AddToRegistry(services, [_demo], assemblies => new FakeRegistry([.. assemblies]));
        ServiceScanning.AddToRegistry(services, [_noJobsOrEvents], assemblies => new OtherRegistry([.. assemblies]));
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<FakeRegistry>().Assemblies.Should().BeEquivalentTo([_demo]);
        provider.GetRequiredService<OtherRegistry>().Assemblies.Should().BeEquivalentTo([_noJobsOrEvents]);
    }

    [Fact]
    public void TheRealRegistrations_UseTheSharedRegistryOnce()
    {
        var services = new ServiceCollection();

        services.AddJobs(_noJobsOrEvents);
        services.AddJobs(_noJobsOrEvents);
        services.AddIntegrationEvents(_noJobsOrEvents);
        services.AddIntegrationEvents(_noJobsOrEvents);

        Descriptors(services, typeof(JobRegistry)).Should().Be(1);
        Descriptors(services, typeof(IntegrationEventRegistry)).Should().Be(1);
    }

    private sealed class OtherRegistry(IReadOnlyCollection<System.Reflection.Assembly> assemblies)
    {
        public IReadOnlyCollection<System.Reflection.Assembly> Assemblies { get; } = assemblies;
    }
}
