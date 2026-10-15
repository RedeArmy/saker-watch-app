using FluentValidation;
using SakerWatch.BuildingBlocks.Application.Idempotency;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.Identity.Application.Features.GetMe;

namespace SakerWatch.Identity.Application.Features.UpdatePreferences;

/// <summary>
/// Sets the caller's preferred UI locale. A retried request (same <c>Idempotency-Key</c>) returns the first result and changes
/// nothing again (ADR-0017); the key is created at the endpoint from the header and is not part of the request hash.
/// </summary>
/// <param name="Locale">One of <see cref="SupportedLocales.All"/>.</param>
/// <param name="IdempotencyKey">Key and scope of this attempt.</param>
public sealed record UpdatePreferencesCommand(string Locale, IdempotencyKeyInfo IdempotencyKey) : ICommand<MeProfile>, IIdempotentRequest;

/// <summary>The UI locales the product supports (product UI is Spanish, es-GT first).</summary>
public static class SupportedLocales
{
    /// <summary>Locale tags accepted for the preference.</summary>
    public static readonly IReadOnlyList<string> All = ["es-GT", "en-US"];
}

/// <summary>Write port for the profile; row-level security limits it to the current user's own row.</summary>
public interface IUserPreferencesWriter
{
    /// <summary>Sets the locale of <paramref name="userId"/>; returns <see langword="false"/> when no active row is visible.</summary>
    Task<bool> SetLocaleAsync(Guid userId, string locale, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>Validates the preference before anything is written.</summary>
public sealed class UpdatePreferencesValidator : AbstractValidator<UpdatePreferencesCommand>
{
    /// <summary>Creates the validator.</summary>
    public UpdatePreferencesValidator()
    {
        RuleFor(x => x.Locale)
            .NotEmpty()
            .Must(locale => SupportedLocales.All.Contains(locale, StringComparer.Ordinal))
            .WithMessage($"Locale must be one of: {string.Join(", ", SupportedLocales.All)}.");
    }
}

/// <summary>Updates the locale and returns the resulting profile.</summary>
/// <param name="currentUser">The caller resolved by the user-resolution middleware.</param>
/// <param name="writer">Preference storage.</param>
/// <param name="reader">Profile storage.</param>
/// <param name="timeProvider">Injected clock.</param>
public sealed class UpdatePreferencesHandler(ICurrentUser currentUser, IUserPreferencesWriter writer, IUserProfileReader reader, TimeProvider timeProvider)
    : IRequestHandler<UpdatePreferencesCommand, Result<MeProfile>>
{
    /// <inheritdoc />
    public async ValueTask<Result<MeProfile>> Handle(UpdatePreferencesCommand request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Unauthorized(MeErrors.NotAuthenticated, "Authentication is required.");
        }

        if (!await writer.SetLocaleAsync(userId, request.Locale, timeProvider.GetUtcNow(), cancellationToken))
        {
            return Error.NotFound(MeErrors.NotFound, "The user was not found.");
        }

        MeProfile? profile = await reader.FindAsync(userId, cancellationToken);
        return profile is null ? Error.NotFound(MeErrors.NotFound, "The user was not found.") : profile;
    }
}
