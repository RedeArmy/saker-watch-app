using StackExchange.Redis;

namespace SakerWatch.BuildingBlocks.Redis;

/// <summary>
/// Builds Redis keys under a version prefix (<c>v1:price:NVDA</c>), the only schema management Redis gets: changing a
/// cached shape means bumping <see cref="Version"/>, and old keys expire by their TTL. Segments are validated so a
/// caller-supplied value can never inject extra key levels or glob characters.
/// </summary>
public static class RedisKeys
{
    /// <summary>Current key-schema version prefix.</summary>
    public const string Version = "v1";

    private const int _maxSegmentLength = 128;

    /// <summary>Joins <paramref name="segments"/> under the version prefix.</summary>
    /// <exception cref="ArgumentException">A segment is empty, too long or contains characters other than letters, digits and <c>-_.</c>.</exception>
    public static RedisKey For(params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Length == 0)
        {
            throw new ArgumentException("At least one key segment is required.", nameof(segments));
        }

        foreach (string segment in segments)
        {
            if (string.IsNullOrEmpty(segment) || segment.Length > _maxSegmentLength || !segment.All(IsSafe))
            {
                throw new ArgumentException("Redis key segments must be 1-128 characters of letters, digits, '-', '_' or '.'.", nameof(segments));
            }
        }

        return $"{Version}:{string.Join(':', segments)}";
    }

    private static bool IsSafe(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.';
}
