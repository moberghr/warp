using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Warp.Core.Handlers;

namespace Warp.Core.Timeout;

public static class TimeoutServiceConfiguration
{
    public static IWarpBuilder<TContext> AddTimeout<TContext>(
        this IWarpBuilder<TContext> builder,
        Action<TimeoutOptions>? configure = null)
        where TContext : DbContext
    {
        if (configure != null)
        {
            builder.Services.Configure(configure);
        }
        else
        {
            builder.Services.AddOptions<TimeoutOptions>();
        }

        // Total-scoped budgets need a publish-time deadline (§8.8); everything else resolves at execution.
        builder.Services.AddTransient(typeof(IPublishPipelineBehavior<>), typeof(TimeoutPublishBehavior<>));
        builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(TimeoutPipelineBehavior<,>));

        return builder;
    }
}
