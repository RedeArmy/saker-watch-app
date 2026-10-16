using AwesomeAssertions;
using SakerWatch.BuildingBlocks.Application.Telemetry;
using SakerWatch.BuildingBlocks.Kernel.Telemetry;

namespace SakerWatch.Application.Tests.Kernel;

public sealed class SakerTelemetryTests
{
    [Fact]
    public void TheSharedMeterAndSource_CarryTheProductNameAndTheBuildVersion()
    {
        SakerTelemetry.Meter.Name.Should().Be("SakerWatch");
        SakerTelemetry.Source.Name.Should().Be("SakerWatch");
        SakerTelemetry.Meter.Version.Should().NotBeNullOrEmpty("it is stamped on every metric's instrumentation scope");
        SakerTelemetry.Source.Version.Should().Be(SakerTelemetry.Meter.Version);
    }

    [Fact]
    public void TheNamesTheHostsSubscribeTo_AreTheSameOneValue()
    {
        UseCaseTelemetry.MeterName.Should().Be(SakerTelemetry.Name);
        UseCaseTelemetry.SourceName.Should().Be(SakerTelemetry.Name);
        UseCaseTelemetry.Source.Should().BeSameAs(SakerTelemetry.Source);
    }

    [Fact]
    public void ACreatedMeter_IsANewInstanceUnderTheSameName_SoItsOwnerCanDisposeIt()
    {
        using var owned = SakerTelemetry.CreateMeter();

        owned.Name.Should().Be(SakerTelemetry.Name);
        owned.Should().NotBeSameAs(SakerTelemetry.Meter);
    }
}
