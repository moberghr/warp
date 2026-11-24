using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Warp.MatrixBench;

/// <summary>One cached SQL Server plan statement: when it was compiled and what it has done since.</summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct PlanStats(DateTime Compiled, long Executions, long LogicalReads);

/// <summary>
/// A reading of the database server's own counters. On PostgreSQL, <c>pg_stat_statements</c> for the round's
/// database plus the WAL position (cluster-wide WAL bytes); on SQL Server, <c>dm_exec_query_stats</c> plan by plan plus the Batch
/// Requests/sec and Transactions/sec counters (cumulative despite their names).
/// </summary>
public sealed record DbSnapshot(
    long Statements,
    long Buffers,
    long StatementWalBytes,
    long? WalBytes,
    long? BatchRequests,
    long? Transactions,
    Dictionary<string, PlanStats>? Plans);

/// <summary>What happened between two readings.</summary>
public sealed record DbDelta(long Statements, long Buffers, long StatementWalBytes, long? WalBytes, long? BatchRequests, long? Transactions);

public static class DbStats
{
    /// <summary>Creates the round's database on the shared server and returns its connection string.</summary>
    public static async Task<BenchTarget> CreateDatabaseAsync(CellOptions options, string database)
    {
        if (options.Provider == BenchProvider.PostgreSql)
        {
            var admin = new NpgsqlConnectionStringBuilder(options.Connection) { Database = "postgres" }.ConnectionString;
            await using (var connection = new NpgsqlConnection(admin))
            {
                await connection.OpenAsync();
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
                await create.ExecuteNonQueryAsync();
            }

            var target = new NpgsqlConnectionStringBuilder(options.Connection)
            {
                Database = database,

                // Room for ten workers, the server tasks, the lock pool and the db job's own connections.
                MaxPoolSize = 100,
            };

            return new BenchTarget(options.Provider, target.ConnectionString, database);
        }

        var master = new SqlConnectionStringBuilder(options.Connection) { InitialCatalog = "master" }.ConnectionString;
        await using (var sql = new SqlConnection(master))
        {
            await sql.OpenAsync();

            // Service Broker carries UseDatabasePush on SQL Server, and the branch's claim needs compatibility
            // level 130 or later. Both set here rather than left to defaults, so every cell starts the same.
            await using var create = new SqlCommand(
                $"""
                CREATE DATABASE [{database}];
                ALTER DATABASE [{database}] SET COMPATIBILITY_LEVEL = 160;
                ALTER DATABASE [{database}] SET ENABLE_BROKER WITH ROLLBACK IMMEDIATE;
                """,
                sql);
            create.CommandTimeout = 120;
            await create.ExecuteNonQueryAsync();
        }

        var builder = new SqlConnectionStringBuilder(options.Connection)
        {
            InitialCatalog = database,
            MaxPoolSize = 200,
        };

        return new BenchTarget(options.Provider, builder.ConnectionString, database);
    }

    public static async Task<DbSnapshot?> CaptureAsync(CellOptions options, string database)
    {
        try
        {
            return options.Provider == BenchProvider.PostgreSql
                ? await CapturePostgresAsync(options.Connection, database)
                : await CaptureSqlServerAsync(options.Connection, database);
        }
        catch (Exception ex) when (ex is NpgsqlException or SqlException or InvalidOperationException)
        {
            Console.WriteLine($"  db stats unavailable: {ex.GetType().Name}: {ex.Message}");

            return null;
        }
    }

    /// <summary>
    /// SQL Server is compared plan by plan, the way <c>DatabaseStatementsDiagnoser</c> does it: a plan
    /// recompiled or evicted takes its whole history out of the view, so subtracting two totals would
    /// subtract work done before the window too. A plan compiled inside the window counts in full.
    /// </summary>
    public static DbDelta Delta(DbSnapshot before, DbSnapshot after)
    {
        var statements = after.Statements - before.Statements;
        var buffers = after.Buffers - before.Buffers;

        if (before.Plans is { } beforePlans && after.Plans is { } afterPlans)
        {
            statements = 0;
            buffers = 0;
            foreach (var (id, plan) in afterPlans)
            {
                var sameCompile = beforePlans.TryGetValue(id, out var earlier)
                    && earlier.Compiled == plan.Compiled
                    && earlier.Executions <= plan.Executions;

                statements += sameCompile ? plan.Executions - earlier.Executions : plan.Executions;
                buffers += sameCompile ? plan.LogicalReads - earlier.LogicalReads : plan.LogicalReads;
            }
        }

        return new DbDelta(
            statements,
            buffers,
            after.StatementWalBytes - before.StatementWalBytes,
            after.WalBytes - before.WalBytes,
            after.BatchRequests - before.BatchRequests,
            after.Transactions - before.Transactions);
    }

