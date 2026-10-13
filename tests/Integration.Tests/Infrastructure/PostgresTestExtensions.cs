using Npgsql;

namespace SakerWatch.Integration.Tests.Infrastructure;

/// <summary>Small helpers to run SQL in tests without repeating data-source plumbing.</summary>
internal static class PostgresTestExtensions
{
    public static async Task<T?> ScalarAsync<T>(this string connectionString, string sql, params (string Name, object? Value)[] parameters)
    {
        await using NpgsqlDataSource source = NpgsqlDataSource.Create(connectionString);
        await using NpgsqlCommand command = source.CreateCommand(sql);
        AddParameters(command, parameters);

        object? result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    public static async Task<int> ExecuteAsync(this string connectionString, string sql, params (string Name, object? Value)[] parameters)
    {
        await using NpgsqlDataSource source = NpgsqlDataSource.Create(connectionString);
        await using NpgsqlCommand command = source.CreateCommand(sql);
        AddParameters(command, parameters);

        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Runs <paramref name="sql"/> on a fresh session of <paramref name="connectionString"/> with
    /// <c>app.current_user_id</c> set the way the API interceptor does (empty for no user).
    /// </summary>
    public static async Task<T?> AsUserScalarAsync<T>(this string connectionString, Guid? user, string sql, params (string Name, object? Value)[] parameters)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await SetUserAsync(connection, user);

        await using NpgsqlCommand command = new(sql, connection);
        AddParameters(command, parameters);
        object? result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    public static async Task<int> AsUserExecuteAsync(this string connectionString, Guid? user, string sql, params (string Name, object? Value)[] parameters)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await SetUserAsync(connection, user);

        await using NpgsqlCommand command = new(sql, connection);
        AddParameters(command, parameters);
        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>Connection string for a different login on the same database.</summary>
    public static string ForLogin(this string connectionString, string username, string password) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Username = username, Password = password, Pooling = false }.ConnectionString;

    private static async Task SetUserAsync(NpgsqlConnection connection, Guid? user)
    {
        await using NpgsqlCommand set = new("SELECT set_config('app.current_user_id', @u, false)", connection);
        set.Parameters.AddWithValue("u", user?.ToString("D") ?? string.Empty);
        await set.ExecuteScalarAsync();
    }

    private static void AddParameters(NpgsqlCommand command, (string Name, object? Value)[] parameters)
    {
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }
}
