using Warp.Core.Adapters;
using Warp.Core.Enums;

namespace Warp.Adapters.Http;

/// <summary>
/// Innermost <see cref="DelegatingHandler"/> for a shared-rate-limited adapter: acquires one token from
/// the cluster-shared <see cref="IAdapterRateLimiter"/> before <b>each physical attempt</b> (the vendor
/// counts attempts, not logical calls, so it sits inside the resilience handler). On overflow it throws
/// <see cref="AdapterRateLimitedException"/>, which the outermost <c>WarpAdapterHandler</c> maps onto the
/// scope as a <c>Throttled</c> outcome (telemetry + counters + call-log row).
/// </summary>
internal sealed class WarpAdapterRateLimitHandler : DelegatingHandler
{
    private readonly string _adapter;
    private readonly int _limit;
    private readonly int _perSeconds;
    private readonly AdapterRateLimitOverflow _overflow;
    private readonly TimeSpan _maxWait;
    private readonly IAdapterRateLimiter _rateLimiter;

    public WarpAdapterRateLimitHandler(
        string adapter,
        int limit,
        int perSeconds,
        AdapterRateLimitOverflow overflow,
        TimeSpan maxWait,
        IAdapterRateLimiter rateLimiter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapter);
        ArgumentNullException.ThrowIfNull(rateLimiter);

        _adapter = adapter;
        _limit = limit;
        _perSeconds = perSeconds;
        _overflow = overflow;
        _maxWait = maxWait;
        _rateLimiter = rateLimiter;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _rateLimiter.AcquireAsync(_adapter, _limit, _perSeconds, _overflow, _maxWait, cancellationToken);

        return await base.SendAsync(request, cancellationToken);
    }
}
