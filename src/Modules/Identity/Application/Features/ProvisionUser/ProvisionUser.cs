using FluentValidation;
using SakerWatch.BuildingBlocks.Application.Audit;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Kernel.Time;

namespace SakerWatch.Identity.Application.Features.ProvisionUser;

/// <summary>Finds or creates the internal user for an external identity (just-in-time provisioning, ADR-0009).</summary>
/// <param name="Identity">The identity proven by a validated token.</param>
public sealed record ProvisionUserCommand(ExternalIdentity Identity) : ICommand<ResolvedUserResult>;

/// <summary>What provisioning found or created.</summary>
/// <param name="User">The internal user and its state.</param>
/// <param name="Created">True when this call created the account.</param>
public sealed record ResolvedUserResult(ResolvedUser User, bool Created);

/// <summary>Storage port for users; implemented in Infrastructure with the security-definer function <c>identity.provision_user</c>.</summary>
public interface IUserDirectory
{
    /// <summary>Creates the user if the pair is unknown, otherwise returns the existing one (refreshing profile data when active).</summary>
    Task<ResolvedUserResult> ProvisionAsync(Guid newId, ExternalIdentity identity, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>Bounds the data a token can push into the user table.</summary>
public sealed class ProvisionUserValidator : AbstractValidator<ProvisionUserCommand>
{
    /// <summary>Creates the validator.</summary>
    public ProvisionUserValidator()
    {
        RuleFor(x => x.Identity.Issuer).NotEmpty().MaximumLength(512);
        RuleFor(x => x.Identity.Subject).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Identity.Email).MaximumLength(320);
        RuleFor(x => x.Identity.DisplayName).MaximumLength(320);
        RuleFor(x => x.Identity.Locale).MaximumLength(35);
    }
}

/// <summary>
/// Provisions the user and, when the account is new, appends <c>user.provisioned</c> to the audit log in the same unit of work.
/// The new id is a UUIDv7 from the injected clock and is used only when the pair is unknown.
/// </summary>
/// <param name="directory">User storage.</param>
/// <param name="audit">Audit log.</param>
/// <param name="ids">Id source.</param>
/// <param name="timeProvider">Injected clock.</param>
public sealed class ProvisionUserHandler(IUserDirectory directory, IAuditTrail audit, IIdGenerator ids, TimeProvider timeProvider)
    : IRequestHandler<ProvisionUserCommand, Result<ResolvedUserResult>>
{
    /// <inheritdoc />
    public async ValueTask<Result<ResolvedUserResult>> Handle(ProvisionUserCommand request, CancellationToken cancellationToken)
    {
        ResolvedUserResult resolved = await directory.ProvisionAsync(ids.NewGuid(), request.Identity, timeProvider.GetUtcNow(), cancellationToken);

        if (resolved.Created)
        {
            await audit.AppendAsync(new AuditEvent("user.provisioned", resolved.User.UserId, Details: new { issuer = request.Identity.Issuer }), cancellationToken);
        }

        return resolved;
    }
}
