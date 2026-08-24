using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Warp.Core.Handlers;

namespace Warp.Core.Retry;

public static class RetryServiceConfiguration
{
    public static IWarpBuilder<TContext> AddRetry<TContext>(
        this IWarpBuilder<TContext> builder,
        Action<RetryOptions>? configure = null)
        where TContext : DbContext
    {
        if (configure != null)
        {
            builder.Services.Configure(configure);
        }
        else
        {
            builder.Services.AddOptions<RetryOptions>();
        }

        builder.Services.AddTransient(typeof(IPublishPipelineBehavior<>), typeof(RetryPublishBehavior<>));

        // Constraint-split shims: only job and message pipelines compose retry. In-memory sends and
        // stream requests never instantiate the behaviour (see RetryJobPipelineBehavior).
        builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(RetryJobPipelineBehavior<,>));
        builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(RetryMessagePipelineBehavior<,>));

        return builder;
    }
}
