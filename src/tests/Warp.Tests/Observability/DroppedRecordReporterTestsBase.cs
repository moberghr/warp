using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Logging;
using Warp.Core.Notifiers;
using Warp.Core.Services;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;

namespace Warp.Tests.Observability;

/// <summary>
/// Database coverage for <see cref="DroppedRecordReporter{TContext}"/> — drains the process-global drop counters,
/// folds each delta to the durable <c>warpsys:records-dropped:{pipeline}</c> stat (so drops are visible in Warp's
/// own dashboard without OTel), and raises a throttled <see cref="RecordsDroppedEvent"/>. Serialized in its own
/// collection so the two provider variants can't race on the process-global <see cref="DroppedRecordCounters"/>.
/// </summary>
[GenerateDatabaseTests(SerializeInCollection = "DroppedRecords")]
public abstract class DroppedRecordReporterTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected DroppedRecordReporterTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetAsync();

        // Reset the process-global counters so a prior test can't leak into this one.
        foreach (var pipeline in Enum.GetValues<DropPipeline>())
        {
            DroppedRecordCounters.Drain(pipeline);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task ReportOnce_WithDrops_PersistsPerPipelineAndFires()
    {
        DroppedRecordCounters.Track(DropPipeline.Adapter, 5);
        DroppedRecordCounters.Track(DropPipeline.Client, 3);
        var spy = new SpyNotifier();

        await CreateReporter(spy).ReportOnceAsync(Ct);

        (await SumDropped(DropPipeline.Adapter)).ShouldBe(5);
        (await SumDropped(DropPipeline.Client)).ShouldBe(3);
        (await SumDropped(DropPipeline.Endpoint)).ShouldBe(0);

        var events = spy.Received.OfType<RecordsDroppedEvent>().ToList();
        events.Count.ShouldBe(2);
        events.ShouldContain(e => string.Equals(e.Pipeline, "adapter", StringComparison.Ordinal) && e.Count == 5);
        events.ShouldContain(e => string.Equals(e.Pipeline, "client", StringComparison.Ordinal) && e.Count == 3);
    }

    [TimedFact]
    public async Task ReportOnce_NoDrops_WritesNothingAndDoesNotFire()
    {
        var spy = new SpyNotifier();

        await CreateReporter(spy).ReportOnceAsync(Ct);

        (await SumDropped(DropPipeline.Adapter)).ShouldBe(0);
        spy.Received.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task ReportOnce_WithinCooldown_PersistsButFiresOnce()
    {
        var spy = new SpyNotifier();
        var reporter = CreateReporter(spy); // one instance → the cooldown state is shared across the two passes

        DroppedRecordCounters.Track(DropPipeline.Adapter, 4);
        await reporter.ReportOnceAsync(Ct);
        DroppedRecordCounters.Track(DropPipeline.Adapter, 6);
        await reporter.ReportOnceAsync(Ct); // immediately after → still inside the 5-min cooldown

        (await SumDropped(DropPipeline.Adapter)).ShouldBe(10); // both deltas persisted
        spy.Received.OfType<RecordsDroppedEvent>().Count(e => string.Equals(e.Pipeline, "adapter", StringComparison.Ordinal)).ShouldBe(1); // fired once
    }

    [TimedFact]
    public async Task DashboardStatus_ReflectsRecentDrops()
    {
        DroppedRecordCounters.Track(DropPipeline.Endpoint, 7);
        await CreateReporter(new SpyNotifier()).ReportOnceAsync(Ct);

        var status = await new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new Warp.Core.Metrics.LocalMetricSource<TestContext>(_fixture.CreateContext())).GetWarpStatus();

        status.EndpointRecordsDropped.ShouldBe(7);
    }

    private DroppedRecordReporter<TestContext> CreateReporter(SpyNotifier spy)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _fixture.CreateContext());
        var provider = services.BuildServiceProvider();

        return new DroppedRecordReporter<TestContext>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new WarpConfiguration()),
            TimeProvider.System,
            TestNotifiers.SpyDispatcher(spy),
            NullLogger<DroppedRecordReporter<TestContext>>.Instance);
    }

    private async Task<long> SumDropped(DropPipeline pipeline)
    {
        var prefix = DroppedRecordKeys.Base(pipeline) + ":";

        return await _fixture.CreateContext().Set<Counter>()
            .Where(x => x.Key.StartsWith(prefix))
            .SumAsync(x => x.Value, Ct);
    }
}
