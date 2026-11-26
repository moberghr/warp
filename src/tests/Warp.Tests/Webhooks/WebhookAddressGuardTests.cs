using System.Net;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Webhooks;

namespace Warp.Tests.Webhooks;

/// <summary>
/// <see cref="WebhookAddressGuard.IsPublic"/> against the IANA special-purpose registries (RFC 6890 and
/// successors), and the <c>PublicAddressesOnly</c> wiring of the <c>warp-webhooks</c> client. The wiring
/// tests send to loopback, which the guard refuses before any socket is opened, so nothing reaches the
/// network.
/// </summary>
[Trait("Category", "NoDb")]
public class WebhookAddressGuardTests
{
    [TimedTheory]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("169.254.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.0.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("192.88.99.1")]
    [InlineData("192.168.0.1")]
    [InlineData("192.168.255.255")]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.255")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.255")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    public void IsPublic_SpecialPurposeIPv4_ReturnsFalse(string address)
    {
        WebhookAddressGuard.IsPublic(IPAddress.Parse(address)).ShouldBeFalse();
    }

    [TimedTheory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("9.255.255.255")]
    [InlineData("11.0.0.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("126.255.255.255")]
    [InlineData("169.253.255.255")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("192.0.1.1")]
    [InlineData("192.0.3.1")]
    [InlineData("192.167.255.255")]
    [InlineData("192.169.0.1")]
    [InlineData("198.17.255.255")]
    [InlineData("198.20.0.1")]
    [InlineData("203.0.114.1")]
    [InlineData("223.255.255.255")]
    public void IsPublic_PublicIPv4_ReturnsTrue(string address)
    {
        WebhookAddressGuard.IsPublic(IPAddress.Parse(address)).ShouldBeTrue();
    }

    [TimedTheory]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::8.8.8.8")]
    [InlineData("64:ff9b:1::1")]
    [InlineData("100::1")]
    [InlineData("2001::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2001:db8:ffff::1")]
    [InlineData("3fff::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("fe80::1")]
    [InlineData("febf::1")]
    [InlineData("fec0::1")]
    [InlineData("ff02::1")]
    [InlineData("ff0e::1")]
    public void IsPublic_SpecialPurposeIPv6_ReturnsFalse(string address)
    {
        WebhookAddressGuard.IsPublic(IPAddress.Parse(address)).ShouldBeFalse();
    }

    [TimedTheory]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2a00:1450::1")]
    [InlineData("2001:200::1")]
    [InlineData("3ffe::1")]
    public void IsPublic_GlobalUnicastIPv6_ReturnsTrue(string address)
    {
        WebhookAddressGuard.IsPublic(IPAddress.Parse(address)).ShouldBeTrue();
    }

    [TimedTheory]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("::ffff:169.254.169.254", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("64:ff9b::127.0.0.1", false)]
    [InlineData("64:ff9b::192.168.1.1", false)]
    [InlineData("64:ff9b::8.8.8.8", true)]
    [InlineData("2002:7f00:1::1", false)]
    [InlineData("2002:a9fe:a9fe::1", false)]
    [InlineData("2002:0808:0808::1", true)]
    public void IsPublic_EmbeddedIPv4_JudgedByTheEmbeddedAddress(string address, bool expected)
    {
        WebhookAddressGuard.IsPublic(IPAddress.Parse(address)).ShouldBe(expected);
    }

    [TimedTheory]
    [InlineData("http://127.0.0.1:9/hook")]
    [InlineData("http://[::1]:9/hook")]
    [InlineData("http://localhost:9/hook")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    public async Task PublicAddressesOnly_LoopbackOrMetadataDestination_RefusedWithHostOnlyMessage(string url)
    {
        await using var sp = BuildProvider(w => w.PublicAddressesOnly = true);
        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(WebhookConstants.AdapterName);

        var ex = await Should.ThrowAsync<HttpRequestException>(() => client.GetAsync(new Uri(url), Ct));

        var refusal = ex.InnerException.ShouldBeOfType<WebhookAddressNotPublicException>();
        refusal.Message.ShouldContain("does not resolve to a public address");
        refusal.Message.ShouldContain(new Uri(url).IdnHost);
    }

    [TimedFact]
    public async Task PublicAddressesOnly_PrimaryHandlerConfiguredBeforeAddWebhooks_FailsClosed()
    {
        // A host handler set BEFORE AddWebhooks is kept, and the guard fails the request closed rather than sending it
        // through an unguarded handler.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TestContext>(o => o.UseInMemoryDatabase($"wh-{Guid.NewGuid():N}"));
        services.AddHttpClient(WebhookConstants.AdapterName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubWebhookHandler(HttpStatusCode.OK));
        services.AddWarp<TestContext>(x => x.AddWebhooks(w => w.PublicAddressesOnly = true));
        await using var sp = services.BuildServiceProvider();

        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(WebhookConstants.AdapterName);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => client.GetAsync(new Uri("http://127.0.0.1:9/hook"), Ct));
        ex.Message.ShouldContain("UseSocketsHttpHandler");
    }

    [TimedFact]
    public async Task PublicAddressesOnly_OptedOut_DoesNotInstallTheGuard()
    {
        // Opted out (local development): the host's own primary handler is used untouched.
        await using var sp = BuildProvider(
            w => w.PublicAddressesOnly = false,
            services => services.AddHttpClient(WebhookConstants.AdapterName)
                .ConfigurePrimaryHttpMessageHandler(() => new StubWebhookHandler(HttpStatusCode.OK)));

        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(WebhookConstants.AdapterName);

        using var response = await client.GetAsync(new Uri("http://127.0.0.1:9/hook"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TimedTheory]
    [InlineData(0)]
    [InlineData(-5)]
    public void AddWarp_NonPositiveWebhookAttemptTimeout_Throws(int seconds)
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(o => o.UseInMemoryDatabase($"wh-{Guid.NewGuid():N}"));

        var ex = Should.Throw<InvalidOperationException>(() =>
            services.AddWarp<TestContext>(x => x.WebhookAttemptTimeout = TimeSpan.FromSeconds(seconds)));

        ex.Message.ShouldContain("WebhookAttemptTimeout");
    }

    [TimedFact]
    public void AddWarp_SubMillisecondWebhookAttemptTimeout_Throws()
    {
        // CancellationTokenSource truncates to whole milliseconds, so 0.5 ms would cancel every attempt at once.
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(o => o.UseInMemoryDatabase($"wh-{Guid.NewGuid():N}"));

        Should.Throw<InvalidOperationException>(() =>
            services.AddWarp<TestContext>(x => x.WebhookAttemptTimeout = TimeSpan.FromTicks(5_000)));
    }

    [TimedFact]
    public void AddWarp_InfiniteWebhookAttemptTimeout_IsAccepted()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(o => o.UseInMemoryDatabase($"wh-{Guid.NewGuid():N}"));

        Should.NotThrow(() => services.AddWarp<TestContext>(x => x.WebhookAttemptTimeout = Timeout.InfiniteTimeSpan));
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    // configureServices runs AFTER AddWarp, so a primary handler it sets is a host swap made after AddWebhooks.
    private static ServiceProvider BuildProvider(Action<WarpWebhookOptions>? configureWebhooks, Action<IServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TestContext>(o => o.UseInMemoryDatabase($"wh-{Guid.NewGuid():N}"));
        services.AddWarp<TestContext>(x => x.AddWebhooks(configureWebhooks));
        configureServices?.Invoke(services);

        return services.BuildServiceProvider();
    }
}
