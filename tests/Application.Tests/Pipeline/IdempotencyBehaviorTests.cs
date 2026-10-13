using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SakerWatch.BuildingBlocks.Application;
using SakerWatch.BuildingBlocks.Application.Idempotency;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.Demo.Features;

namespace SakerWatch.Application.Tests.Pipeline;

public sealed class IdempotencyBehaviorTests : IDisposable
{
    private const string _exampleId = "0123456789abcdef-key";

    private sealed class FakeStore : IIdempotencyStore
    {
        public IdempotencyClaim NextClaim { get; set; } = new IdempotencyClaim.Claimed();

        public List<IdempotencyClaimRequest> Claims { get; } = [];

        public List<IdempotencyCompletion> Completions { get; } = [];

        public Task<IdempotencyClaim> ClaimAsync(IdempotencyClaimRequest request, CancellationToken cancellationToken)
        {
            Claims.Add(request);
            return Task.FromResult(NextClaim);
        }

        public Task CompleteAsync(IdempotencyCompletion completion, CancellationToken cancellationToken)
        {
            Completions.Add(completion);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedUser(Guid? id) : ICurrentUser
    {
        public Guid? UserId => id;
    }

    private readonly FakeStore _store = new();
    private readonly CallLog _log = new();
    private readonly Guid _user = Guid.CreateVersion7();
    private ServiceProvider? _provider;

    private ServiceProvider Provider => _provider ??= Build(_user);

    public void Dispose() => _provider?.Dispose();

    private ServiceProvider Build(Guid? user)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_log);
        services.AddSingleton<IIdempotencyStore>(_store);
        services.AddSingleton<ICurrentUser>(new FixedUser(user));
        services.AddApplicationPipeline(typeof(PingCommand).Assembly);
        services.AddIdempotency(new ConfigurationBuilder().Build());
        return services.BuildServiceProvider();
    }

    private async Task<(TResponse Result, bool Replayed)> Send<TResponse>(IRequest<TResponse> request, ServiceProvider? provider = null)
        where TResponse : IResult<TResponse>
    {
        await using AsyncServiceScope scope = (provider ?? Provider).CreateAsyncScope();
        TResponse result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(request, CancellationToken.None);
        return (result, scope.ServiceProvider.GetRequiredService<IIdempotencyContext>().Replayed);
    }

    private static IdempotencyKeyInfo Key(string key = _exampleId) => new(key, "POST", "/v1/things");

    [Fact]
    public async Task RequestWithoutAKey_BypassesTheStore()
    {
        await Send(new PingCommand("eder"));

        _store.Claims.Should().BeEmpty();
    }

    [Fact]
    public async Task FirstAttempt_RunsTheHandlerAndStoresTheSuccessfulResult()
    {
        (Result<ThingDto> result, bool replayed) = await Send(new CreateThingCommand(Key(), "widget"));

        result.Value.Name.Should().Be("widget");
        replayed.Should().BeFalse();
        _log.Entries.Should().Contain("create");
        IdempotencyClaimRequest claim = _store.Claims.Single();
        claim.UserId.Should().Be(_user);
        claim.Key.Should().Be(Key());
        claim.ExpiresAt.Should().BeAfter(claim.Now.AddHours(23), "the default retention is 48 hours");
        IdempotencyCompletion completion = _store.Completions.Single();
        completion.Body.Should().NotBeNull();
    }

    [Fact]
    public async Task Replay_ReturnsTheStoredResultWithoutRunningTheHandler()
    {
        var stored = new ThingDto(Guid.CreateVersion7(), "from-first-attempt");
        byte[] body = ResultSnapshot.Serialize(Result.Success(stored))!;
        _store.NextClaim = new IdempotencyClaim.Replay(new StoredResponse(200, body));

        (Result<ThingDto> result, bool replayed) = await Send(new CreateThingCommand(Key(), "widget"));

        result.Value.Should().Be(stored);
        replayed.Should().BeTrue();
        _log.Entries.Should().NotContain("create");
        _store.Completions.Should().BeEmpty();
    }

    [Fact]
    public async Task Replay_OfAResultWithoutValue_ReturnsSuccess()
    {
        _store.NextClaim = new IdempotencyClaim.Replay(new StoredResponse(200, ResultSnapshot.Serialize(Result.Success())));

        (Result result, bool replayed) = await Send(new TouchCommand(Key()));

        result.IsSuccess.Should().BeTrue();
        replayed.Should().BeTrue();
    }

    [Fact]
    public async Task Replay_WhenTheFirstResponseWasTooLarge_PointsToAGet()
    {
        _store.NextClaim = new IdempotencyClaim.Replay(new StoredResponse(200, null));

        (Result<ThingDto> result, _) = await Send(new CreateThingCommand(Key(), "widget"));

        result.Error!.Code.Should().Be(IdempotencyErrors.ReplayUnavailable);
        result.Error.Type.Should().Be(ErrorType.Conflict);
    }

    [Fact]
    public async Task Mismatch_ReturnsUnprocessableWithoutRunningTheHandler()
    {
        _store.NextClaim = new IdempotencyClaim.Mismatch();

        (Result<ThingDto> result, _) = await Send(new CreateThingCommand(Key(), "widget"));

        result.Error!.Code.Should().Be(IdempotencyErrors.KeyReuse);
        result.Error.Type.Should().Be(ErrorType.Unprocessable);
        _log.Entries.Should().NotContain("create");
    }

    [Fact]
    public async Task Busy_ReturnsConflictWithoutRunningTheHandler()
    {
        _store.NextClaim = new IdempotencyClaim.Busy();

        (Result<ThingDto> result, _) = await Send(new CreateThingCommand(Key(), "widget"));

        result.Error!.Code.Should().Be(IdempotencyErrors.InProgress);
        result.Error.Type.Should().Be(ErrorType.Conflict);
        _log.Entries.Should().NotContain("create");
    }

    [Fact]
    public async Task FailureResult_IsNotStored()
    {
        (Result<ThingDto> result, _) = await Send(new CreateThingCommand(Key(), "fail"));

        result.IsSuccess.Should().BeFalse();
        _store.Completions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("0123456789abcdef!!!!")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    public async Task MalformedKey_IsRejectedBeforeTheStoreIsTouched(string key)
    {
        (Result<ThingDto> result, _) = await Send(new CreateThingCommand(Key(key), "widget"));

        result.Error!.Code.Should().Be(IdempotencyErrors.KeyInvalid);
        _store.Claims.Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutAnAuthenticatedUser_TheCommandIsRefused()
    {
        using ServiceProvider anonymous = Build(user: null);

        (Result<ThingDto> result, _) = await Send(new CreateThingCommand(Key(), "widget"), anonymous);

        result.Error!.Code.Should().Be(IdempotencyErrors.Unauthenticated);
        result.Error.Type.Should().Be(ErrorType.Unauthorized);
        _store.Claims.Should().BeEmpty();
    }

    [Fact]
    public async Task RequestHash_ChangesWithThePayload_ButNotWithTheKey()
    {
        await Send(new CreateThingCommand(Key("0123456789abcdef-aaa"), "widget", 1));
        await Send(new CreateThingCommand(Key("0123456789abcdef-bbb"), "widget", 1));
        await Send(new CreateThingCommand(Key("0123456789abcdef-aaa"), "widget", 2));

        _store.Claims[0].RequestHash.Should().Equal(_store.Claims[1].RequestHash);
        _store.Claims[0].RequestHash.Should().NotEqual(_store.Claims[2].RequestHash);
    }
}
