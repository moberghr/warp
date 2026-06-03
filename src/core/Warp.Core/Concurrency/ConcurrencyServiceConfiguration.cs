using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
        builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ConcurrencyPipelineBehavior<,>));
        builder.Services.AddTransient(typeof(IPublishPipelineBehavior<>), typeof(ConcurrencyPublishBehavior<>));

        return builder;
    }
}
