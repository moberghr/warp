using Microsoft.EntityFrameworkCore;
using Warp.Core.Data;
using Warp.Core.Data.Entities;

namespace Warp.Core.RateLimit;

/// <summary>
/// Resolves the runtime-effective rate-limit settings for a key by checking the admin override row, on
/// Warp's own context opened only when a job with a rate limit asks (rule 6.17). Scoped — caches lookups
/// for the lifetime of one job execution scope. Cross-scope staleness is intentional: admin updates take
/// effect at the next pickup. Mirrors <see cref="Warp.Core.Concurrency.ConcurrencyLimitResolver"/>.
/// </summary>
public sealed class RateLimitResolver
{
    private readonly Func<string, CancellationToken, Task<RateLimitInfo?>> _lookup;

    // Allocated on first lookup: most jobs carry no rate limit and never ask.
    private Dictionary<string, RateLimitInfo?>? _cache;

    internal RateLimitResolver(DeferredWarpContext warp)
        : this((name, ct) => LookupAsync(warp, name, ct))
    {
    }

    // The admin row lookup as a function, so the pipeline's unit tests can run without a database.
    internal RateLimitResolver(Func<string, CancellationToken, Task<RateLimitInfo?>> lookup)
    {
        _lookup = lookup;
    }

    public async Task<RateLimitInfo?> GetOverride(string name, CancellationToken ct)
    {
        if (_cache != null && _cache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        RateLimitNames.Validate(name);
        var info = await _lookup(name, ct);

        _cache ??= new Dictionary<string, RateLimitInfo?>(StringComparer.Ordinal);
        _cache[name] = info;

        return info;
    }

    private static Task<RateLimitInfo?> LookupAsync(DeferredWarpContext warp, string name, CancellationToken ct)
    {
        return warp.Context.Set<RateLimitOverride>()
            .AsNoTracking()
            .Where(x => x.Name == name)
            .Select(x =>
                new RateLimitInfo(x.Name, x.Count, x.WindowSeconds, x.UpdatedAt))
            .FirstOrDefaultAsync(ct);
    }
}
