using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Handlers;
using Warp.Core.Webhooks;

namespace Warp.Tests.Webhooks;

/// <summary>
/// Regression: <c>AddWarp</c> registers the webhook executor unconditionally (§8.20), and it resolves
/// <see cref="IHttpClientFactory"/> (the <c>warp-webhooks</c> named client). Core must register the factory
/// itself — otherwise a plain <c>AddWarp</c> process (no <c>AddAdapters</c>) has an unresolvable handler that
/// fails <c>ValidateOnBuild</c>, which is exactly what broke <c>dotnet ef</c> and ASP.NET Core startup in
/// Development on 3.5.0 (both build the provider with validation enabled).
/// </summary>
[Trait("Category", "NoDb")]
public class WebhookHttpClientRegistrationTests
{
    [Fact]
    public void AddWarp_RegistersHttpClientFactory_SoWebhookExecutorConstructs()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TestContext>(o => o.UseInMemoryDatabase($"wh-{Guid.NewGuid():N}"));
        services.AddWarp<TestContext>(_ => { });

        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        sp.GetService<IHttpClientFactory>().ShouldNotBeNull();

        // Resolving the handler exercises every constructor dependency — the same construction ValidateOnBuild
        // performs, and which threw (missing IHttpClientFactory) before the fix.
        using var scope = sp.CreateScope();
        Should.NotThrow(() => scope.ServiceProvider.GetRequiredService<IJobHandler<ExecuteWebhookDelivery>>());
    }
}
