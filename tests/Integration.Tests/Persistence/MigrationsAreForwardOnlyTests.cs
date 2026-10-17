using System.Reflection;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SakerWatch.BuildingBlocks.Persistence.Platform;
using SakerWatch.Identity.Infrastructure.Persistence;
using SakerWatch.MarketData.Infrastructure.Persistence;

namespace SakerWatch.Integration.Tests.Persistence;

public sealed class MigrationsAreForwardOnlyTests
{
    public static TheoryData<string> MigrationNames()
    {
        var data = new TheoryData<string>();
        foreach (Type type in AllMigrations())
        {
            data.Add(type.FullName!);
        }

        return data;
    }

    [Fact]
    public void BothDatabasesHaveMigrations_SoTheRuleBelowChecksSomething() =>
        AllMigrations().Should().HaveCountGreaterThan(10);

    [Theory]
    [MemberData(nameof(MigrationNames))]
    public void Down_Throws_BecauseMigrationsAreForwardOnly(string migrationType)
    {
        Type type = AllMigrations().Single(t => t.FullName == migrationType);
        var migration = (Migration)Activator.CreateInstance(type)!;

        Action rollback = () => _ = migration.DownOperations;

        rollback.Should().Throw<NotSupportedException>("a rollback that drops tables would destroy user data (ADR-0025)");
    }

    private static List<Type> AllMigrations() =>
        new[] { typeof(PlatformDbContext).Assembly, typeof(IdentityDbContext).Assembly, typeof(MarketDataDbContext).Assembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsAbstract: false } && t.IsSubclassOf(typeof(Migration)) && t.GetCustomAttribute<MigrationAttribute>() is not null)
            .ToList();
}
