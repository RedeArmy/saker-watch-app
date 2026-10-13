// A throwaway use case that stands in for a real command: it writes one row through a module-style DbContext.
using Microsoft.EntityFrameworkCore;
using SakerWatch.BuildingBlocks.Application.Idempotency;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Security;

namespace SakerWatch.Demo.IntegrationFeatures;

internal sealed class TestUser : ICurrentUser
{
    public Guid? Id { get; set; }

    public Guid? UserId => Id;
}

internal sealed class ProbeItemsContext(DbContextOptions<ProbeItemsContext> options) : DbContext(options)
{
    public DbSet<ProbeItemRow> Items => Set<ProbeItemRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<ProbeItemRow>(e =>
        {
            e.ToTable("probe_items", "public");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Name).HasColumnName("name");
        });
}

internal sealed class ProbeItemRow
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>Counts handler executions and can hold one execution open to model a slow first request.</summary>
internal sealed class HandlerProbe
{
    private int _executions;
    private TaskCompletionSource? _gate;
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Executions => Volatile.Read(ref _executions);

    public Task EnteredFirstExecution => _entered.Task;

    public void HoldNextExecution() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _gate?.TrySetResult();

    public async Task OnExecutedAsync()
    {
        Interlocked.Increment(ref _executions);
        if (_gate is { } gate)
        {
            _entered.TrySetResult();
            await gate.Task;
        }
    }
}

internal sealed record ItemDto(Guid Id, string Name);

internal sealed record CreateItemCommand(
    IdempotencyKeyInfo IdempotencyKey,
    string Name,
    bool ThrowAfterInsert = false,
    bool ReturnFailure = false) : ICommand<ItemDto>, IIdempotentRequest;

internal sealed record PlainCreateItemCommand(string Name) : ICommand<ItemDto>;

internal sealed class CreateItemHandler(ProbeItemsContext db, HandlerProbe probe) : IRequestHandler<CreateItemCommand, Result<ItemDto>>
{
    public async ValueTask<Result<ItemDto>> Handle(CreateItemCommand request, CancellationToken cancellationToken)
    {
        var row = new ProbeItemRow { Id = Guid.CreateVersion7(), Name = request.Name };
        db.Items.Add(row);
        await db.SaveChangesAsync(cancellationToken);

        if (request.ThrowAfterInsert)
        {
            throw new InvalidOperationException("crash after the effect");
        }

        await probe.OnExecutedAsync();
        return request.ReturnFailure
            ? Error.Conflict("demo.item_exists", "Already exists.")
            : new ItemDto(row.Id, row.Name);
    }
}

internal sealed class PlainCreateItemHandler(ProbeItemsContext db) : IRequestHandler<PlainCreateItemCommand, Result<ItemDto>>
{
    public async ValueTask<Result<ItemDto>> Handle(PlainCreateItemCommand request, CancellationToken cancellationToken)
    {
        var row = new ProbeItemRow { Id = Guid.CreateVersion7(), Name = request.Name };
        db.Items.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return new ItemDto(row.Id, row.Name);
    }
}
