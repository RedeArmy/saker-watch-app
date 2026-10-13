using FluentValidation;
using SakerWatch.BuildingBlocks.Application.Messaging;
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

public sealed record ReadQuery : IQuery<int>;

public sealed class ReadHandler : IRequestHandler<ReadQuery, Result<int>>
{
    public ValueTask<Result<int>> Handle(ReadQuery request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success(7));
}

public sealed record UnhandledCommand : ICommand;
