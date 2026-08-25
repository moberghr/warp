using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Warp.Core.Events;
using Warp.Dashboard.Push;
using Warp.Tests.TestData;
using XunitTestContext = Xunit.TestContext;

namespace Warp.Tests.DashboardPush;

/// <summary>
/// Unit tests for <see cref="DashboardBroadcaster{TContext}"/>. Each test constructs its own
/// broadcaster with the coalesce window it needs, drives <see cref="ServerTaskSignals{TContext}"/>
/// directly, and asserts against a <see cref="FakeHubContext"/>. No database, no host.
/// </summary>
[Trait("Category", "NoDb")]
public class DashboardBroadcasterTests
{
    [TimedFact]
    public async Task SingleJobFinalizedSignal_FiresOneBroadcast()
    {
        await using var harness = await Harness.StartAsync(TimeSpan.FromMilliseconds(50));

        harness.Signals.SignalJobFinalized();

        await harness.WaitForBroadcasts(1);
        harness.Hub.CountOf("JobFinalized").ShouldBe(1);
        harness.Hub.CountOf("MessageEnqueued").ShouldBe(0);
    }

    [TimedFact]
    public async Task CoalesceWindow_BurstOfSignals_CollapsesToOneBroadcast()
    {
        await using var harness = await Harness.StartAsync(TimeSpan.FromMilliseconds(100));

        for (var i = 0; i < 50; i++)
        {
            harness.Signals.SignalJobFinalized();
        }

        await harness.WaitForBroadcasts(1);

        // Determinism guard against the "burst was NOT collapsed" failure: if 50 broadcasts
        // are still flushing in the background, this next signal would land at broadcast 51,
        // not broadcast 2. Asserting the post-fire count is exactly 2 catches that without
        // relying on a timing-based "wait N ms and hope no more broadcasts come" guard.
        harness.Signals.SignalJobFinalized();
        await harness.WaitForBroadcasts(2);
        harness.Hub.CountOf("JobFinalized").ShouldBe(2);
    }

    [TimedFact]
    public async Task ZeroWindow_SignalsMapOneToOne()
    {
        await using var harness = await Harness.StartAsync(TimeSpan.Zero);

        for (var i = 0; i < 5; i++)
        {
            harness.Signals.SignalJobFinalized();

            // Let the broadcaster drain between signals. Without this, two signals racing the
            // single Drain pass would coalesce into 1 broadcast even at CoalesceWindow=0.
            await harness.WaitForBroadcasts(i + 1);
        }

        harness.Hub.CountOf("JobFinalized").ShouldBe(5);
    }

    [TimedFact]
    public async Task MessageEnqueuedSignal_FiresMessageEnqueuedEvent()
    {
        await using var harness = await Harness.StartAsync(TimeSpan.FromMilliseconds(50));

        harness.Signals.SignalMessageEnqueued();

        await harness.WaitForBroadcasts(1);
        harness.Hub.CountOf("MessageEnqueued").ShouldBe(1);
        harness.Hub.CountOf("JobFinalized").ShouldBe(0);
    }

    [TimedFact]
    public async Task BothSignalsInSameWindow_FireBothEvents()
    {
        await using var harness = await Harness.StartAsync(TimeSpan.FromMilliseconds(100));

        harness.Signals.SignalJobFinalized();
        harness.Signals.SignalMessageEnqueued();

        await harness.WaitForBroadcasts(2);
        harness.Hub.CountOf("JobFinalized").ShouldBe(1);
        harness.Hub.CountOf("MessageEnqueued").ShouldBe(1);
    }

    [TimedFact]
    public async Task StoppedBroadcaster_DoesNotBroadcastFurtherSignals()
    {
        var stopped = await Harness.StartAsync(TimeSpan.FromMilliseconds(50));

        stopped.Signals.SignalJobFinalized();
        await stopped.WaitForBroadcasts(1);

        await stopped.StopAsync();

        // Witness broadcaster shares the same Signals — provides a deterministic anchor
        // ("did at least one alive broadcaster see this signal?") without a timing wait.
        await using var witness = await Harness.StartOnExistingSignalsAsync(stopped.Signals, TimeSpan.FromMilliseconds(50));

        stopped.Signals.SignalJobFinalized();
        await witness.WaitForBroadcasts(1);

        stopped.Hub.CountOf("JobFinalized").ShouldBe(1);
        witness.Hub.CountOf("JobFinalized").ShouldBe(1);

        await stopped.DisposeAsync();
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly DashboardBroadcaster<TestContext> _broadcaster;
        private readonly CancellationTokenSource _cts = new();
        private bool _stopped;

        private Harness(DashboardBroadcaster<TestContext> broadcaster, ServerTaskSignals<TestContext> signals, FakeHubContext hub)
        {
            _broadcaster = broadcaster;
            Signals = signals;
            Hub = hub;
        }

        public ServerTaskSignals<TestContext> Signals { get; }

        public FakeHubContext Hub { get; }

        public static Task<Harness> StartAsync(TimeSpan coalesceWindow)
            => StartOnExistingSignalsAsync(new ServerTaskSignals<TestContext>(), coalesceWindow);

        public static async Task<Harness> StartOnExistingSignalsAsync(
            ServerTaskSignals<TestContext> signals,
            TimeSpan coalesceWindow)
        {
            var hub = new FakeHubContext();
            var configuration = new WarpDashboardPushConfiguration { CoalesceWindow = coalesceWindow };

            // Empty service provider — no IDashboardStatsService registered, so the
            // broadcaster's TryFetchStatsAsync returns null and the broadcast goes out
            // without a payload. The hub method-count assertions used by these unit
            // tests are unaffected.
            var sp = new ServiceCollection().BuildServiceProvider();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

            var broadcaster = new DashboardBroadcaster<TestContext>(
                signals,
                hub,
                configuration,
                TimeProvider.System,
                scopeFactory,
                NullLogger<DashboardBroadcaster<TestContext>>.Instance);

            var harness = new Harness(broadcaster, signals, hub);
            await broadcaster.StartAsync(harness._cts.Token);

            return harness;
        }

        public Task WaitForBroadcasts(int expected) => Hub.WaitForBroadcastsAsync(expected);

        public async Task StopAsync()
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            await _cts.CancelAsync();
            await _broadcaster.StopAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _broadcaster.Dispose();
            _cts.Dispose();
        }
    }
}
