using System.ComponentModel.DataAnnotations;

namespace SakerWatch.BuildingBlocks.Persistence;

/// <summary>
/// PostgreSQL connection settings. <see cref="Postgres"/> must use a login that is a member of
/// <c>api_role</c> (API) or <c>worker_role</c> (Worker), never the admin login (ADR-0010). Bound from <c>ConnectionStrings</c>.
/// </summary>
public sealed class PostgresOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "ConnectionStrings";

    /// <summary>Runtime connection string (least-privilege role).</summary>
    [Required(AllowEmptyStrings = false)]
    public string Postgres { get; set; } = string.Empty;
}
