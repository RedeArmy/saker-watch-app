using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using SakerWatch.BuildingBlocks.Application;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Transactions;
using SakerWatch.Demo.Features;

namespace SakerWatch.Application.Tests.Pipeline;

public sealed class UnitOfWorkBehaviorTests : IDisposable
{
    private sealed class RecordingUnitOfWork : IUnitOfWork
    {
        public List<string> Calls { get; } = [];

        public Task BeginAsync(CancellationToken cancellationToken)
        {
            Calls.Add("begin");
            return Task.CompletedTask;
        }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            Calls.Add("commit");
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            Calls.Add("rollback");
            return Task.CompletedTask;
        }
    }

    private readonly RecordingUnitOfWork _uow = new();
    private readonly ServiceProvider _provider;

    public UnitOfWorkBehaviorTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new CallLog());
        services.AddSingleton<IUnitOfWork>(_uow);
        services.AddApplicationPipeline(typeof(PingCommand).Assembly);
        _provider = services.BuildServiceProvider();
    }

    public void Dispose() => _provider.Dispose();

    private async Task<TResponse> Send<TResponse>(IRequest<TResponse> request)
        where TResponse : IResult<TResponse>
    {
        await using AsyncServiceScope scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request, CancellationToken.None);
    }

    [Fact]
    public async Task Command_Success_BeginsThenCommits()
    {
        await Send(new PingCommand("eder"));

        _uow.Calls.Should().Equal("begin", "commit");
    }

    [Fact]
    public async Task Command_FailureResult_BeginsThenRollsBack()
    {
        await Send(new RejectedCommand());

        _uow.Calls.Should().Equal("begin", "rollback");
    }

    [Fact]
    public async Task Command_ThatThrows_RollsBackAndRethrows()
    {
        Func<Task> act = async () => await Send(new ThrowingCommand());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        _uow.Calls.Should().Equal("begin", "rollback");
    }

    [Fact]
    public async Task Query_NeverOpensAUnitOfWork()
    {
        await Send(new ReadQuery());

        _uow.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task InvalidCommand_IsRejectedBeforeAnyTransactionOpens()
    {
        Result<string> result = await Send(new PingCommand(""));

        result.Error!.Type.Should().Be(ErrorType.Validation);
        _uow.Calls.Should().BeEmpty("validation runs before the unit of work");
    }
}
