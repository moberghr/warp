using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Notifiers;
using Warp.Core.Services;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.Observability;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.Slo;

/// <summary>
/// Database coverage for the metric arms of <see cref="SloEvaluator{TContext}"/> (custom application metrics §E,
/// R22-R28): <c>metric_point</c> rows and registry rows are inserted directly, one evaluator tick runs at a fixed
/// clock, and the <see cref="SloEvaluation"/> row and the dispatched events are asserted. Reads cover every tier
/// overlapping the window, and partly overlapping coarse buckets are prorated.
/// </summary>
[GenerateDatabaseTests]
public abstract class MetricSloEvaluatorTestsBase : IAsyncLifetime
{
    private const double Tolerance = 1e-6;

    // 12:32 so the window [11:32, 12:32) starts inside an hourly row and a fine row, and ends inside a fine bucket.
    private static readonly DateTime Now = new(2026, 11, 10, 12, 32, 0, DateTimeKind.Utc);

    private readonly IDatabaseFixture _fixture;

    protected MetricSloEvaluatorTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task Evaluate_MetricRatio_IgnoresValuesInNeitherList()
    {
        await SeedInstrumentAsync("payment.results", MetricKind.Counter);
        await SeedPointsAsync(
            Fine("payment.results", "result=success", At(12, 0), sum: 90),
            Fine("payment.results", "result=error", At(12, 0), sum: 10),
            Fine("payment.results", "result=refused", At(12, 0), sum: 1000));
        var id = await SeedObjectiveAsync(Ratio(0.95));
        var spy = new SpyNotifier();

        await RunAsync(spy);

        var eval = await EvalAsync(id);
        eval.Attainment.ShouldBe(0.9, Tolerance);
        eval.State.ShouldBe(SloState.Breaching);
    }

    [TimedFact]
    public async Task Evaluate_MetricRatio_BreachEvent_CarriesMetricLabelFromDefinition()
    {
        await SeedInstrumentAsync("payment.results", MetricKind.Counter);
        await SeedPointsAsync(
            Fine("payment.results", "result=success", At(12, 0), sum: 90),
            Fine("payment.results", "result=error", At(12, 0), sum: 10));
        await SeedObjectiveAsync(Ratio(0.95));
        var spy = new SpyNotifier();

        await RunAsync(spy);

        var evt = spy.Received.ShouldHaveSingleItem().ShouldBeOfType<SloBreachedEvent>();
        evt.Type.ShouldBe(WarpEventType.SloBreached);
        evt.Kind.ShouldBe(SloKind.MetricRatio);
        evt.Metric.ShouldBe("payment.results (result: success / error)");
    }

    [TimedFact]
    public async Task Evaluate_MetricRatio_SevenDayBaselineDrop_Breaches()
    {
        await SeedInstrumentAsync("payment.results", MetricKind.Counter);
        var baselineDay = Now.AddDays(-7);
        await SeedPointsAsync(
            Fine("payment.results", "result=success", At(12, 0), sum: 80),
            Fine("payment.results", "result=error", At(12, 0), sum: 20),
            Hourly("payment.results", "result=success", Hour(baselineDay, 11), sum: 100),
            Hourly("payment.results", "result=success", Hour(baselineDay, 12), sum: 60));
        var def = Ratio(0.1);
        def.BaselineDays = 7;
        var id = await SeedObjectiveAsync(def);
        var spy = new SpyNotifier();

        await RunAsync(spy);

        // Current ratio 0.8 against a baseline ratio of 1.0: a 0.2 drop over the 0.1 allowed.
        var eval = await EvalAsync(id);
        eval.Attainment.ShouldBe(0.2, Tolerance);
        eval.State.ShouldBe(SloState.Breaching);
        spy.Received.ShouldHaveSingleItem().ShouldBeOfType<SloBreachedEvent>().Metric.ShouldBe("payment.results (result: success / error) vs 7d baseline");
    }

    [TimedFact]
    public async Task Evaluate_MetricThreshold_MatchingEventsOverTarget_Breaches()
    {
        await SeedInstrumentAsync("http.server.request.duration", MetricKind.Histogram);
        await SeedPointsAsync(
            Fine("http.server.request.duration", "http.response.status_code=500,http.route=/api/x", At(12, 0), count: 3, sum: 1500),
            Fine("http.server.request.duration", "http.response.status_code=200,http.route=/api/x", At(12, 0), count: 100, sum: 900));
        var id = await SeedObjectiveAsync(Threshold("http.server.request.duration", 2, "http.response.status_code=5*"));
        var spy = new SpyNotifier();

        await RunAsync(spy);

        // Histogram events are the count, not the summed durations.
        var eval = await EvalAsync(id);
        eval.Attainment.ShouldBe(3, Tolerance);
        eval.State.ShouldBe(SloState.Breaching);
        spy.Received.ShouldHaveSingleItem().ShouldBeOfType<SloBreachedEvent>().Metric.ShouldBe("http.server.request.duration [http.response.status_code=5*]");
    }

