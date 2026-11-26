using System.Net.Http;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Slo;
using Warp.Core.Webhooks;

namespace Warp.Tests.Webhooks;

/// <summary>
/// NoDb coverage for 8.1: the #357 failure classification, the endpoint health settings' validation, the webhook SLO
/// kind's validation, and the default interface members of the new <see cref="IWebhookCommandService"/> operations.
/// </summary>
[Trait("Category", "NoDb")]
public class WebhookEndpointHealthUnitTests
{
    public static TheoryData<Exception, WebhookFailureReason> Failures => new()
    {
        { new TaskCanceledException("timeout", new TimeoutException()), WebhookFailureReason.Timeout },
        { new HttpRequestException(HttpRequestError.NameResolutionError, "no such host"), WebhookFailureReason.DnsFailure },
        { new HttpRequestException(HttpRequestError.ConnectionError, "refused", new SocketException((int)SocketError.ConnectionRefused)), WebhookFailureReason.ConnectionRefused },
        { new HttpRequestException(HttpRequestError.SecureConnectionError, "tls"), WebhookFailureReason.TlsError },
        { new HttpRequestException(HttpRequestError.ConnectionError, "reset", new SocketException((int)SocketError.ConnectionReset)), WebhookFailureReason.ConnectionFailed },
        { new HttpRequestException(HttpRequestError.ResponseEnded, "ended"), WebhookFailureReason.ConnectionFailed },
        { new HttpRequestException("guard", new WebhookAddressNotPublicException("not public")), WebhookFailureReason.AddressNotPublic },
        { new InvalidOperationException("signer"), WebhookFailureReason.Other },
    };

    [TimedTheory]
    [MemberData(nameof(Failures))]
    public void ClassifyFailure_MapsTransportErrorsToReasons(Exception exception, WebhookFailureReason expected)
    {
        ExecuteWebhookDeliveryHandler<DbContext>.ClassifyFailure(exception).ShouldBe(expected);
    }

    [TimedTheory]
    [InlineData(nameof(WarpConfiguration.WebhookEndpointProbeInterval))]
    [InlineData(nameof(WarpConfiguration.WebhookEndpointStillFailingAfter))]
    [InlineData(nameof(WarpConfiguration.WebhookEndpointMonitorInterval))]
    public void AddWarp_NonPositiveEndpointHealthSetting_Throws(string setting)
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(o => o.UseInMemoryDatabase($"wh-{Guid.NewGuid():N}"));

        var ex = Should.Throw<InvalidOperationException>(() => services.AddWarp<TestContext>(x =>
        {
            switch (setting)
            {
                case nameof(WarpConfiguration.WebhookEndpointProbeInterval):
                    x.WebhookEndpointProbeInterval = TimeSpan.Zero;
                    break;
                case nameof(WarpConfiguration.WebhookEndpointStillFailingAfter):
                    x.WebhookEndpointStillFailingAfter = TimeSpan.FromDays(-1);
                    break;
                default:
                    x.WebhookEndpointMonitorInterval = TimeSpan.Zero;
                    break;
            }
        }));

        ex.Message.ShouldContain(setting);
    }

    [TimedFact]
    public void AddWarp_EndpointMonitorIntervalNull_IsAccepted()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(o => o.UseInMemoryDatabase($"wh-{Guid.NewGuid():N}"));

        Should.NotThrow(() => services.AddWarp<TestContext>(x => x.WebhookEndpointMonitorInterval = null));
    }

    [TimedFact]
    public void SloValidation_WebhookDeliveryLatency_DefaultsThePercentileAndKeepsTheMinutes()
    {
        var definition = new SloDefinition { Name = "acme", Kind = SloKind.WebhookDeliveryLatency, Dimension = "acme", TargetValue = 5, WindowSeconds = 3600 };

        SloValidation.TryValidate(definition, out var error).ShouldBeTrue(error);

        definition.Percentile.ShouldBe(SloValidation.DefaultLatencyPercentile);
        definition.TargetValue.ShouldBe(5);
    }

    [TimedTheory]
    [InlineData(0, 95)]
    [InlineData(5, 100)]
    public void SloValidation_WebhookDeliveryLatency_RejectsBadValues(double minutes, int percentile)
    {
        var definition = new SloDefinition { Name = "acme", Kind = SloKind.WebhookDeliveryLatency, Dimension = "acme", TargetValue = minutes, Percentile = percentile, WindowSeconds = 3600 };

        SloValidation.TryValidate(definition, out _).ShouldBeFalse();
    }

    [TimedFact]
    public void SloOptions_AddWebhookDeliveryLatency_SeedsTheKind()
    {
        var options = new SloOptions().AddWebhookDeliveryLatency("acme", minutes: 10);

        var seeded = options.Seeded.ShouldHaveSingleItem();
        seeded.Kind.ShouldBe(SloKind.WebhookDeliveryLatency);
        seeded.Dimension.ShouldBe("acme");
        seeded.TargetValue.ShouldBe(10);
        seeded.Percentile.ShouldBe(99);
    }

    [TimedFact]
    public async Task CommandServiceDefaults_LegacyImplementer_ThrowsNotSupported()
    {
        IWebhookCommandService service = new LegacyCommandService();

        await Should.ThrowAsync<NotSupportedException>(() => service.Cancel(Guid.NewGuid()));
        await Should.ThrowAsync<NotSupportedException>(() => service.CancelGroup("acme"));
        await Should.ThrowAsync<NotSupportedException>(() => service.ProbeEndpoint("acme"));
    }

    private sealed class LegacyCommandService : IWebhookCommandService
    {
        public Task<WebhookRedeliveryResult> Redeliver(Guid deliveryId, CancellationToken ct = default)
            => Task.FromResult(WebhookRedeliveryResult.Enqueued);
    }
}
