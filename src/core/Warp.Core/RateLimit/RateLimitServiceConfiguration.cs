using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Warp.Core.Data;
using Warp.Core.Handlers;
using Warp.Core.Helper;

namespace Warp.Core.RateLimit;

public static class RateLimitServiceConfiguration
{
    /// <summary>
    /// Registers the rate-limit addon. Composition order matters when a job carries both
    /// <c>[Mutex]</c>/<c>[Semaphore]</c> and <c>[RateLimit]</c>: register <c>AddConcurrency()</c>
    /// before <c>AddRateLimit()</c> so the mutex check runs outermost. When the mutex is
    /// already held, the rate-limit token is preserved (the bucket is never incremented for a
    /// job that was never going to run). Reversing the order causes mutex rejections to waste
    /// rate-limit tokens until the next window rollover.
    /// </summary>
    public static IWarpBuilder<TContext> AddRateLimit<TContext>(this IWarpBuilder<TContext> builder)
        where TContext : DbContext
    {
        // RateLimitBucket and RateLimitOverride entities are registered unconditionally by
        // WarpModelCustomizer. This opt-in only wires the runtime behavior + admin manager.
        // Process-wide: remembers rejections across jobs and holds this process's leased Wait-mode slots.
        builder.Services.TryAddSingleton<RateLimitGate>();
        builder.Services.AddScoped<RateLimitManager<TContext>>();
        builder.Services.AddScoped<IRateLimitManager, GateInvalidatingRateLimitManager<TContext>>();

        // The pipeline's store and resolver use Warp's own context, opened only for a job with a rate
        // limit (rule 6.17); the admin manager above stays on TContext for AddWarp-only hosts (#344).
        // A host that registered its own IRateLimitManager decides the limit through it, as in 7.x (#349),
        // resolved only when a job with a rate limit asks.
        builder.Services.TryAddScoped<DeferredWarpContext>();
        builder.Services.AddScoped<IRateLimitStore, RateLimitStore>();
        var manager = new ReplacedRegistration(
            builder.Services,
            typeof(IRateLimitManager),
            typeof(GateInvalidatingRateLimitManager<TContext>));
        builder.Services.AddScoped(x => manager.IsReplaced
            ? new RateLimitResolver((name, ct) => x.GetRequiredService<IRateLimitManager>().GetLimit(name, ct))
            : new RateLimitResolver(x.GetRequiredService<DeferredWarpContext>()));

        // Constraint-split shims: only job and message pipelines compose rate limiting.
        builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(RateLimitJobPipelineBehavior<,>));
        builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(RateLimitMessagePipelineBehavior<,>));

        return builder;
    }
}
