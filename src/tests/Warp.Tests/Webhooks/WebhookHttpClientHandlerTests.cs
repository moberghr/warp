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
using Warp.Worker;

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
    public void Default_PrimaryHandlerIsAGuardedSocketsHandlerWithRedirectsOff()
    {
        // PublicAddressesOnly is on by default since 8.1, without any AddWebhooks call.
        using var sp = BuildProvider(configureWebhooks: null);

        var primary = PrimaryHandler(sp).ShouldBeOfType<SocketsHttpHandler>();

        primary.AllowAutoRedirect.ShouldBeFalse();
        primary.ConnectCallback.ShouldNotBeNull();
    }

    [TimedFact]
    public void OptedOut_PrimaryHandlerIsUnguarded()
    {
        using var sp = BuildProvider(w => w.PublicAddressesOnly = false);

        var primary = PrimaryHandler(sp).ShouldBeOfType<SocketsHttpHandler>();

        primary.AllowAutoRedirect.ShouldBeFalse();
        primary.ConnectCallback.ShouldBeNull();
    }

    [TimedFact]
    public void OptedOut_ALaterAddWebhooksWithoutTheSetting_DoesNotTurnTheGuardBackOn()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TestContext>(o => o.UseInMemoryDatabase($"wh-{Guid.NewGuid():N}"));
        services.AddWarp<TestContext>(x =>
        {
            x.AddWebhooks(w => w.PublicAddressesOnly = false);
            x.AddWebhooks(w => w.TrackEndpointHealth = true);
        });
        using var sp = services.BuildServiceProvider();

        PrimaryHandler(sp).ShouldBeOfType<SocketsHttpHandler>().ConnectCallback.ShouldBeNull();
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
        // Warp's default is the FIRST builder action, so a host handler wins in either registration order (with the
        // guard off; with it on, a non-Sockets handler is replaced, covered below).
        using var sp = BuildProvider(
            w => w.PublicAddressesOnly = false,
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
    public async Task PublicAddressesOnly_ReplacedHostHandler_IsReportedOnceTheHostHasStarted()
    {
        var logs = new CapturingLoggerProvider();
        await using var sp = BuildProvider(
            w => w.PublicAddressesOnly = true,
            after: services => services.AddHttpClient(WebhookConstants.AdapterName)
                .ConfigurePrimaryHttpMessageHandler(() => new StubWebhookHandler(HttpStatusCode.OK)),
            logs: logs);

        var startupCheck = ActivatorUtilities.CreateInstance<WebhookClientStartupCheck>(sp);

        await startupCheck.StartedAsync(Xunit.TestContext.Current.CancellationToken);

        logs.Logs.Where(x => x.Level == LogLevel.Warning).ShouldHaveSingleItem().Message.ShouldContain("PublicAddressesOnly replaced");

        // Later clients reuse the handler; the warning is not repeated.
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(WebhookConstants.AdapterName).Dispose();
        logs.Logs.Count(x => x.Level == LogLevel.Warning).ShouldBe(1);
    }

    [TimedFact]
    public async Task StartupCheck_HandlerThatCannotBeBuilt_LogsAndNeverStopsTheHost()
    {
        var logs = new CapturingLoggerProvider();
        await using var sp = BuildProvider(
            w => w.PublicAddressesOnly = true,
            after: services => services.AddHttpClient(WebhookConstants.AdapterName)
                .ConfigurePrimaryHttpMessageHandler(() => throw new InvalidOperationException("not ready")),
            logs: logs);

        var startupCheck = ActivatorUtilities.CreateInstance<WebhookClientStartupCheck>(sp);

        await Should.NotThrowAsync(() => startupCheck.StartedAsync(Xunit.TestContext.Current.CancellationToken));
        logs.Logs.ShouldContain(x => x.Level == LogLevel.Warning && x.Message.Contains("could not be built", StringComparison.Ordinal));
    }

    [TimedFact]
    public void AddWarpServer_RegistersTheStartupCheckOnlyWithAWorker()
    {
        var withWorker = WithContext();
        withWorker.AddWarpServer<TestContext>(_ => { });
        var serviceOnly = WithContext();
        serviceOnly.AddWarpServer<TestContext>(x => x.DisableWorker());
        var publisherOnly = WithContext();
        publisherOnly.AddWarp<TestContext>();

        static ServiceCollection WithContext()
        {
            var services = new ServiceCollection();
            services.AddDbContext<TestContext>(o => o.UseInMemoryDatabase($"wh-{Guid.NewGuid():N}"));

            return services;
        }

        withWorker.Count(x => x.ImplementationType == typeof(WebhookClientStartupCheck)).ShouldBe(1);
        serviceOnly.ShouldNotContain(x => x.ImplementationType == typeof(WebhookClientStartupCheck));
        publisherOnly.ShouldNotContain(x => x.ImplementationType == typeof(WebhookClientStartupCheck));
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
    public void AddWarpTwice_RegistersTheGuardOnce()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(o => o.UseInMemoryDatabase($"wh-{Guid.NewGuid():N}"));
        services.AddWarp<TestContext>();
        services.AddWarp<TestContext>();

        services.Count(x => x.ServiceType == typeof(WebhookHandlerReplacementWarning)).ShouldBe(1);
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
