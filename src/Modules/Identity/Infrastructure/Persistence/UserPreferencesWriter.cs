using Microsoft.EntityFrameworkCore;
using SakerWatch.Identity.Application.Features.UpdatePreferences;

namespace SakerWatch.Identity.Infrastructure.Persistence;

/// <summary><see cref="IUserPreferencesWriter"/> over <c>identity.users</c>; it runs in the command's unit of work and row-level security limits it to the caller's own row.</summary>
/// <param name="db">The identity context.</param>
public sealed class UserPreferencesWriter(IdentityDbContext db) : IUserPreferencesWriter
{
    /// <inheritdoc />
    public async Task<bool> SetLocaleAsync(Guid userId, string locale, DateTimeOffset now, CancellationToken cancellationToken)
    {
        int changed = await db.Users
            .Where(user => user.Id == userId && user.Status == UserStatuses.Active)
            .ExecuteUpdateAsync(set => set.SetProperty(user => user.Locale, locale).SetProperty(user => user.UpdatedAt, now), cancellationToken);
        return changed == 1;
    }
}
