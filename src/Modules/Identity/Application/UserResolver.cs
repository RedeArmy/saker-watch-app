using System.Collections.Concurrent;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.Identity.Application.Features.ProvisionUser;

namespace SakerWatch.Identity.Application;

/// <summary>
/// Remembers which internal user an external identity maps to, so that provisioning (a write) happens once per identity per
/// <see cref="Lifetime"/> instead of once per request. The mapping never changes for an account; the cached state does, so an
/// account that enters deletion is refused at most <see cref="Lifetime"/> later. Only identities of validated tokens get here,
/// and the size is capped.
/// </summary>
/// <param name="timeProvider">Injected clock.</param>
public sealed class ResolvedUserCache(TimeProvider timeProvider)
{
    /// <summary>How long a mapping is trusted.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private const int _maxEntries = 10_000;

    private readonly ConcurrentDictionary<(string Issuer, string Subject), (ResolvedUser User, DateTimeOffset Expires)> _entries = new();

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

    /// <summary>Remembers <paramref name="user"/>.</summary>
    public void Set(ExternalIdentity identity, ResolvedUser user)
    {
        if (_entries.Count >= _maxEntries)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            foreach (KeyValuePair<(string Issuer, string Subject), (ResolvedUser User, DateTimeOffset Expires)> stale in _entries.Where(e => e.Value.Expires <= now))
            {
                _entries.TryRemove(stale.Key, out _);
            }

            if (_entries.Count >= _maxEntries)
            {
                _entries.Clear();
            }
        }

        _entries[(identity.Issuer, identity.Subject)] = (user, timeProvider.GetUtcNow() + Lifetime);
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
