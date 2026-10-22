using AwesomeAssertions;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Application.Providers;

namespace SakerWatch.MarketData.Tests;

public sealed class ProviderErrorsTests
{
    [Fact]
    public void EachFailure_IsClassifiedByItsCode()
    {
        ProviderErrors.Classify(ProviderErrors.RateLimited("p")).Should().Be(ProviderFailureKind.RateLimited);
        ProviderErrors.Classify(ProviderErrors.Unavailable("p")).Should().Be(ProviderFailureKind.Transient);
        ProviderErrors.Classify(ProviderErrors.PlanRestricted("p")).Should().Be(ProviderFailureKind.PlanRestricted);
        ProviderErrors.Classify(ProviderErrors.SymbolNotFound("p", "X")).Should().Be(ProviderFailureKind.SymbolNotFound);
        ProviderErrors.Classify(ProviderErrors.InvalidResponse("p")).Should().Be(ProviderFailureKind.Permanent);
    }

    [Fact]
    public void AnUnknownError_IsPermanent_SoItIsNeverRetriedBlindly() =>
        ProviderErrors.Classify(Error.Failure("something.else", "x")).Should().Be(ProviderFailureKind.Permanent);

    [Fact]
    public void RateLimited_CarriesTheWaitTheProviderAskedFor()
    {
        Error error = ProviderErrors.RateLimited("p", TimeSpan.FromSeconds(42.5));

        ProviderErrors.RetryAfter(error).Should().Be(TimeSpan.FromSeconds(42.5));
    }

    [Fact]
    public void RateLimited_WithoutAWait_HasNoRetryAfter() =>
        ProviderErrors.RetryAfter(ProviderErrors.RateLimited("p")).Should().BeNull();

    [Fact]
    public void RetryAfter_IgnoresADetailThatIsNotANonNegativeNumber()
    {
        ProviderErrors.RetryAfter(new Error("c", "m", ErrorType.Failure, new Dictionary<string, string[]> { ["retry_after_seconds"] = ["soon"] })).Should().BeNull();
        ProviderErrors.RetryAfter(new Error("c", "m", ErrorType.Failure, new Dictionary<string, string[]> { ["retry_after_seconds"] = ["-5"] })).Should().BeNull();
        ProviderErrors.RetryAfter(new Error("c", "m", ErrorType.Failure, new Dictionary<string, string[]> { ["retry_after_seconds"] = ["1", "2"] })).Should().BeNull();
    }

    [Fact]
    public void Messages_NameTheProviderButNeverCarryASecret()
    {
        ProviderErrors.PlanRestricted("twelvedata").Message.Should().Contain("twelvedata");
        ProviderErrors.SymbolNotFound("twelvedata", "ZZZ").Message.Should().Contain("ZZZ");
    }

    [Fact]
    public void RetryAfter_IsCappedAtOneDay_SoAHostileWaitCannotStopAJobForYears() =>
        ProviderErrors.RetryAfter(new Error("c", "m", ErrorType.Failure, new Dictionary<string, string[]> { ["retry_after_seconds"] = ["999999999"] })).Should().Be(TimeSpan.FromDays(1));
}
