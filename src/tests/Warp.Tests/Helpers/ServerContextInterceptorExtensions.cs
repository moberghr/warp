using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Warp.Core;

namespace Warp.Tests.Helpers;

// Fault injection for the worker's own database work. The worker runs on the internal server context,
// whose options come from IWarpServerContextConfigurator, the seam the providers use. Wrapping that
// configurator lets a test fail one specific statement without the worker carrying a test hook.
internal static class ServerContextInterceptorExtensions
{
    // Call from WarpTestServer's configureServices, which runs after the provider registered its own
    // configurator: that instance is taken from its descriptor and delegated to, so none of the
    // provider's connection wiring is repeated here.
    public static IServiceCollection AddServerContextInterceptor(this IServiceCollection services, IInterceptor interceptor)
    {
        var inner = (IWarpServerContextConfigurator)services
            .Last(x => x.ServiceType == typeof(IWarpServerContextConfigurator))
            .ImplementationInstance!;

        services.AddSingleton<IWarpServerContextConfigurator>(new InterceptingConfigurator(inner, interceptor));

        return services;
    }

    private sealed class InterceptingConfigurator : IWarpServerContextConfigurator
    {
        private readonly IWarpServerContextConfigurator _inner;
        private readonly IInterceptor _interceptor;

        public InterceptingConfigurator(IWarpServerContextConfigurator inner, IInterceptor interceptor)
        {
            _inner = inner;
            _interceptor = interceptor;
        }

        public void Configure(DbContextOptionsBuilder optionsBuilder, IServiceProvider applicationServices)
        {
            _inner.Configure(optionsBuilder, applicationServices);
            optionsBuilder.AddInterceptors(_interceptor);
        }
    }
}
