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

        // Scratch-read the configured default so ValidateAddonAttributesOnHandlers (which runs before any
        // provider exists) can reject a handler [Timeout] under a Total-scoped default. Readers take the
        // LAST registration, so a repeat AddTimeout call simply wins.
        var scratch = new TimeoutOptions();
        configure?.Invoke(scratch);
        builder.Services.AddSingleton(new TimeoutStartupDefaults(scratch.Default != null, scratch.DefaultScope));

        builder.Services.AddTransient(typeof(IPublishPipelineBehavior<>), typeof(TimeoutPublishBehavior<>));
        builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(TimeoutPipelineBehavior<,>));

        return builder;
    }
}
