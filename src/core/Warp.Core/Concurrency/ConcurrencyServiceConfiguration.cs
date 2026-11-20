using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        builder.Services.AddScoped<ConcurrencyLimitResolver>();

        // Process-wide: the slots this process holds, so a key it alone fills is refused without a round trip.
        builder.Services.TryAddSingleton<ConcurrencyLocalHolds>();

        // Constraint-split shims: only job and message pipelines compose concurrency.
        builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ConcurrencyJobPipelineBehavior<,>));
        builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ConcurrencyMessagePipelineBehavior<,>));

        return builder;
    }
}
