using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Warp.Core.Handlers;

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
        builder.Services.AddScoped<IRateLimitManager, RateLimitManager<TContext>>();
        builder.Services.AddScoped<IRateLimitStore, RateLimitStore<TContext>>();
        builder.Services.AddScoped<RateLimitResolver>();
        builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(RateLimitPipelineBehavior<,>));
        builder.Services.AddTransient(typeof(IPublishPipelineBehavior<>), typeof(RateLimitPublishBehavior<>));

        return builder;
    }
}
