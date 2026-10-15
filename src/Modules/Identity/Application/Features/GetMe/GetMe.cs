using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Security;

namespace SakerWatch.Identity.Application.Features.GetMe;

/// <summary>The caller's own profile.</summary>
public sealed record GetMeQuery : IQuery<MeProfile>;

/// <summary>What the API shows a user about themselves.</summary>
/// <param name="Id">Internal user id (UUIDv7); never the identity provider's subject.</param>
/// <param name="DisplayName">Name from the identity provider, if it sent one.</param>
/// <param name="Email">Email from the identity provider, if it sent one (Clavaris puts it on the ID token only, A-022).</param>
/// <param name="Locale">Preferred UI locale (BCP 47).</param>
/// <param name="CreatedAt">When the account was first seen (UTC).</param>
public sealed record MeProfile(Guid Id, string? DisplayName, string? Email, string Locale, DateTimeOffset CreatedAt);

/// <summary>Read port for the profile; row-level security limits it to the current user's own row.</summary>
public interface IUserProfileReader
{
    /// <summary>The profile of <paramref name="userId"/>, or <see langword="null"/> when that row is not visible.</summary>
    Task<MeProfile?> FindAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>Errors of the profile use cases.</summary>
public static class MeErrors
{
    /// <summary>The caller is not an authenticated, resolved user.</summary>
    public const string NotAuthenticated = "identity.not_authenticated";

    /// <summary>The user row is not visible (it was deleted meanwhile).</summary>
    public const string NotFound = "identity.user_not_found";
}

/// <summary>Reads the current user's profile.</summary>
/// <param name="currentUser">The caller resolved by the user-resolution middleware.</param>
/// <param name="reader">Profile storage.</param>
public sealed class GetMeHandler(ICurrentUser currentUser, IUserProfileReader reader) : IRequestHandler<GetMeQuery, Result<MeProfile>>
{
    /// <inheritdoc />
    public async ValueTask<Result<MeProfile>> Handle(GetMeQuery request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Unauthorized(MeErrors.NotAuthenticated, "Authentication is required.");
        }

        MeProfile? profile = await reader.FindAsync(userId, cancellationToken);
        return profile is null ? Error.NotFound(MeErrors.NotFound, "The user was not found.") : profile;
    }
}
