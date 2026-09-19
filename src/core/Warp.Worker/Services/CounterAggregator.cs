using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data.Entities;

namespace Warp.Worker.Services;

/// <summary>
/// Aggregates write-optimised <c>Counter</c> rows into the read-optimised <c>Statistic</c>
/// table. Counter writes happen on the hot path (every completed / failed job); this task
/// folds them into the Statistic totals on a short interval and clears the Counter rows.
/// </summary>
public sealed class CounterAggregator<TContext> : IServerTask
    where TContext : DbContext
{
    private readonly DbContext _context;
    private readonly WarpServerConfiguration _configuration;

    public CounterAggregator(
        IWarpServerContext serverContext,
        IOptions<WarpServerConfiguration> configuration)
    {
        _context = serverContext.Context;
        _configuration = configuration.Value;
    }

    public string Name => "AggregateCounters";

    public string? LockKey => "warp:counter-aggregation";

    public TimeSpan? DefaultInterval => _configuration.CounterAggregationInterval;

    // Re-run back-to-back while there is still a backlog. MaxBatchesPerTick caps how much ONE tick
    // drains, so without this a backlog would only shrink by that cap once per CounterAggregationInterval
    // (a minute by default) and could never catch up with a busy worker pool.
    public bool RerunImmediately => true;

    // Re-ticking means this runs far more often than its interval, and every logged run costs a
    // ServerTask UPDATE plus a ServerLog INSERT. ExpirationCleanup sizes ServerLog retention as
    // "300 runs" from the interval, so logging each drain would both amplify writes and blow that
    // estimate. Same stance as the other high-frequency metrics tasks (Heartbeat, BacklogSampler,
    // SloEvaluator, StatisticRollup) — a failure still logs.
    public bool LogOnSuccess => false;

    public async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        var count = await AggregateCountersAsync(ct);

        return count > 0 ? $"Aggregated {count} counter rows" : null;
    }

    // Distinct-key IN-list for the Statistic pre-load stays well under the SQL Server 2100-parameter limit,
    // and each batch's in-memory footprint is bounded regardless of how large the backlog grew.
    private const int AggregationBatchSize = 1000;

    // Hard cap on how much one tick drains, and the reason this task is not allowed to "just finish".
    //
    // LocksWithTransaction defaults to true (§2.16), so the task host wraps the WHOLE of ExecuteAsync in
    // one transaction and every per-batch SaveChangesAsync below only FLUSHES — nothing commits until
    // ExecuteAsync returns. An unbounded drain therefore holds a single transaction open for as long as it
    // takes to empty the table. Measured on a 500k-job backlog (~20 Counter rows per job, 4.3M rows): one
    // transaction open for 51 minutes.
    //
    // A transaction that old pins the vacuum horizon for the entire database — autovacuum cannot reclaim
    // any tuple newer than its snapshot. The job table meanwhile takes two updates per job and gets ZERO
    // HOT updates (CurrentState is part of the worker's fetch index, so every state transition writes new
    // index entries and orphans the old ones), which measured 43% dead tuples that vacuum was not allowed
    // to remove. The worker's claim scan — ORDER BY queue, schedule_time ... FOR UPDATE SKIP LOCKED — then
    // walks an index range that is mostly corpses, and degraded from 3.3ms to 253ms per claim, collapsing
    // throughput from 567 to 27 jobs/sec. The aggregator running longer made the bloat worse, which made
    // the aggregator slower: the backlog was self-reinforcing.
    //
    // Bounding the slice keeps each transaction short (~1s) so vacuum stays unblocked; a large backlog
    // simply drains over consecutive ticks via RerunImmediately. Do NOT remove this cap to "drain faster".
    private const int MaxBatchesPerTick = 20;

    internal async Task<int> AggregateCountersAsync(CancellationToken ct)
    {
        var total = 0;

        // Drain in id-ordered batches instead of materialising the ENTIRE Counter table at once: under a
        // high write-volume backlog (hot adapters write many counters per call) the table can hold hundreds
        // of thousands of rows between ticks. Each batch folds its counters into the Statistic totals and
        // deletes them in one transaction — additive and atomic per batch, so the next batch reads what
        // remains and a mid-drain failure only re-processes an uncommitted batch.
        for (var drained = 0; drained < MaxBatchesPerTick; drained++)
        {
            var batch = await _context.Set<Counter>()
                .OrderBy(x => x.Id)
                .Take(AggregationBatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                break;
            }

            var sums = batch
                .GroupBy(x => x.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Sum(x => (long)x.Value), StringComparer.Ordinal);

            // One query for every existing Statistic in this batch instead of a FindAsync per distinct key
            // (the old N+1 that, at adapter cardinality, meant thousands of round-trips per tick).
            var keys = sums.Keys.ToList();
            var existing = await _context.Set<Statistic>()
                .Where(x => keys.Contains(x.Key))
                .ToDictionaryAsync(x => x.Key, ct);

            foreach (var (key, sum) in sums)
            {
                if (existing.TryGetValue(key, out var stat))
                {
                    stat.Value += sum;
                }
                else
                {
                    _context.Set<Statistic>().Add(new Statistic { Key = key, Value = sum });
                }
            }

            _context.Set<Counter>().RemoveRange(batch);
            await _context.SaveChangesAsync(ct);

            total += batch.Count;

            // Drop the batch's entities from the tracker. They are already flushed, and holding every
            // Counter and Statistic drained this tick would make change detection on each subsequent
            // SaveChanges proportional to everything drained before it.
            _context.ChangeTracker.Clear();
        }

        return total;
    }
}
