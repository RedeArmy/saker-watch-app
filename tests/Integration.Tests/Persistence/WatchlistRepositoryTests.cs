using AwesomeAssertions;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.Watchlist.Application;
using SakerWatch.Watchlist.Domain.Items;
using SakerWatch.Watchlist.Infrastructure.Persistence;

namespace SakerWatch.Integration.Tests.Persistence;

[Trait("Category", "Integration")]
public sealed class WatchlistRepositoryTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    private static readonly DateTimeOffset _now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedUser(Guid? id) : ICurrentUser
    {
        public Guid? UserId => id;
    }

    private static WatchlistItem NewItem(Guid user, Guid? stock = null, string? notes = null, decimal? target = null)
    {
        WatchlistItem item = WatchlistItem.Add(WatchlistItemId.Create(Guid.CreateVersion7()), user, stock ?? Guid.CreateVersion7(), _now).Value;
        item.Update(notes, target, _now).IsSuccess.Should().BeTrue();
        return item;
    }

    // One unit of work as the pipeline runs it: the API login, the user set on the connection, a transaction, committed at the end.
    private static async Task<T> AsAsync<T>(MigratedDatabase db, Guid user, Func<WatchlistRepository, Task<T>> action)
    {
        await using var session = new DbSession(Options.Create(new PostgresOptions { Postgres = db.Api }), new FixedUser(user));
        await session.BeginAsync(CancellationToken.None);
        T result = await action(new WatchlistRepository(session));
        await session.CommitAsync(CancellationToken.None);
        return result;
    }

    [Fact]
    public async Task AnAddedItem_ComesBackWithEveryValue()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var user = Guid.CreateVersion7();
        WatchlistItem item = NewItem(user, notes: "Esperar el informe", target: 123.45678901m);

        AddOutcome outcome = await AsAsync(db, user, repository => repository.AddAsync(item, CancellationToken.None));
        WatchlistItem? found = await AsAsync(db, user, repository => repository.FindByStockAsync(user, item.StockId, CancellationToken.None));

        outcome.Should().Be(AddOutcome.Added);
        found.Should().NotBeNull();
        (found.Id, found.Notes, found.TargetPrice, found.CreatedAt, found.UpdatedAt).Should().Be((item.Id, "Esperar el informe", 123.45678901m, _now, _now));
    }

    [Fact]
    public async Task AnItemWithNoNotesAndNoTarget_IsStoredAsNulls()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var user = Guid.CreateVersion7();
        WatchlistItem item = NewItem(user);
        await AsAsync(db, user, repository => repository.AddAsync(item, CancellationToken.None));

        WatchlistItem? found = await AsAsync(db, user, repository => repository.FindByStockAsync(user, item.StockId, CancellationToken.None));

        (found!.Notes, found.TargetPrice).Should().Be(((string?)null, (decimal?)null));
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM watchlist.watchlist_items WHERE notes IS NULL AND target_price IS NULL")).Should().Be(1);
    }

    [Fact]
    public async Task AddingTheSameStockAgain_IsAnOutcome_NotAnException_AndTheTransactionStaysUsable()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var user = Guid.CreateVersion7();
        var stock = Guid.CreateVersion7();
        await AsAsync(db, user, repository => repository.AddAsync(NewItem(user, stock), CancellationToken.None));

        (AddOutcome again, int count) = await AsAsync(db, user, async repository =>
            (await repository.AddAsync(NewItem(user, stock), CancellationToken.None), await repository.CountAsync(user, CancellationToken.None)));

        again.Should().Be(AddOutcome.AlreadyThere);
        count.Should().Be(1, "the same transaction could still read after the conflict");
    }

    [Fact]
    public async Task TheFiveHundredthStockFits_TheNextOneIsALimitOutcome()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var user = Guid.CreateVersion7();
        await db.Admin.ExecuteAsync(
            "INSERT INTO watchlist.watchlist_items (id, user_id, stock_id, created_at, updated_at) SELECT gen_random_uuid(), @user, gen_random_uuid(), now(), now() FROM generate_series(1, 499)",
            ("user", user));

        AddOutcome last = await AsAsync(db, user, repository => repository.AddAsync(NewItem(user), CancellationToken.None));
        AddOutcome over = await AsAsync(db, user, repository => repository.AddAsync(NewItem(user), CancellationToken.None));
        int count = await AsAsync(db, user, repository => repository.CountAsync(user, CancellationToken.None));

        (last, over, count).Should().Be((AddOutcome.Added, AddOutcome.LimitReached, 500));
    }

    [Fact]
    public async Task Update_ChangesTheNotesTheTargetAndTheDate_AndOnlyOfThatItem()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var user = Guid.CreateVersion7();
        WatchlistItem first = NewItem(user, notes: "uno");
        WatchlistItem second = NewItem(user, notes: "dos");
        await AsAsync(db, user, async repository =>
        {
            await repository.AddAsync(first, CancellationToken.None);
            return await repository.AddAsync(second, CancellationToken.None);
        });
        first.Update("cambiada", 55m, _now.AddHours(1)).IsSuccess.Should().BeTrue();

        await AsAsync(db, user, async repository =>
        {
            await repository.UpdateAsync(first, CancellationToken.None);
            return 0;
        });

        WatchlistItem? changed = await AsAsync(db, user, repository => repository.FindByStockAsync(user, first.StockId, CancellationToken.None));
        WatchlistItem? untouched = await AsAsync(db, user, repository => repository.FindByStockAsync(user, second.StockId, CancellationToken.None));
        (changed!.Notes, changed.TargetPrice, changed.UpdatedAt).Should().Be(("cambiada", 55m, _now.AddHours(1)));
        (untouched!.Notes, untouched.UpdatedAt).Should().Be(("dos", _now));
    }

    [Fact]
    public async Task Remove_DeletesTheItemAndItsMemberships_AndSaysWhetherThereWasOne()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var user = Guid.CreateVersion7();
        WatchlistItem item = NewItem(user);
        await AsAsync(db, user, repository => repository.AddAsync(item, CancellationToken.None));
        var portfolio = Guid.CreateVersion7();
        await db.Admin.ExecuteAsync(
            "INSERT INTO watchlist.portfolios (id, user_id, name, position, created_at, updated_at) VALUES (@id, @user, 'Tech', 0, now(), now())", ("id", portfolio), ("user", user));
        await db.Admin.ExecuteAsync(
            "INSERT INTO watchlist.portfolio_items (portfolio_id, watchlist_item_id, user_id, position, added_at) VALUES (@p, @i, @user, 0, now())",
            ("p", portfolio), ("i", item.Id.Value), ("user", user));

        bool first = await AsAsync(db, user, repository => repository.RemoveByStockAsync(user, item.StockId, CancellationToken.None));
        bool again = await AsAsync(db, user, repository => repository.RemoveByStockAsync(user, item.StockId, CancellationToken.None));

        (first, again).Should().Be((true, false));
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM watchlist.portfolio_items")).Should().Be(0);
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM watchlist.portfolios")).Should().Be(1, "the portfolio itself stays");
    }

    [Fact]
    public async Task OneUser_CannotSeeChangeOrRemoveAnotherUsersItem_EvenWhenAskingForItByName()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        Guid alice = Guid.CreateVersion7(), bob = Guid.CreateVersion7();
        WatchlistItem mine = NewItem(alice, notes: "mine");
        await AsAsync(db, alice, repository => repository.AddAsync(mine, CancellationToken.None));

        // Bob's session asks for Alice's item with Alice's id: the explicit filter would let it through; the policy does not.
        WatchlistItem? seen = await AsAsync(db, bob, repository => repository.FindByStockAsync(alice, mine.StockId, CancellationToken.None));
        int count = await AsAsync(db, bob, repository => repository.CountAsync(alice, CancellationToken.None));
        bool removed = await AsAsync(db, bob, repository => repository.RemoveByStockAsync(alice, mine.StockId, CancellationToken.None));
        mine.Update("hacked", null, _now.AddHours(1)).IsSuccess.Should().BeTrue();
        await AsAsync(db, bob, async repository =>
        {
            await repository.UpdateAsync(mine, CancellationToken.None);
            return 0;
        });

        (seen, count, removed).Should().Be(((WatchlistItem?)null, 0, false));
        (await db.Admin.ScalarAsync<string>("SELECT notes FROM watchlist.watchlist_items")).Should().Be("mine");
    }

    [Fact]
    public async Task TwoUsers_MayHaveTheSameStock()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        Guid alice = Guid.CreateVersion7(), bob = Guid.CreateVersion7(), stock = Guid.CreateVersion7();

        AddOutcome first = await AsAsync(db, alice, repository => repository.AddAsync(NewItem(alice, stock), CancellationToken.None));
        AddOutcome second = await AsAsync(db, bob, repository => repository.AddAsync(NewItem(bob, stock), CancellationToken.None));

        (first, second).Should().Be((AddOutcome.Added, AddOutcome.Added));
    }
}
