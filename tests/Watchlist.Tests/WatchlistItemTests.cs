using AwesomeAssertions;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.Watchlist.Domain.Items;

namespace SakerWatch.Watchlist.Tests;

public sealed class WatchlistItemTests
{
    private static readonly Guid _user = Guid.CreateVersion7();
    private static readonly Guid _stock = Guid.CreateVersion7();
    private static readonly DateTimeOffset _now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static WatchlistItem NewItem() => WatchlistItem.Add(WatchlistItemId.Create(Guid.CreateVersion7()), _user, _stock, _now).Value;

    [Fact]
    public void Add_StartsWithNoNotesAndNoTarget_AndBothDatesAreTheMoment()
    {
        WatchlistItem item = NewItem();

        (item.UserId, item.StockId, item.Notes, item.TargetPrice, item.CreatedAt, item.UpdatedAt).Should().Be((_user, _stock, null, null, _now, _now));
    }

    [Fact]
    public void Add_WithoutAUserOrAStock_IsRefused()
    {
        WatchlistItem.Add(WatchlistItemId.Create(Guid.CreateVersion7()), Guid.Empty, _stock, _now).Error!.Code.Should().Be("watchlist.item.ids_required");
        WatchlistItem.Add(WatchlistItemId.Create(Guid.CreateVersion7()), _user, Guid.Empty, _now).Error!.Code.Should().Be("watchlist.item.ids_required");
    }

    [Fact]
    public void Update_KeepsTrimmedNotesAndTheTarget_AndMovesTheUpdateDate()
    {
        WatchlistItem item = NewItem();
        DateTimeOffset later = _now.AddHours(2);

        Result result = item.Update("  Esperar el informe trimestral  ", 123.45678901m, later);

        result.IsSuccess.Should().BeTrue();
        (item.Notes, item.TargetPrice, item.UpdatedAt, item.CreatedAt).Should().Be(("Esperar el informe trimestral", 123.45678901m, later, _now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Update_BlankNotes_BecomeNoNotes(string? notes)
    {
        WatchlistItem item = NewItem();
        item.Update("algo", null, _now).IsSuccess.Should().BeTrue();

        item.Update(notes, null, _now.AddMinutes(1));

        item.Notes.Should().BeNull();
    }

    [Fact]
    public void Update_WithTheSameValues_ChangesNothing_NotEvenTheUpdateDate()
    {
        WatchlistItem item = NewItem();
        item.Update("nota", 10m, _now.AddMinutes(1));

        item.Update(" nota ", 10m, _now.AddHours(5)).IsSuccess.Should().BeTrue();

        item.UpdatedAt.Should().Be(_now.AddMinutes(1));
    }

    [Fact]
    public void Update_WithNotesOverTheLimit_IsRefused_AndNothingChanges()
    {
        WatchlistItem item = NewItem();
        item.Update("antes", 5m, _now).IsSuccess.Should().BeTrue();

        Result result = item.Update(new string('x', WatchlistItem.MaxNotesLength + 1), 99m, _now.AddHours(1));

        result.Error!.Code.Should().Be("watchlist.item.notes_too_long");
        (item.Notes, item.TargetPrice).Should().Be(("antes", 5m), "a refused update changes neither field");
        item.Update(new string('x', WatchlistItem.MaxNotesLength), null, _now).IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1000000000000")]
    public void Update_WithATargetOutOfRange_IsRefused(string text)
    {
        WatchlistItem item = NewItem();

        Result result = item.Update("nota", decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture), _now.AddHours(1));

        result.Error!.Code.Should().Be("watchlist.item.target_price_invalid");
        item.Notes.Should().BeNull("the notes were valid but the target was not, so nothing changed");
    }

    [Fact]
    public void Update_WithMoreThanEightDecimals_IsRefusedInsteadOfBeingRoundedSilently()
    {
        WatchlistItem item = NewItem();

        item.Update(null, 1.123456789m, _now).Error!.Code.Should().Be("watchlist.item.target_price_precision");
        item.Update(null, 1.12345678m, _now).IsSuccess.Should().BeTrue();
        item.Update(null, WatchlistItem.MaxTargetPrice, _now).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Update_WithNullTarget_ClearsIt()
    {
        WatchlistItem item = NewItem();
        item.Update(null, 10m, _now).IsSuccess.Should().BeTrue();

        item.Update(null, null, _now.AddMinutes(1));

        item.TargetPrice.Should().BeNull();
    }

    [Fact]
    public void Restore_RebuildsTheItemWithoutValidatingAgain()
    {
        var id = WatchlistItemId.Create(Guid.CreateVersion7());

        var item = WatchlistItem.Restore(id, _user, _stock, "n", 3m, _now, _now.AddDays(1));

        (item.Id, item.Notes, item.TargetPrice, item.UpdatedAt).Should().Be((id, "n", 3m, _now.AddDays(1)));
    }

    [Fact]
    public void TheIdentifier_PrintsAsAPlainGuid()
    {
        var guid = Guid.CreateVersion7();

        WatchlistItemId.Create(guid).ToString().Should().Be(guid.ToString("D"));
    }
}
