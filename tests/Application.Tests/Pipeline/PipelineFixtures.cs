using FluentValidation;
using SakerWatch.BuildingBlocks.Application.Events;
using SakerWatch.BuildingBlocks.Application.Idempotency;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Domain;
using SakerWatch.BuildingBlocks.Kernel.Results;

// The namespace's second segment is the module name the telemetry derives (SakerWatch.<Module>.…).
namespace SakerWatch.Demo.Features;

/// <summary>Records the order in which behaviors and handlers run.</summary>
public sealed class CallLog
{
    private readonly List<string> _entries = [];

    public IReadOnlyList<string> Entries => _entries;

    public void Add(string entry) => _entries.Add(entry);
}

public sealed record PingCommand(string Name) : ICommand<string>;

public sealed class PingValidator : AbstractValidator<PingCommand>
{
    public PingValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Name is required.");
        RuleFor(x => x.Name).MaximumLength(10).WithMessage("Name is too long.");
    }
}

public sealed class PingHandler(CallLog log) : IRequestHandler<PingCommand, Result<string>>
{
    public ValueTask<Result<string>> Handle(PingCommand request, CancellationToken cancellationToken)
    {
        log.Add("handler");
        return ValueTask.FromResult(Result.Success($"pong:{request.Name}"));
    }
}

public sealed record RejectedCommand : ICommand;

public sealed class RejectedHandler : IRequestHandler<RejectedCommand, Result>
{
    public ValueTask<Result> Handle(RejectedCommand request, CancellationToken cancellationToken) =>
        ValueTask.FromResult<Result>(Error.Conflict("demo.conflict", "Already exists."));
}

public sealed record BrokenCommand : ICommand;

public sealed class BrokenHandler : IRequestHandler<BrokenCommand, Result>
{
    public ValueTask<Result> Handle(BrokenCommand request, CancellationToken cancellationToken) =>
        ValueTask.FromResult<Result>(Error.Failure("demo.broken", "Something broke."));
}

public sealed record ThrowingCommand : ICommand;

public sealed class ThrowingHandler : IRequestHandler<ThrowingCommand, Result>
{
    public ValueTask<Result> Handle(ThrowingCommand request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("boom");
}

public sealed record WaitingCommand : ICommand;

/// <summary>Stands in for a handler whose caller disconnects: it honours the token it is given.</summary>
public sealed class WaitingHandler : IRequestHandler<WaitingCommand, Result>
{
    public async ValueTask<Result> Handle(WaitingCommand request, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return Result.Success();
    }
}

public sealed record ReadQuery : IQuery<int>;

public sealed class ReadHandler : IRequestHandler<ReadQuery, Result<int>>
{
    public ValueTask<Result<int>> Handle(ReadQuery request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success(7));
}

public sealed record UnhandledCommand : ICommand;

public sealed record ThingDto(Guid Id, string Name);

public sealed record CreateThingCommand(IdempotencyKeyInfo IdempotencyKey, string Name, int Quantity = 1) : ICommand<ThingDto>, IIdempotentRequest;

public sealed class CreateThingHandler(CallLog log) : IRequestHandler<CreateThingCommand, Result<ThingDto>>
{
    public ValueTask<Result<ThingDto>> Handle(CreateThingCommand request, CancellationToken cancellationToken)
    {
        log.Add("create");
        return ValueTask.FromResult(request.Name == "fail"
            ? Result.Failure<ThingDto>(Error.Conflict("demo.thing_exists", "Already exists."))
            : Result.Success(new ThingDto(Guid.CreateVersion7(), request.Name)));
    }
}

public sealed record TouchCommand(IdempotencyKeyInfo IdempotencyKey) : ICommand, IIdempotentRequest;

public sealed class TouchHandler : IRequestHandler<TouchCommand, Result>
{
    public ValueTask<Result> Handle(TouchCommand request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success());
}

[IntegrationEventName("demo.ping_happened.v1")]
public sealed record PingHappened(Guid EventId, DateTimeOffset OccurredAt, string Name) : IDomainEvent;

public sealed record UnnamedHappened(Guid EventId, DateTimeOffset OccurredAt) : IDomainEvent;

[JobType("demo.reindex.v1")]
public sealed record ReindexJob(string Market, int Page);

/// <summary>A handler that is itself generic: it cannot be registered under a closed interface, and scanning must skip it, not crash.</summary>
public sealed record GenericPing<T>(T Value) : IQuery<T>;

public sealed class GenericPingHandler<T> : IRequestHandler<GenericPing<T>, Result<T>>
{
    public ValueTask<Result<T>> Handle(GenericPing<T> request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success(request.Value));
}
