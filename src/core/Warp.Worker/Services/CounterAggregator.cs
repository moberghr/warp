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

    public bool RerunImmediately => false;

    public async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        var count = await AggregateCountersAsync(ct);

        return count > 0 ? $"Aggregated {count} counter rows" : null;
    }

    // Distinct-key IN-list for the Statistic pre-load stays well under the SQL Server 2100-parameter limit,
    // and each batch's in-memory footprint is bounded regardless of how large the backlog grew.
    private const int AggregationBatchSize = 1000;

    internal async Task<int> AggregateCountersAsync(CancellationToken ct)
    {
        var total = 0;

        // Drain in id-ordered batches instead of materialising the ENTIRE Counter table at once: under a
        // high write-volume backlog (hot adapters write many counters per call) the table can hold hundreds
        // of thousands of rows between ticks. Each batch folds its counters into the Statistic totals and
        // deletes them in one transaction — additive and atomic per batch, so the next batch reads what
        // remains and a mid-drain failure only re-processes an uncommitted batch.
        while (true)
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
        }

        return total;
    }
}
