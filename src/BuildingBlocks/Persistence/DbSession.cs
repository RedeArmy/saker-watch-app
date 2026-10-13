using Microsoft.Extensions.Options;
using Npgsql;
using SakerWatch.BuildingBlocks.Kernel.Transactions;

namespace SakerWatch.BuildingBlocks.Persistence;

/// <summary>
/// The scope's single PostgreSQL connection and transaction. Every module DbContext in the scope uses
/// <see cref="Connection"/>, so one transaction can cover all of them (plus the idempotency row and the outbox)
/// and the whole use case commits or rolls back together (ADR-0005, ADR-0017). Outside a unit of work EF opens and
/// closes the connection per operation as usual, and the user-context interceptor tags each open.
/// </summary>
public sealed class DbSession : IUnitOfWork, IAsyncDisposable
{
    private readonly IDbUserContext _user;
    private NpgsqlTransaction? _transaction;
    private int _depth;
    private bool _rollbackOnly;

    /// <summary>Creates a session over the validated runtime connection string.</summary>
    /// <param name="options">Connection settings (least-privilege login).</param>
    /// <param name="user">Source of the validated user id.</param>
    public DbSession(IOptions<PostgresOptions> options, IDbUserContext user)
    {
        ArgumentNullException.ThrowIfNull(options);
        _user = user;
        Connection = new NpgsqlConnection(options.Value.Postgres);
    }

    /// <summary>The shared connection (not opened until needed).</summary>
    public NpgsqlConnection Connection { get; }

    /// <summary>The active transaction, or <see langword="null"/> outside a unit of work.</summary>
    public NpgsqlTransaction? Transaction => _transaction;

    /// <inheritdoc />
    public async Task BeginAsync(CancellationToken cancellationToken)
    {
        if (_depth++ > 0)
        {
            return;
        }

        _rollbackOnly = false;
        try
        {
            if (Connection.State != System.Data.ConnectionState.Open)
            {
                await Connection.OpenAsync(cancellationToken);
            }

            await UserContextSql.ApplyAsync(Connection, _user.CurrentUserId, cancellationToken);
            _transaction = await Connection.BeginTransactionAsync(cancellationToken);
        }
        catch
        {
            _depth = 0;
            await Connection.CloseAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (_depth == 0)
        {
            throw new InvalidOperationException("There is no active unit of work to commit.");
        }

        if (--_depth > 0)
        {
            return;
        }

        NpgsqlTransaction transaction = TakeTransaction();
        try
        {
            if (_rollbackOnly)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new InvalidOperationException("A nested unit of work was rolled back, so the outer one cannot commit.");
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            await transaction.DisposeAsync();
            await Connection.CloseAsync();
        }
    }

    /// <inheritdoc />
    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (_depth == 0)
        {
            return;
        }

        _rollbackOnly = true;
        if (--_depth > 0)
        {
            return;
        }

        NpgsqlTransaction transaction = TakeTransaction();
        try
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        finally
        {
            await transaction.DisposeAsync();
            await Connection.CloseAsync();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // A scope that ends mid-unit (an unhandled failure) must never leave work committed by accident.
        if (_transaction is not null)
        {
            await _transaction.DisposeAsync();
            _transaction = null;
        }

        await Connection.DisposeAsync();
    }

    private NpgsqlTransaction TakeTransaction()
    {
        NpgsqlTransaction transaction = _transaction ?? throw new InvalidOperationException("The unit of work has no transaction.");
        _transaction = null;
        return transaction;
    }
}
