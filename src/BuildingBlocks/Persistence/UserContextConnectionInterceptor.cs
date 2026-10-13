using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SakerWatch.BuildingBlocks.Kernel.Security;

namespace SakerWatch.BuildingBlocks.Persistence;

/// <summary>
/// Sets the PostgreSQL session variable <c>app.current_user_id</c> every time EF opens a connection, which the
/// row-level security policies read (ADR-0010). It is set on every open, including with no user (empty string), so a
/// pooled connection can never carry the previous caller's identity into the next request.
/// </summary>
/// <param name="user">Source of the validated user id.</param>
public sealed class UserContextConnectionInterceptor(ICurrentUser user) : DbConnectionInterceptor
{
    /// <summary>Name of the session variable the policies read.</summary>
    public const string SettingName = "app.current_user_id";

    /// <inheritdoc />
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        UserContextSql.Apply(connection, user.UserId);

    /// <inheritdoc />
    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default) =>
        UserContextSql.ApplyAsync(connection, user.UserId, cancellationToken);
}