    [TimedFact]
    public async Task Evaluate_MetricThreshold_TrafficWithNoMatches_IsHealthyAtZero()
    {
        await SeedInstrumentAsync("http.server.request.duration", MetricKind.Histogram);
        await SeedPointsAsync(Fine("http.server.request.duration", "http.response.status_code=200", At(12, 0), count: 100, sum: 900));
        var id = await SeedObjectiveAsync(Threshold("http.server.request.duration", 0, "http.response.status_code=5*"));
        var spy = new SpyNotifier();

        await RunAsync(spy);

        var eval = await EvalAsync(id);
        eval.State.ShouldBe(SloState.Healthy);
        eval.Attainment.ShouldBe(0, Tolerance);
        spy.Received.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task Evaluate_MetricThreshold_InstrumentWithoutRows_IsNoData_NoAlert()
    {
        await SeedInstrumentAsync("http.server.request.duration", MetricKind.Histogram);

        // A row outside the window does not count as data in it.
        await SeedPointsAsync(Hourly("http.server.request.duration", "http.response.status_code=500", At(9, 0), count: 50, sum: 50));
        var id = await SeedObjectiveAsync(Threshold("http.server.request.duration", 0, "http.response.status_code=5*"));
        var spy = new SpyNotifier();

        await RunAsync(spy);

        (await EvalAsync(id)).State.ShouldBe(SloState.NoData);
        spy.Received.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task Evaluate_MetricThreshold_GaugeInstrument_IsNoData_NoAlert()
    {
        await SeedInstrumentAsync("queue.depth", MetricKind.Gauge);
        await SeedPointsAsync(Fine("queue.depth", "-", At(12, 0), count: 10, sum: 5000));
        var id = await SeedObjectiveAsync(Threshold("queue.depth", 1, null));
        var spy = new SpyNotifier();
        var logs = new CapturingLoggerProvider();

        await RunAsync(spy, new FakeTimeProvider(new DateTimeOffset(Now)), LoggerFor(logs));

        (await EvalAsync(id)).State.ShouldBe(SloState.NoData);
        spy.Received.ShouldBeEmpty();

        // A gauge has no event count: the misconfigured objective is logged at Error, naming the objective and
        // instrument, rather than sitting silently at NoData.
        var error = logs.Logs.Where(x => x.Level == LogLevel.Error).ShouldHaveSingleItem();
        error.Message.ShouldContain("queue.depth");
        error.Message.ShouldContain("gauge");
    }

    [TimedTheory]
    [InlineData(SloState.NoData)]
    [InlineData(SloState.Healthy)]
    public async Task Evaluate_MetricThreshold_PriorStateToBreaching_FiresOneEvent(SloState prior)
    {
        await SeedInstrumentAsync("orders.failed", MetricKind.Counter);
        await SeedPointsAsync(Fine("orders.failed", "-", At(12, 0), sum: 5));
        var id = await SeedObjectiveAsync(Threshold("orders.failed", 1, null));
        await SeedEvaluationAsync(id, prior);
        var spy = new SpyNotifier();

        await RunAsync(spy);

        (await EvalAsync(id)).State.ShouldBe(SloState.Breaching);
        spy.Received.ShouldHaveSingleItem().Type.ShouldBe(WarpEventType.SloBreached);
    }

    [TimedFact]
    public async Task Evaluate_MetricThreshold_BreachingThenHealthyThenBreaching_FiresASecondEvent()
    {
        await SeedInstrumentAsync("orders.failed", MetricKind.Counter);
        await SeedPointsAsync(Fine("orders.failed", "-", At(12, 0), sum: 5));
        var id = await SeedObjectiveAsync(Threshold("orders.failed", 1, null));
        var spy = new SpyNotifier();

        await RunAsync(spy);
        spy.Received.Count.ShouldBe(1);

        // The objective recovered in between: the next breach is a new healthy→breaching edge.
        await SetStateAsync(id, SloState.Healthy);
        await RunAsync(spy);

        (await EvalAsync(id)).State.ShouldBe(SloState.Breaching);
        spy.Received.Count.ShouldBe(2);
        spy.Received.ShouldAllBe(x => x.Type == WarpEventType.SloBreached);
    }

    [TimedFact]
    public async Task Evaluate_ExistingKindBreach_EventHasNoMetricLabel()
    {
        // Kinds 1–5 read Warp's own aggregates, not a custom instrument: Metric stays null so a notifier can tell them apart.
        var suffix = MetricTiers.Suffix(MetricTier.Fine, At(12, 25), 5);
        var ctx = _fixture.CreateContext();
        ctx.Set<Counter>().AddRange(
            new Counter { Key = JobStatsKeys.History(JobStatsKeys.TypeMarker, "MyJob", JobStatsKeys.SucceededToken, suffix), Value = 90 },
            new Counter { Key = JobStatsKeys.History(JobStatsKeys.TypeMarker, "MyJob", JobStatsKeys.FailedToken, suffix), Value = 10 });
        await ctx.SaveChangesAsync(Ct);
        var id = await SeedObjectiveAsync(new SloDefinition { Name = "MyJob succeeds", Kind = SloKind.SuccessRate, Dimension = "MyJob", TargetValue = 0.99, WindowSeconds = 3600, Enabled = true });
        var spy = new SpyNotifier();

        await RunAsync(spy);

        (await EvalAsync(id)).State.ShouldBe(SloState.Breaching);
        var evt = spy.Received.ShouldHaveSingleItem().ShouldBeOfType<SloBreachedEvent>();
        evt.Kind.ShouldBe(SloKind.SuccessRate);
        evt.Metric.ShouldBeNull();
    }

    [TimedFact]
    public async Task Evaluate_MetricThreshold_WindowAcrossTiers_SumsExactlyWithProration()
    {
        await SeedInstrumentAsync("orders.failed", MetricKind.Counter);
        await SeedPointsAsync(
            Hourly("orders.failed", "-", At(10, 0), sum: 1000), // ends before the window: not read
            Hourly("orders.failed", "-", At(11, 0), sum: 60), // [11:00, 12:00) overlaps 28 of 60 min: 28
            Fine("orders.failed", "-", At(11, 30), sum: 5, compacted: true), // [11:30, 11:35) overlaps 3 of 5 min: 3
            Fine("orders.failed", "-", At(12, 0), sum: 10, compacted: true), // inside: 10
            Fine("orders.failed", "-", At(12, 30), sum: 3), // the bucket still being written, two partial rows: 5
            Fine("orders.failed", "-", At(12, 30), sum: 2));
        var id = await SeedObjectiveAsync(Threshold("orders.failed", 45, null));
        var spy = new SpyNotifier();

        await RunAsync(spy);

        var eval = await EvalAsync(id);
        eval.Attainment.ShouldBe(46, Tolerance);
        eval.State.ShouldBe(SloState.Breaching);
    }

    [TimedFact]
    public async Task Evaluate_MetricVolumeFloor_BaselineFromHourlyAndDaily_ProratedAndBreaches()
    {
        await SeedInstrumentAsync("orders.created", MetricKind.Counter);
        var yesterday = Now.AddDays(-1);
        await SeedPointsAsync(
            Fine("orders.created", "-", At(12, 0), sum: 6),
            Fine("orders.created", "-", At(12, 30), sum: 4),
            Hourly("orders.created", "-", Hour(yesterday, 11), sum: 60), // 28 of 60 min: 28
            Hourly("orders.created", "-", Hour(yesterday, 12), sum: 20), // 32 of 60 min: 20 * 32/60
            Daily("orders.created", "-", yesterday.Date, sum: 1440)); // 60 of 1440 min: 60
        var def = new SloDefinition { Name = "Orders keep coming", Kind = SloKind.MetricVolumeFloor, Dimension = "orders.created", TargetValue = 0.5, BaselineDays = 1, WindowSeconds = 3600, Enabled = true };
        var id = await SeedObjectiveAsync(def);
        var spy = new SpyNotifier();

        await RunAsync(spy);

        // Baseline [11:32, 12:32) yesterday = 28 + 20 * 32/60 + 60; current = 10.
        var baseline = 28 + (20 * 32.0 / 60) + 60;
        var eval = await EvalAsync(id);
        eval.Attainment.ShouldBe(10 / baseline, Tolerance);
        eval.State.ShouldBe(SloState.Breaching);
        spy.Received.ShouldHaveSingleItem();
    }

    [TimedFact]
    public async Task Evaluate_MetricGap_PrimaryMinusSecondaryOverTarget_Breaches()
    {
        await SeedInstrumentAsync("orders.created", MetricKind.Counter);
        await SeedInstrumentAsync("orders.paid", MetricKind.Counter);
        await SeedPointsAsync(
            Fine("orders.created", "-", At(12, 0), sum: 10),
            Fine("orders.paid", "status=ok", At(12, 0), sum: 7),
            Fine("orders.paid", "status=late", At(12, 0), sum: 100));
        var def = new SloDefinition { Name = "Orders get paid", Kind = SloKind.MetricGap, Dimension = "orders.created", SecondaryMetric = "orders.paid", SecondaryTagFilter = "status=ok", TargetValue = 2, WindowSeconds = 3600, Enabled = true };
        var id = await SeedObjectiveAsync(def);
        var spy = new SpyNotifier();

        await RunAsync(spy);

        var eval = await EvalAsync(id);
        eval.Attainment.ShouldBe(3, Tolerance);
        eval.State.ShouldBe(SloState.Breaching);
        spy.Received.ShouldHaveSingleItem().ShouldBeOfType<SloBreachedEvent>().Metric.ShouldBe("orders.created - orders.paid [status=ok]");
    }

    [TimedFact]
    public async Task Evaluate_MetricGap_InfiniteGap_IsNoData_NoAlert()
    {
        await SeedInstrumentAsync("big.in", MetricKind.Counter);
        await SeedInstrumentAsync("big.out", MetricKind.UpDownCounter);
        await SeedPointsAsync(
            Fine("big.in", "-", At(12, 0), sum: double.MaxValue),
            Fine("big.out", "-", At(12, 0), sum: -double.MaxValue));
        var def = new SloDefinition { Name = "Overflow", Kind = SloKind.MetricGap, Dimension = "big.in", SecondaryMetric = "big.out", TargetValue = 0, WindowSeconds = 3600, Enabled = true };
        var id = await SeedObjectiveAsync(def);
        var spy = new SpyNotifier();

        await RunAsync(spy);

        // MaxValue - (-MaxValue) is +infinity: Classify would read the NaN result as Healthy; it must be NoData.
        (await EvalAsync(id)).State.ShouldBe(SloState.NoData);
        spy.Received.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task Evaluate_MetricThreshold_OneEventPerEdge_AckSuppressed_FiresAgainAfterAckExpires()
    {
        await SeedInstrumentAsync("orders.failed", MetricKind.Counter);
        await SeedPointsAsync(Fine("orders.failed", "-", At(12, 0), sum: 5));
        var id = await SeedObjectiveAsync(Threshold("orders.failed", 1, null));
        var spy = new SpyNotifier();
        var clock = new FakeTimeProvider(new DateTimeOffset(Now));

        await RunAsync(spy, clock);
        await RunAsync(spy, clock);
        spy.Received.Count.ShouldBe(1);

        await SetAcknowledgedUntilAsync(id, Now.AddMinutes(1));
        await RunAsync(spy, clock);
        (await EvalAsync(id)).State.ShouldBe(SloState.Acknowledged);
        spy.Received.Count.ShouldBe(1);

        clock.Advance(TimeSpan.FromMinutes(2));
        await RunAsync(spy, clock);

        (await EvalAsync(id)).State.ShouldBe(SloState.Breaching);
        spy.Received.Count.ShouldBe(2);
        spy.Received.ShouldAllBe(x => x.Type == WarpEventType.SloBreached);
    }

    [TimedFact]
    public async Task Evaluate_MetricThreshold_ApplicationScoped_CountsOnlyThatApplication()
    {
        await SeedInstrumentAsync("orders.failed", MetricKind.Counter);
        await SeedPointsAsync(
            WithApplication(Fine("orders.failed", "-", At(12, 0), sum: 3), "shop"),
            WithApplication(Fine("orders.failed", "-", At(12, 0), sum: 100), "billing"),
            Fine("orders.failed", "-", At(12, 0), sum: 1000));
        var def = Threshold("orders.failed", 5, null);
        def.Application = "shop";
        var id = await SeedObjectiveAsync(def);
        var spy = new SpyNotifier();

        await RunAsync(spy);

        var eval = await EvalAsync(id);
        eval.Attainment.ShouldBe(3, Tolerance);
        eval.State.ShouldBe(SloState.Healthy);
        spy.Received.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task Evaluate_MetricThreshold_ApplicationWithCommaAndOverLongName_MatchesTheSanitizedStoredForm()
    {
        // The collector stores MetricSeries.Sanitize(ApplicationName): ',' and '=' become '-', capped at 64 characters.
        await SeedInstrumentAsync("orders.failed", MetricKind.Counter);
        await SeedPointsAsync(
            WithApplication(Fine("orders.failed", "-", At(12, 0), sum: 3), "shop-eu-west"),
            WithApplication(Fine("orders.failed", "-", At(12, 0), sum: 7), new string('a', 64)));
        var comma = Threshold("orders.failed", 100, null);
        comma.Application = "shop,eu=west";
        var commaId = await SeedObjectiveAsync(comma);
        var tooLong = Threshold("orders.failed", 100, null);
        tooLong.Name = "too long";
        tooLong.Application = new string('a', 70);
        var tooLongId = await SeedObjectiveAsync(tooLong);

        await RunAsync(new SpyNotifier());

        (await EvalAsync(commaId)).Attainment.ShouldBe(3, Tolerance);
        (await EvalAsync(tooLongId)).Attainment.ShouldBe(7, Tolerance);
    }

    [TimedFact]
    public async Task Evaluate_MetricRatio_NoGoodOrBadEvents_IsNoData_NoAlert()
    {
        await SeedInstrumentAsync("payment.results", MetricKind.Counter);
        await SeedPointsAsync(Fine("payment.results", "result=refused", At(12, 0), sum: 1000));
        var id = await SeedObjectiveAsync(Ratio(0.95));
        var spy = new SpyNotifier();

        await RunAsync(spy);

        (await EvalAsync(id)).State.ShouldBe(SloState.NoData);
        spy.Received.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task Evaluate_MetricVolumeFloor_ZeroBaseline_IsNoData_NoAlert()
    {
        await SeedInstrumentAsync("orders.created", MetricKind.Counter);
        await SeedPointsAsync(Fine("orders.created", "-", At(12, 0), sum: 6));
        var def = new SloDefinition { Name = "Orders keep coming", Kind = SloKind.MetricVolumeFloor, Dimension = "orders.created", TargetValue = 0.5, BaselineDays = 1, WindowSeconds = 3600, Enabled = true };
        var id = await SeedObjectiveAsync(def);
        var spy = new SpyNotifier();

        await RunAsync(spy);

        (await EvalAsync(id)).State.ShouldBe(SloState.NoData);
        spy.Received.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task Evaluate_MetricGap_NeitherInstrumentHasRows_IsNoData_NoAlert()
    {
        await SeedInstrumentAsync("orders.created", MetricKind.Counter);
        await SeedInstrumentAsync("orders.paid", MetricKind.Counter);
        var def = new SloDefinition { Name = "Orders get paid", Kind = SloKind.MetricGap, Dimension = "orders.created", SecondaryMetric = "orders.paid", TargetValue = 0, WindowSeconds = 3600, Enabled = true };
        var id = await SeedObjectiveAsync(def);
        var spy = new SpyNotifier();

        await RunAsync(spy);

        (await EvalAsync(id)).State.ShouldBe(SloState.NoData);
        spy.Received.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task Evaluate_MetricThreshold_RowsWithoutRegistryRow_IsNoData_NoAlert()
    {
        // The flusher writes the registry row after the points; until then the event quantity is unknown.
        await SeedPointsAsync(Fine("orders.failed", "-", At(12, 0), sum: 50));
        var id = await SeedObjectiveAsync(Threshold("orders.failed", 1, null));
        var spy = new SpyNotifier();

        await RunAsync(spy);

        (await EvalAsync(id)).State.ShouldBe(SloState.NoData);
        spy.Received.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task Evaluate_RowsWithoutRegistryRow_WarnsOncePerObjectiveAndInstrument_ThenAgainAfterTenMinutes()
    {
        // A registry row that never arrives would otherwise read as NoData forever without a word.
        var instrument = $"orders.unregistered.{Guid.NewGuid():N}";
        await SeedPointsAsync(Fine(instrument, "customer=c-secret-1", At(12, 0), sum: 50));
        var id = await SeedObjectiveAsync(Threshold(instrument, 1, "customer=*"));
        var logs = new CapturingLoggerProvider();
        var clock = new FakeTimeProvider(new DateTimeOffset(Now));

        await RunAsync(new SpyNotifier(), clock, LoggerFor(logs));
        await RunAsync(new SpyNotifier(), clock, LoggerFor(logs));

        (await EvalAsync(id)).State.ShouldBe(SloState.NoData);
        var warning = logs.Logs.Where(x => x.Level == LogLevel.Warning).ShouldHaveSingleItem();
        warning.Message.ShouldContain(instrument);
        warning.Message.ShouldContain($"{instrument} under 1");
        logs.Logs.ShouldAllBe(x => !x.Message.Contains("c-secret-1", StringComparison.Ordinal));

        clock.Advance(TimeSpan.FromMinutes(11));
        await RunAsync(new SpyNotifier(), clock, LoggerFor(logs));

        logs.Logs.Count(x => x.Level == LogLevel.Warning).ShouldBe(2);
    }

    [TimedFact]
    public async Task Evaluate_OneObjectiveThrows_IsNoDataAndLoggedAndTheHealthyObjectiveIsStillEvaluated()
    {
        await SeedInstrumentAsync("orders.created", MetricKind.Counter);
        await SeedPointsAsync(Fine("orders.created", "-", At(12, 0), sum: 3));

        // A stored baseline past TimeSpan's range: the arithmetic throws before any read.
        var corrupt = new SloDefinition { Name = "Corrupt floor", Kind = SloKind.MetricVolumeFloor, Dimension = "orders.created", TargetValue = 0.5, BaselineDays = int.MaxValue, WindowSeconds = 3600, Enabled = true };
        var corruptId = await SeedObjectiveAsync(corrupt);
        var healthyId = await SeedObjectiveAsync(Threshold("orders.created", 10, null));
        var logs = new CapturingLoggerProvider();

        await RunAsync(new SpyNotifier(), new FakeTimeProvider(new DateTimeOffset(Now)), LoggerFor(logs));

        (await EvalAsync(corruptId)).State.ShouldBe(SloState.NoData);
        var healthy = await EvalAsync(healthyId);
        healthy.State.ShouldBe(SloState.Healthy);
        healthy.Attainment.ShouldBe(3, Tolerance);
        var error = logs.Logs.Where(x => x.Level == LogLevel.Error).ShouldHaveSingleItem();
        error.Message.ShouldContain(corruptId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        error.Message.ShouldContain("Corrupt floor");
    }

    [TimedFact]
    public async Task Evaluate_MetricThreshold_NegativeEventTotal_IsNoData_NoAlert()
    {
        // An up-down counter driven below zero is not a count of events; it used to read Healthy.
        await SeedInstrumentAsync("connections.delta", MetricKind.UpDownCounter);
        await SeedPointsAsync(Fine("connections.delta", "-", At(12, 0), sum: -5));
        var id = await SeedObjectiveAsync(Threshold("connections.delta", 1, null));
        var spy = new SpyNotifier();

        await RunAsync(spy);

        (await EvalAsync(id)).State.ShouldBe(SloState.NoData);
        spy.Received.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task Evaluate_MetricThreshold_EmptyFilterTotalOnlyRead_EqualsThePerTagRead()
    {
        // Every row carries `k`, so `k=*` matches exactly what an empty filter matches, but forces the per-tag read.
        await SeedInstrumentAsync("orders.failed", MetricKind.Counter);
        await SeedPointsAsync(
            Hourly("orders.failed", "k=a", At(11, 0), sum: 60),
            Hourly("orders.failed", "k=b", At(11, 0), sum: 30),
            Fine("orders.failed", "k=a", At(11, 30), sum: 5, compacted: true),
            Fine("orders.failed", "k=b", At(12, 0), sum: 10, compacted: true),
            Fine("orders.failed", "k=a", At(12, 30), sum: 3),
            Fine("orders.failed", "k=a", At(12, 30), sum: 2),
            Fine("orders.failed", "k=b", At(12, 30), sum: 4));
        var totalOnly = await SeedObjectiveAsync(Threshold("orders.failed", 1000, null));
        var perTag = Threshold("orders.failed", 1000, "k=*");
        perTag.Name = "per tag";
        var perTagId = await SeedObjectiveAsync(perTag);

        var evaluator = await RunAsync(new SpyNotifier());

        // 90 * 28/60 + 5 * 3/5 + 10 + 9 = 64
        var fast = await EvalAsync(totalOnly);
        var slow = await EvalAsync(perTagId);
        fast.Attainment.ShouldBe(64, Tolerance);
        slow.Attainment.ShouldBe(fast.Attainment, Tolerance);
        slow.BudgetRemaining.ShouldBe(fast.BudgetRemaining, Tolerance);
        slow.BurnRateShort.ShouldBe(fast.BurnRateShort, Tolerance);
        slow.BurnRateLong.ShouldBe(fast.BurnRateLong, Tolerance);
        evaluator.TotalOnlyReads.ShouldBe(1);
    }

    [TimedFact]
    public async Task Evaluate_MetricRatioAndFilteredKinds_NeverTakeTheTotalOnlyRead()
    {
        await SeedInstrumentAsync("payment.results", MetricKind.Counter);
        await SeedPointsAsync(Fine("payment.results", "result=success", At(12, 0), sum: 90));
        await SeedObjectiveAsync(Ratio(0.5));

        var evaluator = await RunAsync(new SpyNotifier());

        evaluator.TotalOnlyReads.ShouldBe(0);
    }

    [TimedFact]
    public async Task Evaluate_MetricLatency_EnoughEventsUnderThreshold_IsHealthy()
    {
        await SeedInstrumentAsync(Duration, MetricKind.Histogram);
        await SeedPointsAsync(Histogram(Duration, "-", MetricTier.Fine, At(12, 0), "0.1,0.25,0.5", 90, 8, 2, 0));
        var id = await SeedObjectiveAsync(Latency(0.25, 95));
        var spy = new SpyNotifier();

        await RunAsync(spy);

        // 98 of 100 events in buckets bounded at <= 0.25: 2% slow against the 5% a p95 allows, burn 0.4.
        var eval = await EvalAsync(id);
        eval.State.ShouldBe(SloState.Healthy);
        eval.Attainment.ShouldBe(0.98, Tolerance);
        eval.BurnRateLong.ShouldBe(0.4, Tolerance);
        eval.BudgetRemaining.ShouldBe(0.6, Tolerance);
        spy.Received.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task Evaluate_MetricLatency_TooManySlowEvents_BreachesWithLabelFromDefinition()
    {
        await SeedInstrumentAsync(Duration, MetricKind.Histogram);
        await SeedPointsAsync(
            Histogram(Duration, "http.route=/api/x", MetricTier.Fine, At(12, 0), "0.1,0.25,0.5", 80, 10, 5, 5),
            Histogram(Duration, "http.route=/api/y", MetricTier.Fine, At(12, 0), "0.1,0.25,0.5", 0, 0, 0, 1000));
        var def = Latency(0.25, 95);
        def.TagFilter = "http.route=/api/x";
        var id = await SeedObjectiveAsync(def);
        var spy = new SpyNotifier();

        await RunAsync(spy);

        // Only /api/x matches: 90 of 100 within 0.25, 10% slow against 5% allowed.
        var eval = await EvalAsync(id);
        eval.State.ShouldBe(SloState.Breaching);
        eval.Attainment.ShouldBe(0.9, Tolerance);
        eval.BurnRateLong.ShouldBe(2, Tolerance);
        var evt = spy.Received.ShouldHaveSingleItem().ShouldBeOfType<SloBreachedEvent>();
        evt.Kind.ShouldBe(SloKind.MetricLatency);
        evt.Metric.ShouldBe($"{Duration} [http.route=/api/x] p95 <= 0.25");
    }

    [TimedFact]
    public async Task Evaluate_MetricLatency_BadRecentBucketOverAGoodHistory_BurnsTheShortWindow()
    {
        // Short window = 3600 / 12 floored to 5 min: [12:27, 12:32). 1000 fast events at 12:00 are outside it; the
        // still-filling 12:30 bucket (10 events, all slow) is inside it, clipped at now.
        await SeedInstrumentAsync(Duration, MetricKind.Histogram);
        await SeedPointsAsync(
            Histogram(Duration, "-", MetricTier.Fine, At(12, 0), "0.1,0.25,0.5", 1000, 0, 0, 0),
            Histogram(Duration, "-", MetricTier.Fine, At(12, 30), "0.1,0.25,0.5", 0, 0, 0, 10));
        var id = await SeedObjectiveAsync(Latency(0.25, 95));

        await RunAsync(new SpyNotifier());

        var eval = await EvalAsync(id);
        eval.BurnRateShort.ShouldBe(1.0 / 0.05, Tolerance);
        eval.BurnRateLong.ShouldBe(10.0 / 1010 / 0.05, Tolerance);
        eval.BurnRateShort.ShouldBeGreaterThan(eval.BurnRateLong);
    }

    [TimedFact]
    public async Task Evaluate_MetricGaugeThreshold_BadRecentBucketOverAGoodHistory_BurnsTheShortWindow()
    {
        // Long window [11:32, 12:32): 28 min of the 11:00 hour + six 5-min buckets 12:00-12:25, all compliant, and the
        // 12:30 bucket (2 min so far) in violation: 2 of 60 observed minutes. Short window [12:27, 12:32): 3 compliant
        // minutes of the 12:25 bucket and the 2 violating ones: 40 %.
        await SeedInstrumentAsync(Depth, MetricKind.Gauge);
        await SeedPointsAsync(
        [
            Gauge(Depth, "-", MetricTier.Hourly, At(11, 0), count: 120, sum: 6000, min: 1, max: 99),
            .. Enumerable.Range(0, 6).Select(x => Gauge(Depth, "-", MetricTier.Fine, At(12, 5 * x), count: 10, sum: 500, min: 10, max: 90)),
            Gauge(Depth, "-", MetricTier.Fine, At(12, 30), count: 4, sum: 800, min: 150, max: 250, compacted: false),
        ]);
        var id = await SeedObjectiveAsync(GaugeThreshold(100, SloComparison.AtMost, SloGaugeAggregate.Average, 95));

        await RunAsync(new SpyNotifier());

        var eval = await EvalAsync(id);
        eval.Attainment.ShouldBe(58.0 / 60, Tolerance);
        eval.BurnRateLong.ShouldBe(2.0 / 60 / 0.05, Tolerance);
        eval.BurnRateShort.ShouldBe(0.4 / 0.05, Tolerance);
    }

    [TimedFact]
    public async Task Evaluate_MetricLatency_ThresholdBetweenBounds_ComparesWithBucketUpperBounds()
    {
        await SeedInstrumentAsync(Duration, MetricKind.Histogram);

        // 0.3 lies inside (0.25, 0.5]: those events cannot be told apart from 0.45 ones, so they count as slow.
        await SeedPointsAsync(Histogram(Duration, "-", MetricTier.Fine, At(12, 0), "0.1,0.25,0.5", 50, 40, 10, 0));
        var id = await SeedObjectiveAsync(Latency(0.3, 95));

        await RunAsync(new SpyNotifier());

        var eval = await EvalAsync(id);
        eval.Attainment.ShouldBe(0.9, Tolerance);
        eval.State.ShouldBe(SloState.Breaching);
    }

    [TimedFact]
    public async Task Evaluate_MetricLatency_WindowAcrossFineAndHourlyTiersAndLadders_ProratesAndSplitsEachRow()
    {
        await SeedInstrumentAsync(Duration, MetricKind.Histogram);
        await SeedPointsAsync(
            Histogram(Duration, "-", MetricTier.Hourly, At(10, 0), "0.1,0.25,0.5", 0, 0, 0, 900), // before the window: not read
            Histogram(Duration, "-", MetricTier.Hourly, At(11, 0), "0.1,0.25,0.5", 60, 0, 0, 60), // 28 of 60 min: 28 within, 28 over
            Histogram(Duration, "-", MetricTier.Fine, At(12, 0), "0.1,0.25,0.5", 100, 0, 0, 0), // 100 within
            Histogram(Duration, "-", MetricTier.Fine, At(12, 5), "0.2,1", 10, 2, 0)); // its own ladder: 10 within, 2 over
        var id = await SeedObjectiveAsync(Latency(0.25, 70));

        await RunAsync(new SpyNotifier());

        var eval = await EvalAsync(id);
        eval.Attainment.ShouldBe(138.0 / 168, Tolerance);
        eval.State.ShouldBe(SloState.Healthy);
    }

    [TimedFact]
    public async Task Evaluate_MetricLatency_NoEventsInTheWindow_IsNoData_NoAlert()
    {
        await SeedInstrumentAsync(Duration, MetricKind.Histogram);
        await SeedPointsAsync(Histogram(Duration, "-", MetricTier.Hourly, At(9, 0), "0.1,0.25,0.5", 0, 0, 0, 50));
        var id = await SeedObjectiveAsync(Latency(0.25, 95));
        var spy = new SpyNotifier();

        await RunAsync(spy);

        (await EvalAsync(id)).State.ShouldBe(SloState.NoData);
        spy.Received.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task Evaluate_MetricLatency_RowsWithoutBucketDetail_AreNoData()
    {
        // The rollup keeps the counts but drops the bucket CSV when a sum would not fit the column: no split is possible.
        await SeedInstrumentAsync(Duration, MetricKind.Histogram);
        await SeedPointsAsync(Fine(Duration, "-", At(12, 0), sum: 50, count: 100));
        var id = await SeedObjectiveAsync(Latency(0.25, 95));

        await RunAsync(new SpyNotifier());

        (await EvalAsync(id)).State.ShouldBe(SloState.NoData);
    }

    [TimedFact]
    public async Task Evaluate_MetricLatency_CounterInstrument_IsNoDataAndLoggedAtError()
    {
        await SeedInstrumentAsync("orders.failed", MetricKind.Counter);
        await SeedPointsAsync(Fine("orders.failed", "-", At(12, 0), sum: 5));
        var def = Latency(1, 95);
        def.Dimension = "orders.failed";
        var id = await SeedObjectiveAsync(def);
        var spy = new SpyNotifier();
        var logs = new CapturingLoggerProvider();

        await RunAsync(spy, new FakeTimeProvider(new DateTimeOffset(Now)), LoggerFor(logs));

        (await EvalAsync(id)).State.ShouldBe(SloState.NoData);
        spy.Received.ShouldBeEmpty();
        var error = logs.Logs.Where(x => x.Level == LogLevel.Error).ShouldHaveSingleItem();
        error.Message.ShouldContain("orders.failed");
        error.Message.ShouldContain("Counter");
    }

    [TimedFact]
    public async Task Evaluate_MetricGaugeThreshold_AverageUnderTarget_IsHealthy()
    {
        await SeedInstrumentAsync(Depth, MetricKind.Gauge);
        await SeedPointsAsync(
            Gauge(Depth, "-", MetricTier.Fine, At(12, 0), count: 10, sum: 500, min: 10, max: 90),
            Gauge(Depth, "-", MetricTier.Fine, At(12, 5), count: 10, sum: 800, min: 40, max: 120));
        var id = await SeedObjectiveAsync(GaugeThreshold(100, SloComparison.AtMost, SloGaugeAggregate.Average, 95));
        var spy = new SpyNotifier();

        await RunAsync(spy);

        var eval = await EvalAsync(id);
        eval.State.ShouldBe(SloState.Healthy);
        eval.Attainment.ShouldBe(1, Tolerance);
        spy.Received.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task Evaluate_MetricGaugeThreshold_MaxOverTargetInABucket_BreachesWithLabelFromDefinition()
    {
        await SeedInstrumentAsync(Depth, MetricKind.Gauge);
        await SeedPointsAsync(
            Gauge(Depth, "queue=orders", MetricTier.Fine, At(12, 0), count: 10, sum: 500, min: 10, max: 90),
            Gauge(Depth, "queue=orders", MetricTier.Fine, At(12, 5), count: 10, sum: 800, min: 40, max: 120),
            Gauge(Depth, "queue=other", MetricTier.Fine, At(12, 10), count: 10, sum: 99000, min: 9000, max: 9999));
        var def = GaugeThreshold(100, SloComparison.AtMost, SloGaugeAggregate.Max, 95);
        def.TagFilter = "queue=orders";
        var id = await SeedObjectiveAsync(def);
        var spy = new SpyNotifier();

        await RunAsync(spy);

        // Two observed 5-minute buckets of queue=orders, one with max 120: half the observed time violates.
        var eval = await EvalAsync(id);
        eval.Attainment.ShouldBe(0.5, Tolerance);
        eval.State.ShouldBe(SloState.Breaching);
        var evt = spy.Received.ShouldHaveSingleItem().ShouldBeOfType<SloBreachedEvent>();
        evt.Kind.ShouldBe(SloKind.MetricGaugeThreshold);
        evt.Metric.ShouldBe($"{Depth} [queue=orders] max <= 100 in 95% of the window");
    }

    [TimedFact]
    public async Task Evaluate_MetricGaugeThreshold_AtLeastOnTheMinimum_BreachesWhenABucketDipsBelow()
    {
        await SeedInstrumentAsync("pool.free", MetricKind.Gauge);
        await SeedPointsAsync(
            Gauge("pool.free", "-", MetricTier.Fine, At(12, 0), count: 10, sum: 300, min: 20, max: 40),
            Gauge("pool.free", "-", MetricTier.Fine, At(12, 5), count: 10, sum: 300, min: 2, max: 40));
        var def = GaugeThreshold(5, SloComparison.AtLeast, SloGaugeAggregate.Min, 95);
        def.Dimension = "pool.free";
        var id = await SeedObjectiveAsync(def);

        await RunAsync(new SpyNotifier());

        var eval = await EvalAsync(id);
        eval.Attainment.ShouldBe(0.5, Tolerance);
        eval.State.ShouldBe(SloState.Breaching);
    }

    [TimedFact]
    public async Task Evaluate_MetricGaugeThreshold_TwoProcessesInOneBucket_AreAveragedNotSummed()
    {
        // Two partial rows for one bucket (two processes, or one before compaction): avg 50 and avg 150 make 100, at
        // the target, so it complies; summed they would read 200.
        await SeedInstrumentAsync(Depth, MetricKind.Gauge);
        await SeedPointsAsync(
            Gauge(Depth, "-", MetricTier.Fine, At(12, 0), count: 2, sum: 100, min: 40, max: 60, compacted: false),
            Gauge(Depth, "-", MetricTier.Fine, At(12, 0), count: 2, sum: 300, min: 140, max: 160, compacted: false));
        var avgId = await SeedObjectiveAsync(GaugeThreshold(100, SloComparison.AtMost, SloGaugeAggregate.Average, 95));
        var max = GaugeThreshold(100, SloComparison.AtMost, SloGaugeAggregate.Max, 95);
        max.Name = "max";
        var maxId = await SeedObjectiveAsync(max);

        await RunAsync(new SpyNotifier());

        (await EvalAsync(avgId)).State.ShouldBe(SloState.Healthy);
        (await EvalAsync(maxId)).State.ShouldBe(SloState.Breaching);
    }

    [TimedFact]
    public async Task Evaluate_MetricGaugeThreshold_WindowAcrossFineAndHourlyTiers_WeighsBucketsByTheTimeTheyCover()
    {
        await SeedInstrumentAsync(Depth, MetricKind.Gauge);
        await SeedPointsAsync(
            Gauge(Depth, "-", MetricTier.Hourly, At(10, 0), count: 10, sum: 99000, min: 1, max: 9999), // before the window: not read
            Gauge(Depth, "-", MetricTier.Hourly, At(11, 0), count: 120, sum: 6000, min: 1, max: 99), // avg 50, 28 min in the window
            Gauge(Depth, "-", MetricTier.Fine, At(12, 0), count: 10, sum: 2000, min: 150, max: 250), // avg 200, 5 min, violates
            Gauge(Depth, "-", MetricTier.Fine, At(12, 30), count: 4, sum: 80, min: 10, max: 30, compacted: false)); // still filling: 2 min
        var id = await SeedObjectiveAsync(GaugeThreshold(100, SloComparison.AtMost, SloGaugeAggregate.Average, 80));

        await RunAsync(new SpyNotifier());

        // Observed 28 + 5 + 2 = 35 minutes, 5 of them in violation.
        var eval = await EvalAsync(id);
        eval.Attainment.ShouldBe(30.0 / 35, Tolerance);
        eval.State.ShouldBe(SloState.Healthy);
        eval.BurnRateShort.ShouldBe(0, Tolerance);
    }

    [TimedFact]
    public async Task Evaluate_MetricGaugeThreshold_NoSamplesInTheWindow_IsNoData_NoAlert()
    {
        await SeedInstrumentAsync(Depth, MetricKind.Gauge);
        await SeedPointsAsync(Gauge(Depth, "-", MetricTier.Hourly, At(9, 0), count: 10, sum: 99000, min: 1, max: 9999));
        var id = await SeedObjectiveAsync(GaugeThreshold(100, SloComparison.AtMost, SloGaugeAggregate.Average, 95));
        var spy = new SpyNotifier();

        await RunAsync(spy);

        (await EvalAsync(id)).State.ShouldBe(SloState.NoData);
        spy.Received.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task Evaluate_MetricGaugeThreshold_HistogramInstrument_IsNoDataAndLoggedAtError()
    {
        await SeedInstrumentAsync(Duration, MetricKind.Histogram);
        await SeedPointsAsync(Fine(Duration, "-", At(12, 0), sum: 5, count: 10));
        var def = GaugeThreshold(1, SloComparison.AtMost, SloGaugeAggregate.Average, 95);
        def.Dimension = Duration;
        var id = await SeedObjectiveAsync(def);
        var logs = new CapturingLoggerProvider();

        await RunAsync(new SpyNotifier(), new FakeTimeProvider(new DateTimeOffset(Now)), LoggerFor(logs));

        (await EvalAsync(id)).State.ShouldBe(SloState.NoData);
        var error = logs.Logs.Where(x => x.Level == LogLevel.Error).ShouldHaveSingleItem();
        error.Message.ShouldContain(Duration);
        error.Message.ShouldContain("Histogram");
    }

    private static DateTime At(int hour, int minute) => new(Now.Year, Now.Month, Now.Day, hour, minute, 0, DateTimeKind.Utc);

    private static DateTime Hour(DateTime day, int hour) => new(day.Year, day.Month, day.Day, hour, 0, 0, DateTimeKind.Utc);

    private static SloDefinition Ratio(double target)
        => new() { Name = "Payments succeed", Kind = SloKind.MetricRatio, Dimension = "payment.results", RatioTag = "result", GoodValues = "success", BadValues = "error", TargetValue = target, WindowSeconds = 3600, Enabled = true };

    private static SloDefinition Threshold(string instrument, double max, string? filter)
        => new() { Name = $"{instrument} under {max}", Kind = SloKind.MetricThreshold, Dimension = instrument, TagFilter = filter, TargetValue = max, WindowSeconds = 3600, Enabled = true };

    private const string Duration = "http.server.request.duration";

    private const string Depth = "queue.depth";

    private static SloDefinition GaugeThreshold(double threshold, SloComparison comparison, SloGaugeAggregate aggregate, int percentile)
        => new() { Name = $"depth {aggregate} {comparison} {threshold}", Kind = SloKind.MetricGaugeThreshold, Dimension = Depth, TargetValue = threshold, Comparison = comparison, GaugeAggregate = aggregate, Percentile = percentile, WindowSeconds = 3600, Enabled = true };

    private static MetricPoint Gauge(string instrument, string tags, MetricTier tier, DateTime bucket, long count, double sum, double min, double max, bool compacted = true)
    {
        var point = Point(instrument, tags, tier, bucket, count, sum, compacted);
        point.Min = min;
        point.Max = max;

        return point;
    }

    private static SloDefinition Latency(double threshold, int percentile)
        => new() { Name = $"latency p{percentile} under {threshold}", Kind = SloKind.MetricLatency, Dimension = Duration, TargetValue = threshold, Percentile = percentile, WindowSeconds = 3600, Enabled = true };

    // One histogram row: the bounds CSV and its counts (one per bound plus the overflow bucket).
    private static MetricPoint Histogram(string instrument, string tags, MetricTier tier, DateTime bucket, string bounds, params long[] counts)
    {
        var point = Point(instrument, tags, tier, bucket, counts.Sum(), 0, tier != MetricTier.Fine);
        point.Bounds = bounds;
        point.Buckets = string.Join(',', counts);

        return point;
    }

    private static MetricPoint Fine(string instrument, string tags, DateTime bucket, double sum, long count = 1, bool compacted = false)
        => Point(instrument, tags, MetricTier.Fine, bucket, count, sum, compacted);

    private static MetricPoint Hourly(string instrument, string tags, DateTime bucket, double sum, long count = 1)
        => Point(instrument, tags, MetricTier.Hourly, bucket, count, sum, true);

    private static MetricPoint Daily(string instrument, string tags, DateTime bucket, double sum)
        => Point(instrument, tags, MetricTier.Daily, bucket, 1, sum, true);

    private static MetricPoint Point(string instrument, string tags, MetricTier tier, DateTime bucket, long count, double sum, bool compacted)
        => new() { Instrument = instrument, Tags = tags, Tier = tier, BucketStart = bucket, Count = count, Sum = sum, Compacted = compacted };

    private static MetricPoint WithApplication(MetricPoint point, string application)
    {
        point.Application = application;

        return point;
    }

    private async Task SeedInstrumentAsync(string name, MetricKind kind)
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<MetricInstrument>().Add(new MetricInstrument { Name = name, Meter = "Test.Meter", Kind = kind, FirstSeenAt = Now.AddDays(-30), LastSeenAt = Now });
        await ctx.SaveChangesAsync(Ct);
    }

    private async Task SeedPointsAsync(params MetricPoint[] points)
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<MetricPoint>().AddRange(points);
        await ctx.SaveChangesAsync(Ct);
    }

    private async Task<int> SeedObjectiveAsync(SloDefinition def)
    {
        def.CreatedAt = Now;
        def.UpdatedAt = Now;
        var ctx = _fixture.CreateContext();
        ctx.Set<SloDefinition>().Add(def);
        await ctx.SaveChangesAsync(Ct);

        return def.Id;
    }

    private async Task SetAcknowledgedUntilAsync(int id, DateTime until)
    {
        var ctx = _fixture.CreateContext();
        var eval = await ctx.Set<SloEvaluation>().FirstAsync(x => x.SloDefinitionId == id, Ct);
        eval.AcknowledgedUntil = until;
        await ctx.SaveChangesAsync(Ct);
    }

    private async Task SeedEvaluationAsync(int id, SloState state)
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<SloEvaluation>().Add(new SloEvaluation { SloDefinitionId = id, State = state, LastEvaluatedAt = Now.AddMinutes(-1) });
        await ctx.SaveChangesAsync(Ct);
    }

    private async Task SetStateAsync(int id, SloState state)
    {
        var ctx = _fixture.CreateContext();
        var eval = await ctx.Set<SloEvaluation>().FirstAsync(x => x.SloDefinitionId == id, Ct);
        eval.State = state;
        await ctx.SaveChangesAsync(Ct);
    }

    private async Task<SloEvaluation> EvalAsync(int id)
    {
        var eval = await _fixture.CreateContext().Set<SloEvaluation>().AsNoTracking().FirstOrDefaultAsync(x => x.SloDefinitionId == id, Ct);

        return eval.ShouldNotBeNull();
    }

    private static ILogger<SloEvaluator<TestContext>> LoggerFor(CapturingLoggerProvider logs)
        => new CapturingLoggerFactory(logs).CreateLogger<SloEvaluator<TestContext>>();

    private Task<SloEvaluator<TestContext>> RunAsync(SpyNotifier spy) => RunAsync(spy, new FakeTimeProvider(new DateTimeOffset(Now)));

    private async Task<SloEvaluator<TestContext>> RunAsync(SpyNotifier spy, TimeProvider clock, ILogger<SloEvaluator<TestContext>>? logger = null)
    {
        var evaluator = new SloEvaluator<TestContext>(
            new TestServerContext(_fixture.CreateContext()),
            Options.Create(new WarpServerConfiguration()),
            clock,
            TestNotifiers.SpyDispatcher(spy),
            logger ?? NullLogger<SloEvaluator<TestContext>>.Instance);

        await evaluator.ExecuteAsync(Ct);
        await evaluator.OnCommittedAsync(Ct);

        return evaluator;
    }
}
