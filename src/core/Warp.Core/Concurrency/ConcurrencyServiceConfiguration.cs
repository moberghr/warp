using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Warp.Core.Data;
using Warp.Core.Handlers;

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
        builder.Services.TryAddScoped<DeferredWarpContext>();
        builder.Services.AddScoped(x => new ConcurrencyLimitResolver(x.GetRequiredService<DeferredWarpContext>()));

        // Process-wide: the slots this process holds, so a key it alone fills is refused without a round trip.
        builder.Services.TryAddSingleton<ConcurrencyLocalHolds>();

        // Constraint-split shims: only job and message pipelines compose concurrency.
        builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ConcurrencyJobPipelineBehavior<,>));
        builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ConcurrencyMessagePipelineBehavior<,>));

        return builder;
    }
}
