using Microsoft.EntityFrameworkCore;

namespace Warp.Core.RateLimit;

// The registered IRateLimitManager: forwards to RateLimitManager and, once a limit change has committed,
// forgets the key's cached rejection so this process applies the new limit to its very next job (other
// processes pick it up within RateLimitGate.MaxClosedAge). A decorator rather than a constructor parameter
// keeps RateLimitManager's public constructor unchanged and every dependency constructor-injected.
internal sealed class GateInvalidatingRateLimitManager<TContext> : IRateLimitManager
    where TContext : DbContext
{
    private readonly RateLimitManager<TContext> _inner;
    private readonly RateLimitGate _gate;

    public GateInvalidatingRateLimitManager(RateLimitManager<TContext> inner, RateLimitGate gate)
    {
        _inner = inner;
        _gate = gate;
    }

    public async Task AddOrUpdateLimit(string name, int count, int windowSeconds, CancellationToken ct = default)
    {
        await _inner.AddOrUpdateLimit(name, count, windowSeconds, ct);
        _gate.Invalidate(name);
    }

    public async Task<bool> RemoveLimit(string name, CancellationToken ct = default)
    {
        var removed = await _inner.RemoveLimit(name, ct);
        if (removed)
        {
            _gate.Invalidate(name);
        }

        return removed;
    }

    public Task<RateLimitInfo?> GetLimit(string name, CancellationToken ct = default) => _inner.GetLimit(name, ct);

    public Task<IReadOnlyList<RateLimitInfo>> ListLimits(CancellationToken ct = default) => _inner.ListLimits(ct);
}
