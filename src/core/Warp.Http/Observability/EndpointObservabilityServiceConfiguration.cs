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
/// recorder/flusher/middleware and the addons flag (keyed on <see cref="IEndpointObservabilityMarker"/>).
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

        // Presence marker for the dashboard "endpoints" flag, registered regardless of sink (the middleware
        // observes under every sink; under Otel the detail rides the request span, §8.24). Keyed on this
        // rather than IEndpointCallRecorder, which is absent under Otel-only.
        builder.Services.TryAddSingleton<IEndpointObservabilityMarker, EndpointObservabilityMarker>();

        // Database / Both: the DB-backed recorder owns the bounded channel + the flusher drains it onto the
        // user's TContext (§0.5). Under Otel-only NEITHER is registered — no DB rows and no Counter-aggregate
        // writes for this surface: the middleware enriches the ambient request span with the captured detail
        // and the OTel meters carry the aggregates (§8.24). The middleware takes the recorder optionally.
        if (sink is RecordingSink.Database or RecordingSink.Both)
        {
            builder.Services.TryAddSingleton(x => new DbEndpointCallRecorder(
                x.GetRequiredService<IOptions<WarpConfiguration>>().Value.CallLogBufferCapacity));
            builder.Services.TryAddSingleton<IEndpointCallRecorder>(x => x.GetRequiredService<DbEndpointCallRecorder>());

            var flusherType = typeof(EndpointCallFlusher<>).MakeGenericType(contextType);
            builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IHostedService), flusherType));
        }

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
