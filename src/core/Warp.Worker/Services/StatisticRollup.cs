using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Services;

namespace Warp.Worker.Services;

/// <summary>
/// Downsamples time-bucketed <see cref="Statistic"/> rows across the metrics retention tiers (§8.30) — fine
/// (5-min) → hourly → daily — summing each expiring bucket into its coarser parent before deleting it, then
/// deleting daily buckets past their retention. Replaces the old delete-only hourly prune (which dropped detail
/// instead of rolling it up). Family-agnostic: it keys purely on the trailing <c>:{marker}:{stamp}</c> (or a
/// legacy unmarked <c>:{yyyy-MM-dd-HH}</c>), so every hist/pcth family rolls with one implementation and all
/// pre-3.10 hourly rows migrate to the marked scheme automatically. Lifetime totals, lifetime <c>pct</c>, and
/// the <c>qbacklog</c> gauge have no parseable tier suffix and are left untouched.
/// <para>
/// Sums-then-deletes inside the task's lock transaction (§2.3), so a crash mid-run can't double count — the
/// source rows are gone once their value lands in the parent. Retention ordering guarantees fine → hourly and
/// hourly → daily sources are disjoint from their targets (fine retention ≪ hourly retention ≪ daily).
/// </para>
/// </summary>
public sealed class StatisticRollup<TContext> : IServerTask
    where TContext : DbContext
{
    private readonly DbContext _context;
    private readonly WarpServerConfiguration _configuration;
    private readonly TimeProvider _timeProvider;

    public StatisticRollup(
        IWarpServerContext serverContext,
        IOptions<WarpServerConfiguration> configuration,
        TimeProvider timeProvider)
    {
        _context = serverContext.Context;
        _configuration = configuration.Value;
        _timeProvider = timeProvider;
    }

    public string Name => "RollupStatistics";

    public string? LockKey => "warp:stat-rollup";

    public TimeSpan? DefaultInterval => _configuration.StatisticRollupInterval;

    public bool RerunImmediately => false;

    public bool LogOnSuccess => false;

    public async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var fineCutoff = now - _configuration.FineResolutionRetention;
        var hourlyCutoff = now - _configuration.HourlyStatisticsRetention;
        var dailyCutoff = _configuration.DailyStatisticsRetention is { } daily ? now - daily : (DateTime?)null;
        var fineMinutes = _configuration.FineResolutionMinutes;

        // Only bucketed keys carry a ':'; the coarse SQL filter narrows before the in-memory tier parse.
        var rows = await _context.Set<Statistic>()
            .Where(x => EF.Functions.Like(x.Key, "%:%"))
            .Select(x => new { x.Key, x.Value })
            .ToListAsync(ct);

        var additions = new Dictionary<string, long>(StringComparer.Ordinal);
        var deletions = new List<string>();
        var fineTargets = new HashSet<string>(StringComparer.Ordinal);

        // Pass 1 — fine → hourly. Sum each stale fine bucket into its hourly parent and record which hourly keys
        // received a contribution this tick.
        foreach (var row in rows)
        {
            if (MetricTiers.TryClassifyKey(row.Key, out var baseKey, out var tier, out var bucketStart)
                && tier == MetricTier.Fine
                && bucketStart < fineCutoff)
            {
                var target = baseKey + MetricTiers.Suffix(MetricTier.Hourly, bucketStart, fineMinutes);
                additions[target] = additions.GetValueOrDefault(target) + row.Value;
                fineTargets.Add(target);
                deletions.Add(row.Key);
            }
        }

        // The second pass rolls hourly buckets into daily and prunes daily. When an hourly bucket also received
        // a fine contribution this tick, the rollup leaves it in place instead of rolling it, so the just-added
        // fine value is never lost. That bucket rolls on the next tick with the full value included. The
        // deferral only matters when rollup was disabled past the hourly window; otherwise the deferred set is
        // empty. A naive guard that just dropped the colliding target here would silently discard the fine value.
        foreach (var row in rows)
        {
            if (!MetricTiers.TryClassifyKey(row.Key, out var baseKey, out var tier, out var bucketStart))
            {
                continue;
            }

            if (tier == MetricTier.Hourly && bucketStart < hourlyCutoff)
            {
                if (fineTargets.Contains(row.Key))
                {
                    continue;
                }

                var target = baseKey + MetricTiers.Suffix(MetricTier.Daily, bucketStart, fineMinutes);
                additions[target] = additions.GetValueOrDefault(target) + row.Value;
                deletions.Add(row.Key);
            }
            else if (tier == MetricTier.Daily && dailyCutoff is { } dc && bucketStart < dc)
            {
                deletions.Add(row.Key);
            }
        }

        if (additions.Count == 0 && deletions.Count == 0)
        {
            return null;
        }

        var targetKeys = additions.Keys.ToList();
        var existing = await _context.Set<Statistic>()
            .Where(x => targetKeys.Contains(x.Key))
            .ToDictionaryAsync(x => x.Key, StringComparer.Ordinal, ct);

        foreach (var (key, add) in additions)
        {
            if (existing.TryGetValue(key, out var stat))
            {
                stat.Value += add;
            }
            else
            {
                _context.Set<Statistic>().Add(new Statistic { Key = key, Value = add });
            }
        }

        if (deletions.Count > 0)
        {
            await _context.Set<Statistic>()
                .Where(x => deletions.Contains(x.Key))
                .ExecuteDeleteAsync(ct);
        }

        await _context.SaveChangesAsync(ct);

        return $"Rolled {additions.Count} bucket(s), removed {deletions.Count} source row(s)";
    }
}
