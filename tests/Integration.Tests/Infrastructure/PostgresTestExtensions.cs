using Npgsql;

namespace SakerWatch.Integration.Tests.Infrastructure;

/// <summary>Small helpers to run SQL in tests without repeating data-source plumbing.</summary>
internal static class PostgresTestExtensions
{
    public static async Task<T?> ScalarAsync<T>(this string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlDataSource source = NpgsqlDataSource.Create(connectionString);
        await using NpgsqlCommand command = source.CreateCommand(sql);
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        object? result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    public static async Task<int> ExecuteAsync(this string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlDataSource source = NpgsqlDataSource.Create(connectionString);
        await using NpgsqlCommand command = source.CreateCommand(sql);
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>Connection string for a different login on the same database.</summary>
    public static string ForLogin(this string connectionString, string username, string password) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Username = username, Password = password, Pooling = false }.ConnectionString;
}
