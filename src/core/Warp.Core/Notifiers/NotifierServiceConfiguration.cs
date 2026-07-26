using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Warp.Core.Notifiers;

/// <summary>
/// Registration for host operational-event notifiers. Targets the non-generic <see cref="IWarpBuilder"/>
/// receiver (§2.13, the <c>AddBackgroundService&lt;T&gt;</c> precedent) — the seam needs no <c>TContext</c>.
/// The <see cref="WarpNotifierDispatcher"/> itself is registered by <c>AddWarp</c> so every dispatch site
/// resolves it regardless of whether any notifier is registered; this method only contributes the host's
/// implementation to the notifier set.
/// </summary>
public static class NotifierServiceConfiguration
{
    /// <summary>
    /// Registers a host <see cref="IWarpNotifier"/> implementation. Call once per implementation inside the
    /// <c>AddWarp</c> / <c>AddWarpServer</c> lambda:
    /// <code>
    /// services.AddWarp&lt;AppDbContext&gt;(opt =>
    /// {
    ///     opt.UsePostgreSql();
    ///     opt.AddNotifier&lt;TeamsNotifier&gt;();
    /// });
    /// </code>
    /// Several notifiers can be registered; all receive every event. Registering the same type twice is a
    /// no-op.
    /// </summary>
    public static IWarpBuilder AddNotifier<T>(this IWarpBuilder builder)
        where T : class, IWarpNotifier
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IWarpNotifier, T>());

        return builder;
    }
}
