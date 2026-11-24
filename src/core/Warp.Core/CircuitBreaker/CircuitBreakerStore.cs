using Microsoft.EntityFrameworkCore;
using Warp.Core.Data;
using Warp.Core.Data.Entities;

namespace Warp.Core.CircuitBreaker;

/// <summary>
/// The circuit breaker's state rows, on Warp's own context opened only when a job asks (rule 6.17):
/// never the application's context.
/// </summary>
internal sealed class CircuitBreakerStore : ICircuitBreakerStore
{
    private readonly DeferredWarpContext _warp;
    private readonly IDatabaseExceptionClassifier _exceptionClassifier;

    public CircuitBreakerStore(DeferredWarpContext warp, IDatabaseExceptionClassifier exceptionClassifier)
    {
        _warp = warp;
        _exceptionClassifier = exceptionClassifier;
    }

    public Task<CircuitBreakerState?> GetAsync(string groupKey, CancellationToken cancellationToken)
    {
        return _warp.Context.Set<CircuitBreakerState>()
            .AsNoTracking()
            .Where(x => x.GroupKey == groupKey)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task ResetAsync(string groupKey, CancellationToken cancellationToken)
    {
        await _warp.Context.Set<CircuitBreakerState>()
            .Where(x => x.GroupKey == groupKey)
            .Where(x => x.FailureCount > 0 || x.State != CircuitState.Closed)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.FailureCount, 0)
                    .SetProperty(x => x.OpenUntil, (DateTime?)null)
                    .SetProperty(x => x.State, CircuitState.Closed),
                cancellationToken);
    }

    public async Task RecordFailureAsync(string groupKey, int threshold, TimeSpan duration, DateTime now, CancellationToken cancellationToken)
    {
        var openUntilIfThreshold = now + duration;
        var affected = await _warp.Context.Set<CircuitBreakerState>()
            .Where(x => x.GroupKey == groupKey)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.FailureCount, x => x.FailureCount + 1)
                    .SetProperty(x => x.LastFailureAt, now)
                    .SetProperty(x => x.OpenUntil, x => x.FailureCount + 1 >= threshold || x.State == CircuitState.HalfOpen ? openUntilIfThreshold : x.OpenUntil)
                    .SetProperty(x => x.State, x => x.FailureCount + 1 >= threshold || x.State == CircuitState.HalfOpen ? CircuitState.Open : x.State),
                cancellationToken);

        if (affected > 0)
        {
            return;
        }

        await using var scope = _warp.CreateIsolatedScope(out var insertContext);
        try
        {
            var trips = threshold <= 1;
            insertContext.Set<CircuitBreakerState>().Add(
                new CircuitBreakerState
                {
                    GroupKey = groupKey,
                    FailureCount = 1,
                    LastFailureAt = now,
                    OpenUntil = trips ? openUntilIfThreshold : null,
                    State = trips ? CircuitState.Open : CircuitState.Closed,
                });
            await insertContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (_exceptionClassifier.IsUniqueConstraintViolation(ex))
        {
            // Concurrent worker inserted the row first — fall back to ExecuteUpdate.
            // Non-unique DbUpdateException (CHECK, FK, column-length) skips this catch
            // and propagates so the operator sees the real constraint violation.
            await _warp.Context.Set<CircuitBreakerState>()
                .Where(x => x.GroupKey == groupKey)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(x => x.FailureCount, x => x.FailureCount + 1)
                        .SetProperty(x => x.LastFailureAt, now)
                        .SetProperty(x => x.OpenUntil, x => x.FailureCount + 1 >= threshold || x.State == CircuitState.HalfOpen ? openUntilIfThreshold : x.OpenUntil)
                        .SetProperty(x => x.State, x => x.FailureCount + 1 >= threshold || x.State == CircuitState.HalfOpen ? CircuitState.Open : x.State),
                    cancellationToken);
        }
    }

    public async Task<bool> TryBeginProbeAsync(string groupKey, DateTime now, CancellationToken cancellationToken)
    {
        // Atomic Open → HalfOpen transition. Exactly one concurrent caller's ExecuteUpdate
        // affects a row; every other caller observes the row already in HalfOpen and returns
        // false. No distributed lock — Postgres and SQL Server both guarantee per-row
        // serialization on UPDATE.
        var affected = await _warp.Context.Set<CircuitBreakerState>()
            .Where(x => x.GroupKey == groupKey)
            .Where(x => x.State == CircuitState.Open)
            .Where(x => x.OpenUntil != null && x.OpenUntil <= now)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.State, CircuitState.HalfOpen),
                cancellationToken);

        return affected > 0;
    }
}
