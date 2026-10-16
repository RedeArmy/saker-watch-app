using AwesomeAssertions;
using SakerWatch.BuildingBlocks.Application.Behaviors;

namespace SakerWatch.Application.Tests.Pipeline;

public sealed class ValidationFieldNameTests
{
    [Theory]
    [InlineData("Name", "name")]
    [InlineData("Locale", "locale")]
    [InlineData("DisplayName", "displayName")]
    [InlineData("Identity.DisplayName", "identity.displayName")]
    [InlineData("Items[0].Ticker", "items[0].ticker")]
    [InlineData("URL", "url")]
    [InlineData("alreadyCamel", "alreadyCamel")]
    [InlineData("", "")]
    public void ValidationFields_AreReportedUnderTheNameTheJsonBodyUses(string propertyPath, string expected)
    {
        ValidationBehavior<Fake, SakerWatch.BuildingBlocks.Kernel.Results.Result>.FieldName(propertyPath).Should().Be(expected);
    }

    private sealed record Fake : SakerWatch.BuildingBlocks.Application.Messaging.ICommand;
}
