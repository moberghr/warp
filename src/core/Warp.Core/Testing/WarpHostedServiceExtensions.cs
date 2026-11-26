using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Warp.Core.Testing;

/// <summary>Helpers for an application's own test suite.</summary>
public static class WarpHostedServiceExtensions
{
    /// <summary>
    /// Removes every <see cref="IHostedService"/> that belongs to a Warp package: the worker and dispatcher
    /// hosts, server registration and heartbeat, the server tasks, the background-service host, the flushers
    /// and seeders, dashboard push, and the startup checks. The application's own hosted services stay.
    /// <para>
    /// For tests that boot the application (for example through <c>WebApplicationFactory</c>) without
    /// exercising Warp: call it from <c>ConfigureTestServices</c>, after the application's <c>AddWarp</c> /
    /// <c>AddWarpServer</c>, and the host starts with no database or with EF Core in-memory. Everything else
    /// Warp registered stays: <c>IMediator</c> works, and publishing still stages <c>Job</c> rows on the
    /// application's context. Nothing runs them.
    /// </para>
    /// </summary>
    /// <remarks>
    /// A descriptor is Warp's when its implementation type, its instance's type, or, for a factory, the factory's
    /// declared return type or the type that declared the factory is in an assembly marked
    /// <c>[AssemblyMetadata("Warp.Package", "true")]</c>, which every Warp package carries. Assembly names are not
    /// used: an application may itself be called <c>Warp.Something</c>.
    /// </remarks>
    /// <param name="services">The application's service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection RemoveWarpHostedServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(IHostedService) && IsWarpDescriptor(services[i]))
            {
                services.RemoveAt(i);
            }
        }

        return services;
    }

    internal static bool IsWarpDescriptor(ServiceDescriptor descriptor)
    {
        if (descriptor.IsKeyedService)
        {
            return WarpPackageAssemblies.IsWarpPackage(descriptor.KeyedImplementationType)
                || WarpPackageAssemblies.IsWarpPackage(descriptor.KeyedImplementationInstance?.GetType())
                || IsWarpFactory(descriptor.KeyedImplementationFactory);
        }

        return WarpPackageAssemblies.IsWarpPackage(descriptor.ImplementationType)
            || WarpPackageAssemblies.IsWarpPackage(descriptor.ImplementationInstance?.GetType())
            || IsWarpFactory(descriptor.ImplementationFactory);
    }

    // The return type is the concrete type when the registration named one (ServiceDescriptor.Singleton<IHostedService,
    // DashboardBroadcaster<T>>(sp => ...)); the declaring type is the class whose code wrote the lambda.
    private static bool IsWarpFactory(Delegate? factory) =>
        factory is not null
        && (WarpPackageAssemblies.IsWarpPackage(factory.Method.ReturnType)
            || WarpPackageAssemblies.IsWarpPackage(factory.Method.DeclaringType));
}
