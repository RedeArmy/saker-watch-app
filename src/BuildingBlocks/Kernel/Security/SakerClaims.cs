namespace SakerWatch.BuildingBlocks.Kernel.Security;

/// <summary>Claim types used inside the application: the raw JWT names, not the Microsoft ones (ADR-0009).</summary>
public static class SakerClaims
{
    /// <summary>External account id (<c>sub</c>).</summary>
    public const string Subject = "sub";

    /// <summary>Identity provider issuer (<c>iss</c>).</summary>
    public const string Issuer = "iss";

    /// <summary>Profile email.</summary>
    public const string Email = "email";

    /// <summary>Profile display name.</summary>
    public const string Name = "name";

    /// <summary>Profile locale.</summary>
    public const string Locale = "locale";

    /// <summary>One OAuth scope (repeated per scope).</summary>
    public const string Scope = "scope";

    /// <summary>Role claim type (reserved; roles, if any, are our own).</summary>
    public const string Role = "role";

    /// <summary>Internal user id, added after just-in-time provisioning. Never taken from a token.</summary>
    public const string UserId = "saker_user_id";
}
