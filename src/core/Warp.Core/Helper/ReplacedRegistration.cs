using Microsoft.Extensions.DependencyInjection;

namespace Warp.Core.Helper;

// Tells whether the host replaced a service Warp registered, by reading the service collection's last
// registration for it, which is what the container resolves. Read once, on first use: by then the host
// has finished registering, in whichever order its calls ran. Asking the container instead would build
// the service, and Warp's own admin managers build the application's DbContext (rule 6.17).
internal sealed class ReplacedRegistration
{
    private readonly Lazy<bool> _replaced;

    public ReplacedRegistration(IServiceCollection services, Type serviceType, Type warpImplementation)
    {
        _replaced = new Lazy<bool>(() =>
        {
            var last = services.LastOrDefault(x => x.ServiceType == serviceType);

            return last != null && last.ImplementationType != warpImplementation;
        });
    }

    public bool IsReplaced => _replaced.Value;
}
