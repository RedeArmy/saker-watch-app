using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace SakerWatch.BuildingBlocks.Persistence;

/// <summary>
/// Makes <c>SaveChanges</c> of any module context join the scope's transaction instead of starting its own, which is
/// what lets several contexts commit atomically. Without an active unit of work it does nothing and EF behaves normally.
/// </summary>
/// <param name="session">The scope's database session.</param>
public sealed class AmbientTransactionInterceptor(DbSession session) : SaveChangesInterceptor
{
    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (eventData.Context is { } context && session.Transaction is { } transaction && !IsJoined(context, transaction))
        {
            context.Database.UseTransaction(transaction);
        }

        return result;
    }

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (eventData.Context is { } context && session.Transaction is { } transaction && !IsJoined(context, transaction))
        {
            await context.Database.UseTransactionAsync(transaction, cancellationToken);
        }

        return result;
    }

    private static bool IsJoined(DbContext context, Npgsql.NpgsqlTransaction transaction) =>
        ReferenceEquals(context.Database.CurrentTransaction?.GetDbTransaction(), transaction);
}
