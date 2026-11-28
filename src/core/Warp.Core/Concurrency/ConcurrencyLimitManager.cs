using Microsoft.EntityFrameworkCore;
using Warp.Core.Data.Entities;

namespace Warp.Core.Concurrency;

public class ConcurrencyLimitManager<TContext> : IConcurrencyLimitManager
    where TContext : DbContext
{
    private readonly DbContext _context;
    private readonly TimeProvider _timeProvider;

    public ConcurrencyLimitManager(TContext context, TimeProvider timeProvider)
        : this((DbContext)context, timeProvider)
    {
    }

    // The Warp context (rule 2.14): AddWarp passes IWarpContext.Context, which is not a TContext.
    internal ConcurrencyLimitManager(DbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task AddOrUpdateLimit(string name, int limit, CancellationToken ct = default)
    {
        ConcurrencyNames.Validate(name);
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "Limit must be at least 1.");
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var existing = await _context.Set<ConcurrencyLimit>()
            .Where(x => x.Name == name)
            .FirstOrDefaultAsync(ct);

        if (existing != null)
        {
            existing.Limit = limit;
            existing.UpdatedAt = now;
        }
        else
        {
            await _context.Set<ConcurrencyLimit>().AddAsync(
                new ConcurrencyLimit
                {
                    Name = name,
                    Limit = limit,
                    UpdatedAt = now,
                },
                ct);
        }

        await _context.SaveChangesAsync(ct);
    }

    public async Task<bool> RemoveLimit(string name, CancellationToken ct = default)
    {
        ConcurrencyNames.Validate(name);

        var existing = await _context.Set<ConcurrencyLimit>()
            .Where(x => x.Name == name)
            .FirstOrDefaultAsync(ct);

        if (existing == null)
        {
            return false;
        }

        _context.Set<ConcurrencyLimit>().Remove(existing);
        await _context.SaveChangesAsync(ct);

        return true;
    }

    public async Task<ConcurrencyLimitInfo?> GetLimit(string name, CancellationToken ct = default)
    {
        ConcurrencyNames.Validate(name);

        return await _context.Set<ConcurrencyLimit>()
            .AsNoTracking()
            .Where(x => x.Name == name)
            .Select(x =>
                new ConcurrencyLimitInfo(x.Name, x.Limit, x.UpdatedAt))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<ConcurrencyLimitInfo>> ListLimits(CancellationToken ct = default)
    {
        return await _context.Set<ConcurrencyLimit>()
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x =>
                new ConcurrencyLimitInfo(x.Name, x.Limit, x.UpdatedAt))
            .ToListAsync(ct);
    }
}
