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
        // The admin manager runs on the Warp context (rule 2.14, #344), in every AddWarp process.
        object WarpManager(IServiceProvider x) => new ConcurrencyLimitManager<TContext>(
            x.GetRequiredService<IWarpContext>().Context,
            x.GetRequiredService<TimeProvider>());
        builder.Services.AddScoped(typeof(IConcurrencyLimitManager), WarpManager);

        // The pipeline's resolver uses the server context, opened only for a job with a concurrency key (rule 6.17).
        // A host that registered its own IConcurrencyLimitManager decides the limit through it, as in 7.x
        // (#349), resolved only when a job with a concurrency key asks.
        builder.Services.TryAddScoped<DeferredWarpContext>();
        var manager = new ReplacedRegistration(
            builder.Services,
            typeof(IConcurrencyLimitManager),
            WarpManager);
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
