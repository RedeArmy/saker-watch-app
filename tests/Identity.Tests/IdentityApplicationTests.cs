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

    private sealed class FakeWriter(MeProfile? updated) : IUserPreferencesWriter
    {
        public string? LocaleSet { get; private set; }

        public Task<MeProfile?> SetLocaleAsync(Guid userId, string locale, DateTimeOffset now, CancellationToken cancellationToken)
        {
            LocaleSet = locale;
            return Task.FromResult(updated);
        }
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
        var writer = new FakeWriter(_profile);
        var handler = new UpdatePreferencesHandler(new FakeUser(null), writer, TimeProvider.System);

        Result<MeProfile> result = await handler.Handle(Update(), CancellationToken.None);

        result.Error!.Code.Should().Be(MeErrors.NotAuthenticated);
        writer.LocaleSet.Should().BeNull("nothing is written for an unauthenticated caller");
    }

    [Fact]
    public async Task UpdatePreferences_WhenNoActiveRowIsVisible_IsNotFound()
    {
        var handler = new UpdatePreferencesHandler(new FakeUser(_userId), new FakeWriter(null), TimeProvider.System);

        Result<MeProfile> result = await handler.Handle(Update(), CancellationToken.None);

        result.Error!.Code.Should().Be(MeErrors.NotFound);
    }

    [Fact]
    public async Task UpdatePreferences_ReturnsTheProfileTheWriterReadBack()
    {
        var writer = new FakeWriter(_profile);
        var handler = new UpdatePreferencesHandler(new FakeUser(_userId), writer, TimeProvider.System);

        Result<MeProfile> result = await handler.Handle(Update(), CancellationToken.None);

        result.Value.Should().Be(_profile);
        writer.LocaleSet.Should().Be("en-US");
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
        cache.TryGet(_identity with { Subject = "overflow" }, out _).Should().BeFalse("a cache full of fresh entries does not grow, and does not evict them either");
        cache.TryGet(_identity with { Subject = "t0" }, out _).Should().BeTrue();
    }

    [Fact]
    public void ProvisionUserValidator_AcceptsEveryValueTheTokenValidatorLetsThrough_AndRefusesOneMore()
    {
        var validator = new ProvisionUserValidator();
        var atTheLimit = new ExternalIdentity(
            new string('i', ExternalIdentityLimits.MaxIssuerLength),
            new string('s', ExternalIdentityLimits.MaxSubjectLength),
            new string('e', ExternalIdentityLimits.MaxProfileTextLength),
            new string('n', ExternalIdentityLimits.MaxProfileTextLength),
            new string('l', ExternalIdentityLimits.MaxLocaleLength));

        validator.Validate(new ProvisionUserCommand(atTheLimit)).IsValid.Should().BeTrue();
        validator.Validate(new ProvisionUserCommand(atTheLimit with { Subject = atTheLimit.Subject + "x" })).IsValid.Should().BeFalse();
        validator.Validate(new ProvisionUserCommand(atTheLimit with { Locale = atTheLimit.Locale + "x" })).IsValid.Should().BeFalse();
    }

    private static ExternalIdentity Person(int n) => new("https://idp.example/o/1", $"subject-{n}", null, null, null);

    private static ResolvedUser Resolved() => new(Guid.CreateVersion7(), UserAccountStatus.Active);

    [Fact]
    public void FullCache_KeepsTheFreshEntries_AndSimplyDoesNotRememberTheNewcomer()
    {
        var clock = new FakeTimeProvider();
        var cache = new ResolvedUserCache(clock, maxEntries: 3);
        for (int i = 1; i <= 3; i++)
        {
            cache.Set(Person(i), Resolved());
        }

        cache.Set(Person(4), Resolved());

        cache.TryGet(Person(4), out _).Should().BeFalse("there was no room and nothing had expired");
        for (int i = 1; i <= 3; i++)
        {
            cache.TryGet(Person(i), out _).Should().BeTrue("a burst of new identities must not evict the ones already known");
        }
    }

    [Fact]
    public void FullCache_SweepsExpiredEntries_ToMakeRoom_ButNotMoreOftenThanOncePerMinute()
    {
        var clock = new FakeTimeProvider();
        var cache = new ResolvedUserCache(clock, maxEntries: 2);
        cache.Set(Person(1), Resolved());
        cache.Set(Person(2), Resolved());

        clock.Advance(ResolvedUserCache.Lifetime + TimeSpan.FromSeconds(1));
        cache.Set(Person(3), Resolved());

        cache.TryGet(Person(3), out _).Should().BeTrue("the expired entries were swept to make room");
        cache.TryGet(Person(1), out _).Should().BeFalse();

        // A second sweep inside the same minute is refused, even though entries 3 expire later: fill, expire, and try again at once.
        cache.Set(Person(4), Resolved());
        clock.Advance(ResolvedUserCache.Lifetime + TimeSpan.FromSeconds(1));
        cache.Set(Person(5), Resolved());
        cache.TryGet(Person(5), out _).Should().BeTrue("a minute had passed since the last sweep, so a new one was allowed");
    }

    [Fact]
    public void RefreshingAKnownIdentity_WorksEvenWhenTheCacheIsFull()
    {
        var clock = new FakeTimeProvider();
        var cache = new ResolvedUserCache(clock, maxEntries: 1);
        cache.Set(Person(1), Resolved());
        ResolvedUser newer = Resolved();

        cache.Set(Person(1), newer);

        cache.TryGet(Person(1), out ResolvedUser? found).Should().BeTrue();
        found.Should().Be(newer);
    }
}
