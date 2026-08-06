using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Refit;
using Warp.Core.Metrics;

namespace Warp.Metrics.Prometheus;

/// <summary>Options for the Prometheus metric-source backend.</summary>
public sealed class PrometheusMetricSourceOptions
{
    /// <summary>Base address of the Prometheus HTTP API (the server exposing <c>/api/v1/query</c>).</summary>
    public string BaseAddress { get; set; } = "http://localhost:9090";

    /// <summary>How far back an open-ended ("all history") series read looks, since a Prometheus range query must be bounded.</summary>
    public TimeSpan DefaultLookback { get; set; } = TimeSpan.FromDays(7);
}

public static class PrometheusMetricSourceExtensions
{
    /// <summary>
    /// Selects Prometheus as the metric-read backend: the dashboard and SLO evaluator read Warp's own metrics back
    /// from Prometheus (via the Refit <see cref="IPrometheusQueryApi"/>) instead of the local Statistic/Counter
    /// fold. Call after <c>AddWarp</c> — it replaces the default <c>LocalMetricSource</c> registration.
    /// </summary>
    public static IServiceCollection AddPrometheusMetricSource(this IServiceCollection services, Action<PrometheusMetricSourceOptions>? configure = null)
    {
        var options = new PrometheusMetricSourceOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddRefitClient<IPrometheusQueryApi>()
            .ConfigureHttpClient(client => client.BaseAddress = new Uri(options.BaseAddress));

        // Replace the local backend AddWarp registered by default (§ selector).
        services.RemoveAll<IMetricSource>();
        services.AddScoped<IMetricSource, PrometheusMetricSource>();

        return services;
    }
}
