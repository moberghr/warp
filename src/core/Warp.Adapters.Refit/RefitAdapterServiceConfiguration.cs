using Microsoft.Extensions.DependencyInjection;
using Refit;
using Warp.Adapters.Http;
using Warp.Core;

namespace Warp.Adapters.Refit;

/// <summary>
/// Registers Refit interfaces as observed Warp adapters. <c>AddAdapter&lt;TApi&gt;("vendor", a =&gt; ...)</c>
/// wires a named, Refit-backed <see cref="HttpClient"/> onto the standard <c>Warp.Adapters.Http</c>
/// pipeline (<c>WarpAdapterHandler</c> + optional shared rate limit) and names each outbound
/// call after the interface method — existing Refit interfaces, DTOs, auth handlers, and
/// <see cref="RefitSettings"/> (e.g. XML-over-REST serializers) all pass through unchanged.
/// </summary>
public static class RefitAdapterServiceConfiguration
{
    /// <summary>
    /// Registers the Refit interface <typeparamref name="TApi"/> as an adapter named
    /// <paramref name="name"/> (the adapter's cluster-wide identity). The typed client binds to the
    /// named Warp client, so calls flow through the observing handler and record one call row each,
    /// with the operation set to the interface method name. Configure capture tiers and the shared
    /// rate limit via <paramref name="configure"/>; supply optional Refit behaviour (custom
    /// serializer, auth header getter, exception factory) via <paramref name="refitSettings"/>.
    /// </summary>
    public static IWarpBuilder AddAdapter<TApi>(
        this IWarpBuilder builder,
        string name,
        Action<WarpAdapterHttpOptions> configure,
        RefitSettings? refitSettings = null)
        where TApi : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        // Bind the Refit typed client to a Warp client named `name`, then attach the operation-name
        // reader as an additional handler. Registering it here — before the HTTP AddAdapter wires
        // WarpAdapterHandler — makes the reader the outer handler, so the ambient operation scope it
        // pushes from Refit's RestMethodInfo is already active when the Warp handler resolves the name.
        builder.Services
            .AddRefitClient<TApi>(refitSettings, name)
            .AddHttpMessageHandler(() => new RefitOperationNameReader());

        // Wire the standard observing pipeline (WarpAdapterHandler + shared rate limit) and
        // ensure the Core recording services are present, all on the same named client.
        builder.AddAdapter(name, configure);

        return builder;
    }
}
