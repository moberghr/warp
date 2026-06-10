using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
    private readonly TContext _context;
    private readonly WarpServerConfiguration _configuration;

    public CounterAggregator(
        TContext context,
        IOptions<WarpServerConfiguration> configuration)
    {
        _context = context;
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

    internal async Task<int> AggregateCountersAsync(CancellationToken ct)
    {
        var counters = await _context.Set<Counter>().ToListAsync(ct);
        if (counters.Count == 0)
        {
            return 0;
        }

        var grouped = counters
            .GroupBy(x => x.Key)
            .Select(g => new { Key = g.Key, Sum = g.Sum(x => x.Value) });

        foreach (var group in grouped)
        {
            var stat = await _context.Set<Statistic>().FindAsync([group.Key], ct);
            if (stat != null)
            {
                stat.Value += group.Sum;
            }
            else
            {
                _context.Set<Statistic>().Add(new Statistic { Key = group.Key, Value = group.Sum });
            }
        }

        _context.Set<Counter>().RemoveRange(counters);
        await _context.SaveChangesAsync(ct);

        return counters.Count;
    }
}
