using Npgsql;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.Identity.Application.Features.GetMe;
using SakerWatch.Identity.Application.Features.UpdatePreferences;

namespace SakerWatch.Identity.Infrastructure.Persistence;

/// <summary>
/// <see cref="IUserPreferencesWriter"/> over <c>identity.users</c>: one <c>UPDATE ... RETURNING</c> changes the locale and reads the
/// profile back, so the command costs one round trip. It runs in the command's unit of work and row-level security limits it to the
/// caller's own row.
/// </summary>
/// <param name="session">The scope's database session.</param>
public sealed class UserPreferencesWriter(DbSession session) : IUserPreferencesWriter
{
    /// <inheritdoc />
    public async Task<MeProfile?> SetLocaleAsync(Guid userId, string locale, DateTimeOffset now, CancellationToken cancellationToken)
    {
        NpgsqlTransaction transaction = session.Transaction
            ?? throw new InvalidOperationException("Updating preferences runs inside the command's unit of work.");

        await using var command = new NpgsqlCommand(
            """
            UPDATE identity.users
            SET locale = @locale, updated_at = @now
            WHERE id = @id AND status = 'active'
            RETURNING id, display_name, email, locale, created_at
            """,
            session.Connection,
            transaction);
        command.Parameters.AddWithValue("locale", locale);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());
        command.Parameters.AddWithValue("id", userId);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MeProfile(
            reader.GetGuid(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4));
    }
}
