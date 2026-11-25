using Npgsql;
using Warp.Core.Handlers;

namespace Warp.Comparison;

/// <summary>
/// The jobs both libraries run. <c>noop</c> does nothing, so it measures the library alone: claiming a job, running
/// it and recording that it finished. <c>work</c> reads one row and updates it with the same SQL in both libraries, so
/// the handler's own database work shares the server with the queue, as it does in an application. <c>jitter</c>
/// waits 10 to 300 ms, so workers free up one at a time and the claims show how the queue refills them.
/// </summary>
public static class Workload
{
    public const string ItemTable = "comparison_item";

    // Set once before any job runs; both libraries' handlers read it.
    public static string ConnectionString { get; set; } = string.Empty;

    private static long _busyMs;

    /// <summary>Milliseconds the jitter handlers spent in their delay since the last reset: the work the workers had to do.</summary>
    public static long BusyMs => Interlocked.Read(ref _busyMs);

    public static void ResetBusy() => Interlocked.Exchange(ref _busyMs, 0);

    /// <summary>
    /// The jitter job's delay, 10 to 300 ms, fixed by the job's position in the backlog (a multiplicative hash) so every
    /// arm gets the same delays in the same order and the ideal drain time is the same for all of them.
    /// </summary>
    public static int JitterDelayMs(int index) => 10 + (int)(unchecked((uint)index * 2654435761u) % 291);

    public static async Task JitterAsync(int delayMs, CancellationToken ct)
    {
        await Task.Delay(delayMs, ct);
        Interlocked.Add(ref _busyMs, delayMs);
    }

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

public sealed class JitterJob : IJob
{
    public int DelayMs { get; set; }
}

public sealed class JitterJobHandler : IJobHandler<JitterJob>
{
    public Task HandleAsync(JitterJob message, CancellationToken cancellationToken) =>
        Workload.JitterAsync(message.DelayMs, cancellationToken);
}

// Hangfire: a job is a serialized method call.
public static class HangfireJobs
{
    public static void Noop()
    {
        // Empty on purpose: the empty job measures the library alone.
    }

    public static Task Work(int itemId) => Workload.WorkAsync(itemId, CancellationToken.None);

    public static Task Jitter(int delayMs) => Workload.JitterAsync(delayMs, CancellationToken.None);
}
