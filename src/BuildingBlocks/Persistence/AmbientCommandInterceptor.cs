using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SakerWatch.BuildingBlocks.Persistence;

/// <summary>
/// Puts every command EF creates on the scope's transaction. <see cref="AmbientTransactionInterceptor"/> only covers
/// <c>SaveChanges</c>; queries, <c>ExecuteUpdate</c> and <c>ExecuteDelete</c> never go through it, and would otherwise depend on the
/// driver quietly running a command without a transaction inside the connection's open one. Making it explicit means a rollback
/// always undoes them, whatever the driver does. Without an active unit of work it does nothing.
/// </summary>
/// <param name="session">The scope's database session.</param>
public sealed class AmbientCommandInterceptor(DbSession session) : DbCommandInterceptor
{
    /// <inheritdoc />
    public override DbCommand CommandCreated(CommandEndEventData eventData, DbCommand result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Transaction is null && session.Transaction is { } transaction && ReferenceEquals(result.Connection, session.Connection))
        {
            result.Transaction = transaction;
        }

        return result;
    }
}
