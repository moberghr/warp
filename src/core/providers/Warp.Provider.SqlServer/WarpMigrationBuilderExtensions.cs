using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Warp.Core.Data;

namespace Warp.Provider.SqlServer;

/// <summary>Helpers for a host's own EF Core migrations.</summary>
public static class WarpMigrationBuilderExtensions
{
    /// <summary>
    /// Recreates the job table's four filtered indexes (8.0, rule 6.15) with one
    /// <c>CREATE INDEX ... WITH (DROP_EXISTING = ON, ONLINE = ON)</c> each, which replaces the old index in
    /// place while the table stays readable and writable. Call it from the migration in place of the
    /// generated <c>DropIndex</c> / <c>CreateIndex</c> pairs for those indexes, passing the migration's
    /// <c>TargetModel</c>: the index names, columns and filters are read from it, so they match your schema
    /// and naming convention.
    /// <para>
    /// Online index operations exist only on Enterprise (and Developer / Evaluation), Azure SQL Database and
    /// Azure SQL Managed Instance. Elsewhere the statement falls back to <c>ONLINE = OFF</c>: the build then
    /// locks the table while it runs, so apply the migration in a quiet window there. Each statement runs
    /// outside the migration's transaction, so its lock ends with the build.
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

    internal static IReadOnlyList<string> BuildStatements(IReadOnlyModel targetModel)
    {
        var statements = new List<string>();
        foreach (var index in WarpJobIndexes.FindPartial(targetModel))
        {
            var table = string.IsNullOrEmpty(index.Schema) ? Quote(index.Table) : $"{Quote(index.Schema)}.{Quote(index.Table)}";
            var columns = string.Join(", ", index.Columns.Select(Quote));
            var unique = index.IsUnique ? "UNIQUE " : string.Empty;
            var create = $"CREATE {unique}INDEX {Quote(index.Name)} ON {table} ({columns}) WHERE {index.Filter} WITH (DROP_EXISTING = ";

            // Engine editions 3 (Enterprise, Developer, Evaluation), 5 (Azure SQL Database) and 8 (Managed
            // Instance) build indexes online; every other edition rejects ONLINE = ON, so it builds offline.
            // DROP_EXISTING needs the index to exist: on a database that never had it, it is just created.
            statements.Add($"""
                DECLARE @online nvarchar(3) = CASE WHEN CAST(SERVERPROPERTY('EngineEdition') AS int) IN (3, 5, 8) THEN N'ON' ELSE N'OFF' END;
                DECLARE @existing nvarchar(3) = CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'{Literal(table)}') AND name = N'{Literal(index.Name)}') THEN N'ON' ELSE N'OFF' END;
                DECLARE @sql nvarchar(max) = N'{Literal(create)}' + @existing + N', ONLINE = ' + @online + N');';
                EXEC sp_executesql @sql;
                """);
        }

        return statements;
    }

    private static string Quote(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string Literal(string text) => text.Replace("'", "''", StringComparison.Ordinal);
}
