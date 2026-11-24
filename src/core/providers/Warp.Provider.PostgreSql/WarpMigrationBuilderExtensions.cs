using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Warp.Core.Data;

namespace Warp.Provider.PostgreSql;

/// <summary>Helpers for a host's own EF Core migrations.</summary>
public static class WarpMigrationBuilderExtensions
{
    // PostgreSQL truncates identifiers past NAMEDATALEN - 1 bytes; the temporary name must stay distinct.
    private const int MaxIdentifierLength = 63;
    private const string TemporarySuffix = "_warp_new";

    /// <summary>
    /// Recreates the job table's four partial indexes (8.0, rule 6.15) without locking the table: each is
    /// built with <c>CREATE INDEX CONCURRENTLY</c> under a temporary name, the old one is dropped with
    /// <c>DROP INDEX CONCURRENTLY</c>, and the new one renamed. Call it from the migration in place of the
    /// generated <c>DropIndex</c> / <c>CreateIndex</c> pairs for those indexes, passing the migration's
    /// <c>TargetModel</c>: the index names, columns and filters are read from it, so they match your schema
    /// and naming convention.
    /// <para>
    /// Every statement runs outside the migration's transaction (<c>suppressTransaction</c>), as concurrent
    /// index builds must. If one fails, the statements before it stay applied; running the migration again
    /// first drops a temporary index a failed build left behind. A migration of its own is easiest to recover.
    /// </para>
    /// </summary>
    /// <param name="migrationBuilder">The migration's builder.</param>
    /// <param name="targetModel">The migration's <c>TargetModel</c>.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static MigrationBuilder RecreateWarpJobIndexesOnline(this MigrationBuilder migrationBuilder, IReadOnlyModel targetModel)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        foreach (var sql in BuildStatements(targetModel))
        {
            migrationBuilder.Sql(sql, suppressTransaction: true);
        }

        return migrationBuilder;
    }

    // One statement per entry: CREATE / DROP INDEX CONCURRENTLY cannot share a command with another statement.
    internal static IReadOnlyList<string> BuildStatements(IReadOnlyModel targetModel)
    {
        var statements = new List<string>();
        foreach (var index in WarpJobIndexes.FindPartial(targetModel))
        {
            var temporary = TemporaryName(index.Name);
            var columns = string.Join(", ", index.Columns.Select(Quote));
            var unique = index.IsUnique ? "UNIQUE " : string.Empty;
            statements.Add($"DROP INDEX CONCURRENTLY IF EXISTS {Qualified(index.Schema, temporary)};");
            statements.Add($"CREATE {unique}INDEX CONCURRENTLY {Quote(temporary)} ON {Qualified(index.Schema, index.Table)} ({columns}) WHERE {index.Filter};");
            statements.Add($"DROP INDEX CONCURRENTLY IF EXISTS {Qualified(index.Schema, index.Name)};");
            statements.Add($"ALTER INDEX {Qualified(index.Schema, temporary)} RENAME TO {Quote(index.Name)};");
        }

        return statements;
    }

    private static string TemporaryName(string name) =>
        name.Length + TemporarySuffix.Length <= MaxIdentifierLength
            ? name + TemporarySuffix
            : name[..(MaxIdentifierLength - TemporarySuffix.Length)] + TemporarySuffix;

    private static string Qualified(string? schema, string name) =>
        string.IsNullOrEmpty(schema) ? Quote(name) : $"{Quote(schema)}.{Quote(name)}";

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
