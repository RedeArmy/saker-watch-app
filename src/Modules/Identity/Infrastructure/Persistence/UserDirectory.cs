using Npgsql;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.Identity.Application.Features.ProvisionUser;

namespace SakerWatch.Identity.Infrastructure.Persistence;

/// <summary>
/// <see cref="IUserDirectory"/> over <c>identity.provision_user</c>: a security-definer function, because at this point the
/// caller is not known yet and row-level security would hide the table. It runs in the command's unit of work.
/// </summary>
/// <param name="session">The scope's database session.</param>
public sealed class UserDirectory(DbSession session) : IUserDirectory
{
    /// <inheritdoc />
    public async Task<ResolvedUserResult> ProvisionAsync(Guid newId, ExternalIdentity identity, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        NpgsqlTransaction transaction = session.Transaction
            ?? throw new InvalidOperationException("Provisioning runs inside the command's unit of work.");

        await using var command = new NpgsqlCommand(
            "SELECT user_id, user_status, was_created FROM identity.provision_user(@id, @issuer, @subject, @email, @name, @locale, @now)",
            session.Connection,
            transaction);
        command.Parameters.AddWithValue("id", newId);
        command.Parameters.AddWithValue("issuer", identity.Issuer);
        command.Parameters.AddWithValue("subject", identity.Subject);
        command.Parameters.AddWithValue("email", (object?)identity.Email ?? DBNull.Value);
        command.Parameters.AddWithValue("name", (object?)identity.DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("locale", (object?)identity.Locale ?? DBNull.Value);
        command.Parameters.AddWithValue("now", now);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new ResolvedUserResult(new ResolvedUser(reader.GetGuid(0), ToStatus(reader.GetString(1))), reader.GetBoolean(2));
    }

    private static UserAccountStatus ToStatus(string status) => status switch
    {
        UserStatuses.Active => UserAccountStatus.Active,
        UserStatuses.PendingDeletion => UserAccountStatus.PendingDeletion,
        UserStatuses.Deleted => UserAccountStatus.Deleted,
        _ => throw new InvalidOperationException($"Unknown account status '{status}'."),
    };
}
