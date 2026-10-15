using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using SakerWatch.BuildingBlocks.Application.Idempotency;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.Identity.Application;
using SakerWatch.Identity.Application.Features.GetMe;
using SakerWatch.Identity.Application.Features.ProvisionUser;
using SakerWatch.Identity.Application.Features.UpdatePreferences;

namespace SakerWatch.Identity.Tests;

/// <summary>Unit tests of the Identity use cases and the resolution cache, with in-memory fakes.</summary>
public sealed class IdentityApplicationTests
{
    private static readonly Guid _userId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset _created = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly MeProfile _profile = new(_userId, "Ada", null, "es-GT", _created);
    private static readonly ExternalIdentity _identity = new("https://idp.example/o/1", "subject-1", null, null, null);

    private sealed class FakeUser(Guid? id) : ICurrentUser
    {
        public Guid? UserId { get; } = id;
    }

    private sealed class FakeReader(MeProfile? profile) : IUserProfileReader
    {
        public Task<MeProfile?> FindAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult(profile);
    }

    private sealed class FakeWriter(bool found) : IUserPreferencesWriter
    {
        public Task<bool> SetLocaleAsync(Guid userId, string locale, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(found);
    }

    private sealed class FakeSender(Result<ResolvedUserResult> result) : ISender
    {
        public int Calls { get; private set; }

        public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
            where TResponse : IResult<TResponse>
        {
            Calls++;
            return ValueTask.FromResult((TResponse)(object)result);
        }
    }

    private static UpdatePreferencesCommand Update() =>
        new("en-US", new IdempotencyKeyInfo("0123456789abcdef", "PUT", "/v1/me/preferences"));

    [Fact]
    public async Task GetMe_WithoutAUser_IsUnauthorized()
    {
        var handler = new GetMeHandler(new FakeUser(null), new FakeReader(_profile));

        Result<MeProfile> result = await handler.Handle(new GetMeQuery(), CancellationToken.None);

        result.Error!.Code.Should().Be(MeErrors.NotAuthenticated);
        result.Error.Type.Should().Be(ErrorType.Unauthorized);
    }

    [Fact]
    public async Task GetMe_WhenTheRowIsNotVisible_IsNotFound()
    {
        var handler = new GetMeHandler(new FakeUser(_userId), new FakeReader(null));

        Result<MeProfile> result = await handler.Handle(new GetMeQuery(), CancellationToken.None);

        result.Error!.Code.Should().Be(MeErrors.NotFound);
    }

    [Fact]
    public async Task GetMe_ReturnsTheProfile()
    {
        var handler = new GetMeHandler(new FakeUser(_userId), new FakeReader(_profile));

        Result<MeProfile> result = await handler.Handle(new GetMeQuery(), CancellationToken.None);

        result.Value.Should().Be(_profile);
    }

    [Fact]
    public async Task UpdatePreferences_WithoutAUser_IsUnauthorized()
    {
        var handler = new UpdatePreferencesHandler(new FakeUser(null), new FakeWriter(true), new FakeReader(_profile), TimeProvider.System);

        Result<MeProfile> result = await handler.Handle(Update(), CancellationToken.None);

        result.Error!.Code.Should().Be(MeErrors.NotAuthenticated);
    }

    [Fact]
    public async Task UpdatePreferences_WhenNoRowWasUpdated_IsNotFound()
    {
        var handler = new UpdatePreferencesHandler(new FakeUser(_userId), new FakeWriter(false), new FakeReader(_profile), TimeProvider.System);

        Result<MeProfile> result = await handler.Handle(Update(), CancellationToken.None);

        result.Error!.Code.Should().Be(MeErrors.NotFound);
    }

    [Fact]
    public async Task UpdatePreferences_WhenTheRowVanishesBeforeTheRead_IsNotFound()
    {
        var handler = new UpdatePreferencesHandler(new FakeUser(_userId), new FakeWriter(true), new FakeReader(null), TimeProvider.System);

        Result<MeProfile> result = await handler.Handle(Update(), CancellationToken.None);

        result.Error!.Code.Should().Be(MeErrors.NotFound);
    }

    [Fact]
    public async Task UpdatePreferences_ReturnsTheProfile()
    {
        var handler = new UpdatePreferencesHandler(new FakeUser(_userId), new FakeWriter(true), new FakeReader(_profile), TimeProvider.System);

        Result<MeProfile> result = await handler.Handle(Update(), CancellationToken.None);

        result.Value.Should().Be(_profile);
    }

    [Fact]
    public async Task Resolver_CachesSuccess_AndDoesNotCacheFailure()
    {
        var clock = new FakeTimeProvider();
        var cache = new ResolvedUserCache(clock);
        var failing = new FakeSender(Error.Failure("x", "boom"));
        var resolver = new UserResolver(failing, cache);

        (await resolver.ResolveAsync(_identity, CancellationToken.None)).IsSuccess.Should().BeFalse();
        (await resolver.ResolveAsync(_identity, CancellationToken.None)).IsSuccess.Should().BeFalse();
        failing.Calls.Should().Be(2, "a failure must be retried, not remembered");

        var working = new FakeSender(new ResolvedUserResult(new ResolvedUser(_userId, UserAccountStatus.Active), true));
        var cached = new UserResolver(working, cache);
        await cached.ResolveAsync(_identity, CancellationToken.None);
        await cached.ResolveAsync(_identity, CancellationToken.None);
        working.Calls.Should().Be(1);

        clock.Advance(ResolvedUserCache.Lifetime + TimeSpan.FromSeconds(1));
        await cached.ResolveAsync(_identity, CancellationToken.None);
        working.Calls.Should().Be(2, "an expired mapping is read again so a deleted account is refused");
    }

    [Fact]
    public void Cache_StaysBounded_AndDropsExpiredEntriesFirst()
    {
        var clock = new FakeTimeProvider();
        var cache = new ResolvedUserCache(clock);
        var user = new ResolvedUser(_userId, UserAccountStatus.Active);
        for (int i = 0; i < 10_000; i++)
        {
            cache.Set(_identity with { Subject = $"s{i}" }, user);
        }

        clock.Advance(ResolvedUserCache.Lifetime + TimeSpan.FromSeconds(1));
        cache.Set(_identity with { Subject = "fresh" }, user);
        cache.TryGet(_identity with { Subject = "fresh" }, out _).Should().BeTrue();
        cache.TryGet(_identity with { Subject = "s1" }, out _).Should().BeFalse();

        for (int i = 0; i < 10_000; i++)
        {
            cache.Set(_identity with { Subject = $"t{i}" }, user);
        }

        cache.Set(_identity with { Subject = "overflow" }, user);
        cache.TryGet(_identity with { Subject = "overflow" }, out _).Should().BeTrue("the cache is cleared rather than grown without bound");
    }
}
