using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Warp.Core.Data;
using Warp.Core.Data.Entities;

namespace Warp.Core.RateLimit;

public class RateLimitStore<TContext> : IRateLimitStore
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDatabaseExceptionClassifier _exceptionClassifier;

    public RateLimitStore(TContext context, IServiceScopeFactory scopeFactory, IDatabaseExceptionClassifier exceptionClassifier)
    {
        _context = context;
        _scopeFactory = scopeFactory;
        _exceptionClassifier = exceptionClassifier;
    }

    public Task<RateLimitBucket?> GetAsync(string name, CancellationToken ct)
    {
        return _context.Set<RateLimitBucket>()
            .AsNoTracking()
            .Where(x => x.Name == name)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<bool> SetNextSlotAsync(string name, DateTime nextSlotUtc, CancellationToken ct)
    {
        var affected = await _context.Set<RateLimitBucket>()
            .Where(x => x.Name == name)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextSlotUtc, nextSlotUtc), ct);

        return affected > 0;
    }

    /// <summary>
    /// Persists the bucket state before the handler runs. This is one of the few places that
    /// deviates from CLAUDE.md §5.7 ("Services should not call SaveChanges — the caller saves"):
    /// the rate-limit invariant must hold across the handler boundary, so the pipeline behaviour
    /// has to commit the increment before yielding. Uses EF Core's ExecuteUpdate (which
    /// bypasses the scoped DbContext's change tracker) followed by a fresh-scope insert,
    /// keeping the rate-limit write isolated from any other tracked changes the caller may have.
    /// </summary>
    public async Task UpsertAsync(string name, DateTime windowStartUtc, int currentCount, string? timestampsJson, DateTime updatedAt, CancellationToken ct)
    {
        var affected = await _context.Set<RateLimitBucket>()
            .Where(x => x.Name == name)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.WindowStartUtc, windowStartUtc)
                    .SetProperty(x => x.CurrentCount, currentCount)
                    .SetProperty(x => x.TimestampsJson, timestampsJson)
                    .SetProperty(x => x.UpdatedAt, updatedAt),
                ct);

        if (affected > 0)
        {
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var insertContext = scope.ServiceProvider.GetRequiredService<TContext>();
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
            await _context.Set<RateLimitBucket>()
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
}
