using System.ComponentModel.DataAnnotations;

namespace SakerWatch.BuildingBlocks.Mongo;

/// <summary>
/// MongoDB connection settings. The runtime connection string must use a per-database <c>readWrite</c> user, never the
/// root user used for bootstrap and migrations. Bound from <c>ConnectionStrings:Mongo</c> and <c>Mongo:Database</c>.
/// </summary>
public sealed class MongoOptions
{
    /// <summary>Configuration section of the database name.</summary>
    public const string SectionName = "Mongo";

    /// <summary>Runtime connection string (<c>ConnectionStrings:Mongo</c>).</summary>
    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Database that holds the documents.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Database { get; set; } = "saker";
}
