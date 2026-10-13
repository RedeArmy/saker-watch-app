using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace SakerWatch.BuildingBlocks.Persistence.Conventions;

/// <summary>Applies snake_case to columns, keys and indexes so the schema reads as plain PostgreSQL (ADR-0013 naming).</summary>
public static class SnakeCaseNaming
{
    /// <summary>
    /// Renames columns, primary keys, foreign keys and indexes of every entity to snake_case. Table names are set
    /// explicitly per entity so schemas stay readable; call this at the end of <c>OnModelCreating</c>.
    /// </summary>
    public static ModelBuilder UseSnakeCaseNames(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        foreach (IMutableEntityType entity in modelBuilder.Model.GetEntityTypes())
        {
            string? table = entity.GetTableName();
            foreach (IMutableProperty property in entity.GetProperties())
            {
                property.SetColumnName(ToSnake(property.GetColumnName()));
            }

            foreach (IMutableKey key in entity.GetKeys())
            {
                key.SetName($"pk_{table}");
            }

            foreach (IMutableIndex index in entity.GetIndexes())
            {
                string columns = string.Join('_', index.Properties.Select(p => ToSnake(p.GetColumnName())));
                index.SetDatabaseName($"{(index.IsUnique ? "ux" : "ix")}_{table}_{columns}");
            }
        }

        return modelBuilder;
    }

    /// <summary>Converts <c>PascalCase</c> to <c>snake_case</c>.</summary>
    public static string ToSnake(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var builder = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c))
            {
                bool startsWord = i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1])));
                if (startsWord)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
