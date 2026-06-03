using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Warp.Core.BackgroundServices;

public static class BackgroundServiceConfiguration
{
    /// <summary>
    /// Registers a <see cref="WarpBackgroundService"/> subclass with the Warp host.
    /// Call once per service type inside the <c>AddWarpWorker</c> lambda:
    /// <code>
    /// services.AddWarpWorker&lt;AppDbContext&gt;(opt =>
    /// {
    ///     opt.UsePostgreSql();
    ///     opt.AddBackgroundService&lt;KafkaDrainService&gt;();
    /// });
    /// </code>
    /// Calling <c>AddBackgroundService&lt;T&gt;()</c> twice for the same <typeparamref name="T"/> is
    /// a no-op — the second call is detected and skipped.
    /// </summary>
    /// <typeparam name="T">
    /// Concrete <see cref="WarpBackgroundService"/> subclass. Must be a class (not an interface or
    /// abstract type). The compile-time constraint prevents accidental registration of the base class
    /// itself or an unrelated type.
    /// </typeparam>
    public static IWarpBuilder AddBackgroundService<T>(this IWarpBuilder builder)
        where T : WarpBackgroundService
    {
        // Idempotency guard: if T is already registered as a singleton we've been here before.
        if (builder.Services.Any(d => d.ServiceType == typeof(T) && d.Lifetime == ServiceLifetime.Singleton))
        {
            return builder;
        }

        // Register the concrete type as a singleton so it is created once per process.
        builder.Services.TryAddSingleton<T>();

        // Register the alias so the host can discover all WarpBackgroundService instances
        // via GetServices<WarpBackgroundService>() without knowing the concrete types.
        // The idempotency guard above ensures this executes at most once per T, so a
        // plain AddSingleton is safe — no risk of duplicate alias entries.
        builder.Services.AddSingleton<WarpBackgroundService>(sp => sp.GetRequiredService<T>());

        return builder;
    }
}
