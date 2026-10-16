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

    /// <summary>
    /// Maps a locale tag that arrives from outside (an identity-provider claim) to a supported one: an exact match in any letter case,
    /// otherwise the first supported tag of the same language ("es-MX" gives "es-GT"). Returns <see langword="null"/> when the language
    /// is not supported or the text is empty, so storage falls back to the default instead of keeping arbitrary text.
    /// </summary>
    public static string? Normalize(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        string candidate = tag.Trim();
        string? exact = All.FirstOrDefault(supported => string.Equals(supported, candidate, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        string language = candidate[..(candidate.IndexOfAny(['-', '_']) is var cut and >= 0 ? cut : candidate.Length)];
        return All.FirstOrDefault(supported => supported.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Write port for the profile; row-level security limits it to the current user's own row.</summary>
public interface IUserPreferencesWriter
{
    /// <summary>
    /// Sets the locale of <paramref name="userId"/> and returns the resulting profile, read in the same statement;
    /// <see langword="null"/> when no active row is visible.
    /// </summary>
    Task<MeProfile?> SetLocaleAsync(Guid userId, string locale, DateTimeOffset now, CancellationToken cancellationToken);
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
/// <param name="writer">Preference storage; it returns the updated profile, so no second read is needed.</param>
/// <param name="timeProvider">Injected clock.</param>
public sealed class UpdatePreferencesHandler(ICurrentUser currentUser, IUserPreferencesWriter writer, TimeProvider timeProvider)
    : IRequestHandler<UpdatePreferencesCommand, Result<MeProfile>>
{
    /// <inheritdoc />
    public async ValueTask<Result<MeProfile>> Handle(UpdatePreferencesCommand request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Unauthorized(MeErrors.NotAuthenticated, "Authentication is required.");
        }

        MeProfile? profile = await writer.SetLocaleAsync(userId, request.Locale, timeProvider.GetUtcNow(), cancellationToken);
        return profile is null ? Error.NotFound(MeErrors.NotFound, "The user was not found.") : profile;
    }
}
