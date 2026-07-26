using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Warp.Core.Enums;
using Warp.Core.Notifiers;

namespace Warp.Tests.Notifiers;

/// <summary>
/// NoDb coverage for the operational-event notifier seam: the dispatcher fans an event to every registered
/// notifier, swallows + logs a throwing one without stopping the rest, is inert with none registered, and
/// <c>AddNotifier&lt;T&gt;</c> contributes to the resolved set. Also pins the redaction-safe field set.
/// </summary>
[Trait("Category", "NoDb")]
public class WarpNotifierDispatcherTests
{
    [TimedFact]
    public async Task DispatchAsync_FiresEveryNotifierExactlyOnce()
    {
        var a = new SpyNotifier();
        var b = new SpyNotifier();
        var dispatcher = new WarpNotifierDispatcher([a, b], NullLogger<WarpNotifierDispatcher>.Instance);

        await dispatcher.DispatchAsync(SampleEvent(), CancellationToken.None);

        a.Received.Count.ShouldBe(1);
        b.Received.Count.ShouldBe(1);
        a.Received[0].Type.ShouldBe(WarpEventType.InstanceDown);
    }

    [TimedFact]
    public async Task DispatchAsync_ThrowingNotifier_IsSwallowed_OthersStillFire()
    {
        var throwing = new ThrowingNotifier();
        var after = new SpyNotifier();

        // throwing is registered FIRST — the second notifier must still receive the event.
        var dispatcher = new WarpNotifierDispatcher([throwing, after], NullLogger<WarpNotifierDispatcher>.Instance);

        // Must not throw out of the dispatcher.
        await Should.NotThrowAsync(() => dispatcher.DispatchAsync(SampleEvent(), CancellationToken.None));

        after.Received.Count.ShouldBe(1);
    }

    [TimedFact]
    public async Task DispatchAsync_NoNotifiers_IsNoOp()
    {
        var dispatcher = new WarpNotifierDispatcher([], NullLogger<WarpNotifierDispatcher>.Instance);

        await Should.NotThrowAsync(() => dispatcher.DispatchAsync(SampleEvent(), CancellationToken.None));
    }

    [TimedFact]
    public void AddNotifier_RegistersIntoTheResolvedSet()
    {
        var services = new ServiceCollection();
        var builder = new Warp.Core.WarpBuilder<TestContext>(services);

        builder.AddNotifier<SpyNotifier>();
        builder.AddNotifier<AnotherSpyNotifier>();

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetServices<IWarpNotifier>().ToList();

        resolved.Count.ShouldBe(2);
        resolved.ShouldContain(x => x is SpyNotifier);
        resolved.ShouldContain(x => x is AnotherSpyNotifier);
    }

    [TimedFact]
    public void WebhookDeliveryExhaustedEvent_CarriesNoPayloadBody()
    {
        // Redaction-safe (§1.2): the event exposes identity/linkage only. Pin the property set so a future
        // edit that adds a body/headers field trips this test.
        var names = typeof(WebhookDeliveryExhaustedEvent).GetProperties().Select(x => x.Name).ToHashSet(StringComparer.Ordinal);

        names.ShouldNotContain("Body");
        names.ShouldNotContain("Payload");
        names.ShouldNotContain("Headers");
        names.ShouldNotContain("Secret");
        names.ShouldContain(nameof(WebhookDeliveryExhaustedEvent.DeliveryId));
        names.ShouldContain(nameof(WebhookDeliveryExhaustedEvent.AttemptCount));
    }

    private static InstanceDownEvent SampleEvent() =>
        new()
        {
            Type = WarpEventType.InstanceDown,
            Severity = WarpEventSeverity.Warning,
            TimestampUtc = DateTime.UtcNow,
            MachineName = "test-host",
            Message = "instance down",
            InstanceId = Guid.NewGuid(),
            ApplicationName = "orders",
            IsServer = false,
        };

    private sealed class SpyNotifier : IWarpNotifier
    {
        public List<WarpOperationalEvent> Received { get; } = [];

        public Task NotifyAsync(WarpOperationalEvent evt, CancellationToken ct)
        {
            Received.Add(evt);

            return Task.CompletedTask;
        }
    }

    private sealed class AnotherSpyNotifier : IWarpNotifier
    {
        public Task NotifyAsync(WarpOperationalEvent evt, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ThrowingNotifier : IWarpNotifier
    {
        public Task NotifyAsync(WarpOperationalEvent evt, CancellationToken ct)
            => throw new InvalidOperationException("notifier boom");
    }
}
