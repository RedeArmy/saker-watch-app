using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using SakerWatch.BuildingBlocks.Persistence;

namespace SakerWatch.Integration.Tests.Persistence;

/// <summary>The pool of the runtime connection is sized on purpose (TD-026): these need no database.</summary>
public sealed class PostgresPoolingTests
{
    private const string _login = "Host=127.0.0.1;Database=saker;Username=saker_api;Password=s3cret;";

    private static int PoolOf(string connectionString) => new NpgsqlConnectionStringBuilder(connectionString).MaxPoolSize;

    [Fact]
    public void AConnectionStringThatSaysNothingAboutThePool_GetsTheSizeAsked()
    {
        string sized = PostgresPooling.Apply(_login, 35);

        PoolOf(sized).Should().Be(35);
        new NpgsqlConnectionStringBuilder(sized).Password.Should().Be("s3cret", "the rest of the string is kept");
    }

    [Theory]
    [InlineData("Maximum Pool Size=7", 7)]
    [InlineData("MaxPoolSize=7", 7)]
    [InlineData("Maximum Pool Size=100", 100)]
    public void APoolSizeInTheConnectionString_WinsOverTheDefault_UnderAnySpelling(string setting, int expected)
    {
        string sized = PostgresPooling.Apply(_login + setting, 20);

        PoolOf(sized).Should().Be(expected, "whoever wrote the size in the string knows the server it points to; 100 written down is a choice, not an absence");
    }

    [Fact]
    public void AStringNpgsqlCannotRead_IsReturnedAsItIs()
    {
        PostgresPooling.Apply("this is not; a connection=string=", 20).Should().Be("this is not; a connection=string=");
    }

    [Fact]
    public void TheValidator_RefusesAnUnreadableString_WithoutEchoingIt()
    {
        var options = new PostgresOptions { Postgres = "Host=db;Password=s3cret;Bogus Keyword=1" };

        ValidateOptionsResult result = new PostgresOptionsValidator().Validate(null, options);

        result.Failed.Should().BeTrue();
        string.Join(' ', result.Failures!).Should().NotContain("s3cret").And.Contain("ConnectionStrings:Postgres");
    }

    [Fact]
    public void TheValidator_AcceptsAReadableString_AndLeavesTheMissingOneToTheRequiredCheck()
    {
        new PostgresOptionsValidator().Validate(null, new PostgresOptions { Postgres = _login }).Succeeded.Should().BeTrue();
        new PostgresOptionsValidator().Validate(null, new PostgresOptions { Postgres = string.Empty }).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(null, 20)]
    [InlineData("12", 12)]
    public void TheRegisteredOptions_CarryTheSizedString(string? configured, int expected)
    {
        var settings = new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = _login };
        if (configured is not null)
        {
            settings["ConnectionStrings:PostgresMaxPoolSize"] = configured;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddSakerPostgres(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        string connection = provider.GetRequiredService<IOptions<PostgresOptions>>().Value.Postgres;

        PoolOf(connection).Should().Be(expected);
    }

    [Fact]
    public void APoolSizeOutsideTheRange_StopsTheHost()
    {
        var settings = new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = _login, ["ConnectionStrings:PostgresMaxPoolSize"] = "1" };
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddSakerPostgres(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        Func<PostgresOptions> read = () => provider.GetRequiredService<IOptions<PostgresOptions>>().Value;

        read.Should().Throw<OptionsValidationException>();
    }
}
