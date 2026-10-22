using AwesomeAssertions;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SakerWatch.Hosts.Migrator;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.Watchlist.Infrastructure.Persistence;

namespace SakerWatch.Integration.Tests.Persistence;

[Trait("Category", "Integration")]
public sealed class WatchlistSchemaTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    private const string _insertItem =
        "INSERT INTO watchlist.watchlist_items (id, user_id, stock_id, notes, target_price, created_at, updated_at) VALUES (@id, @user, @stock, @notes, @target, now(), now())";

    private const string _insertPortfolio =
        "INSERT INTO watchlist.portfolios (id, user_id, name, description, position, created_at, updated_at) VALUES (@id, @user, @name, @description, @position, now(), now())";

    private const string _insertMember =
        "INSERT INTO watchlist.portfolio_items (portfolio_id, watchlist_item_id, user_id, position, added_at) VALUES (@portfolio, @item, @user, 0, now())";

    private static (string Name, object? Value)[] Item(Guid id, Guid user, Guid? stock = null, string? notes = null, decimal? target = null) =>
        [("id", id), ("user", user), ("stock", stock ?? Guid.CreateVersion7()), ("notes", notes), ("target", target)];

    private static (string Name, object? Value)[] Portfolio(Guid id, Guid user, string name = "Tecnología", string? description = null, int position = 0) =>
        [("id", id), ("user", user), ("name", name), ("description", description), ("position", position)];

    private static async Task ExpectAsync(Func<Task> action, string sqlState, string? constraint = null)
    {
        PostgresException error = (await action.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(sqlState);
        if (constraint is not null)
        {
            error.ConstraintName.Should().Be(constraint);
        }
    }

    // As the API does: a connection of the API role with the user set. Rows are written as that user, so the policies and triggers apply.
    private static async Task<Guid> AddItemAsync(MigratedDatabase db, Guid user, Guid? stock = null)
    {
        var id = Guid.CreateVersion7();
        await db.Api.AsUserExecuteAsync(user, _insertItem, Item(id, user, stock));
        return id;
    }

    private static async Task<Guid> AddPortfolioAsync(MigratedDatabase db, Guid user, string name = "Tecnología")
    {
        var id = Guid.CreateVersion7();
        await db.Api.AsUserExecuteAsync(user, _insertPortfolio, Portfolio(id, user, name));
        return id;
    }

    [Fact]
    public async Task Migrate_OnAnEmptyDatabase_CreatesTheThreeTables()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();

        (await db.Admin.ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'watchlist' AND table_name IN ('watchlist_items', 'portfolios', 'portfolio_items')"))
            .Should().Be(3);
    }

    [Fact]
    public async Task Migrate_RunTwice_SecondRunAppliesNothing()
    {
        string admin = await postgres.CreateDatabaseAsync();
        await MigrationSteps.PlatformStep(admin).RunAsync(CancellationToken.None);
        var step = MigrationSteps.WatchlistStep(admin);

        int first = await step.RunAsync(CancellationToken.None);
        int second = await step.RunAsync(CancellationToken.None);

        first.Should().Be(typeof(WatchlistDbContext).Assembly.GetTypes().Count(type => type is { IsAbstract: false } && type.IsSubclassOf(typeof(Migration))), "every migration of the assembly is applied once");
        second.Should().Be(0);
    }

    [Fact]
    public async Task RowLevelSecurity_EachUserSeesOnlyTheirOwnRows_InEveryTable()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        Guid alice = Guid.CreateVersion7(), bob = Guid.CreateVersion7();
        Guid aliceItem = await AddItemAsync(db, alice);
        Guid alicePortfolio = await AddPortfolioAsync(db, alice);
        await db.Api.AsUserExecuteAsync(alice, _insertMember, ("portfolio", alicePortfolio), ("item", aliceItem), ("user", alice));
        await AddItemAsync(db, bob);

        foreach (string table in new[] { "watchlist_items", "portfolios", "portfolio_items" })
        {
            (await db.Api.AsUserScalarAsync<long>(alice, $"SELECT count(*) FROM watchlist.{table}")).Should().Be(1, table);
        }

        (await db.Api.AsUserScalarAsync<long>(bob, "SELECT count(*) FROM watchlist.watchlist_items")).Should().Be(1);
        (await db.Api.AsUserScalarAsync<long>(bob, "SELECT count(*) FROM watchlist.portfolios")).Should().Be(0);
        (await db.Api.AsUserScalarAsync<long>(bob, "SELECT count(*) FROM watchlist.portfolio_items")).Should().Be(0);
        (await db.Api.AsUserScalarAsync<long>(null, "SELECT count(*) FROM watchlist.watchlist_items")).Should().Be(0, "no user means no rows");
    }

    [Fact]
    public async Task RowLevelSecurity_AUserCannotChangeDeleteOrPlantRowsOfAnother()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        Guid alice = Guid.CreateVersion7(), bob = Guid.CreateVersion7();
        Guid aliceItem = await AddItemAsync(db, alice);
        Guid alicePortfolio = await AddPortfolioAsync(db, alice);

        (await db.Api.AsUserExecuteAsync(bob, "UPDATE watchlist.watchlist_items SET notes = 'mine'")).Should().Be(0);
        (await db.Api.AsUserExecuteAsync(bob, "DELETE FROM watchlist.watchlist_items")).Should().Be(0);
        (await db.Api.AsUserExecuteAsync(bob, "DELETE FROM watchlist.portfolios")).Should().Be(0);
        await ExpectAsync(() => db.Api.AsUserExecuteAsync(bob, _insertItem, Item(Guid.CreateVersion7(), alice)), "42501");
        await ExpectAsync(() => db.Api.AsUserExecuteAsync(bob, _insertPortfolio, Portfolio(Guid.CreateVersion7(), alice, "Otra")), "42501");
        await ExpectAsync(() => db.Api.AsUserExecuteAsync(null, _insertItem, Item(Guid.CreateVersion7(), alice)), "42501");
        await ExpectAsync(
            () => db.Api.AsUserExecuteAsync(alice, "UPDATE watchlist.watchlist_items SET user_id = @bob", ("bob", bob)), "42501");
        (await db.Admin.ScalarAsync<string?>("SELECT notes FROM watchlist.watchlist_items WHERE id = @id", ("id", aliceItem))).Should().BeNull();
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM watchlist.portfolios WHERE id = @id", ("id", alicePortfolio))).Should().Be(1);
    }

    [Fact]
    public async Task Membership_CannotMixTwoUsers_EvenWithTheBypass()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        Guid alice = Guid.CreateVersion7(), bob = Guid.CreateVersion7();
        Guid aliceItem = await AddItemAsync(db, alice);
        Guid bobPortfolio = await AddPortfolioAsync(db, bob);

        // The administrator connection is not subject to the policies: only the composite foreign keys stand in the way.
        await ExpectAsync(() => db.Admin.ExecuteAsync(_insertMember, ("portfolio", bobPortfolio), ("item", aliceItem), ("user", alice)), "23503");
        await ExpectAsync(() => db.Admin.ExecuteAsync(_insertMember, ("portfolio", bobPortfolio), ("item", aliceItem), ("user", bob)), "23503");
    }

    [Fact]
    public async Task AStockIsOnceInAWatchlist_ButSeveralUsersMayTrackTheSameStock()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        Guid alice = Guid.CreateVersion7(), bob = Guid.CreateVersion7(), stock = Guid.CreateVersion7();
        await AddItemAsync(db, alice, stock);

        await AddItemAsync(db, bob, stock);

        await ExpectAsync(() => AddItemAsync(db, alice, stock), "23505");
    }

    [Fact]
    public async Task PortfolioNames_AreUniquePerUserIgnoringLetterCase_ButNotAcrossUsers()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        Guid alice = Guid.CreateVersion7(), bob = Guid.CreateVersion7();
        await AddPortfolioAsync(db, alice, "Tecnología");

        await AddPortfolioAsync(db, bob, "Tecnología");
        await AddPortfolioAsync(db, alice, "Salud");

        await ExpectAsync(() => AddPortfolioAsync(db, alice, "TECNOLOGÍA"), "23505");
        await ExpectAsync(() => AddPortfolioAsync(db, alice, "tecnología"), "23505");
    }

    [Theory]
    [InlineData("ck_portfolios_name", " Tecnología")]
    [InlineData("ck_portfolios_name", "Tecnología ")]
    [InlineData("ck_portfolios_name", "")]
    public async Task PortfolioName_IsTrimmedAndBounded_ByTheDatabaseToo(string constraint, string name)
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var user = Guid.CreateVersion7();

        await ExpectAsync(() => db.Api.AsUserExecuteAsync(user, _insertPortfolio, Portfolio(Guid.CreateVersion7(), user, name)), "23514", constraint);
    }

    [Fact]
    public async Task PortfolioName_OverSixtyCharacters_DoesNotFitTheColumn()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var user = Guid.CreateVersion7();

        await ExpectAsync(() => db.Api.AsUserExecuteAsync(user, _insertPortfolio, Portfolio(Guid.CreateVersion7(), user, new string('x', 61))), "22001");
        await db.Api.AsUserExecuteAsync(user, _insertPortfolio, Portfolio(Guid.CreateVersion7(), user, new string('x', 60)));
    }

    [Fact]
    public async Task OtherChecks_RefuseAnEmptyDescriptionNoteAWorthlessTargetAndANegativePosition()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var user = Guid.CreateVersion7();

        await ExpectAsync(() => db.Api.AsUserExecuteAsync(user, _insertPortfolio, Portfolio(Guid.CreateVersion7(), user, description: "")), "23514", "ck_portfolios_description");
        await ExpectAsync(() => db.Api.AsUserExecuteAsync(user, _insertPortfolio, Portfolio(Guid.CreateVersion7(), user, position: -1)), "23514", "ck_portfolios_position");
        await ExpectAsync(() => db.Api.AsUserExecuteAsync(user, _insertItem, Item(Guid.CreateVersion7(), user, notes: "")), "23514", "ck_watchlist_items_notes");
        await ExpectAsync(() => db.Api.AsUserExecuteAsync(user, _insertItem, Item(Guid.CreateVersion7(), user, target: 0m)), "23514", "ck_watchlist_items_target_price");
        await db.Api.AsUserExecuteAsync(user, _insertItem, Item(Guid.CreateVersion7(), user, notes: "Esperar el informe", target: 123.45678901m));
        (await db.Admin.ScalarAsync<decimal>("SELECT target_price FROM watchlist.watchlist_items")).Should().Be(123.45678901m);
    }

    [Fact]
    public async Task TheWatchlist_StopsAtFiveHundredStocksForEachUser()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        Guid alice = Guid.CreateVersion7(), bob = Guid.CreateVersion7();
        await db.Api.AsUserExecuteAsync(
            alice,
            "INSERT INTO watchlist.watchlist_items (id, user_id, stock_id, created_at, updated_at) SELECT gen_random_uuid(), @user, gen_random_uuid(), now(), now() FROM generate_series(1, 500)",
            ("user", alice));

        await ExpectAsync(() => AddItemAsync(db, alice), "23514", "ck_watchlist_items_max_per_user");
        await AddItemAsync(db, bob);
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM watchlist.watchlist_items WHERE user_id = @user", ("user", alice))).Should().Be(500);
    }

    // Every attempt holds its own transaction open for a moment after the insert, as a slow request would. Without the lock in the trigger, all of
    // them count the same rows, pass, and commit together; with it, each waits for the one before and counts again.
    private static async Task<int> RaceAsync(MigratedDatabase db, Guid user, int attempts, Func<int, (string Sql, (string Name, object? Value)[] Parameters)> insert, string constraint)
    {
        var connections = new List<NpgsqlConnection>();
        for (int i = 0; i < attempts; i++)
        {
            var connection = new NpgsqlConnection(db.Api);
            await connection.OpenAsync();
            await using (var set = new NpgsqlCommand("SELECT set_config('app.current_user_id', @u, false)", connection))
            {
                set.Parameters.AddWithValue("u", user.ToString("D"));
                await set.ExecuteScalarAsync();
            }

            connections.Add(connection);
        }

        var go = new TaskCompletionSource();
        Task<bool>[] tasks = [.. connections.Select(async (connection, i) =>
        {
            await go.Task;
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
            try
            {
                (string sql, (string Name, object? Value)[] parameters) = insert(i);
                await using (var command = new NpgsqlCommand(sql, connection, transaction))
                {
                    foreach ((string name, object? value) in parameters)
                    {
                        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
                    }

                    await command.ExecuteNonQueryAsync();
                }

                await using (var pause = new NpgsqlCommand("SELECT pg_sleep(0.3)", connection, transaction))
                {
                    await pause.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();
                return true;
            }
            catch (PostgresException error) when (error.ConstraintName == constraint)
            {
                return false;
            }
        })];
        go.SetResult();
        bool[] results = await Task.WhenAll(tasks);
        foreach (NpgsqlConnection connection in connections)
        {
            await connection.DisposeAsync();
        }

        return results.Count(accepted => accepted);
    }

    [Fact]
    public async Task TheWatchlistCeiling_HoldsWhenRequestsRaceForTheLastPlace()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var user = Guid.CreateVersion7();
        await db.Api.AsUserExecuteAsync(
            user,
            "INSERT INTO watchlist.watchlist_items (id, user_id, stock_id, created_at, updated_at) SELECT gen_random_uuid(), @user, gen_random_uuid(), now(), now() FROM generate_series(1, 499)",
            ("user", user));

        int accepted = await RaceAsync(db, user, 6, _ => (_insertItem, Item(Guid.CreateVersion7(), user)), "ck_watchlist_items_max_per_user");

        accepted.Should().Be(1, "only one of the six fits");
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM watchlist.watchlist_items WHERE user_id = @user", ("user", user))).Should().Be(500);
    }

    [Fact]
    public async Task APortfolioCeiling_HoldsWhenRequestsRaceForTheLastPlace()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var user = Guid.CreateVersion7();
        for (int i = 0; i < 19; i++)
        {
            await AddPortfolioAsync(db, user, $"Cartera {i}");
        }

        int accepted = await RaceAsync(db, user, 6, i => (_insertPortfolio, Portfolio(Guid.CreateVersion7(), user, $"Carrera {i}")), "ck_portfolios_max_per_user");

        accepted.Should().Be(1);
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM watchlist.portfolios WHERE user_id = @user", ("user", user))).Should().Be(20);
        await ExpectAsync(() => AddPortfolioAsync(db, user, "Una más"), "23514", "ck_portfolios_max_per_user");
    }

    [Fact]
    public async Task APortfolioMembershipCeiling_HoldsWhenRequestsRaceForTheLastPlace()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var user = Guid.CreateVersion7();
        Guid portfolio = await AddPortfolioAsync(db, user);
        await db.Api.AsUserExecuteAsync(
            user,
            "INSERT INTO watchlist.watchlist_items (id, user_id, stock_id, created_at, updated_at) SELECT gen_random_uuid(), @user, gen_random_uuid(), now(), now() FROM generate_series(1, 105)",
            ("user", user));
        Guid[] items = [.. await ItemsOfAsync(db, user)];
        foreach (Guid item in items.Take(99))
        {
            await db.Api.AsUserExecuteAsync(user, _insertMember, ("portfolio", portfolio), ("item", item), ("user", user));
        }

        int accepted = await RaceAsync(db, user, 6, i => (_insertMember, [("portfolio", portfolio), ("item", items[99 + i]), ("user", user)]), "ck_portfolio_items_max_per_portfolio");

        accepted.Should().Be(1);
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM watchlist.portfolio_items WHERE portfolio_id = @p", ("p", portfolio))).Should().Be(100);
    }

    [Fact]
    public async Task APortfolio_HoldsAtMostAHundredStocks_AndTheLimitIsPerPortfolio()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var user = Guid.CreateVersion7();
        Guid full = await AddPortfolioAsync(db, user, "Llena");
        Guid other = await AddPortfolioAsync(db, user, "Otra");
        await db.Api.AsUserExecuteAsync(
            user,
            "INSERT INTO watchlist.watchlist_items (id, user_id, stock_id, created_at, updated_at) SELECT gen_random_uuid(), @user, gen_random_uuid(), now(), now() FROM generate_series(1, 101)",
            ("user", user));
        Guid[] items = [.. await ItemsOfAsync(db, user)];
        foreach (Guid item in items.Take(100))
        {
            await db.Api.AsUserExecuteAsync(user, _insertMember, ("portfolio", full), ("item", item), ("user", user));
        }

        await ExpectAsync(() => db.Api.AsUserExecuteAsync(user, _insertMember, ("portfolio", full), ("item", items[100]), ("user", user)), "23514", "ck_portfolio_items_max_per_portfolio");
        await db.Api.AsUserExecuteAsync(user, _insertMember, ("portfolio", other), ("item", items[100]), ("user", user));
    }

    [Fact]
    public async Task Deleting_AWatchlistItemOrAPortfolio_RemovesOnlyTheMemberships()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var user = Guid.CreateVersion7();
        Guid itemA = await AddItemAsync(db, user), itemB = await AddItemAsync(db, user);
        Guid portfolio = await AddPortfolioAsync(db, user);
        await db.Api.AsUserExecuteAsync(user, _insertMember, ("portfolio", portfolio), ("item", itemA), ("user", user));
        await db.Api.AsUserExecuteAsync(user, _insertMember, ("portfolio", portfolio), ("item", itemB), ("user", user));

        await db.Api.AsUserExecuteAsync(user, "DELETE FROM watchlist.watchlist_items WHERE id = @id", ("id", itemA));
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM watchlist.portfolio_items")).Should().Be(1);

        await db.Api.AsUserExecuteAsync(user, "DELETE FROM watchlist.portfolios WHERE id = @id", ("id", portfolio));
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM watchlist.portfolio_items")).Should().Be(0);
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM watchlist.watchlist_items")).Should().Be(1, "the stock itself stays on the watchlist");
    }

    [Fact]
    public async Task TheWorker_ReadsNoTable_AndLearnsTheTrackedStocksOnlyThroughTheFunction()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        Guid alice = Guid.CreateVersion7(), bob = Guid.CreateVersion7(), shared = Guid.CreateVersion7(), onlyBob = Guid.CreateVersion7();
        await AddItemAsync(db, alice, shared);
        await AddItemAsync(db, bob, shared);
        await AddItemAsync(db, bob, onlyBob);

        await ExpectAsync(() => db.Worker.ExecuteAsync("SELECT * FROM watchlist.watchlist_items"), "42501");
        await ExpectAsync(() => db.Worker.ExecuteAsync("SELECT * FROM watchlist.portfolios"), "42501");
        await ExpectAsync(() => db.Worker.ExecuteAsync("DELETE FROM watchlist.portfolio_items"), "42501");
        await using var connection = new NpgsqlConnection(db.Worker);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT * FROM watchlist.tracked_stock_ids()", connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        var seen = new List<Guid>();
        while (await reader.ReadAsync())
        {
            seen.Add(reader.GetGuid(0));
        }

        reader.FieldCount.Should().Be(1, "no user id and no note travel with the stock ids");
        seen.Should().BeEquivalentTo([shared, onlyBob]);
    }

    [Fact]
    public async Task TheApiRole_CannotCallTheTrackedStocksFunction_AndPublicHasNothing()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();

        await ExpectAsync(() => db.Api.AsUserExecuteAsync(Guid.CreateVersion7(), "SELECT * FROM watchlist.tracked_stock_ids()"), "42501");
        (await db.Admin.ScalarAsync<bool>("SELECT has_table_privilege('public', 'watchlist.watchlist_items', 'SELECT, INSERT, UPDATE, DELETE')")).Should().BeFalse();
        (await db.Admin.ScalarAsync<bool>("SELECT has_table_privilege('api_role', 'watchlist.portfolios', 'TRUNCATE')")).Should().BeFalse();
        (await db.Admin.ScalarAsync<bool>("SELECT has_function_privilege('public', 'watchlist.tracked_stock_ids()', 'EXECUTE')")).Should().BeFalse();
    }

    private static async Task<IEnumerable<Guid>> ItemsOfAsync(MigratedDatabase db, Guid user)
    {
        await using var connection = new NpgsqlConnection(db.Admin);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT id FROM watchlist.watchlist_items WHERE user_id = @user ORDER BY id", connection);
        command.Parameters.AddWithValue("user", user);
        var ids = new List<Guid>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    [Fact]
    public async Task UpgradeFromTheFirstVersion_KeepsEveryRow_AndStartsToIsolateThemByUser()
    {
        string admin = await postgres.CreateDatabaseAsync();
        await MigrationSteps.PlatformStep(admin).RunAsync(CancellationToken.None);
        var step = MigrationSteps.WatchlistStep(admin);
        await step.MigrateToAsync("CreateWatchlist", CancellationToken.None);
        Guid ana = Guid.CreateVersion7(), ben = Guid.CreateVersion7(), portfolio = Guid.CreateVersion7(), item = Guid.CreateVersion7();
        await admin.ExecuteAsync(_insertItem, Item(item, ana));
        await admin.ExecuteAsync(_insertItem, Item(Guid.CreateVersion7(), ben));
        await admin.ExecuteAsync(_insertPortfolio, Portfolio(portfolio, ana));
        await admin.ExecuteAsync(_insertMember, ("portfolio", portfolio), ("item", item), ("user", ana));

        await step.RunAsync(CancellationToken.None);

        string api = await TimescalePostgresFixture.CreateRoleLoginAsync(admin, "api_role");
        (await admin.ScalarAsync<long>("SELECT count(*) FROM watchlist.watchlist_items")).Should().Be(2, "the upgrade keeps what was there");
        (await api.AsUserScalarAsync<long>(ana, "SELECT count(*) FROM watchlist.watchlist_items")).Should().Be(1);
        (await api.AsUserScalarAsync<long>(ben, "SELECT count(*) FROM watchlist.portfolios")).Should().Be(0);
        (await api.AsUserScalarAsync<long>(ana, "SELECT count(*) FROM watchlist.portfolio_items")).Should().Be(1);
    }

    [Fact]
    public async Task AtTheWatchlistCeiling_AddingAStockAlreadyThere_IsAQuietNoOp_AndANewOneIsStillRefused()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var user = Guid.CreateVersion7();
        var held = Guid.CreateVersion7();
        await db.Api.AsUserExecuteAsync(user, _insertItem, Item(Guid.CreateVersion7(), user, held));
        await db.Api.AsUserExecuteAsync(
            user,
            "INSERT INTO watchlist.watchlist_items (id, user_id, stock_id, created_at, updated_at) SELECT gen_random_uuid(), @user, gen_random_uuid(), now(), now() FROM generate_series(1, 499)",
            ("user", user));
        const string onConflict = _insertItem + " ON CONFLICT (user_id, stock_id) DO NOTHING";

        int again = await db.Api.AsUserExecuteAsync(user, onConflict, Item(Guid.CreateVersion7(), user, held));

        again.Should().Be(0, "the stock is already there, so the statement does nothing and the ceiling is not the answer");
        await ExpectAsync(() => db.Api.AsUserExecuteAsync(user, onConflict, Item(Guid.CreateVersion7(), user)), "23514", "ck_watchlist_items_max_per_user");
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM watchlist.watchlist_items WHERE user_id = @user", ("user", user))).Should().Be(500);
    }

    [Fact]
    public async Task AtThePortfolioCeiling_CreatingOneWithAnExistingName_IsAQuietNoOp_AndANewNameIsStillRefused()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var user = Guid.CreateVersion7();
        for (int i = 0; i < 20; i++)
        {
            await AddPortfolioAsync(db, user, $"P{i}");
        }

        const string onConflict = _insertPortfolio + " ON CONFLICT (user_id, lower(name)) DO NOTHING";
        int sameName = await db.Api.AsUserExecuteAsync(user, onConflict, Portfolio(Guid.CreateVersion7(), user, "p3"));

        sameName.Should().Be(0);
        await ExpectAsync(() => db.Api.AsUserExecuteAsync(user, onConflict, Portfolio(Guid.CreateVersion7(), user, "A new one")), "23514", "ck_portfolios_max_per_user");
    }

    [Fact]
    public async Task AtThePortfolioMembershipCeiling_AddingAMemberAlreadyThere_IsAQuietNoOp_AndANewMemberIsStillRefused()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var user = Guid.CreateVersion7();
        Guid portfolio = await AddPortfolioAsync(db, user);
        await db.Api.AsUserExecuteAsync(
            user,
            "INSERT INTO watchlist.watchlist_items (id, user_id, stock_id, created_at, updated_at) SELECT gen_random_uuid(), @user, gen_random_uuid(), now(), now() FROM generate_series(1, 101)",
            ("user", user));
        Guid[] items = [.. await ItemsOfAsync(db, user)];
        foreach (Guid item in items.Take(100))
        {
            await db.Api.AsUserExecuteAsync(user, _insertMember, ("portfolio", portfolio), ("item", item), ("user", user));
        }

        const string onConflict = _insertMember + " ON CONFLICT (portfolio_id, watchlist_item_id) DO NOTHING";
        int again = await db.Api.AsUserExecuteAsync(user, onConflict, ("portfolio", portfolio), ("item", items[0]), ("user", user));

        again.Should().Be(0);
        await ExpectAsync(() => db.Api.AsUserExecuteAsync(user, onConflict, ("portfolio", portfolio), ("item", items[100]), ("user", user)), "23514", "ck_portfolio_items_max_per_portfolio");
    }
}
