using System.Security.Cryptography;
using System.Text;

namespace SakerWatch.BuildingBlocks.Kernel.Security;

/// <summary>
/// The pseudonymous actor recorded in the audit log: HMAC-SHA256 of the internal user id keyed with a secret pepper
/// (ADR-0016). It is stable, so one person's actions can be correlated, but it is not the user id and cannot be reversed
/// without the pepper, so deleting an account needs no audit rewrite.
/// </summary>
public static class ActorReference
{
    /// <summary>Smallest accepted pepper.</summary>
    public const int MinimumPepperBytes = 32;

    /// <summary>The lower-case hex reference of <paramref name="userId"/>.</summary>
    public static string For(Guid userId, ReadOnlySpan<byte> pepper)
    {
        if (pepper.Length < MinimumPepperBytes)
        {
            throw new ArgumentException($"The audit pepper must be at least {MinimumPepperBytes} bytes.", nameof(pepper));
        }

        byte[] hash = HMACSHA256.HashData(pepper, Encoding.UTF8.GetBytes(userId.ToString("D")));
        return Convert.ToHexStringLower(hash);
    }
}
