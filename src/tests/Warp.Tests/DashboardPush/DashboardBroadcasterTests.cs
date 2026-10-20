using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Warp.Core.Events;
using Warp.Core.Models;
using Warp.Core.Services;
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

    [TimedFact]
    public async Task MinBroadcastInterval_FirstSignal_IsNotHeldByTheFloor()
    {
        // The fake clock never moves here, so a broadcast that waited on the floor would never arrive.
        var clock = new FakeTimeProvider();
        await using var harness = await Harness.StartAsync(TimeSpan.Zero, minBroadcastInterval: TimeSpan.FromSeconds(1), time: clock);

        harness.Signals.SignalJobFinalized();

        await harness.WaitForBroadcasts(1);
        harness.Hub.CountOf("JobFinalized").ShouldBe(1);
    }

    [TimedFact]
    public async Task MinBroadcastInterval_SignalsInsideTheFloor_AreHeldUntilItHasPassed()
    {
        var clock = new FakeTimeProvider();
        await using var harness = await Harness.StartAsync(TimeSpan.Zero, minBroadcastInterval: TimeSpan.FromSeconds(1), time: clock);
        harness.Signals.SignalJobFinalized();
        await harness.WaitForBroadcasts(1);

        for (var i = 0; i < 20; i++)
        {
            harness.Signals.SignalJobFinalized();
        }

        // Nothing goes out while the fake clock stands inside the floor; without the floor the
        // broadcast would land at once and this wait would succeed.
        await Should.ThrowAsync<TimeoutException>(() => harness.Hub.WaitForBroadcastsAsync(2, TimeSpan.FromMilliseconds(300)));

        // With a zero coalesce window each held signal is its own broadcast once released; folding them
        // together is the coalesce window's job (CoalesceWindow_BurstOfSignals_CollapsesToOneBroadcast).
        clock.Advance(TimeSpan.FromSeconds(1));
        await harness.WaitForBroadcasts(2);
        harness.Hub.CountOf("JobFinalized").ShouldBeGreaterThanOrEqualTo(2);
    }

    [TimedFact]
    public async Task NoConnectedClients_BroadcastsWithoutFetchingStats()
    {
        var stats = new Mock<IDashboardStatsService>();
        await using var harness = await Harness.StartAsync(TimeSpan.Zero, stats: stats.Object, connectedClients: 0);

        harness.Signals.SignalJobFinalized();
        await harness.WaitForBroadcasts(1);

        stats.Verify(x => x.GetWarpStatus(), Times.Never);
        harness.Hub.Broadcasts.Single().Args.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task LastClientDisconnects_StatsStopBeingFetched()
    {
        var stats = new Mock<IDashboardStatsService>();
        stats.Setup(x => x.GetWarpStatus()).ReturnsAsync(new DashboardStatistics());
        var connections = new DashboardConnectionCounter();
        var hub = await FakeDashboardClients.ConnectAsync(connections);
        await using var harness = await Harness.StartAsync(TimeSpan.Zero, stats: stats.Object, connections: connections);

        harness.Signals.SignalJobFinalized();
        await harness.WaitForBroadcasts(1);
        stats.Verify(x => x.GetWarpStatus(), Times.Once);

        await hub.OnDisconnectedAsync(null);
        connections.Count.ShouldBe(0);
        harness.Signals.SignalJobFinalized();
        await harness.WaitForBroadcasts(2);

        stats.Verify(x => x.GetWarpStatus(), Times.Once);
    }

    [TimedFact]
    public async Task ConnectedClient_BothEventsInOneDrain_ShareOneStatsSnapshot()
    {
        var stats = new Mock<IDashboardStatsService>();
        stats.Setup(x => x.GetWarpStatus()).ReturnsAsync(new DashboardStatistics());
        await using var harness = await Harness.StartAsync(TimeSpan.FromMilliseconds(100), stats: stats.Object, connectedClients: 1);

        harness.Signals.SignalJobFinalized();
        harness.Signals.SignalMessageEnqueued();
        await harness.WaitForBroadcasts(2);

        stats.Verify(x => x.GetWarpStatus(), Times.Once);
        harness.Hub.Broadcasts.ShouldAllBe(x => x.Args.Length == 1);
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

        public static Task<Harness> StartAsync(
            TimeSpan coalesceWindow,
            TimeSpan? minBroadcastInterval = null,
            IDashboardStatsService? stats = null,
            int? connectedClients = null,
            TimeProvider? time = null,
            DashboardConnectionCounter? connections = null)
            => StartOnExistingSignalsAsync(new ServerTaskSignals<TestContext>(), coalesceWindow, minBroadcastInterval, stats, connectedClients, time, connections);

        public static async Task<Harness> StartOnExistingSignalsAsync(
            ServerTaskSignals<TestContext> signals,
            TimeSpan coalesceWindow,
            TimeSpan? minBroadcastInterval = null,
            IDashboardStatsService? stats = null,
            int? connectedClients = null,
            TimeProvider? time = null,
            DashboardConnectionCounter? connections = null)
        {
            var hub = new FakeHubContext();

            // The floor defaults to zero here so the count-based tests stay fast; the floor's
            // own tests opt in.
            var configuration = new WarpDashboardPushConfiguration
            {
                CoalesceWindow = coalesceWindow,
                MinBroadcastInterval = minBroadcastInterval ?? TimeSpan.Zero,
            };

            // Without a stats service the broadcaster's TryFetchStatsAsync returns null and the
            // broadcast goes out without a payload, which the method-count assertions ignore.
            var services = new ServiceCollection();
            if (stats is not null)
            {
                services.AddScoped(_ => stats);
            }

            var sp = services.BuildServiceProvider();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

            if (connectedClients is { } clients)
            {
                connections ??= new DashboardConnectionCounter();
                if (clients > 0)
                {
                    await FakeDashboardClients.ConnectAsync(connections, clients);
                }
            }

            var broadcaster = new DashboardBroadcaster<TestContext>(
                signals,
                hub,
                configuration,
                time ?? TimeProvider.System,
                scopeFactory,
                NullLogger<DashboardBroadcaster<TestContext>>.Instance,
                connections);

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
