using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Warp.Core.Metrics;

/// <summary>
/// Opt-in registration for the <c>System.Diagnostics.Metrics</c> collector, called inside the <c>AddWarp</c> /
/// <c>AddWarpServer</c> lambda:
/// <code>
/// opt.AddMetrics(m =&gt; m.CollectMeter("Shop.Payments").CollectAspNetCoreHttp().CollectHttpClient());
/// </code>
/// Host code emits through the standard .NET API (<c>IMeterFactory</c>, <c>Counter&lt;T&gt;</c>, …) and takes no
/// Warp dependency. Without this call nothing is registered and no listener starts. Calling it more than once
/// merges the configurations.
/// </summary>
public static class MetricsServiceConfiguration
{
    public static IWarpBuilder AddMetrics(this IWarpBuilder builder, Action<WarpMetricsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        // Fail fast on a bad configuration at registration, not at first resolve.
        var probe = new WarpMetricsOptions();
        configure(probe);
        probe.Validate();

        // The flusher's only delay: zero would spin it, a negative value would throw on every iteration.
        if (builder.Configuration.MetricFlushInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"MetricFlushInterval must be greater than zero (was {builder.Configuration.MetricFlushInterval}). "
                + "It is the interval at which aggregated custom metrics are written out as metric_point rows.");
        }

        builder.Services.Configure(configure);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<IMetricsMarker, MetricsMarker>();

        builder.Services.TryAddSingleton(x => new MetricCardinalityGuard(
            ResolveOptions(x),
            x.GetRequiredService<TimeProvider>(),
            x.GetRequiredService<ILogger<MetricCardinalityGuard>>()));

        builder.Services.TryAddSingleton(x => new MetricAggregator(
            x.GetRequiredService<TimeProvider>(),
            x.GetRequiredService<IOptions<WarpConfiguration>>().Value.FineResolutionMinutes,
            ResolveOptions(x).MaxPendingBuckets));

        builder.Services.TryAddSingleton(x =>
        {
            var options = ResolveOptions(x);
            var configuration = x.GetRequiredService<IOptions<WarpConfiguration>>().Value;

            return new WarpMeterCollector(
                options,
                x.GetRequiredService<MetricAggregator>(),
                x.GetRequiredService<MetricCardinalityGuard>(),
                configuration.ApplicationName,
                options.CollectByVersion ? configuration.ApplicationVersion : null);
        });

        // Registered after the collector host: hosted services stop in reverse order, so the flusher's final flush
        // runs while the listener still feeds the aggregator.
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, MetricCollectorHost>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, MetricFlusher>());

        // The AddWarp-only sink. A Warp server registers its own sink with a Replace (AddServerHostCore), which
        // wins whichever of the two calls ran first, and this TryAdd leaves it alone when it ran first.
        var sinkType = typeof(ContextMetricPointSink<>).MakeGenericType(ResolveContextType(builder));
        builder.Services.TryAddScoped(typeof(IMetricPointSink), sinkType);

        return builder;
    }

    // The registration-time probe validates each AddMetrics lambda on its own; repeated calls merge into one options
    // object, and a host can also Configure<WarpMetricsOptions> directly. Re-validating the MERGED object where it is
    // consumed keeps a bad combination from reaching the collector. Validation is idempotent and cheap.
    private static WarpMetricsOptions ResolveOptions(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<WarpMetricsOptions>>().Value;
        options.Validate();

        return options;
    }

    private static Type ResolveContextType(IWarpBuilder builder)
    {
        var contextType = builder.GetType()
            .GetInterfaces()
            .Where(x => x.IsGenericType)
            .Where(x => x.GetGenericTypeDefinition() == typeof(IWarpBuilder<>))
            .Select(x => x.GetGenericArguments()[0])
            .FirstOrDefault();

        return contextType ?? throw new InvalidOperationException(
            "AddMetrics() could not determine the DbContext type from the Warp builder. Call it inside the "
            + "AddWarp<TContext>() / AddWarpServer<TContext>() configuration lambda so the metric flusher can "
            + "write on your context.");
    }
}
