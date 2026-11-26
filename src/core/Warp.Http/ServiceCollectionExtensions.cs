using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Warp.Http;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Warp.Http services. Must be called once during application startup,
    /// before <see cref="EndpointRouteBuilderExtensions.MapWarpHttp"/>. Idempotent; every call's
    /// <paramref name="configure"/> applies to the same options.
    /// </summary>
    public static IServiceCollection AddWarpHttp(this IServiceCollection services, Action<WarpHttpOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // One options instance, configured by every call: a second AddWarpHttp(o => ...) used to be dropped by
        // TryAddSingleton, so a host turning ValidateAntiforgeryOnFormEndpoints on after a library had called
        // AddWarpHttp() silently kept form endpoints unprotected.
        var existing = services
            .Where(x => x.ServiceType == typeof(WarpHttpOptions))
            .Select(x => x.ImplementationInstance)
            .OfType<WarpHttpOptions>()
            .LastOrDefault();

        var options = existing ?? new WarpHttpOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);

        return services;
    }
}
