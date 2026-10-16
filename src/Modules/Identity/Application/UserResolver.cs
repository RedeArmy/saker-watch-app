using System.Collections.Concurrent;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.Identity.Application.Features.ProvisionUser;

namespace SakerWatch.Identity.Application;

/// <summary>
/// Remembers which internal user an external identity maps to, so that provisioning (a write) happens once per identity per
/// <see cref="Lifetime"/> instead of once per request. The mapping never changes for an account; the cached state does, so an
/// account that enters deletion is refused at most <see cref="Lifetime"/> later. Only identities of validated tokens get here.
/// The size is capped: when the cap is reached, expired entries are swept (at most once a minute, so a full cache never makes every
/// request walk it) and, if the cache is still full, the new identity is simply not remembered. Dropping everyone to make room, as an
/// earlier version did, would have sent a burst of provisioning writes to the database.
/// </summary>
/// <param name="timeProvider">Injected clock.</param>
/// <param name="maxEntries">Most identities remembered at once.</param>
public sealed class ResolvedUserCache(TimeProvider timeProvider, int maxEntries = ResolvedUserCache.DefaultMaxEntries)
{
    /// <summary>How long a mapping is trusted.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>Default cap on remembered identities.</summary>
    public const int DefaultMaxEntries = 10_000;

    private static readonly TimeSpan _sweepSpacing = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<(string Issuer, string Subject), (ResolvedUser User, DateTimeOffset Expires)> _entries = new();
    private long _nextSweepTicks;

    /// <summary>The cached user, if present and fresh.</summary>
    public bool TryGet(ExternalIdentity identity, out ResolvedUser? user)
    {
        if (_entries.TryGetValue((identity.Issuer, identity.Subject), out var entry) && entry.Expires > timeProvider.GetUtcNow())
        {
            user = entry.User;
            return true;
        }

        user = null;
        return false;
    }

    /// <summary>Remembers <paramref name="user"/>, unless the cache is full of fresh entries.</summary>
    public void Set(ExternalIdentity identity, ResolvedUser user)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        (string Issuer, string Subject) key = (identity.Issuer, identity.Subject);

        if (_entries.Count >= maxEntries && !_entries.ContainsKey(key) && !MadeRoom(now))
        {
            return;
        }

        _entries[key] = (user, now + Lifetime);
    }

    // Sweeps expired entries, but only when a minute has passed since the last sweep; true when there is room afterwards.
    private bool MadeRoom(DateTimeOffset now)
    {
        long next = Interlocked.Read(ref _nextSweepTicks);
        if (now.UtcTicks >= next && Interlocked.CompareExchange(ref _nextSweepTicks, (now + _sweepSpacing).UtcTicks, next) == next)
        {
            foreach (KeyValuePair<(string Issuer, string Subject), (ResolvedUser User, DateTimeOffset Expires)> stale in _entries.Where(e => e.Value.Expires <= now))
            {
                _entries.TryRemove(stale.Key, out _);
            }
        }

        return _entries.Count < maxEntries;
    }
}

/// <summary><see cref="IUserResolver"/> over the provisioning use case, with the short-lived cache.</summary>
/// <param name="sender">Runs the provisioning command through the pipeline (validation, unit of work, audit).</param>
/// <param name="cache">Shared mapping cache.</param>
public sealed class UserResolver(ISender sender, ResolvedUserCache cache) : IUserResolver
{
    /// <inheritdoc />
    public async Task<Result<ResolvedUser>> ResolveAsync(ExternalIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (cache.TryGet(identity, out ResolvedUser? cached))
        {
            return cached!;
        }

        Result<ResolvedUserResult> result = await sender.Send(new ProvisionUserCommand(identity), cancellationToken);
        if (!result.IsSuccess)
        {
            return result.Error!;
        }

        cache.Set(identity, result.Value.User);
        return result.Value.User;
    }
}
