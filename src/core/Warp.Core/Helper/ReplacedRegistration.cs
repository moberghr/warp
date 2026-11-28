using Microsoft.Extensions.DependencyInjection;

namespace Warp.Core.Helper;

// Tells whether the host replaced a service Warp registered, by reading the service collection's last
// registration for it, which is what the container resolves. Read once, on first use: by then the host
// has finished registering, in whichever order its calls ran. Asking the container instead would build
// the service, and Warp's own admin managers build a context (rule 6.17).
internal sealed class ReplacedRegistration
{
    private readonly Lazy<bool> _replaced;

    public ReplacedRegistration(IServiceCollection services, Type serviceType, Type warpImplementation)
    {
        _replaced = new Lazy<bool>(() =>
        {
            // Unkeyed only: a keyed registration is not what an unkeyed resolve returns, and reading
            // ImplementationType on one throws.
            var last = services.LastOrDefault(x => !x.IsKeyedService && x.ServiceType == serviceType);

            return last != null && last.ImplementationType != warpImplementation;
        });
    }

    // Warp's own registration is a factory (it builds the service on the Warp context, #344): recognised by the
    // factory instance, which no host registration can share.
    public ReplacedRegistration(IServiceCollection services, Type serviceType, Func<IServiceProvider, object> warpFactory)
    {
        _replaced = new Lazy<bool>(() =>
        {
            var last = services.LastOrDefault(x => !x.IsKeyedService && x.ServiceType == serviceType);

            return last != null && !ReferenceEquals(last.ImplementationFactory, warpFactory);
        });
    }

    public bool IsReplaced => _replaced.Value;
}
