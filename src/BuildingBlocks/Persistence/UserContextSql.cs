using System.Data.Common;
using System.Globalization;

namespace SakerWatch.BuildingBlocks.Persistence;

/// <summary>The single place that writes <c>app.current_user_id</c> into a database session (ADR-0010).</summary>
internal static class UserContextSql
{
    public static void Apply(DbConnection connection, Guid? user)
    {
        using DbCommand command = Create(connection, user);
        command.ExecuteNonQuery();
    }

    public static async Task ApplyAsync(DbConnection connection, Guid? user, CancellationToken cancellationToken)
    {
        await using DbCommand command = Create(connection, user);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DbCommand Create(DbConnection connection, Guid? user)
    {
        DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT set_config('app.current_user_id', @user, false)";

        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "user";
        parameter.Value = user?.ToString("D", CultureInfo.InvariantCulture) ?? string.Empty;
        command.Parameters.Add(parameter);
        return command;
    }
}
