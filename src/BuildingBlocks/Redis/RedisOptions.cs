using System.ComponentModel.DataAnnotations;

namespace SakerWatch.BuildingBlocks.Redis;

/// <summary>Redis connection settings, bound from <c>ConnectionStrings:Redis</c> (StackExchange.Redis format, including the password).</summary>
public sealed class RedisOptions
{
    /// <summary>StackExchange.Redis configuration string.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; set; } = string.Empty;
}
