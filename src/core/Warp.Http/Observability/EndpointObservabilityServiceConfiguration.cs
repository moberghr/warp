using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Warp.Core;
using Warp.Core.Endpoints;

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

        // Singleton bounded-channel recorder (persists across scopes); IEndpointCallRecorder resolves to the
        // same instance and doubles as the addons-flag marker (only this method registers it).
        builder.Services.TryAddSingleton<DbEndpointCallRecorder>();
        builder.Services.TryAddSingleton<IEndpointCallRecorder>(x => x.GetRequiredService<DbEndpointCallRecorder>());

        // The flusher drains the channel onto the user's TContext (§0.5). AddEndpointObservability is
        // non-generic, but the concrete builder is always an IWarpBuilder<TContext>; recover TContext and
        // register the closed generic flusher as a hosted service.
        var contextType = ResolveContextType(builder);
        var flusherType = typeof(EndpointCallFlusher<>).MakeGenericType(contextType);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IHostedService), flusherType));

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
