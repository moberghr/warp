using System.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace Warp.Core.Data.Queries;

/// <summary>
/// Runs a short check-then-insert under the transaction-scoped lock (§2.16, <c>RunUnderTransactionLockAsync</c>, safe
/// through any pooler) and WAITS for it: the lock is a try-lock, and a caller that loses it retries until the holder has
/// committed, so it then sees the holder's rows instead of inserting a copy. Used by the config seeders and the board's
/// tile cap, where N replicas starting at once (or two dashboard users) would otherwise each pass the check.
/// </summary>
internal static class TransactionLock
{
    /// <summary>The lock the board seeder and the dashboard's tile insert share (the tile cap).</summary>
    public const string MetricBoardKey = "warp:metric-board";

    /// <summary>The lock saved-view writes share (the per-author and shared caps).</summary>
    public const string SavedViewKey = "warp:saved-view";

    /// <summary>The lock the SLO seeders of every replica share.</summary>
    public const string SloSeedKey = "warp:slo-seed";

    /// <summary>How long a seeder waits for another replica's seed (a few single-table statements) before giving up.</summary>
    public static readonly TimeSpan SeedTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long a dashboard command waits for another insert or a replica's seed to commit.</summary>
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Runs <paramref name="work"/> under <paramref name="lockKey"/>. Without a provider (<paramref name="queries"/> null:
    /// an <c>AddWarp</c>-only host that never called <c>UsePostgreSql</c>/<c>UseSqlServer</c>) there is no lock to take,
    /// and the work runs unguarded as before. Throws <see cref="TimeoutException"/> when the lock stays held past
    /// <paramref name="timeout"/>.
    /// </summary>
    public static async Task<T> RunAsync<TContext, T>(
        IWarpSqlQueries<TContext>? queries,
        DbContext context,
        string lockKey,
        TimeSpan timeout,
        Func<DbContext, CancellationToken, Task<T>> work,
        CancellationToken ct)
        where TContext : DbContext
    {
        if (queries is null)
        {
            return await work(context, ct);
        }

        // Elapsed wall time, not TimeProvider: the timeout bounds a real wait, and a test's frozen clock must not make it endless.
        var waited = Stopwatch.StartNew();
        while (true)
        {
            var (held, result) = await queries.RunUnderTransactionLockAsync(context, lockKey, work, ct);
            if (held)
            {
                return result!;
            }

            if (waited.Elapsed >= timeout)
            {
                throw new TimeoutException($"The '{lockKey}' lock was still held after {timeout}.");
            }

            await Task.Delay(RetryDelay, ct);
        }
    }
}
