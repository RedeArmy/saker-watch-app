using System.Security.Cryptography;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.BuildingBlocks.Persistence.Audit;

namespace SakerWatch.Integration.Tests.Persistence;

/// <summary>The audit pepper is validated when the options are first read, so a host with a bad secret never starts.</summary>
public sealed class AuditOptionsTests
{
    private static AuditOptions Read(string? pepper)
    {
        var services = new ServiceCollection();
        services.AddSakerPostgres(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=unused",
            ["Audit:ActorPepper"] = pepper,
        }).Build());
        using ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<AuditOptions>>().Value;
    }

    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(300)]
    [InlineData(4096)]
    public void APepperOfAnyLengthFromTheMinimum_IsAccepted(int bytes)
    {
        string pepper = Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes));

        Read(pepper).ActorPepper.Should().Be(pepper);
    }

    [Fact]
    public void APepperThatIsTooShort_SaysSoAndHowShort()
    {
        Action act = () => Read(Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));

        act.Should().Throw<OptionsValidationException>().WithMessage("*at least 32 bytes*decodes to 16*");
    }

    [Fact]
    public void APepperThatIsNotBase64_SaysSo()
    {
        Action act = () => Read("this is not base64 !!");

        act.Should().Throw<OptionsValidationException>().WithMessage("*valid Base64*");
    }

    [Fact]
    public void AMissingPepper_IsRefused()
    {
        Action act = () => Read(null);

        act.Should().Throw<OptionsValidationException>();
    }
}
