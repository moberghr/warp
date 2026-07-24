using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Endpoints;
using Warp.Core.Observability;

namespace Warp.Http.Observability;

/// <summary>
/// Opt-in registration + pipeline wiring for inbound endpoint observability. <c>AddEndpointObservability</c>
/// (inside the <c>AddWarp</c>/<c>AddWarpServer</c> lambda) registers the recording pipeline + capture
/// options; <c>UseWarpHttpObservability</c> (after <c>UseRouting</c>, before the endpoints run) installs the
/// middleware. The read service <c>IEndpointQueryService</c> is always registered by <c>AddWarp</c>, so the
/// dashboard serves <c>/api/endpoints</c> even in processes that don't record; this method gates the
/// recorder/flusher/middleware and the addons flag (keyed on <see cref="IEndpointCallRecorder"/> presence).
/// </summary>
public static class EndpointObservabilityServiceConfiguration
{
    public static IWarpBuilder AddEndpointObservability(
        this IWarpBuilder builder,
        Action<WarpEndpointObservabilityOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The recorder/channel is a SINGLE per-process singleton, so the sink is a process-level choice:
        // build a throwaway options bag, apply the caller's config, and read Sink to select the recorder.
        var options = new WarpEndpointObservabilityOptions();
        configure?.Invoke(options);
        var sink = options.Sink;

        var contextType = ResolveContextType(builder);

        // Database / Both: the DB-backed recorder owns the bounded channel + the flusher drains it onto the
        // user's TContext (§0.5). Under Otel-only NEITHER is registered — no DB rows and no Counter-aggregate
        // writes for this surface (aggregates come from OTel meters instead).
        if (sink is RecordingSink.Database or RecordingSink.Both)
        {
            builder.Services.TryAddSingleton(x => new DbEndpointCallRecorder(
                x.GetRequiredService<IOptions<WarpConfiguration>>().Value.CallLogBufferCapacity));

            var flusherType = typeof(EndpointCallFlusher<>).MakeGenericType(contextType);
            builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IHostedService), flusherType));
        }

        // Otel / Both: Core ships the OTel recorder (structured OTLP log per call; no DB writes).
        if (sink is RecordingSink.Otel or RecordingSink.Both)
        {
            builder.Services.TryAddSingleton<OtelEndpointCallRecorder>();
        }

        // Bind IEndpointCallRecorder to the selected recorder (or a composite fanning to both). This binding
        // also doubles as the addons-flag marker — registered under every sink so the "endpoints" nav flag
        // reports true even under Otel-only.
        RegisterRecorder(builder.Services, sink);

        if (configure is not null)
        {
            builder.Services.Configure(configure);
        }

        return builder;
    }

    /// <summary>
    /// Installs the inbound observability middleware. Place after <c>UseRouting</c> (so the matched endpoint
    /// and its <see cref="WarpEndpointIdentity"/> are resolved) and before the endpoints execute. No-ops for
    /// any endpoint that is not Warp-mapped. Requires <c>AddEndpointObservability()</c> to have registered
    /// the recorder.
    /// </summary>
    public static IApplicationBuilder UseWarpHttpObservability(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<WarpInboundObservabilityMiddleware>();
    }

    private static void RegisterRecorder(IServiceCollection services, RecordingSink sink)
    {
        switch (sink)
        {
            case RecordingSink.Otel:
                services.TryAddSingleton<IEndpointCallRecorder>(x => x.GetRequiredService<OtelEndpointCallRecorder>());

                break;

            case RecordingSink.Both:
                services.TryAddSingleton<IEndpointCallRecorder>(x => new CompositeEndpointCallRecorder(
                    x.GetRequiredService<DbEndpointCallRecorder>(),
                    x.GetRequiredService<OtelEndpointCallRecorder>()));

                break;

            default:
                services.TryAddSingleton<IEndpointCallRecorder>(x => x.GetRequiredService<DbEndpointCallRecorder>());

                break;
        }
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
            "AddEndpointObservability() could not determine the DbContext type from the Warp builder. Call it "
            + "inside the AddWarp<TContext>() / AddWarpServer<TContext>() configuration lambda so the endpoint "
            + "call-log flusher can resolve your context.");
    }
}
