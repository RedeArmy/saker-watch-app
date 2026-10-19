using AwesomeAssertions;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.Watchlist.Application;
using SakerWatch.Watchlist.Domain.Portfolios;
using SakerWatch.Watchlist.Infrastructure.Persistence;

namespace SakerWatch.Integration.Tests.Persistence;

[Trait("Category", "Integration")]
public sealed class PortfolioRepositoryTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    private static readonly DateTimeOffset _now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedUser(Guid? id) : ICurrentUser
    {
        public Guid? UserId => id;
    }

    private static DbSession Session(MigratedDatabase db, Guid user) => new(Options.Create(new PostgresOptions { Postgres = db.Api }), new FixedUser(user));

    private static async Task<T> AsAsync<T>(MigratedDatabase db, Guid user, Func<PortfolioRepository, Task<T>> action)
    {
        await using DbSession session = Session(db, user);
        await session.BeginAsync(CancellationToken.None);
        T result = await action(new PortfolioRepository(session));
        await session.CommitAsync(CancellationToken.None);
        return result;
    }

    private static Portfolio NewPortfolio(Guid user, string name, int position = 0) =>
        Portfolio.Create(PortfolioId.Create(Guid.CreateVersion7()), user, name, null, position, _now).Value;

    private static async Task<Portfolio> AddAsync(MigratedDatabase db, Guid user, string name, int position = 0)
    {
        Portfolio portfolio = NewPortfolio(user, name, position);
        (await AsAsync(db, user, repository => repository.AddAsync(portfolio, CancellationToken.None))).Should().Be(PortfolioWrite.Done);
        return portfolio;
    }

    // Puts n stocks on the user's watchlist and returns their catalog ids.
    private static async Task<Guid[]> FollowAsync(MigratedDatabase db, Guid user, int count)
    {
        Guid[] stocks = [.. Enumerable.Range(0, count).Select(_ => Guid.CreateVersion7())];
        foreach (Guid stock in stocks)
        {
            await db.Admin.ExecuteAsync(
                "INSERT INTO watchlist.watchlist_items (id, user_id, stock_id, created_at, updated_at) VALUES (gen_random_uuid(), @user, @stock, now(), now())",
                ("user", user), ("stock", stock));
        }

        return stocks;
    }

    private static Task<MemberAdd> PutAsync(MigratedDatabase db, Guid user, Portfolio portfolio, Guid stock) =>
        AsAsync(db, user, repository => repository.AddMemberAsync(user, portfolio.Id, stock, _now, CancellationToken.None));

    private static Task<long> CountAsync(MigratedDatabase db, string table, string where = "true") =>
        db.Admin.ScalarAsync<long>($"SELECT count(*) FROM watchlist.{table} WHERE {where}");

    [Fact]
    public async Task APortfolio_IsStoredAndComesBackWithItsValues_ListedByPositionWithTheirCounts()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var user = Guid.CreateVersion7();
        Portfolio second = await AddAsync(db, user, "Imanes", position: 1);
        Portfolio first = await AddAsync(db, user, "Petróleo", position: 0);
        Guid[] stocks = await FollowAsync(db, user, 3);
        foreach (Guid stock in stocks)
        {
            await PutAsync(db, user, first, stock);
        }

        IReadOnlyList<PortfolioSummary> list = await AsAsync(db, user, repository => repository.ListAsync(user, CancellationToken.None));
        PortfolioSummary? found = await AsAsync(db, user, repository => repository.FindAsync(user, first.Id, CancellationToken.None));

        list.Select(s => (s.Portfolio.Name.Value, s.ItemCount)).Should().Equal(("Petróleo", 3), ("Imanes", 0));
        found!.Portfolio.Should().Match<Portfolio>(p => p.Id == first.Id && p.Position == 0 && p.CreatedAt == _now && p.UserId == user && p.Description == null);
        second.Id.Should().NotBe(first.Id);
    }

    [Fact]
    public async Task APortfolioName_IsUniquePerUserIgnoringCase_AsAnOutcomeNotAnException_AndAnotherUserMayReuseIt()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        Guid alice = Guid.CreateVersion7(), bob = Guid.CreateVersion7();
        await AddAsync(db, alice, "Tecnología");

        (PortfolioWrite same, long count) = await AsAsync(db, alice, async repository =>
            (await repository.AddAsync(NewPortfolio(alice, "TECNOLOGÍA"), CancellationToken.None), (await repository.ListAsync(alice, CancellationToken.None)).Count));
        PortfolioWrite other = await AsAsync(db, bob, repository => repository.AddAsync(NewPortfolio(bob, "Tecnología"), CancellationToken.None));

        (same, count, other).Should().Be((PortfolioWrite.NameTaken, 1L, PortfolioWrite.Done));
    }

    [Fact]
    public async Task TheTwentyFirstPortfolio_IsALimitOutcome_ForThatUserOnly()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        Guid alice = Guid.CreateVersion7(), bob = Guid.CreateVersion7();
        for (int i = 0; i < 20; i++)
        {
            await AddAsync(db, alice, $"Cartera {i}", i);
        }

        PortfolioWrite over = await AsAsync(db, alice, repository => repository.AddAsync(NewPortfolio(alice, "Una más", 20), CancellationToken.None));
        PortfolioWrite bobs = await AsAsync(db, bob, repository => repository.AddAsync(NewPortfolio(bob, "Mía"), CancellationToken.None));

        (over, bobs).Should().Be((PortfolioWrite.LimitReached, PortfolioWrite.Done));
    }

    [Fact]
    public async Task Update_ChangesTheNameAndTheDescription_AReusedNameIsAnOutcome_AndAnotherUsersPortfolioIsNotFound()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        Guid alice = Guid.CreateVersion7(), bob = Guid.CreateVersion7();
        Portfolio one = await AddAsync(db, alice, "Uno");
        await AddAsync(db, alice, "Dos", 1);
        one.Edit("Tres", "Con descripción", _now.AddHours(1)).IsSuccess.Should().BeTrue();

        PortfolioWrite renamed = await AsAsync(db, alice, repository => repository.UpdateAsync(one, CancellationToken.None));
        one.Edit("DOS", null, _now.AddHours(2)).IsSuccess.Should().BeTrue();
        PortfolioWrite taken = await AsAsync(db, alice, repository => repository.UpdateAsync(one, CancellationToken.None));
        PortfolioWrite theirs = await AsAsync(db, bob, repository => repository.UpdateAsync(one, CancellationToken.None));

        (renamed, taken, theirs).Should().Be((PortfolioWrite.Done, PortfolioWrite.NameTaken, PortfolioWrite.NotFound));
        (await db.Admin.ScalarAsync<string>("SELECT concat_ws('|', name, description) FROM watchlist.portfolios WHERE id = @id", ("id", one.Id.Value))).Should().Be("Tres|Con descripción");
    }

    [Fact]
    public async Task APortfolio_IsInvisibleToAnotherUser()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        Guid alice = Guid.CreateVersion7(), bob = Guid.CreateVersion7();
        Portfolio mine = await AddAsync(db, alice, "Mía");

        PortfolioSummary? seen = await AsAsync(db, bob, repository => repository.FindAsync(alice, mine.Id, CancellationToken.None));
        IReadOnlyList<PortfolioSummary> list = await AsAsync(db, bob, repository => repository.ListAsync(alice, CancellationToken.None));
        bool deleted = await AsAsync(db, bob, repository => repository.DeleteAsync(alice, mine.Id, true, CancellationToken.None));

        (seen, list.Count, deleted).Should().Be(((PortfolioSummary?)null, 0, false));
        (await CountAsync(db, "portfolios")).Should().Be(1);
    }

    [Fact]
    public async Task Reorder_NeedsExactlyTheUsersPortfolios_AndChangesNothingOtherwise()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        Guid alice = Guid.CreateVersion7(), bob = Guid.CreateVersion7();
        Portfolio a = await AddAsync(db, alice, "A", 0);
        Portfolio b = await AddAsync(db, alice, "B", 1);
        Portfolio c = await AddAsync(db, alice, "C", 2);
        Portfolio stranger = await AddAsync(db, bob, "Ajena");

        bool missing = await AsAsync(db, alice, r => r.ReorderAsync(alice, [a.Id, b.Id], CancellationToken.None));
        bool repeated = await AsAsync(db, alice, r => r.ReorderAsync(alice, [a.Id, a.Id, b.Id], CancellationToken.None));
        bool foreign = await AsAsync(db, alice, r => r.ReorderAsync(alice, [a.Id, b.Id, stranger.Id], CancellationToken.None));
        bool unchanged = (await AsAsync(db, alice, r => r.ListAsync(alice, CancellationToken.None))).Select(s => s.Portfolio.Name.Value).SequenceEqual(["A", "B", "C"]);
        bool accepted = await AsAsync(db, alice, r => r.ReorderAsync(alice, [c.Id, a.Id, b.Id], CancellationToken.None));
        bool again = await AsAsync(db, alice, r => r.ReorderAsync(alice, [c.Id, a.Id, b.Id], CancellationToken.None));

        (missing, repeated, foreign, unchanged, accepted, again).Should().Be((false, false, false, true, true, true));
        (await AsAsync(db, alice, r => r.ListAsync(alice, CancellationToken.None))).Select(s => (s.Portfolio.Name.Value, s.Portfolio.Position)).Should().Equal(("C", 0), ("A", 1), ("B", 2));
        (await AsAsync(db, alice, r => r.ReorderAsync(alice, [], CancellationToken.None))).Should().BeFalse("she has portfolios, so an empty order is not exact");
        (await AsAsync(db, Guid.CreateVersion7(), r => r.ReorderAsync(Guid.Empty, [], CancellationToken.None))).Should().BeTrue("nothing to order is a valid order");
    }

    [Fact]
    public async Task Members_AreAddedAtTheEnd_AddingAgainIsAnOutcome_AndOnlyWatchlistStocksOfAnOwnPortfolioQualify()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        Guid alice = Guid.CreateVersion7(), bob = Guid.CreateVersion7();
        Portfolio mine = await AddAsync(db, alice, "Mía");
        Portfolio theirs = await AddAsync(db, bob, "Ajena");
        Guid[] followed = await FollowAsync(db, alice, 3);
        Guid[] bobs = await FollowAsync(db, bob, 1);

        MemberAdd[] added = [await PutAsync(db, alice, mine, followed[0]), await PutAsync(db, alice, mine, followed[1]), await PutAsync(db, alice, mine, followed[2])];
        MemberAdd again = await PutAsync(db, alice, mine, followed[1]);
        MemberAdd notFollowed = await PutAsync(db, alice, mine, Guid.CreateVersion7());
        MemberAdd someoneElses = await PutAsync(db, alice, mine, bobs[0]);
        MemberAdd intoTheirs = await PutAsync(db, alice, theirs, followed[0]);
        MemberAdd intoMissing = await AsAsync(db, alice, r => r.AddMemberAsync(alice, PortfolioId.Create(Guid.CreateVersion7()), followed[0], _now, CancellationToken.None));

        added.Should().AllBeEquivalentTo(MemberAdd.Added);
        (again, notFollowed, someoneElses, intoTheirs, intoMissing).Should().Be(
            (MemberAdd.AlreadyThere, MemberAdd.ItemNotOnWatchlist, MemberAdd.ItemNotOnWatchlist, MemberAdd.PortfolioNotFound, MemberAdd.PortfolioNotFound));
        IReadOnlyList<PortfolioMember> members = await AsAsync(db, alice, r => r.MembersAsync(alice, mine.Id, CancellationToken.None));
        members.Select(m => (m.StockId, m.Position)).Should().Equal((followed[0], 0), (followed[1], 1), (followed[2], 2));
        (await CountAsync(db, "portfolio_items", $"portfolio_id = '{theirs.Id.Value}'")).Should().Be(0);
    }

    [Fact]
    public async Task APortfolio_HoldsAtMostAHundredStocks_AsAnOutcome()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var user = Guid.CreateVersion7();
        Portfolio portfolio = await AddAsync(db, user, "Llena");
        Portfolio other = await AddAsync(db, user, "Otra", 1);
        Guid[] stocks = await FollowAsync(db, user, 101);
        foreach (Guid stock in stocks.Take(100))
        {
            (await PutAsync(db, user, portfolio, stock)).Should().Be(MemberAdd.Added);
        }

        MemberAdd over = await PutAsync(db, user, portfolio, stocks[100]);
        MemberAdd elsewhere = await PutAsync(db, user, other, stocks[100]);

        (over, elsewhere).Should().Be((MemberAdd.LimitReached, MemberAdd.Added));
    }

    [Fact]
    public async Task RemovingAMember_KeepsTheStockOnTheWatchlist_AndReportsWhetherItWasThere()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var user = Guid.CreateVersion7();
        Portfolio portfolio = await AddAsync(db, user, "P");
        Guid[] stocks = await FollowAsync(db, user, 2);
        await PutAsync(db, user, portfolio, stocks[0]);
        await PutAsync(db, user, portfolio, stocks[1]);

        bool first = await AsAsync(db, user, r => r.RemoveMemberAsync(user, portfolio.Id, stocks[0], CancellationToken.None));
        bool again = await AsAsync(db, user, r => r.RemoveMemberAsync(user, portfolio.Id, stocks[0], CancellationToken.None));
        bool unknown = await AsAsync(db, user, r => r.RemoveMemberAsync(user, portfolio.Id, Guid.CreateVersion7(), CancellationToken.None));

        (first, again, unknown).Should().Be((true, false, false));
        (await CountAsync(db, "watchlist_items")).Should().Be(2);
        (await AsAsync(db, user, r => r.MembersAsync(user, portfolio.Id, CancellationToken.None))).Select(m => m.StockId).Should().Equal(stocks[1]);
    }

    [Fact]
    public async Task ReorderMembers_NeedsExactlyTheStocksOfThePortfolio()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var user = Guid.CreateVersion7();
        Portfolio portfolio = await AddAsync(db, user, "P");
        Guid[] stocks = await FollowAsync(db, user, 3);
        foreach (Guid stock in stocks)
        {
            await PutAsync(db, user, portfolio, stock);
        }

        bool missing = await AsAsync(db, user, r => r.ReorderMembersAsync(user, portfolio.Id, [stocks[0], stocks[1]], CancellationToken.None));
        bool repeated = await AsAsync(db, user, r => r.ReorderMembersAsync(user, portfolio.Id, [stocks[0], stocks[0], stocks[1]], CancellationToken.None));
        bool foreign = await AsAsync(db, user, r => r.ReorderMembersAsync(user, portfolio.Id, [stocks[0], stocks[1], Guid.CreateVersion7()], CancellationToken.None));
        bool accepted = await AsAsync(db, user, r => r.ReorderMembersAsync(user, portfolio.Id, [stocks[2], stocks[0], stocks[1]], CancellationToken.None));

        (missing, repeated, foreign, accepted).Should().Be((false, false, false, true));
        (await AsAsync(db, user, r => r.MembersAsync(user, portfolio.Id, CancellationToken.None))).Select(m => (m.StockId, m.Position))
            .Should().Equal((stocks[2], 0), (stocks[0], 1), (stocks[1], 2));
    }

    // Three stocks: A only in the portfolio being deleted, B in it and in another portfolio, C on the watchlist in none.
    private static async Task<(Guid User, Portfolio Doomed, Portfolio Other, Guid A, Guid B, Guid C)> DeletionWorldAsync(MigratedDatabase db)
    {
        var user = Guid.CreateVersion7();
        Portfolio doomed = await AddAsync(db, user, "Petróleo", 0);
        Portfolio other = await AddAsync(db, user, "Energía", 1);
        Guid[] stocks = await FollowAsync(db, user, 3);
        await PutAsync(db, user, doomed, stocks[0]);
        await PutAsync(db, user, doomed, stocks[1]);
        await PutAsync(db, user, other, stocks[1]);
        return (user, doomed, other, stocks[0], stocks[1], stocks[2]);
    }

    [Fact]
    public async Task Preview_ListsOnlyTheStocksNoOtherPortfolioHolds()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        (Guid user, Portfolio doomed, Portfolio other, Guid a, _, _) = await DeletionWorldAsync(db);

        DeletionPreview preview = await AsAsync(db, user, r => r.PreviewDeletionAsync(user, doomed.Id, CancellationToken.None));
        DeletionPreview shared = await AsAsync(db, user, r => r.PreviewDeletionAsync(user, other.Id, CancellationToken.None));

        (preview.StocksToRemove, preview.StocksKept).Should().BeEquivalentTo((new[] { a }, 1));
        (shared.StocksToRemove, shared.StocksKept).Should().BeEquivalentTo((Array.Empty<Guid>(), 1), "its only stock is also in the other portfolio");
    }

    [Fact]
    public async Task DeletingWithOrphans_RemovesThePortfolioAndTheStocksNoOtherPortfolioHolds_AndNothingElse()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        (Guid user, Portfolio doomed, Portfolio other, Guid a, Guid b, Guid c) = await DeletionWorldAsync(db);

        bool deleted = await AsAsync(db, user, r => r.DeleteAsync(user, doomed.Id, removeOrphans: true, CancellationToken.None));
        bool again = await AsAsync(db, user, r => r.DeleteAsync(user, doomed.Id, removeOrphans: true, CancellationToken.None));

        (deleted, again).Should().Be((true, false));
        (await CountAsync(db, "portfolios", $"id = '{doomed.Id.Value}'")).Should().Be(0);
        (await CountAsync(db, "portfolios", $"id = '{other.Id.Value}'")).Should().Be(1);
        (await CountAsync(db, "watchlist_items", $"stock_id = '{a}'")).Should().Be(0, "only the deleted portfolio held it");
        (await CountAsync(db, "watchlist_items", $"stock_id = '{b}'")).Should().Be(1, "another portfolio holds it");
        (await CountAsync(db, "watchlist_items", $"stock_id = '{c}'")).Should().Be(1, "it was in no portfolio, so it was never at stake");
        (await AsAsync(db, user, r => r.MembersAsync(user, other.Id, CancellationToken.None))).Select(m => m.StockId).Should().Equal(b);
    }

    [Fact]
    public async Task DeletingWithoutOrphans_RemovesJustThePortfolioAndItsMemberships()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        (Guid user, Portfolio doomed, _, _, _, _) = await DeletionWorldAsync(db);

        await AsAsync(db, user, r => r.DeleteAsync(user, doomed.Id, removeOrphans: false, CancellationToken.None));

        (await CountAsync(db, "watchlist_items")).Should().Be(3);
        (await CountAsync(db, "portfolio_items")).Should().Be(1, "only the other portfolio's membership is left");
    }

    [Fact]
    public async Task ARequestThatPutsAStockInAnotherPortfolioWhileTheDeleteRuns_KeepsThatStock()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        (Guid user, Portfolio doomed, Portfolio other, Guid a, _, _) = await DeletionWorldAsync(db);
        await using DbSession adding = Session(db, user);
        await adding.BeginAsync(CancellationToken.None);
        var addingRepository = new PortfolioRepository(adding);
        (await addingRepository.AddMemberAsync(user, other.Id, a, _now, CancellationToken.None)).Should().Be(MemberAdd.Added);

        // The delete starts while the membership is uncommitted: it must wait for it, then see it.
        Task<bool> deleting = AsAsync(db, user, r => r.DeleteAsync(user, doomed.Id, removeOrphans: true, CancellationToken.None));
        await Task.Delay(500);
        deleting.IsCompleted.Should().BeFalse("it waits for the uncommitted membership of the stock it was about to remove");
        await adding.CommitAsync(CancellationToken.None);
        await deleting.WaitAsync(TimeSpan.FromSeconds(30));

        (await CountAsync(db, "watchlist_items", $"stock_id = '{a}'")).Should().Be(1, "the stock is held by another portfolio now");
        (await AsAsync(db, user, r => r.MembersAsync(user, other.Id, CancellationToken.None))).Select(m => m.StockId).Should().Contain(a);
    }

    [Fact]
    public async Task ARequestThatPutsAStockInAnotherPortfolioAfterTheDeleteRemovedIt_FindsItGone()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        (Guid user, Portfolio doomed, Portfolio other, Guid a, _, _) = await DeletionWorldAsync(db);
        await using DbSession deleting = Session(db, user);
        await deleting.BeginAsync(CancellationToken.None);
        (await new PortfolioRepository(deleting).DeleteAsync(user, doomed.Id, removeOrphans: true, CancellationToken.None)).Should().BeTrue();

        // The delete has locked and removed the item but not committed: the other request waits for it and then cannot add a stock that is gone.
        Task<MemberAdd> adding = PutAsync(db, user, other, a);
        await Task.Delay(500);
        adding.IsCompleted.Should().BeFalse();
        await deleting.CommitAsync(CancellationToken.None);

        (await adding.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(MemberAdd.ItemNotOnWatchlist);
        (await CountAsync(db, "portfolio_items", $"portfolio_id = '{other.Id.Value}'")).Should().Be(1, "only the shared stock, never a membership of a deleted item");
    }

    [Fact]
    public async Task MembersAddedInParallel_GetDistinctPositionsWithoutGaps()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var user = Guid.CreateVersion7();
        Portfolio portfolio = await AddAsync(db, user, "Carrera");
        Guid[] stocks = await FollowAsync(db, user, 12);

        MemberAdd[] results = await Task.WhenAll(stocks.Select(stock => PutAsync(db, user, portfolio, stock)));

        results.Should().OnlyContain(result => result == MemberAdd.Added);
        (await db.Admin.ScalarAsync<long>($"SELECT count(DISTINCT position) FROM watchlist.portfolio_items WHERE portfolio_id = '{portfolio.Id.Value}'")).Should().Be(12);
        (await db.Admin.ScalarAsync<int>($"SELECT max(position) FROM watchlist.portfolio_items WHERE portfolio_id = '{portfolio.Id.Value}'")).Should().Be(11);
    }

    [Fact]
    public async Task PortfoliosCreatedInParallel_GetDistinctPositionsWithoutGaps()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var user = Guid.CreateVersion7();

        // The way the use case creates one: lock the order, read the next position, insert.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(n => AsAsync(db, user, async repository =>
        {
            await repository.LockOrderAsync(user, CancellationToken.None);
            int next = (await repository.ListAsync(user, CancellationToken.None)).Count;
            return await repository.AddAsync(NewPortfolio(user, $"P{n}", next), CancellationToken.None);
        })));

        (await db.Admin.ScalarAsync<long>($"SELECT count(DISTINCT position) FROM watchlist.portfolios WHERE user_id = '{user}'")).Should().Be(8);
        (await db.Admin.ScalarAsync<int>($"SELECT max(position) FROM watchlist.portfolios WHERE user_id = '{user}'")).Should().Be(7);
    }

    [Fact]
    public async Task AMemberAddedWhileTheStocksAreBeingReordered_WaitsForTheOrder_AndLandsAfterIt()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var user = Guid.CreateVersion7();
        Portfolio portfolio = await AddAsync(db, user, "Orden");
        Guid[] stocks = await FollowAsync(db, user, 3);
        await PutAsync(db, user, portfolio, stocks[0]);
        await PutAsync(db, user, portfolio, stocks[1]);
        await using DbSession reordering = Session(db, user);
        await reordering.BeginAsync(CancellationToken.None);
        (await new PortfolioRepository(reordering).ReorderMembersAsync(user, portfolio.Id, [stocks[1], stocks[0]], CancellationToken.None)).Should().BeTrue();

        Task<MemberAdd> adding = PutAsync(db, user, portfolio, stocks[2]);
        await Task.Delay(500);
        adding.IsCompleted.Should().BeFalse("it queues behind the reorder instead of reading positions that are about to change");
        await reordering.CommitAsync(CancellationToken.None);
        (await adding.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(MemberAdd.Added);

        (await AsAsync(db, user, r => r.MembersAsync(user, portfolio.Id, CancellationToken.None))).Select(m => m.StockId).Should().Equal(stocks[1], stocks[0], stocks[2]);
    }
}
