using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Warp.Core;

namespace Warp.Tests.Helpers;

internal static class WarpTestServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IWarpServerContext"/> as the fixture's <typeparamref name="TContext"/>
    /// wrapped in <see cref="TestServerContext"/>, so manually-constructed server tasks / hosts
    /// resolve a server context backed by the test's context. Pairs with
    /// <c>services.AddScoped&lt;TContext&gt;(...)</c>.
    /// </summary>
    public static IServiceCollection AddTestServerContext<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        services.AddScoped<IWarpServerContext>(x => new TestServerContext(x.GetRequiredService<TContext>()));

        // The admin managers' context (rule 2.14). AddWarp registers it; a harness that composes an addon by hand
        // without AddWarp needs it too.
        services.TryAddScoped<IWarpContext>(x => new WarpContextHandle(x.GetRequiredService<TContext>()));

        return services;
    }
}
