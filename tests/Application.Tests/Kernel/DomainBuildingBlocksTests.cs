using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using SakerWatch.BuildingBlocks.Kernel.Domain;
using SakerWatch.BuildingBlocks.Kernel.Time;

namespace SakerWatch.Application.Tests.Kernel;

public sealed class DomainBuildingBlocksTests
{
    private readonly record struct DemoId(Guid Value) : IStronglyTypedId<DemoId>
    {
        public static DemoId Create(Guid value) => new(value);
    }

    private sealed record DemoHappened(Guid EventId, DateTimeOffset OccurredAt) : IDomainEvent;

    private sealed class DemoAggregate(DemoId id) : AggregateRoot<DemoId>(id)
    {
        public void Happen(IDomainEvent domainEvent) => Raise(domainEvent);
    }

    private sealed class OtherAggregate(DemoId id) : AggregateRoot<DemoId>(id);

    private sealed class Money(decimal amount, string currency) : ValueObject
    {
        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return amount;
            yield return currency;
        }
    }

    [Fact]
    public void Entities_WithSameTypeAndId_AreEqual()
    {
        var id = new DemoId(Guid.CreateVersion7());

        (new DemoAggregate(id) == new DemoAggregate(id)).Should().BeTrue();
        new DemoAggregate(id).GetHashCode().Should().Be(new DemoAggregate(id).GetHashCode());
    }

    [Fact]
    public void Entities_WithDifferentIdOrType_AreNotEqual()
    {
        var id = new DemoId(Guid.CreateVersion7());

        (new DemoAggregate(id) != new DemoAggregate(new DemoId(Guid.CreateVersion7()))).Should().BeTrue();
        new DemoAggregate(id).Equals((object)new OtherAggregate(id)).Should().BeFalse();
        new DemoAggregate(id).Equals(null).Should().BeFalse();
    }

    [Fact]
    public void PullDomainEvents_ReturnsPendingEventsOnce()
    {
        var aggregate = new DemoAggregate(new DemoId(Guid.CreateVersion7()));
        var first = new DemoHappened(Guid.CreateVersion7(), DateTimeOffset.UnixEpoch);
        aggregate.Happen(first);

        aggregate.DomainEvents.Should().ContainSingle().Which.Should().Be(first);
        aggregate.PullDomainEvents().Should().ContainSingle();
        aggregate.PullDomainEvents().Should().BeEmpty();
        aggregate.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void ValueObjects_WithSameComponents_AreEqual()
    {
        (new Money(10.50m, "USD") == new Money(10.50m, "USD")).Should().BeTrue();
        new Money(10.50m, "USD").GetHashCode().Should().Be(new Money(10.50m, "USD").GetHashCode());
        (new Money(10.50m, "USD") != new Money(10.50m, "GTQ")).Should().BeTrue();
        new Money(1m, "USD").Equals(null).Should().BeFalse();
    }

    [Fact]
    public void IdGenerator_ProducesUuidV7_OrderedByTime()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
        var generator = new TimeProviderIdGenerator(clock);

        Guid earlier = generator.NewGuid();
        clock.Advance(TimeSpan.FromSeconds(5));
        DemoId later = generator.New<DemoId>();

        earlier.Version.Should().Be(7);
        later.Value.Version.Should().Be(7);
        later.Value.CompareTo(earlier).Should().BePositive();
    }
}
