using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Warp.Demo.ServiceDefaults;

/// <summary>
/// Aspire service defaults shared by the demo's .NET services (the partner API and the Warp dashboard
/// app). Wires OpenTelemetry (OTLP export to the Aspire dashboard), service discovery, and standard
/// HTTP resilience. The tracing/metrics config adds the <c>Warp</c> source and meter, so adapter
/// <c>Client</c> spans and the <c>warp.adapter.*</c> / <c>warp.webhooks.*</c> meters show up in the
/// Aspire dashboard's trace and metric views alongside the Warp dashboard's own surfaces.
/// </summary>
public static class Extensions
{
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        // NOTE: deliberately NOT calling http.AddStandardResilienceHandler() here. The Aspire template
        // adds it by default, but a *global* Polly handler wraps EVERY HttpClient — including Warp's
        // adapter clients and the warp-webhooks delivery client — and that conflicts with Warp's model:
        //   • Webhooks: the delivery layer owns retries (RetrySchedule + scheduled jobs). An external
        //     Polly retry double-retries each scheduled attempt and each retry lands its own adapter
        //     call-log row, so a 3-attempt schedule shows up as ~12 "attempts" in the dashboard.
        //   • Adapters: the payment/shipping adapters already opt into resilience per-adapter via
        //     a.UseResilience(); a global handler stacks a second, redundant resilience layer on top.
        // Warp adapters configure resilience per-adapter; the webhook adapter intentionally has none.
        builder.Services.ConfigureHttpClientDefaults(http => http.AddServiceDiscovery());

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter("Warp"))
            .WithTracing(tracing =>
                tracing
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddSource("Warp"));

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Health endpoints are exposed only outside Production so the demo never leaks internals.
        if (app.Environment.IsDevelopment())
        {
            app.MapHealthChecks("/health");
            app.MapHealthChecks("/alive", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live"),
            });
        }

        return app;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }
}
