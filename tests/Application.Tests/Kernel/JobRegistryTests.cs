using AwesomeAssertions;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.Demo.Features;

namespace SakerWatch.Application.Tests.Kernel;

public sealed class JobRegistryTests
{
    [JobType("demo.clash.v1")]
    private sealed record ClashOne(int N);

    [JobType("demo.clash.v1")]
    private sealed record ClashTwo(int N);

    private static readonly JobRegistry _registry = new([typeof(ReindexJob)]);

    [Fact]
    public void NameOf_ReturnsTheStableNameFromTheAttribute()
    {
        _registry.NameOf(typeof(ReindexJob)).Should().Be("demo.reindex.v1");
    }

    [Fact]
    public void Serialize_ThenDeserialize_RoundTripsThePayload()
    {
        var original = new ReindexJob("XNAS", 7);

        object restored = _registry.Deserialize("demo.reindex.v1", JobRegistry.Serialize(original));

        restored.Should().Be(original);
    }

    [Fact]
    public void NameOf_AnUnregisteredPayload_Throws()
    {
        Action act = () => _registry.NameOf(typeof(string));

        act.Should().Throw<InvalidOperationException>().WithMessage("*not a registered job payload*");
    }

    [Fact]
    public void Deserialize_AnUnknownName_Throws()
    {
        Action act = () => _registry.Deserialize("demo.gone.v1", "{}");

        act.Should().Throw<InvalidOperationException>().WithMessage("*Unknown job type*");
    }

    [Fact]
    public void Constructor_TwoPayloadsWithTheSameName_Throws()
    {
        Action act = () => _ = new JobRegistry([typeof(ClashOne), typeof(ClashTwo)]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*demo.clash.v1*");
    }
}
