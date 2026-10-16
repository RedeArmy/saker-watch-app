using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SakerWatch.BuildingBlocks.Application;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Kernel.Transactions;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.BuildingBlocks.Web.Security;

namespace SakerWatch.Integration.Tests.Persistence;

/// <summary>
/// A host composes the pipeline, the persistence and the current user in whatever order its Program happens to list them. The
/// pipeline's no-op unit of work and anonymous user are defaults that must lose to the real ones in every order, otherwise commands
/// would silently run without a transaction (or as nobody).
/// </summary>
public sealed class CompositionOrderTests
{
    private static readonly string[][] _orders =
    [
        ["pipeline", "postgres", "user"],
        ["pipeline", "user", "postgres"],
        ["postgres", "pipeline", "user"],
        ["postgres", "user", "pipeline"],
        ["user", "pipeline", "postgres"],
        ["user", "postgres", "pipeline"],
    ];

    public static TheoryData<string> Orders => [.. _orders.Select(order => string.Join(" -> ", order))];

    [Theory]
    [MemberData(nameof(Orders))]
    public async Task TheRealUnitOfWorkAndTheRealUser_WinInEveryOrder(string order)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=unused",
            ["Audit:ActorPepper"] = Convert.ToBase64String(new byte[40]),
        }).Build();

        foreach (string step in order.Split(" -> "))
        {
            switch (step)
            {
                case "pipeline":
                    services.AddApplicationPipeline(typeof(SakerWatch.Identity.Application.IAssemblyMarker).Assembly);
                    break;
                case "postgres":
                    services.AddSakerPostgres(configuration);
                    break;
                default:
                    services.AddSakerCurrentUser();
                    break;
            }
        }

        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IUnitOfWork>().Should().BeOfType<DbSession>($"in the order {order}");
        scope.ServiceProvider.GetRequiredService<ICurrentUser>().Should().BeOfType<CurrentUserAccessor>($"in the order {order}");
    }
}
