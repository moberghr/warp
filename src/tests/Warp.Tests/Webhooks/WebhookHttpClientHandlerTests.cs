using System.Net;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Warp.Core;
using Warp.Core.Webhooks;
using Warp.Tests.Observability;

namespace Warp.Tests.Webhooks;

/// <summary>
/// The primary handler of the <c>warp-webhooks</c> client (8.1): Warp's default is a
/// <see cref="SocketsHttpHandler"/> with redirects off; a handler the host configures keeps its own
/// redirect choice; <c>PublicAddressesOnly</c> attaches the guard to a SocketsHttpHandler and replaces any
/// other handler with Warp's default, logging one Warning.
/// </summary>
[Trait("Category", "NoDb")]
public class WebhookHttpClientHandlerTests
{
    [TimedFact]
    public void Default_PrimaryHandlerIsSocketsHandlerWithRedirectsOff()
    {
        using var sp = BuildProvider(configureWebhooks: null);

        var primary = PrimaryHandler(sp).ShouldBeOfType<SocketsHttpHandler>();

        primary.AllowAutoRedirect.ShouldBeFalse();
        primary.ConnectCallback.ShouldBeNull();
    }

    [TimedFact]
    public void HostUseSocketsHttpHandler_WithoutRedirectSetting_KeepsRedirectsOff()
    {
        // UseSocketsHttpHandler reuses Warp's handler instance, so unrelated host settings compose with it.
        using var sp = BuildProvider(
            configureWebhooks: null,
            after: services => services.AddHttpClient(WebhookConstants.AdapterName)
                .UseSocketsHttpHandler((handler, _) => handler.MaxConnectionsPerServer = 7));

        var primary = PrimaryHandler(sp).ShouldBeOfType<SocketsHttpHandler>();

        primary.AllowAutoRedirect.ShouldBeFalse();
        primary.MaxConnectionsPerServer.ShouldBe(7);
    }

    [TimedFact]
    public void HostSocketsHandlerThatAllowsRedirects_ChoiceIsKept()
    {
        using var sp = BuildProvider(
            configureWebhooks: null,
            after: services => services.AddHttpClient(WebhookConstants.AdapterName)
                .UseSocketsHttpHandler((handler, _) => handler.AllowAutoRedirect = true));

        PrimaryHandler(sp).ShouldBeOfType<SocketsHttpHandler>().AllowAutoRedirect.ShouldBeTrue();
    }

    [TimedFact]
    public void HostPrimaryHandlerRegisteredBeforeAddWarp_IsUsedUntouched()
    {
        // Warp's default is the FIRST builder action, so a host handler wins in either registration order.
        using var sp = BuildProvider(
            configureWebhooks: null,
            before: services => services.AddHttpClient(WebhookConstants.AdapterName)
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = true }));

        PrimaryHandler(sp).ShouldBeOfType<HttpClientHandler>().AllowAutoRedirect.ShouldBeTrue();
    }

    [TimedFact]
    public void PublicAddressesOnly_HostSocketsHandler_GetsTheGuardAndKeepsItsSettings()
    {
        var logs = new CapturingLoggerProvider();
        using var sp = BuildProvider(
            w => w.PublicAddressesOnly = true,
            after: services => services.AddHttpClient(WebhookConstants.AdapterName)
                .UseSocketsHttpHandler((handler, _) => handler.AllowAutoRedirect = true),
            logs: logs);

        var primary = PrimaryHandler(sp).ShouldBeOfType<SocketsHttpHandler>();

        primary.ConnectCallback.ShouldNotBeNull();
        primary.AllowAutoRedirect.ShouldBeTrue();
        logs.Logs.ShouldNotContain(x => x.Level == LogLevel.Warning);
    }

    [TimedTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void PublicAddressesOnly_HostNonSocketsHandler_ReplacedWithGuardedDefaultAndWarnedOnce(bool registeredBeforeAddWarp)
    {
        var logs = new CapturingLoggerProvider();
        void HostHandler(IServiceCollection services) => services.AddHttpClient(WebhookConstants.AdapterName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = true });

        using var sp = BuildProvider(
            w => w.PublicAddressesOnly = true,
            before: registeredBeforeAddWarp ? HostHandler : null,
            after: registeredBeforeAddWarp ? null : HostHandler,
            logs: logs);

        var primary = PrimaryHandler(sp).ShouldBeOfType<SocketsHttpHandler>();

        primary.ConnectCallback.ShouldNotBeNull();
        primary.AllowAutoRedirect.ShouldBeFalse();

        var warning = logs.Logs.Where(x => x.Level == LogLevel.Warning).ShouldHaveSingleItem();
        warning.Message.ShouldContain("\"warp-webhooks\"");
        warning.Message.ShouldContain(typeof(HttpClientHandler).FullName!);
        warning.Message.ShouldContain("proxy, client certificates");
    }

    [TimedFact]
    public async Task PublicAddressesOnly_ReplacedHostHandler_IsReportedAtHostStartup()
    {
        var logs = new CapturingLoggerProvider();
        await using var sp = BuildProvider(
            w => w.PublicAddressesOnly = true,
            after: services => services.AddHttpClient(WebhookConstants.AdapterName)
                .ConfigurePrimaryHttpMessageHandler(() => new StubWebhookHandler(HttpStatusCode.OK)),
            logs: logs);

        var startupCheck = sp.GetServices<IHostedService>().OfType<WebhookClientStartupCheck>().ShouldHaveSingleItem();

        await startupCheck.StartAsync(Xunit.TestContext.Current.CancellationToken);

        logs.Logs.Where(x => x.Level == LogLevel.Warning).ShouldHaveSingleItem().Message.ShouldContain("PublicAddressesOnly replaced");

        // Later clients reuse the handler; the warning is not repeated.
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(WebhookConstants.AdapterName).Dispose();
        logs.Logs.Count(x => x.Level == LogLevel.Warning).ShouldBe(1);
    }

    [TimedFact]
    public void ReplacementWarning_ReportedTwice_LogsOnce()
    {
        // The handler is rebuilt on every factory rotation (2 min by default); the warning must not repeat.
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(x => x.AddProvider(logs));
        var warning = new WebhookHandlerReplacementWarning(factory.CreateLogger<WebhookHandlerReplacementWarning>());

        warning.Report("Some.Handler");
        warning.Report("Some.Handler");

        logs.Logs.Count(x => x.Level == LogLevel.Warning).ShouldBe(1);
    }

    [TimedFact]
    public void Default_WithoutPublicAddressesOnly_RegistersNoStartupCheck()
    {
        using var sp = BuildProvider(configureWebhooks: null);

        sp.GetServices<IHostedService>().OfType<WebhookClientStartupCheck>().ShouldBeEmpty();
    }

    private static HttpMessageHandler PrimaryHandler(IServiceProvider sp)
    {
        var handler = sp.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(WebhookConstants.AdapterName);
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        return handler;
    }

    private static ServiceProvider BuildProvider(
        Action<WarpWebhookOptions>? configureWebhooks,
        Action<IServiceCollection>? before = null,
        Action<IServiceCollection>? after = null,
        CapturingLoggerProvider? logs = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(x =>
        {
            if (logs is not null)
            {
                x.AddProvider(logs);
            }
        });
        services.AddDbContext<TestContext>(o => o.UseInMemoryDatabase($"wh-{Guid.NewGuid():N}"));
        before?.Invoke(services);
        services.AddWarp<TestContext>(x => x.AddWebhooks(configureWebhooks));
        after?.Invoke(services);

        return services.BuildServiceProvider();
    }
}
