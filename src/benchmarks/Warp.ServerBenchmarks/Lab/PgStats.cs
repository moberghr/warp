using Npgsql;

namespace Warp.ServerBenchmarks.Lab;

/// <summary>
/// A point-in-time read of the server-side statistics views, so load is measured by the database
/// itself rather than inferred from the client.
/// <para>
/// Everything is captured as a snapshot and reported as a difference between two snapshots. Diffing
/// rather than calling <c>pg_stat_reset()</c> keeps this usable against an instance where the login
/// is not a superuser, which matters as soon as it is pointed at a real database instead of a
/// throwaway container.
/// </para>
/// <para>
/// Scoping to "our own load" relies on Warp owning the database it is pointed at: the
/// <c>pg_stat_database</c> and <c>pg_stat_user_tables</c> rows are per-database already, and the
/// <c>pg_stat_statements</c> read is filtered to the current database's OID. The harness's own
/// bookkeeping queries are excluded by name.
/// </para>
/// </summary>
public sealed class PgStats
{
    public required Dictionary<long, StatementStat> Statements { get; init; }

    public required DatabaseStat Database { get; init; }

    public required Dictionary<string, TableStat> Tables { get; init; }

    public sealed record StatementStat(long Calls, double TotalExecMs, long Rows, long Blocks, string Query);

    public sealed record DatabaseStat(
        long XactCommit,
        long XactRollback,
        long TupReturned,
        long TupFetched,
        long TupInserted,
        long TupUpdated,
        long TupDeleted,
        long BlksRead,
        long BlksHit);

    public sealed record TableStat(long Inserted, long Updated, long Deleted, long SeqScan, long IdxScan);

    public static async Task<bool> HasStatStatementsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM pg_extension WHERE extname = 'pg_stat_statements';", connection);
        var count = (long)(await command.ExecuteScalarAsync() ?? 0L);

        return count > 0;
    }

    public static async Task TryCreateExtensionAsync(string connectionString)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "CREATE EXTENSION IF NOT EXISTS pg_stat_statements;", connection);
            await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException)
        {
            // Not a superuser, or the library is not preloaded. Capture() degrades to the
            // pg_stat_database and pg_stat_user_tables numbers, which need no extension.
        }
    }

    /// <summary>An empty snapshot, for providers whose server-side statistics this does not read.</summary>
    public static PgStats Empty => new()
    {
        Statements = [],
        Database = new DatabaseStat(0, 0, 0, 0, 0, 0, 0, 0, 0),
        Tables = [],
    };

    public static async Task<PgStats> CaptureAsync(string connectionString, bool withStatements)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        return new PgStats
        {
            Statements = withStatements ? await ReadStatementsAsync(connection) : [],
            Database = await ReadDatabaseAsync(connection),
            Tables = await ReadTablesAsync(connection),
        };
    }

    private static async Task<Dictionary<long, StatementStat>> ReadStatementsAsync(NpgsqlConnection connection)
    {
        var result = new Dictionary<long, StatementStat>();

        await using var command = new NpgsqlCommand(
            """
            SELECT queryid, calls, total_exec_time, rows,
                   COALESCE(shared_blks_hit, 0) + COALESCE(shared_blks_read, 0) AS blocks, query
            FROM pg_stat_statements
            WHERE dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
              AND query NOT LIKE '%pg_stat_%'
              AND queryid IS NOT NULL;
            """,
            connection);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result[reader.GetInt64(0)] = new StatementStat(
                reader.GetInt64(1), reader.GetDouble(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetString(5));
        }

        return result;
    }

    private static async Task<DatabaseStat> ReadDatabaseAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT xact_commit, xact_rollback, tup_returned, tup_fetched,
                   tup_inserted, tup_updated, tup_deleted, blks_read, blks_hit
            FROM pg_stat_database
            WHERE datname = current_database();
            """,
            connection);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return new DatabaseStat(0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        return new DatabaseStat(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8));
    }

    private static async Task<Dictionary<string, TableStat>> ReadTablesAsync(NpgsqlConnection connection)
    {
        var result = new Dictionary<string, TableStat>(StringComparer.Ordinal);

        await using var command = new NpgsqlCommand(
            """
            SELECT relname, n_tup_ins, n_tup_upd, n_tup_del,
                   COALESCE(seq_scan, 0), COALESCE(idx_scan, 0)
            FROM pg_stat_user_tables;
            """,
            connection);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result[reader.GetString(0)] = new TableStat(
                reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5));
        }

        return result;
    }

    /// <summary>Subtracts <paramref name="before"/> from this snapshot, yielding the window's load.</summary>
    public PgStatsDelta Since(PgStats before)
    {
        var statements = new List<StatementStat>();
        foreach (var (queryId, after) in Statements)
        {
            var baseline = before.Statements.GetValueOrDefault(queryId);
            var calls = after.Calls - (baseline?.Calls ?? 0);
            if (calls <= 0)
            {
                continue;
            }

            statements.Add(new StatementStat(
                calls,
                after.TotalExecMs - (baseline?.TotalExecMs ?? 0),
                after.Rows - (baseline?.Rows ?? 0),
                after.Blocks - (baseline?.Blocks ?? 0),
                after.Query));
        }

        var tables = new Dictionary<string, TableStat>(StringComparer.Ordinal);
        foreach (var (name, after) in Tables)
        {
            var baseline = before.Tables.GetValueOrDefault(name);
            var stat = new TableStat(
                after.Inserted - (baseline?.Inserted ?? 0),
                after.Updated - (baseline?.Updated ?? 0),
                after.Deleted - (baseline?.Deleted ?? 0),
                after.SeqScan - (baseline?.SeqScan ?? 0),
                after.IdxScan - (baseline?.IdxScan ?? 0));

            if (stat.Inserted + stat.Updated + stat.Deleted + stat.SeqScan + stat.IdxScan > 0)
            {
                tables[name] = stat;
            }
        }

        return new PgStatsDelta
        {
            Statements = [.. statements.OrderByDescending(x => x.TotalExecMs)],
            Database = new DatabaseStat(
                Database.XactCommit - before.Database.XactCommit,
                Database.XactRollback - before.Database.XactRollback,
                Database.TupReturned - before.Database.TupReturned,
                Database.TupFetched - before.Database.TupFetched,
                Database.TupInserted - before.Database.TupInserted,
                Database.TupUpdated - before.Database.TupUpdated,
                Database.TupDeleted - before.Database.TupDeleted,
                Database.BlksRead - before.Database.BlksRead,
                Database.BlksHit - before.Database.BlksHit),
            Tables = tables,
        };
    }
}

public sealed class PgStatsDelta
{
    public required IReadOnlyList<PgStats.StatementStat> Statements { get; init; }

    public required PgStats.DatabaseStat Database { get; init; }

    public required Dictionary<string, PgStats.TableStat> Tables { get; init; }

    public long TotalCalls => Statements.Sum(x => x.Calls);

    public double TotalExecMs => Statements.Sum(x => x.TotalExecMs);

    public long TotalTransactions => Database.XactCommit + Database.XactRollback;
}