    /// <summary>
    /// CPU the database container has used so far, in milliseconds, read from its cgroup. Null when the
    /// container was not named or docker is not reachable: a missing figure, never a zero.
    /// </summary>
    public static async Task<double?> ReadContainerCpuMsAsync(string? container)
    {
        if (string.IsNullOrEmpty(container))
        {
            return null;
        }

        // cgroup v2 first (ubuntu-24.04 runners), then v1.
        var v2 = await DockerCatAsync(container, "/sys/fs/cgroup/cpu.stat");
        var usage = v2?
            .Split('\n')
            .Select(x => x.Trim())
            .Where(x => x.StartsWith("usage_usec ", StringComparison.Ordinal))
            .Select(x => x["usage_usec ".Length..])
            .FirstOrDefault();

        if (usage is not null && long.TryParse(usage, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
        {
            return microseconds / 1000.0;
        }

        var v1 = await DockerCatAsync(container, "/sys/fs/cgroup/cpuacct/cpuacct.usage");

        return long.TryParse(v1?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var nanoseconds)
            ? nanoseconds / 1_000_000.0
            : null;
    }

    private static async Task<string?> DockerCatAsync(string container, string path)
    {
        try
        {
            var start = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add(container);
            start.ArgumentList.Add("cat");
            start.ArgumentList.Add(path);

            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            return process.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static async Task<DbSnapshot> CapturePostgresAsync(string connection, string database)
    {
        // Read through the admin database, where the start script created the extension. Its own traffic
        // is therefore attributed to that database and falls outside the dbid filter.
        var admin = new NpgsqlConnectionStringBuilder(connection) { Database = "postgres" }.ConnectionString;
        await using var npgsql = new NpgsqlConnection(admin);
        await npgsql.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            SELECT COALESCE(SUM(calls), 0)::bigint,
                   COALESCE(SUM(shared_blks_hit + shared_blks_read), 0)::bigint,
                   COALESCE(SUM(wal_bytes), 0)::bigint
            FROM pg_stat_statements
            WHERE dbid = (SELECT oid FROM pg_database WHERE datname = @db);
            """,
            npgsql);
        command.Parameters.AddWithValue("db", database);

        long statements;
        long buffers;
        long statementWal;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            await reader.ReadAsync();
            statements = reader.GetInt64(0);
            buffers = reader.GetInt64(1);
            statementWal = reader.GetInt64(2);
        }

        long? wal = null;
        try
        {
            await using var walCommand = new NpgsqlCommand("SELECT pg_wal_lsn_diff(pg_current_wal_lsn(), '0/0')::bigint;", npgsql);
            wal = Convert.ToInt64(await walCommand.ExecuteScalarAsync());
        }
        catch (PostgresException)
        {
            // A server that refuses the WAL position (a replica) leaves the cluster figure missing; the per-statement WAL stays.
        }

        return new DbSnapshot(statements, buffers, statementWal, wal, null, null, null);
    }

    private static async Task<DbSnapshot> CaptureSqlServerAsync(string connection, string database)
    {
        var master = new SqlConnectionStringBuilder(connection) { InitialCatalog = "master" }.ConnectionString;
        await using var sql = new SqlConnection(master);
        await sql.OpenAsync();

        // One batch whose text names dm_exec_query_stats throughout, which is how it keeps itself out of the count.
        await using var command = new SqlCommand(
            """
            SELECT CONVERT(varchar(130), qs.plan_handle, 1) + ':' + CAST(qs.statement_start_offset AS varchar(12)) AS id,
                   qs.creation_time, qs.execution_count, qs.total_logical_reads
            FROM sys.dm_exec_query_stats qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
            WHERE st.text NOT LIKE '%dm_exec_query_stats%';

            SELECT RTRIM(counter_name), cntr_value /* dm_exec_query_stats */
            FROM sys.dm_os_performance_counters
            WHERE (RTRIM(counter_name) = 'Batch Requests/sec' AND object_name LIKE '%SQL Statistics%')
               OR (RTRIM(counter_name) = 'Transactions/sec' AND object_name LIKE '%:Databases%' AND RTRIM(instance_name) = @db);
            """,
            sql);
        command.Parameters.AddWithValue("@db", database);

        await using var reader = await command.ExecuteReaderAsync();

        var plans = new Dictionary<string, PlanStats>(StringComparer.Ordinal);
        long statements = 0;
        long buffers = 0;
        while (await reader.ReadAsync())
        {
            var id = reader.GetString(0);
            var plan = new PlanStats(reader.GetDateTime(1), Convert.ToInt64(reader.GetValue(2)), Convert.ToInt64(reader.GetValue(3)));
            plans[id] = plans.TryGetValue(id, out var same)
                ? same with { Executions = same.Executions + plan.Executions, LogicalReads = same.LogicalReads + plan.LogicalReads }
                : plan;
            statements += plan.Executions;
            buffers += plan.LogicalReads;
        }

        long? batchRequests = null;
        long? transactions = null;
        if (await reader.NextResultAsync())
        {
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                var value = Convert.ToInt64(reader.GetValue(1));
                if (string.Equals(name, "Batch Requests/sec", StringComparison.Ordinal))
                {
                    batchRequests = value;
                }
                else
                {
                    transactions = value;
                }
            }
        }

        return new DbSnapshot(statements, buffers, 0, null, batchRequests, transactions, plans);
    }
}
