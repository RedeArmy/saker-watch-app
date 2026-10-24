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

    /// <summary>
    /// Connections this process may hold open. Applied to <see cref="Postgres"/> when the string does not say <c>Maximum Pool Size</c> itself (<see cref="PostgresPooling"/>).
    /// A request that finds the pool empty waits for a connection, up to the driver's timeout, instead of opening one more.
    /// The processes of a deployment together must stay under <c>max_connections</c> of the server (docs/runbooks/scaling-the-api.md).
    /// </summary>
    [Range(2, 200)]
    public int PostgresMaxPoolSize { get; set; } = DefaultMaxPoolSize;

    /// <summary>Default size of the pool: room for the load of one API process or one worker, and for two API replicas and a worker within the 100 connections of a default server.</summary>
    public const int DefaultMaxPoolSize = 20;
}
