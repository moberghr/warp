using Npgsql;
using Warp.Core.Handlers;

namespace Warp.Core.Handlers;

/// <summary>
/// A benchmark job that does some real work: it reads one row of a small table and updates it, with plain
/// Npgsql. Benchmarks comparing job libraries run the same database work in every library's job, so the
/// difference between them is the library's own overhead. The bench host registers
/// <see cref="BenchWorkDatabase"/> and creates the table with <see cref="BenchWorkDatabase.CreateItemsAsync"/>.
/// </summary>
public class BenchWorkRequest : IJob
{
    public int ItemId { get; set; }
}

public class BenchWorkCommand : IJobHandler<BenchWorkRequest>
{
    private readonly BenchWorkDatabase _database;

    public BenchWorkCommand(BenchWorkDatabase database)
    {
        _database = database;
    }

    public async Task HandleAsync(BenchWorkRequest message, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var read = new NpgsqlCommand("SELECT value FROM bench.item WHERE id = @id", connection);
        read.Parameters.AddWithValue("id", message.ItemId);
        var value = (long)(await read.ExecuteScalarAsync(cancellationToken) ?? 0L);

        await using var update = new NpgsqlCommand("UPDATE bench.item SET value = @value, updated_at = now() WHERE id = @id", connection);
        update.Parameters.AddWithValue("id", message.ItemId);
        update.Parameters.AddWithValue("value", value + 1);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>The bench database the work job reads and updates (PostgreSQL).</summary>
public sealed class BenchWorkDatabase
{
    public BenchWorkDatabase(string connectionString)
    {
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    /// <summary>Creates <c>bench.item</c> with <paramref name="items"/> rows (ids 0 to items - 1).</summary>
    public async Task CreateItemsAsync(int items, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            @"CREATE SCHEMA IF NOT EXISTS bench;
              DROP TABLE IF EXISTS bench.item;
              CREATE TABLE bench.item (id int PRIMARY KEY, value bigint NOT NULL, updated_at timestamptz NOT NULL);
              INSERT INTO bench.item (id, value, updated_at) SELECT g, 0, now() FROM generate_series(0, @last) g;",
            connection);
        command.Parameters.AddWithValue("last", items - 1);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
