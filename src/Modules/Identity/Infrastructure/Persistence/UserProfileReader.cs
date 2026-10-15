using Microsoft.EntityFrameworkCore;
using SakerWatch.Identity.Application.Features.GetMe;

namespace SakerWatch.Identity.Infrastructure.Persistence;

/// <summary><see cref="IUserProfileReader"/> over <c>identity.users</c>; row-level security shows the caller only their own row.</summary>
/// <param name="db">The identity context.</param>
public sealed class UserProfileReader(IdentityDbContext db) : IUserProfileReader
{
    /// <inheritdoc />
    public Task<MeProfile?> FindAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Users
            .AsNoTracking()
            .Where(user => user.Id == userId && user.Status == UserStatuses.Active)
            .Select(user => new MeProfile(user.Id, user.DisplayName, user.Email, user.Locale, user.CreatedAt))
            .SingleOrDefaultAsync(cancellationToken);
}
