using Microsoft.EntityFrameworkCore;
using Warp.Core.Data;
using Warp.Core.Data.Entities;

namespace Warp.Core.Concurrency;

/// <summary>
/// Resolves the runtime-effective concurrency limit for a key by checking the admin override row, on
/// Warp's own context opened only when a job with a concurrency key asks (rule 6.17). Scoped — caches
/// lookups for the lifetime of one job execution scope so the pipeline doesn't re-query the DB if it is
/// invoked more than once for the same key. Cross-scope staleness is intentional: admin updates take
/// effect at the next pickup.
/// </summary>
public sealed class ConcurrencyLimitResolver
{
    private readonly Func<string, CancellationToken, Task<int?>> _lookup;

    // Allocated on first lookup: most jobs carry no concurrency key and never ask.
    private Dictionary<string, int?>? _cache;

    // For a host that builds the pipeline behaviour itself: limits are read through the given manager.
    // Warp's own registration reads its tables directly instead (rule 6.17), so a replaced IConcurrencyLimitManager
    // is not consulted by the registered pipeline.
    public ConcurrencyLimitResolver(IConcurrencyLimitManager manager)
        : this(async (name, ct) => (await manager.GetLimit(name, ct))?.Limit)
    {
    }

    internal ConcurrencyLimitResolver(DeferredWarpContext warp)
        : this((name, ct) => LookupAsync(warp, name, ct))
    {
    }

    // The admin row lookup as a function, so the pipeline's unit tests can run without a database.
    internal ConcurrencyLimitResolver(Func<string, CancellationToken, Task<int?>> lookup)
    {
        _lookup = lookup;
    }

    public async Task<int?> GetLimit(string name, CancellationToken ct)
    {
        if (_cache != null && _cache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        ConcurrencyNames.Validate(name);
        var resolved = await _lookup(name, ct);

        _cache ??= new Dictionary<string, int?>(StringComparer.Ordinal);
        _cache[name] = resolved;

        return resolved;
    }

    private static Task<int?> LookupAsync(DeferredWarpContext warp, string name, CancellationToken ct)
    {
        return warp.Context.Set<ConcurrencyLimit>()
            .AsNoTracking()
            .Where(x => x.Name == name)
            .Select(x => (int?)x.Limit)
            .FirstOrDefaultAsync(ct);
    }
}
