using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Warp.Core.Handlers;

namespace Warp.Core.CircuitBreaker;

public static class CircuitBreakerServiceConfiguration
{
    public static IWarpBuilder<TContext> AddCircuitBreaker<TContext>(
        this IWarpBuilder<TContext> builder,
        Action<CircuitBreakerOptions>? configure = null)
        where TContext : DbContext
    {
        builder.Services.AddOptions<CircuitBreakerOptions>();
        if (configure != null)
        {
            builder.Services.Configure(configure);
        }

        // CircuitBreakerState entity is registered unconditionally by WarpModelCustomizer
        // regardless of whether this opt-in was called — the schema is always present so
        // multi-host migrations don't have to mirror addon opt-ins across hosts. Behaviors
        // and services below remain opt-in.
        builder.Services.AddScoped<ICircuitBreakerStore, CircuitBreakerStore<TContext>>();
        builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(CircuitBreakerPipelineBehavior<,>));

        return builder;
    }
}
