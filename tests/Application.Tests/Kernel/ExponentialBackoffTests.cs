using AwesomeAssertions;
using SakerWatch.BuildingBlocks.Kernel.Time;

namespace SakerWatch.Application.Tests.Kernel;

public sealed class ExponentialBackoffTests
{
    private static ExponentialBackoff NoJitter(double baseSeconds = 2, double capSeconds = 600) =>
        new(TimeSpan.FromSeconds(baseSeconds), TimeSpan.FromSeconds(capSeconds), jitterRatio: 0, nextRandom: () => 0.5);

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(5, 32)]
    public void Delay_DoublesWithEveryFailedAttempt(int attempt, double expectedSeconds)
    {
        NoJitter().Delay(attempt).TotalSeconds.Should().Be(expectedSeconds);
    }

    [Fact]
    public void Delay_NeverExceedsTheCap()
    {
        NoJitter(capSeconds: 60).Delay(20).TotalSeconds.Should().Be(60);
        NoJitter(capSeconds: 60).Delay(1000).TotalSeconds.Should().Be(60);
    }

    [Fact]
    public void Delay_WithJitter_StaysWithinTheConfiguredBand()
    {
        var low = new ExponentialBackoff(TimeSpan.FromSeconds(10), TimeSpan.FromHours(1), 0.2, () => 0.0);
        var high = new ExponentialBackoff(TimeSpan.FromSeconds(10), TimeSpan.FromHours(1), 0.2, () => 0.999999);

        low.Delay(1).TotalSeconds.Should().BeApproximately(8, 0.001);
        high.Delay(1).TotalSeconds.Should().BeApproximately(12, 0.001);
    }

    [Fact]
    public void Delay_WithRealRandomness_NeverLeavesTheBand_AndSpreadsRetries()
    {
        var backoff = new ExponentialBackoff(TimeSpan.FromSeconds(10), TimeSpan.FromHours(1), 0.2);

        double[] samples = Enumerable.Range(0, 500).Select(_ => backoff.Delay(3).TotalSeconds).ToArray();

        samples.Should().OnlyContain(s => s >= 32 && s <= 48);
        samples.Distinct().Count().Should().BeGreaterThan(100, "jitter must desynchronize retries");
    }

    [Fact]
    public void Constructor_RejectsNonsenseSettings()
    {
        Action zeroBase = () => _ = new ExponentialBackoff(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Action capBelowBase = () => _ = new ExponentialBackoff(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));
        Action badJitter = () => _ = new ExponentialBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), 1.5);

        zeroBase.Should().Throw<ArgumentOutOfRangeException>();
        capBelowBase.Should().Throw<ArgumentOutOfRangeException>();
        badJitter.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Delay_RequiresAtLeastOneFailedAttempt()
    {
        Action act = () => NoJitter().Delay(0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
