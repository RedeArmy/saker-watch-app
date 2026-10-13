using AwesomeAssertions;
using SakerWatch.BuildingBlocks.Application.Events;
using SakerWatch.BuildingBlocks.Kernel.Domain;
using SakerWatch.Demo.Features;

namespace SakerWatch.Application.Tests.Kernel;

public sealed class IntegrationEventRegistryTests
{
    [IntegrationEventName("demo.clash.v1")]
    private sealed record ClashOne(Guid EventId, DateTimeOffset OccurredAt) : IDomainEvent;

    [IntegrationEventName("demo.clash.v1")]
    private sealed record ClashTwo(Guid EventId, DateTimeOffset OccurredAt) : IDomainEvent;

    [IntegrationEventName("demo.not_an_event.v1")]
    private sealed class NotAnEvent;

    private static readonly IntegrationEventRegistry _registry = new([typeof(PingHappened)]);

    [Fact]
    public void NameOf_ReturnsTheStableNameFromTheAttribute()
    {
        _registry.NameOf(new PingHappened(Guid.CreateVersion7(), DateTimeOffset.UtcNow, "x")).Should().Be("demo.ping_happened.v1");
    }

    [Fact]
    public void Serialize_ThenDeserialize_RoundTripsTheMessage()
    {
        var original = new PingHappened(Guid.CreateVersion7(), new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero), "nvda");

        IDomainEvent restored = _registry.Deserialize("demo.ping_happened.v1", IntegrationEventRegistry.Serialize(original));

        restored.Should().Be(original);
    }

    [Fact]
    public void NameOf_AnUnregisteredEvent_Throws()
    {
        Action act = () => _registry.NameOf(new UnnamedHappened(Guid.CreateVersion7(), DateTimeOffset.UtcNow));

        act.Should().Throw<InvalidOperationException>().WithMessage("*not a registered integration event*");
    }

    [Fact]
    public void Deserialize_AnUnknownName_Throws()
    {
        Action act = () => _registry.Deserialize("demo.nope.v1", "{}");

        act.Should().Throw<InvalidOperationException>().WithMessage("*Unknown integration event*");
    }

    [Fact]
    public void Constructor_TwoTypesWithTheSameName_Throws()
    {
        Action act = () => _ = new IntegrationEventRegistry([typeof(ClashOne), typeof(ClashTwo)]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*demo.clash.v1*");
    }

    [Fact]
    public void Constructor_ANamedTypeThatIsNotAnEvent_Throws()
    {
        Action act = () => _ = new IntegrationEventRegistry([typeof(NotAnEvent)]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*not an IDomainEvent*");
    }
}
