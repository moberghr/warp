using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Warp.MatrixBench;

/// <summary>The round's database, as the harness and the db-job handler both need it.</summary>
public sealed record BenchTarget(BenchProvider Provider, string ConnectionString, string Database);

/// <summary>The harness's DbContext. Warp adds its own model to it; nothing else lives here.</summary>
public sealed class MatrixContext : DbContext
{
    public MatrixContext(DbContextOptions<MatrixContext> options)
        : base(options)
    {
    }
}

/// <summary>
/// The <c>bench.item</c> table the db job reads and writes: 1,000 rows, a counter each. Plain ADO.NET on a
/// pooled connection, deliberately outside any DbContext, so the job's own work is identical on both refs.
/// </summary>
public sealed class BenchItemStore
{
    public const int ItemCount = 1_000;

    private readonly BenchTarget _target;

    public BenchItemStore(BenchTarget target)
    {
        _target = target;
    }

    public static async Task CreateAsync(BenchTarget target)
    {
        if (target.Provider == BenchProvider.PostgreSql)
        {
            await using var connection = new NpgsqlConnection(target.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                $"""
                CREATE SCHEMA bench;
                CREATE TABLE bench.item (
                    id int PRIMARY KEY,
                    value bigint NOT NULL DEFAULT 0,
                    updated_at timestamptz NOT NULL DEFAULT now());
                INSERT INTO bench.item (id) SELECT g FROM generate_series(0, {ItemCount - 1}) g;
                """,
                connection);
            await command.ExecuteNonQueryAsync();

            return;
        }

        await using var sql = new SqlConnection(target.ConnectionString);
        await sql.OpenAsync();
        await using (var schema = new SqlCommand("CREATE SCHEMA bench;", sql))
        {
            await schema.ExecuteNonQueryAsync();
        }

        await using var table = new SqlCommand(
            $"""
            CREATE TABLE bench.item (
                id int PRIMARY KEY,
                value bigint NOT NULL DEFAULT 0,
                updated_at datetime2 NOT NULL DEFAULT SYSUTCDATETIME());
            WITH n AS (SELECT TOP ({ItemCount}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS id
                       FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT INTO bench.item (id) SELECT id FROM n;
            """,
            sql);
        await table.ExecuteNonQueryAsync();
    }

    public static async Task ResetAsync(BenchTarget target) =>
        await ExecuteAsync(target, "UPDATE bench.item SET value = 0;");

    public static async Task<long> SumAsync(BenchTarget target)
    {
        if (target.Provider == BenchProvider.PostgreSql)
        {
            await using var connection = new NpgsqlConnection(target.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT COALESCE(SUM(value), 0)::bigint FROM bench.item;", connection);

            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        await using var sql = new SqlConnection(target.ConnectionString);
        await sql.OpenAsync();
        await using var sum = new SqlCommand("SELECT COALESCE(SUM(value), 0) FROM bench.item;", sql);

        return Convert.ToInt64(await sum.ExecuteScalarAsync());
    }

    /// <summary>Read, then write back one more: a lost update or a second execution both show in the final sum.</summary>
    public async Task IncrementAsync(int id, CancellationToken cancellationToken)
    {
        if (_target.Provider == BenchProvider.PostgreSql)
        {
            await using var connection = new NpgsqlConnection(_target.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            await using var select = new NpgsqlCommand("SELECT value FROM bench.item WHERE id = @id", connection);
            select.Parameters.AddWithValue("id", id);
            var value = Convert.ToInt64(await select.ExecuteScalarAsync(cancellationToken));

            await using var update = new NpgsqlCommand("UPDATE bench.item SET value = @value, updated_at = now() WHERE id = @id", connection);
            update.Parameters.AddWithValue("value", value + 1);
            update.Parameters.AddWithValue("id", id);
            await update.ExecuteNonQueryAsync(cancellationToken);

            return;
        }

        await using var sql = new SqlConnection(_target.ConnectionString);
        await sql.OpenAsync(cancellationToken);

        await using var read = new SqlCommand("SELECT value FROM bench.item WHERE id = @id", sql);
        read.Parameters.AddWithValue("@id", id);
        var current = Convert.ToInt64(await read.ExecuteScalarAsync(cancellationToken));

        await using var write = new SqlCommand("UPDATE bench.item SET value = @value, updated_at = SYSUTCDATETIME() WHERE id = @id", sql);
        write.Parameters.AddWithValue("@value", current + 1);
        write.Parameters.AddWithValue("@id", id);
        await write.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(BenchTarget target, string sql)
    {
        if (target.Provider == BenchProvider.PostgreSql)
        {
            await using var connection = new NpgsqlConnection(target.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();

            return;
        }

        await using var server = new SqlConnection(target.ConnectionString);
        await server.OpenAsync();
        await using var statement = new SqlCommand(sql, server);
        await statement.ExecuteNonQueryAsync();
    }
}
