using Microsoft.Extensions.Options;
using Npgsql;

namespace SakerWatch.BuildingBlocks.Persistence;

/// <summary>
/// Gives the runtime connection string a pool of a size chosen on purpose. Npgsql's default is 100 connections per process and PostgreSQL's default
/// <c>max_connections</c> is 100 for everybody, so the API, the worker and the migrator could not all be given what they ask for: a burst, or a second API
/// replica, ends in "too many clients" for some of them instead of a request that waits its turn in the pool.
/// </summary>
public static class PostgresPooling
{
    /// <summary>Keyword of the pool size in a connection string.</summary>
    public const string PoolSizeKeyword = "Maximum Pool Size";

    /// <summary>
    /// <paramref name="connectionString"/> with <c>Maximum Pool Size</c> set to <paramref name="maxPoolSize"/>, unless it already names one (under any of
    /// its spellings), which then wins. A string that cannot be read is returned unchanged: <see cref="PostgresOptionsValidator"/> reports it.
    /// </summary>
    public static string Apply(string connectionString, int maxPoolSize)
    {
        if (!TryParse(connectionString, out NpgsqlConnectionStringBuilder? parsed) || parsed is null || parsed.ShouldSerialize(PoolSizeKeyword))
        {
            return connectionString;
        }

        NpgsqlConnectionStringBuilder builder = parsed;
        builder.MaxPoolSize = maxPoolSize;
        return builder.ConnectionString;
    }

    internal static bool TryParse(string connectionString, out NpgsqlConnectionStringBuilder? builder)
    {
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
            return true;
        }
        catch (ArgumentException)
        {
            builder = null;
            return false;
        }
    }
}

/// <summary>Refuses a runtime connection string Npgsql cannot read, at start-up, without echoing it (it holds the password).</summary>
public sealed class PostgresOptionsValidator : IValidateOptions<PostgresOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, PostgresOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return string.IsNullOrWhiteSpace(options.Postgres) || PostgresPooling.TryParse(options.Postgres, out _)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("ConnectionStrings:Postgres is not a PostgreSQL connection string Npgsql can read.");
    }
}
