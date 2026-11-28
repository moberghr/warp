using Microsoft.EntityFrameworkCore;
using Warp.Core.Data.Entities;

namespace Warp.Core.RateLimit;

public class RateLimitManager<TContext> : IRateLimitManager
    where TContext : DbContext
{
    private readonly DbContext _context;
    private readonly TimeProvider _timeProvider;

    public RateLimitManager(TContext context, TimeProvider timeProvider)
        : this((DbContext)context, timeProvider)
    {
    }

    // The Warp context (rule 2.14): AddWarp passes IWarpContext.Context, which is not a TContext.
    internal RateLimitManager(DbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task AddOrUpdateLimit(string name, int count, int windowSeconds, CancellationToken ct = default)
    {
        RateLimitNames.Validate(name);
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Count must be at least 1.");
        }

        if (windowSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSeconds), windowSeconds, "WindowSeconds must be at least 1.");
        }

        if (windowSeconds > RateLimitAttribute.MaxWindowSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSeconds), windowSeconds, $"WindowSeconds must be at most {RateLimitAttribute.MaxWindowSeconds} (7 days).");
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var existing = await _context.Set<RateLimitOverride>()
            .Where(x => x.Name == name)
            .FirstOrDefaultAsync(ct);

        if (existing != null)
        {
            existing.Count = count;
            existing.WindowSeconds = windowSeconds;
            existing.UpdatedAt = now;
        }
        else
        {
            await _context.Set<RateLimitOverride>().AddAsync(
                new RateLimitOverride
                {
                    Name = name,
                    Count = count,
                    WindowSeconds = windowSeconds,
                    UpdatedAt = now,
                },
                ct);
        }

        await _context.SaveChangesAsync(ct);
    }

    public async Task<bool> RemoveLimit(string name, CancellationToken ct = default)
    {
        RateLimitNames.Validate(name);

        var existing = await _context.Set<RateLimitOverride>()
            .Where(x => x.Name == name)
            .FirstOrDefaultAsync(ct);

        if (existing == null)
        {
            return false;
        }

        _context.Set<RateLimitOverride>().Remove(existing);
        await _context.SaveChangesAsync(ct);

        return true;
    }

    public async Task<RateLimitInfo?> GetLimit(string name, CancellationToken ct = default)
    {
        RateLimitNames.Validate(name);

        return await _context.Set<RateLimitOverride>()
            .AsNoTracking()
            .Where(x => x.Name == name)
            .Select(x =>
                new RateLimitInfo(x.Name, x.Count, x.WindowSeconds, x.UpdatedAt))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<RateLimitInfo>> ListLimits(CancellationToken ct = default)
    {
        return await _context.Set<RateLimitOverride>()
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x =>
                new RateLimitInfo(x.Name, x.Count, x.WindowSeconds, x.UpdatedAt))
            .ToListAsync(ct);
    }
}
