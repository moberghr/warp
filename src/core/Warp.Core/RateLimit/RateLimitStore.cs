using Microsoft.EntityFrameworkCore;
using Warp.Core.Data;
using Warp.Core.Data.Entities;

namespace Warp.Core.RateLimit;

/// <summary>
/// The rate-limit pipeline's bucket rows, on Warp's own context opened only when a job with a rate limit
/// asks (rule 6.17): never the application's context.
/// </summary>
internal sealed class RateLimitStore : IRateLimitStore
{
    private readonly DeferredWarpContext _warp;
    private readonly IDatabaseExceptionClassifier _exceptionClassifier;

    public RateLimitStore(DeferredWarpContext warp, IDatabaseExceptionClassifier exceptionClassifier)
    {
        _warp = warp;
        _exceptionClassifier = exceptionClassifier;
    }

    public Task<RateLimitBucket?> GetAsync(string name, CancellationToken ct)
    {
        return _warp.Context.Set<RateLimitBucket>()
            .AsNoTracking()
            .Where(x => x.Name == name)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<bool> SetNextSlotAsync(string name, DateTime nextSlotUtc, CancellationToken ct)
    {
        var affected = await _warp.Context.Set<RateLimitBucket>()
            .Where(x => x.Name == name)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextSlotUtc, nextSlotUtc), ct);

        return affected > 0;
    }

    /// <summary>
    /// Persists the bucket state before the handler runs. This is one of the few places that
    /// deviates from CLAUDE.md §5.7 ("Services should not call SaveChanges — the caller saves"):
    /// the rate-limit invariant must hold across the handler boundary, so the pipeline behaviour
    /// has to commit the increment before yielding. Uses EF Core's ExecuteUpdate followed by an
    /// insert on an isolated context, so an insert that loses the unique-key race leaves nothing
    /// tracked behind.
    /// </summary>
    public async Task UpsertAsync(string name, DateTime windowStartUtc, int currentCount, string? timestampsJson, DateTime updatedAt, CancellationToken ct)
    {
        if (await UpdateAsync(name, windowStartUtc, currentCount, timestampsJson, updatedAt, ct) > 0)
        {
            return;
        }

        await using var scope = _warp.CreateIsolatedScope(out var insertContext);
        try
        {
            insertContext.Set<RateLimitBucket>().Add(
                new RateLimitBucket
                {
                    Name = name,
                    WindowStartUtc = windowStartUtc,
                    CurrentCount = currentCount,
                    TimestampsJson = timestampsJson,
                    UpdatedAt = updatedAt,
                });
            await insertContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (_exceptionClassifier.IsUniqueConstraintViolation(ex))
        {
            await UpdateAsync(name, windowStartUtc, currentCount, timestampsJson, updatedAt, ct);
        }
    }

    private Task<int> UpdateAsync(string name, DateTime windowStartUtc, int currentCount, string? timestampsJson, DateTime updatedAt, CancellationToken ct)
    {
        return _warp.Context.Set<RateLimitBucket>()
            .Where(x => x.Name == name)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.WindowStartUtc, windowStartUtc)
                    .SetProperty(x => x.CurrentCount, currentCount)
                    .SetProperty(x => x.TimestampsJson, timestampsJson)
                    .SetProperty(x => x.UpdatedAt, updatedAt),
                ct);
    }
}
