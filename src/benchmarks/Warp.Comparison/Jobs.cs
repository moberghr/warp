using Npgsql;
using Warp.Core.Handlers;

namespace Warp.Comparison;

/// <summary>
/// The two jobs both libraries run. <see cref="Noop"/> does nothing, so it measures the library alone: claiming a
/// job, running it and recording that it finished. <see cref="Work"/> reads one row and updates it with the same
/// SQL in both libraries, so the handler's own database work shares the server with the queue, as it does in an
/// application.
/// </summary>
public static class Workload
{
    public const string ItemTable = "comparison_item";

    // Set once before any job runs; both libraries' handlers read it.
    public static string ConnectionString { get; set; } = string.Empty;

    public static async Task WorkAsync(int itemId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);

        await using (var read = new NpgsqlCommand($"SELECT value FROM {ItemTable} WHERE id = @id", connection))
        {
            read.Parameters.AddWithValue("id", itemId);
            await read.ExecuteScalarAsync(ct);
        }

        await using var update = new NpgsqlCommand($"UPDATE {ItemTable} SET value = value + 1 WHERE id = @id", connection);
        update.Parameters.AddWithValue("id", itemId);
        await update.ExecuteNonQueryAsync(ct);
    }
}

// Warp: a job is a class, run by its handler (found by the source generator).
public sealed class NoopJob : IJob;

public sealed class NoopJobHandler : IJobHandler<NoopJob>
{
    public Task HandleAsync(NoopJob message, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class WorkJob : IJob
{
    public int ItemId { get; set; }
}

public sealed class WorkJobHandler : IJobHandler<WorkJob>
{
    public Task HandleAsync(WorkJob message, CancellationToken cancellationToken) =>
        Workload.WorkAsync(message.ItemId, cancellationToken);
}

// Hangfire: a job is a serialized method call.
public static class HangfireJobs
{
    public static void Noop()
    {
        // Empty on purpose: the empty job measures the library alone.
    }

    public static Task Work(int itemId) => Workload.WorkAsync(itemId, CancellationToken.None);
}
