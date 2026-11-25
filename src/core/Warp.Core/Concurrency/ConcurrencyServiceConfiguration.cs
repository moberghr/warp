using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Warp.Core.Data;
using Warp.Core.Handlers;
using Warp.Core.Helper;

namespace Warp.Core.Concurrency;

public static class ConcurrencyServiceConfiguration
{
    public static IWarpBuilder<TContext> AddConcurrency<TContext>(this IWarpBuilder<TContext> builder)
        where TContext : DbContext
    {
        // ConcurrencyLimit entity is registered unconditionally by WarpModelCustomizer.
        // This opt-in only wires the runtime behavior + admin manager service.
        builder.Services.AddScoped<IConcurrencyLimitManager, ConcurrencyLimitManager<TContext>>();

        // The pipeline's resolver uses Warp's own context, opened only for a job with a concurrency key
        // (rule 6.17); the admin manager above stays on TContext for AddWarp-only hosts (#344).
        // A host that registered its own IConcurrencyLimitManager decides the limit through it, as in 7.x
        // (#349), resolved only when a job with a concurrency key asks.
        builder.Services.TryAddScoped<DeferredWarpContext>();
        var manager = new ReplacedRegistration(
            builder.Services,
            typeof(IConcurrencyLimitManager),
            typeof(ConcurrencyLimitManager<TContext>));
        builder.Services.AddScoped(x => manager.IsReplaced
            ? new ConcurrencyLimitResolver(async (name, ct) =>
                (await x.GetRequiredService<IConcurrencyLimitManager>().GetLimit(name, ct))?.Limit)
            : new ConcurrencyLimitResolver(x.GetRequiredService<DeferredWarpContext>()));

        // Process-wide: the slots this process holds, so a key it alone fills is refused without a round trip.
        builder.Services.TryAddSingleton<ConcurrencyLocalHolds>();

        // Constraint-split shims: only job and message pipelines compose concurrency.
        builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ConcurrencyJobPipelineBehavior<,>));
        builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ConcurrencyMessagePipelineBehavior<,>));

        return builder;
    }
}
