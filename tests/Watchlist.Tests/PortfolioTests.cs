using AwesomeAssertions;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.Watchlist.Domain;
using SakerWatch.Watchlist.Domain.Portfolios;

namespace SakerWatch.Watchlist.Tests;

public sealed class PortfolioTests
{
    private static readonly Guid _user = Guid.CreateVersion7();
    private static readonly DateTimeOffset _now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static Portfolio NewPortfolio(string name = "Tecnología") =>
        Portfolio.Create(PortfolioId.Create(Guid.CreateVersion7()), _user, name, null, 0, _now).Value;

    [Theory]
    [InlineData("Tecnología", "Tecnología")]
    [InlineData("  Tecnología", "Tecnología")]
    [InlineData("Tecnología   ", "Tecnología")]
    [InlineData("\t Salud y bienestar \n", "Salud y bienestar")]
    public void Name_IsTrimmedAtBothEnds_AndKeepsItsInnerSpaces(string typed, string stored) =>
        PortfolioName.Create(typed).Value.Value.Should().Be(stored);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("    ")]
    public void Name_ThatIsEmptyAfterTrimming_IsRefused(string? typed) =>
        PortfolioName.Create(typed).Error!.Code.Should().Be("watchlist.portfolio.name_required");

    [Fact]
    public void Name_OverSixtyCharacters_IsRefused_AndSixtyIsAccepted()
    {
        PortfolioName.Create(new string('x', PortfolioName.MaxLength)).IsSuccess.Should().BeTrue();
        PortfolioName.Create(new string('x', PortfolioName.MaxLength + 1)).Error!.Code.Should().Be("watchlist.portfolio.name_too_long");
        PortfolioName.Create("  " + new string('x', PortfolioName.MaxLength) + "  ").IsSuccess.Should().BeTrue("the limit applies after trimming");
    }

    [Fact]
    public void Name_WithAControlCharacter_IsRefused() =>
        PortfolioName.Create("Tec\u0007nología").Error!.Code.Should().Be("watchlist.portfolio.name_invalid");

    [Fact]
    public void Names_ThatDifferOnlyInLetterCase_AreTheSame()
    {
        PortfolioName.Create("Tecnología").Value.Should().Be(PortfolioName.Create("  TECNOLOGÍA ").Value);
        PortfolioName.Create("Tecnología").Value.Should().NotBe(PortfolioName.Create("Tecnologia").Value, "an accent is a different letter");
        PortfolioName.Create("Hola").Value.ToString().Should().Be("Hola");
    }

    [Fact]
    public void Create_StoresTheTrimmedNameAndDescription_AtThePosition()
    {
        Result<Portfolio> result = Portfolio.Create(PortfolioId.Create(Guid.CreateVersion7()), _user, "  Largo plazo ", "  Para cinco años  ", 3, _now);

        Portfolio portfolio = result.Value;
        (portfolio.Name.Value, portfolio.Description, portfolio.Position, portfolio.UserId, portfolio.CreatedAt, portfolio.UpdatedAt)
            .Should().Be(("Largo plazo", "Para cinco años", 3, _user, _now, _now));
    }

    [Fact]
    public void Create_WithoutAnOwnerOrWithABadPositionOrName_IsRefused()
    {
        var id = PortfolioId.Create(Guid.CreateVersion7());

        Portfolio.Create(id, Guid.Empty, "A", null, 0, _now).Error!.Code.Should().Be("watchlist.portfolio.user_required");
        Portfolio.Create(id, _user, "A", null, -1, _now).Error!.Code.Should().Be("watchlist.portfolio.position_invalid");
        Portfolio.Create(id, _user, " ", null, 0, _now).Error!.Code.Should().Be("watchlist.portfolio.name_required");
        Portfolio.Create(id, _user, "A", new string('d', Portfolio.MaxDescriptionLength + 1), 0, _now).Error!.Code.Should().Be("watchlist.portfolio.description_too_long");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Description_ThatIsBlank_BecomesNone(string? description) =>
        Portfolio.NormalizeDescription(description).Value.Should().BeNull();

    [Fact]
    public void Edit_ChangesBothAndMovesTheUpdateDate()
    {
        Portfolio portfolio = NewPortfolio();
        DateTimeOffset later = _now.AddMinutes(5);

        Result result = portfolio.Edit("  Salud ", "Hospitales", later);

        result.IsSuccess.Should().BeTrue();
        (portfolio.Name.Value, portfolio.Description, portfolio.UpdatedAt, portfolio.CreatedAt).Should().Be(("Salud", "Hospitales", later, _now));
    }

    [Fact]
    public void Edit_WithTheSameValues_ChangesNothing_AndRenamingOnlyTheCaseDoes()
    {
        Portfolio portfolio = NewPortfolio("Tecnología");

        portfolio.Edit(" Tecnología ", null, _now.AddHours(1)).IsSuccess.Should().BeTrue();
        portfolio.UpdatedAt.Should().Be(_now);

        portfolio.Edit("TECNOLOGÍA", null, _now.AddHours(2)).IsSuccess.Should().BeTrue();
        (portfolio.Name.Value, portfolio.UpdatedAt).Should().Be(("TECNOLOGÍA", _now.AddHours(2)));
    }

    [Fact]
    public void Edit_WithABadNameOrDescription_IsRefused_AndNothingChanges()
    {
        Portfolio portfolio = NewPortfolio();

        portfolio.Edit(" ", "otra", _now.AddHours(1)).Error!.Code.Should().Be("watchlist.portfolio.name_required");
        portfolio.Edit("Nueva", new string('d', Portfolio.MaxDescriptionLength + 1), _now.AddHours(1)).Error!.Code.Should().Be("watchlist.portfolio.description_too_long");

        (portfolio.Name.Value, portfolio.Description, portfolio.UpdatedAt).Should().Be(("Tecnología", null, _now));
    }

    [Fact]
    public void MoveTo_ChangesThePosition_RefusesANegativeOne_AndIgnoresTheSame()
    {
        Portfolio portfolio = NewPortfolio();

        portfolio.MoveTo(0, _now.AddHours(1)).IsSuccess.Should().BeTrue();
        portfolio.UpdatedAt.Should().Be(_now);
        portfolio.MoveTo(4, _now.AddHours(2)).IsSuccess.Should().BeTrue();
        (portfolio.Position, portfolio.UpdatedAt).Should().Be((4, _now.AddHours(2)));
        portfolio.MoveTo(-1, _now.AddHours(3)).Error!.Code.Should().Be("watchlist.portfolio.position_invalid");
        portfolio.Position.Should().Be(4);
    }

    [Fact]
    public void Restore_RebuildsThePortfolioWithoutValidatingAgain()
    {
        var id = PortfolioId.Create(Guid.CreateVersion7());
        PortfolioName name = PortfolioName.Create("Largo").Value;

        var portfolio = Portfolio.Restore(id, _user, name, "d", 2, _now, _now.AddDays(1));

        (portfolio.Id, portfolio.Name, portfolio.Description, portfolio.Position, portfolio.UpdatedAt).Should().Be((id, name, "d", 2, _now.AddDays(1)));
        id.ToString().Should().Be(id.Value.ToString("D"));
    }

    [Fact]
    public void TheLimits_DefaultToWhatAdr0026Says_AndNeverExceedTheDatabaseCeilings()
    {
        WatchlistLimits.Defaults.Should().Be(new WatchlistLimits(500, 20, 100));
        WatchlistLimits.Defaults.IsWithinCeilings.Should().BeTrue();
        new WatchlistLimits(501, 20, 100).IsWithinCeilings.Should().BeFalse();
        new WatchlistLimits(500, 21, 100).IsWithinCeilings.Should().BeFalse();
        new WatchlistLimits(500, 20, 101).IsWithinCeilings.Should().BeFalse();
        new WatchlistLimits(0, 20, 100).IsWithinCeilings.Should().BeFalse();
        new WatchlistLimits(10, 2, 5).IsWithinCeilings.Should().BeTrue();
    }

    [Theory]
    [InlineData("a\u0000b")]
    [InlineData("a\u0007b")]
    [InlineData("a\u001bb")]
    public void ADescriptionWithControlCharacters_IsRefused_NotLeftForTheDatabaseToFail(string description)
    {
        Portfolio.NormalizeDescription(description).Error!.Code.Should().Be("watchlist.portfolio.description_invalid");
        Portfolio.Create(PortfolioId.Create(Guid.CreateVersion7()), _user, "A", description, 0, _now).Error!.Code.Should().Be("watchlist.portfolio.description_invalid");
    }

    [Fact]
    public void ADescriptionMayHaveLineBreaksAndTabs() =>
        Portfolio.NormalizeDescription("uno\ndos\r\n\ttres").Value.Should().Be("uno\ndos\r\n\ttres");

    [Fact]
    public void AStoredName_IsRestoredWithoutBeingCheckedAgain_SoARuleThatBecomesStricterDoesNotBreakTheRowsAlreadyThere()
    {
        var restored = PortfolioName.Restore("Nombre\u0001antiguo");

        restored.Value.Should().Be("Nombre\u0001antiguo");
        PortfolioName.Create("Nombre\u0001antiguo").IsSuccess.Should().BeFalse();
    }
}
