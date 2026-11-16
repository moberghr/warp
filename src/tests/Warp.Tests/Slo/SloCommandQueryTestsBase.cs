using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Services;
using Warp.Core.Slo;
using Warp.Dashboard;
using Warp.Dashboard.Endpoints;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.Observability;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Slo;

/// <summary>
/// Database coverage for the SLO read/command services (§8.31): objectives insert/update/delete, acknowledge
/// (which flips a breaching evaluation to Acknowledged), and the query list/detail projecting a definition joined
/// to its 1:1 evaluation. Each test drives one public method (§4.8).
/// </summary>
[GenerateDatabaseTests]
public abstract class SloCommandQueryTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected SloCommandQueryTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task Upsert_Insert_ThenListedNotYetEvaluated()
    {
        var id = await Command().Upsert(Objective("Emails succeed", SloKind.SuccessRate, "SendEmail", 0.99), Ct);

        id.ShouldBeGreaterThan(0);
        var list = await Query().GetObjectives(Ct);
        var row = list.Items.ShouldHaveSingleItem();
        row.Id.ShouldBe(id);
        row.Name.ShouldBe("Emails succeed");
        row.Evaluated.ShouldBeFalse();
        row.State.ShouldBe(SloState.Healthy);
    }

    [TimedFact]
    public async Task Upsert_Update_ChangesFields()
    {
        var id = await Command().Upsert(Objective("X", SloKind.SuccessRate, "SendEmail", 0.99), Ct);

        await Command().Upsert(new SloDefinition { Id = id, Name = "Y", Kind = SloKind.SuccessRate, Dimension = "SendEmail", TargetValue = 0.95, WindowSeconds = 7200, Enabled = false }, Ct);

        var o = await Query().GetObjective(id, Ct);
        o.ShouldNotBeNull();
        o.Name.ShouldBe("Y");
        o.TargetValue.ShouldBe(0.95);
        o.WindowSeconds.ShouldBe(7200);
        o.Enabled.ShouldBeFalse();
    }

    [TimedFact]
    public async Task Delete_RemovesObjectiveAndEvaluation()
    {
        var id = await Command().Upsert(Objective("X", SloKind.SuccessRate, "SendEmail", 0.99), Ct);
        await SeedEvaluationAsync(id, SloState.Healthy);

        (await Command().Delete(id, Ct)).ShouldBeTrue();

        (await Query().GetObjective(id, Ct)).ShouldBeNull();
        (await _fixture.CreateContext().Set<SloEvaluation>().AnyAsync(x => x.SloDefinitionId == id, Ct)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task Acknowledge_BreachingObjective_BecomesAcknowledged()
    {
        var id = await Command().Upsert(Objective("X", SloKind.SuccessRate, "SendEmail", 0.99), Ct);
        await SeedEvaluationAsync(id, SloState.Breaching);

        var until = DateTime.UtcNow.AddHours(1);
        (await Command().Acknowledge(id, until, Ct)).ShouldBeTrue();

        var o = await Query().GetObjective(id, Ct);
        o.ShouldNotBeNull();
        o.State.ShouldBe(SloState.Acknowledged);
        o.AcknowledgedUntil.ShouldNotBeNull();
    }

    [TimedFact]
    public async Task Delete_UnknownId_ReturnsFalse()
        => (await Command().Delete(99999, Ct)).ShouldBeFalse();

    [TimedFact]
    public async Task Upsert_MetricRatio_NewFieldsRoundTripThroughGetObjectiveAndList()
    {
        var def = new SloDefinition { Name = "Payments", Kind = SloKind.MetricRatio, Dimension = "payment.results", TagFilter = "region=eu*", RatioTag = "result", GoodValues = "success,ok*", BadValues = "error", BaselineDays = 7, TargetValue = 0.1, WindowSeconds = 3600, Enabled = true };

        var id = await Command().Upsert(def, Ct);

        var detail = await Query().GetObjective(id, Ct);
        var listed = (await Query().GetObjectives(Ct)).Items.ShouldHaveSingleItem();
        foreach (var o in new[] { detail.ShouldNotBeNull(), listed })
        {
            o.Kind.ShouldBe(SloKind.MetricRatio);
            o.TagFilter.ShouldBe("region=eu*");
            o.RatioTag.ShouldBe("result");
            o.GoodValues.ShouldBe("success,ok*");
            o.BadValues.ShouldBe("error");
            o.BaselineDays.ShouldBe(7);
            o.SecondaryMetric.ShouldBeNull();
            o.SecondaryTagFilter.ShouldBeNull();
        }
    }

    [TimedFact]
    public async Task Upsert_MetricGap_SecondaryFieldsRoundTrip()
    {
        var def = new SloDefinition { Name = "Paid", Kind = SloKind.MetricGap, Dimension = "orders.created", TagFilter = "channel=web", SecondaryMetric = "orders.paid", SecondaryTagFilter = "status=ok", TargetValue = 0, WindowSeconds = 3600, Enabled = true };

        var id = await Command().Upsert(def, Ct);

        var o = (await Query().GetObjective(id, Ct)).ShouldNotBeNull();
        o.TagFilter.ShouldBe("channel=web");
        o.SecondaryMetric.ShouldBe("orders.paid");
        o.SecondaryTagFilter.ShouldBe("status=ok");
        o.BaselineDays.ShouldBeNull();
    }

    [TimedFact]
    public async Task Upsert_UpdateToAnotherKind_PersistsTheClearedFields()
    {
        var id = await Command().Upsert(new SloDefinition { Name = "Payments", Kind = SloKind.MetricRatio, Dimension = "payment.results", RatioTag = "result", GoodValues = "success", BadValues = "error", BaselineDays = 1, TargetValue = 0.1, WindowSeconds = 3600, Enabled = true }, Ct);

        // The endpoint validates before Upsert; validation clears the ratio-only fields for a threshold.
        var update = new SloDefinition { Id = id, Name = "Errors", Kind = SloKind.MetricThreshold, Dimension = "payment.results", TagFilter = "result=error", RatioTag = "result", GoodValues = "success", BadValues = "error", BaselineDays = 1, TargetValue = 5, WindowSeconds = 3600, Enabled = true };
        SloValidation.TryValidate(update, out var error).ShouldBeTrue(error);
        await Command().Upsert(update, Ct);

        var o = (await Query().GetObjective(id, Ct)).ShouldNotBeNull();
        o.Kind.ShouldBe(SloKind.MetricThreshold);
        o.TagFilter.ShouldBe("result=error");
        o.RatioTag.ShouldBeNull();
        o.GoodValues.ShouldBeNull();
        o.BadValues.ShouldBeNull();
        o.BaselineDays.ShouldBeNull();
    }

    [TimedFact]
    public async Task Seeder_TwoMetricObjectivesOnOneInstrumentWithDifferentFilters_BothInsert_AndReseedIsIdempotent()
    {
        static void Configure(SloOptions o) => o
            .AddMetricThreshold("5xx", "http.server.request.duration", 0, tagFilter: "http.response.status_code=5*")
            .AddMetricThreshold("4xx", "http.server.request.duration", 100, tagFilter: "http.response.status_code=4*");

        await SeedFromConfigAsync(Configure);
        await SeedFromConfigAsync(Configure);

        var rows = await _fixture.CreateContext().Set<SloDefinition>().AsNoTracking().OrderBy(x => x.Name).ToListAsync(Ct);
        rows.Count.ShouldBe(2);
        rows[0].TagFilter.ShouldBe("http.response.status_code=4*");
        rows[1].TagFilter.ShouldBe("http.response.status_code=5*");
    }

    [TimedTheory]
    [InlineData(nameof(SloDefinition.TagFilter))]
    [InlineData(nameof(SloDefinition.SecondaryMetric))]
    [InlineData(nameof(SloDefinition.RatioTag))]
    public async Task Seeder_MetricObjectiveFieldEditedInDashboard_ReseedKeepsOneRowWithTheEdit(string field)
    {
        // #332: the dashboard edit changes a field the seeder used to match on; the restart must find the row by name.
        await SeedFromConfigAsync(ConfigureEditable);
        var ctx = _fixture.CreateContext();
        var row = await ctx.Set<SloDefinition>().Where(x => x.Name == EditableNameFor(field)).SingleAsync(Ct);
        ApplyDashboardEdit(row, field);
        await ctx.SaveChangesAsync(Ct);

        await SeedFromConfigAsync(ConfigureEditable);

        var rows = await _fixture.CreateContext().Set<SloDefinition>().AsNoTracking().Where(x => x.Name == EditableNameFor(field)).ToListAsync(Ct);
        rows.Count.ShouldBe(1);
        ReadEditedField(rows[0], field).ShouldBe("edited");
    }

    [TimedFact]
    public async Task Seeder_MetricObjectiveRenamedInDashboard_ReseedMatchesByFieldsAndKeepsOneRow()
    {
        await SeedFromConfigAsync(o => o.AddMetricThreshold("5xx", "http.server.request.duration", 0, tagFilter: "status=5*"));
        var ctx = _fixture.CreateContext();
        var row = await ctx.Set<SloDefinition>().SingleAsync(Ct);
        row.Name = "server errors";
        await ctx.SaveChangesAsync(Ct);

        await SeedFromConfigAsync(o => o.AddMetricThreshold("5xx", "http.server.request.duration", 0, tagFilter: "status=5*"));

        var rows = await _fixture.CreateContext().Set<SloDefinition>().AsNoTracking().ToListAsync(Ct);
        rows.Count.ShouldBe(1);
        rows[0].Name.ShouldBe("server errors");
    }

    [TimedFact]
    public void AddMetric_SameKindNameAndApplicationTwice_Throws()
    {
        var options = new SloOptions().AddMetricThreshold("5xx", "a", 0);

        Should.Throw<ArgumentException>(() => options.AddMetricThreshold("5xx", "b", 0));
    }

    [TimedTheory]
    [InlineData("5XX", null)]
    [InlineData("5xx", "API")]
    public void AddMetric_NameOrApplicationDifferingOnlyByCase_Throws(string name, string? application)
    {
        // The seeder matches names in SQL: on SQL Server's case-insensitive collation these would be one objective.
        var options = new SloOptions().AddMetricThreshold("5xx", "a", 0, application: application is null ? null : "api");

        Should.Throw<ArgumentException>(() => options.AddMetricThreshold(name, "b", 0, application: application));
    }

    [TimedFact]
    public async Task Seeder_TwoReplicasStartAtOnce_SeedOneCopy()
    {
        // Two replicas with the same config. The first is pinned inside the seed lock after its existence checks
        // (BarrierSignal, N=2); the second must not get to check until the first commits, or both insert.
        static void Configure(SloOptions o) => o
            .AddMetricThreshold("5xx", "http.server.request.duration", 0, tagFilter: "status=5*")
            .AddObjective(SloKind.SuccessRate, "SendEmail", 0.99);
        var barrier = new BarrierSignal();

        async Task Pin(CancellationToken ct)
        {
            barrier.Running.Release();
            await barrier.CanFinish.WaitAsync(ct);
        }

        var first = SeedFromConfigAsync(Configure, Pin);
        (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue();
        var second = SeedFromConfigAsync(Configure, Pin);

        // The second replica is held at the lock, so it cannot reach the pin while the first is inside.
        (await barrier.Running.WaitAsync(TimeSpan.FromMilliseconds(500), Ct)).ShouldBeFalse();
        barrier.CanFinish.Release(2);
        await Task.WhenAll(first, second);

        var rows = await _fixture.CreateContext().Set<SloDefinition>().AsNoTracking().OrderBy(x => x.Kind).ToListAsync(Ct);
        rows.Select(x => x.Kind).ShouldBe([SloKind.SuccessRate, SloKind.MetricThreshold]);
    }

    [TimedFact]
    public async Task Seeder_StoredObjectiveWithTheSameNameButOtherFields_IsKeptAndTheSkipIsLogged()
    {
        await SeedFromConfigAsync(o => o.AddMetricThreshold("5xx", "http.server.request.duration", 0, tagFilter: "status=5*"));
        var logs = new CapturingLoggerProvider();

        await SeedFromConfigAsync(o => o.AddMetricThreshold("5xx", "http.server.duration", 0, tagFilter: "code=5*"), logs: logs);

        var row = await _fixture.CreateContext().Set<SloDefinition>().AsNoTracking().SingleAsync(Ct);
        row.Dimension.ShouldBe("http.server.request.duration");
        var log = logs.Logs.ShouldHaveSingleItem();
        log.Level.ShouldBe(LogLevel.Information);
        log.Fields["Name"].ShouldBe("5xx");
        log.Fields["Kind"].ShouldBe(SloKind.MetricThreshold);
        log.Fields["Fields"].ShouldBe("Dimension, TagFilter");

        // Field names only: never the filter's tag values (§1.2).
        log.Message.ShouldNotContain("code=5*");
    }

    [TimedFact]
    public async Task Upsert_EditOfADeletedObjective_ReturnsZeroAndRecreatesNothing()
    {
        var id = await Command().Upsert(Objective("X", SloKind.SuccessRate, "SendEmail", 0.99), Ct);
        (await Command().Delete(id, Ct)).ShouldBeTrue();

        var result = await Command().Upsert(new SloDefinition { Id = id, Name = "Y", Kind = SloKind.SuccessRate, Dimension = "SendEmail", TargetValue = 0.95, WindowSeconds = 3600 }, Ct);

        result.ShouldBe(0);
        (await _fixture.CreateContext().Set<SloDefinition>().AnyAsync(Ct)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task PostSlo_EditOfADeletedObjective_Returns404AndRecreatesNothing()
    {
        var id = await Command().Upsert(Objective("X", SloKind.SuccessRate, "SendEmail", 0.99), Ct);
        (await Command().Delete(id, Ct)).ShouldBeTrue();

        var status = await PostObjectiveAsync(new { id, name = "Y", kind = (int)SloKind.SuccessRate, dimension = "SendEmail", targetValue = 0.99, windowSeconds = 3600, enabled = true });

        status.ShouldBe(HttpStatusCode.NotFound);
        (await _fixture.CreateContext().Set<SloDefinition>().AnyAsync(Ct)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task Seeder_GapWithGaugeSecondaryInstrument_IsSkipped_AndTheValidSeedStillInserts()
    {
        // The seeder runs the endpoint's validator: a gauge has no event count, as a gap's secondary too.
        await SeedInstrumentAsync("orders.started", MetricKind.Counter);
        await SeedInstrumentAsync("queue.depth", MetricKind.Gauge);

        await SeedFromConfigAsync(o => o
            .AddMetricGap("gauge gap", "orders.started", "queue.depth", 5)
            .AddMetricThreshold("orders under 10", "orders.started", 10));

        var row = await _fixture.CreateContext().Set<SloDefinition>().AsNoTracking().SingleAsync(Ct);
        row.Name.ShouldBe("orders under 10");
    }

    [TimedFact]
    public async Task PostSlo_SevenDayBaselineBeyondHourlyRetention_Returns400()
    {
        // R29: 7 days of hourly retention is short of 7 days + the 1-hour window + 1 hour.
        var status = await PostObjectiveAsync(MetricRequest(SloKind.MetricRatio, "payment.results", 0.1, "result", "success", "error", baselineDays: 7));

        status.ShouldBe(HttpStatusCode.BadRequest);
        (await _fixture.CreateContext().Set<SloDefinition>().AnyAsync(Ct)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task PostSlo_GaugeInstrument_Returns400()
    {
        await SeedInstrumentAsync("queue.depth", MetricKind.Gauge);

        var status = await PostObjectiveAsync(MetricRequest(SloKind.MetricThreshold, "queue.depth", 10));

        status.ShouldBe(HttpStatusCode.BadRequest);
        (await _fixture.CreateContext().Set<SloDefinition>().AnyAsync(Ct)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task PostSlo_GapWithGaugeSecondaryInstrument_Returns400()
    {
        await SeedInstrumentAsync("orders.started", MetricKind.Counter);
        await SeedInstrumentAsync("queue.depth", MetricKind.Gauge);

        var status = await PostObjectiveAsync(MetricRequest(SloKind.MetricGap, "orders.started", 5, secondaryMetric: "queue.depth"));

        status.ShouldBe(HttpStatusCode.BadRequest);
        (await _fixture.CreateContext().Set<SloDefinition>().AnyAsync(Ct)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task PostSlo_ValidMetricRatio_PersistsTheNewFields()
    {
        await SeedInstrumentAsync("payment.results", MetricKind.Counter);

        var status = await PostObjectiveAsync(MetricRequest(SloKind.MetricRatio, "payment.results", 0.1, "result", "success", "error", baselineDays: 1, tagFilter: "step=authorize"));

        status.ShouldBe(HttpStatusCode.OK);
        var row = await _fixture.CreateContext().Set<SloDefinition>().AsNoTracking().SingleAsync(Ct);
        row.TagFilter.ShouldBe("step=authorize");
        row.RatioTag.ShouldBe("result");
        row.GoodValues.ShouldBe("success");
        row.BadValues.ShouldBe("error");
        row.BaselineDays.ShouldBe(1);
    }

    [TimedFact]
    public async Task Seeder_LatencyTierAddedInALaterDeploy_IsSeeded_AndReseedIsIdempotent()
    {
        // Two tiers on one histogram differ only by threshold; the field match must not take the new one for the old.
        await SeedFromConfigAsync(o => o.AddMetricLatency("p95 warn", "http.server.request.duration", 0.5, tagFilter: "http.route=/api/x"));

        static void BothTiers(SloOptions o) => o
            .AddMetricLatency("p95 warn", "http.server.request.duration", 0.5, tagFilter: "http.route=/api/x")
            .AddMetricLatency("p95 page", "http.server.request.duration", 1, tagFilter: "http.route=/api/x");
        await SeedFromConfigAsync(BothTiers);
        await SeedFromConfigAsync(BothTiers);

        var rows = await _fixture.CreateContext().Set<SloDefinition>().AsNoTracking().OrderBy(x => x.TargetValue).ToListAsync(Ct);
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(x => x.Kind == SloKind.MetricLatency && x.Percentile == 95 && x.TagFilter == "http.route=/api/x");
        rows[0].Name.ShouldBe("p95 warn");
        rows[1].Name.ShouldBe("p95 page");
    }

    [TimedFact]
    public async Task Seeder_LatencyObjectiveRenamedInDashboard_ReseedMatchesByFieldsAndKeepsOneRow()
    {
        await SeedFromConfigAsync(o => o.AddMetricLatency("p99", "http.server.request.duration", 2.5, percentile: 99));
        var ctx = _fixture.CreateContext();
        var row = await ctx.Set<SloDefinition>().SingleAsync(Ct);
        row.Name.ShouldBe("p99");
        row.Percentile.ShouldBe(99);
        row.Name = "slow requests";
        await ctx.SaveChangesAsync(Ct);

        await SeedFromConfigAsync(o => o.AddMetricLatency("p99", "http.server.request.duration", 2.5, percentile: 99));

        var rows = await _fixture.CreateContext().Set<SloDefinition>().AsNoTracking().ToListAsync(Ct);
        rows.ShouldHaveSingleItem().Name.ShouldBe("slow requests");
    }

    [TimedFact]
    public async Task Seeder_LatencyOnCounterInstrument_IsSkipped_AndTheValidSeedStillInserts()
    {
        await SeedInstrumentAsync("orders.started", MetricKind.Counter);
        await SeedInstrumentAsync("http.server.request.duration", MetricKind.Histogram);

        await SeedFromConfigAsync(o => o
            .AddMetricLatency("counter latency", "orders.started", 1)
            .AddMetricLatency("request latency", "http.server.request.duration", 1));

        var row = await _fixture.CreateContext().Set<SloDefinition>().AsNoTracking().SingleAsync(Ct);
        row.Name.ShouldBe("request latency");
    }

    [TimedFact]
    public void AddMetricLatency_InvalidPercentileOrDuplicateName_Throws()
    {
        Should.Throw<ArgumentException>(() => new SloOptions().AddMetricLatency("p", "h", 1, percentile: 100));

        var options = new SloOptions().AddMetricLatency("p95", "h", 1);
        Should.Throw<ArgumentException>(() => options.AddMetricLatency("p95", "h", 2));
    }

    [TimedTheory]
    [InlineData(MetricKind.Counter)]
    [InlineData(MetricKind.UpDownCounter)]
    [InlineData(MetricKind.Gauge)]
    public async Task PostSlo_LatencyOnNonHistogramInstrument_Returns400(MetricKind kind)
    {
        await SeedInstrumentAsync("some.instrument", kind);

        var status = await PostObjectiveAsync(MetricRequest(SloKind.MetricLatency, "some.instrument", 1, percentile: 95));

        status.ShouldBe(HttpStatusCode.BadRequest);
        (await _fixture.CreateContext().Set<SloDefinition>().AnyAsync(Ct)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task PostSlo_ValidMetricLatency_PersistsThresholdPercentileAndFilter()
    {
        await SeedInstrumentAsync("http.server.request.duration", MetricKind.Histogram);

        var status = await PostObjectiveAsync(MetricRequest(SloKind.MetricLatency, "http.server.request.duration", 0.25, tagFilter: "http.route=/api/x", percentile: 99));

        status.ShouldBe(HttpStatusCode.OK);
        var row = await _fixture.CreateContext().Set<SloDefinition>().AsNoTracking().SingleAsync(Ct);
        row.Kind.ShouldBe(SloKind.MetricLatency);
        row.TargetValue.ShouldBe(0.25);
        row.Percentile.ShouldBe(99);
        row.TagFilter.ShouldBe("http.route=/api/x");
    }

    [TimedTheory]
    [InlineData("threshold")]
    [InlineData("comparison")]
    [InlineData("aggregate")]
    public async Task Seeder_GaugeTierDifferingInExactlyOneField_IsSeededBesideTheFirst_AndReseedIsIdempotent(string field)
    {
        // Each of the three gauge match fields alone must tell two tiers on one instrument apart: a tier added in a later
        // deploy that differs only there is seeded, not matched to the first.
        await SeedInstrumentAsync("queue.depth", MetricKind.Gauge);
        static void First(SloOptions o) => o.AddMetricGaugeThreshold("depth base", "queue.depth", 100);
        void Both(SloOptions o) => _ = field switch
        {
            "threshold" => o.AddMetricGaugeThreshold("depth base", "queue.depth", 100).AddMetricGaugeThreshold("depth tier", "queue.depth", 200),
            "comparison" => o.AddMetricGaugeThreshold("depth base", "queue.depth", 100).AddMetricGaugeThreshold("depth tier", "queue.depth", 100, comparison: SloComparison.AtLeast),
            _ => o.AddMetricGaugeThreshold("depth base", "queue.depth", 100).AddMetricGaugeThreshold("depth tier", "queue.depth", 100, aggregate: SloGaugeAggregate.Max),
        };

        await SeedFromConfigAsync(First);
        await SeedFromConfigAsync(Both);
        await SeedFromConfigAsync(Both);

        var rows = await _fixture.CreateContext().Set<SloDefinition>().AsNoTracking().OrderBy(x => x.Name).ToListAsync(Ct);
        rows.Select(x => x.Name).ShouldBe(["depth base", "depth tier"]);
        var (baseTier, tier) = (rows[0], rows[1]);
        (tier.TargetValue, tier.Comparison, tier.GaugeAggregate).ShouldBe(field switch
        {
            "threshold" => (200d, (SloComparison?)SloComparison.AtMost, (SloGaugeAggregate?)SloGaugeAggregate.Average),
            "comparison" => (100d, SloComparison.AtLeast, SloGaugeAggregate.Average),
            _ => (100d, SloComparison.AtMost, SloGaugeAggregate.Max),
        });
        (baseTier.TargetValue, baseTier.Comparison, baseTier.GaugeAggregate).ShouldBe((100d, (SloComparison?)SloComparison.AtMost, (SloGaugeAggregate?)SloGaugeAggregate.Average));
    }

    [TimedFact]
    public async Task Seeder_GaugeThresholdTiers_BothInsertWithTheirOptions_AndReseedIsIdempotent()
    {
        await SeedInstrumentAsync("queue.depth", MetricKind.Gauge);

        static void Configure(SloOptions o) => o
            .AddMetricGaugeThreshold("depth warn", "queue.depth", 100)
            .AddMetricGaugeThreshold("depth page", "queue.depth", 500, aggregate: SloGaugeAggregate.Max, percentile: 99);

        await SeedFromConfigAsync(o => o.AddMetricGaugeThreshold("depth warn", "queue.depth", 100));
        await SeedFromConfigAsync(Configure);
        await SeedFromConfigAsync(Configure);

        var rows = await _fixture.CreateContext().Set<SloDefinition>().AsNoTracking().OrderBy(x => x.TargetValue).ToListAsync(Ct);
        rows.Count.ShouldBe(2);
        rows[0].Name.ShouldBe("depth warn");
        rows[0].Comparison.ShouldBe(SloComparison.AtMost);
        rows[0].GaugeAggregate.ShouldBe(SloGaugeAggregate.Average);
        rows[0].Percentile.ShouldBe(95);
        rows[1].Name.ShouldBe("depth page");
        rows[1].GaugeAggregate.ShouldBe(SloGaugeAggregate.Max);
        rows[1].Percentile.ShouldBe(99);
    }

    [TimedFact]
    public async Task Seeder_GaugeThresholdOnCounterInstrument_IsSkipped_AndTheValidSeedStillInserts()
    {
        await SeedInstrumentAsync("orders.started", MetricKind.Counter);
        await SeedInstrumentAsync("queue.depth", MetricKind.Gauge);

        await SeedFromConfigAsync(o => o
            .AddMetricGaugeThreshold("counter level", "orders.started", 10)
            .AddMetricGaugeThreshold("depth", "queue.depth", 10, SloComparison.AtLeast, SloGaugeAggregate.Min));

        var row = await _fixture.CreateContext().Set<SloDefinition>().AsNoTracking().SingleAsync(Ct);
        row.Name.ShouldBe("depth");
        row.Comparison.ShouldBe(SloComparison.AtLeast);
        row.GaugeAggregate.ShouldBe(SloGaugeAggregate.Min);
    }

    [TimedTheory]
    [InlineData(MetricKind.Counter)]
    [InlineData(MetricKind.UpDownCounter)]
    [InlineData(MetricKind.Histogram)]
    public async Task PostSlo_GaugeThresholdOnNonGaugeInstrument_Returns400(MetricKind kind)
    {
        await SeedInstrumentAsync("some.instrument", kind);

        var status = await PostObjectiveAsync(MetricRequest(SloKind.MetricGaugeThreshold, "some.instrument", 10));

        status.ShouldBe(HttpStatusCode.BadRequest);
        (await _fixture.CreateContext().Set<SloDefinition>().AnyAsync(Ct)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task PostSlo_ValidGaugeThreshold_PersistsComparisonAggregateAndPercentile_AndReadsThemBack()
    {
        await SeedInstrumentAsync("queue.depth", MetricKind.Gauge);

        var status = await PostObjectiveAsync(MetricRequest(SloKind.MetricGaugeThreshold, "queue.depth", 5, percentile: 90, comparison: SloComparison.AtLeast, gaugeAggregate: SloGaugeAggregate.Min));

        status.ShouldBe(HttpStatusCode.OK);
        var row = await _fixture.CreateContext().Set<SloDefinition>().AsNoTracking().SingleAsync(Ct);
        row.Comparison.ShouldBe(SloComparison.AtLeast);
        row.GaugeAggregate.ShouldBe(SloGaugeAggregate.Min);
        row.Percentile.ShouldBe(90);
        var model = (await Query().GetObjective(row.Id, Ct)).ShouldNotBeNull();
        model.Comparison.ShouldBe(SloComparison.AtLeast);
        model.GaugeAggregate.ShouldBe(SloGaugeAggregate.Min);
    }

    [TimedFact]
    public async Task Upsert_GaugeThresholdChangedToThreshold_ClearsTheGaugeOptions()
    {
        var id = await Command().Upsert(new SloDefinition { Name = "Depth", Kind = SloKind.MetricGaugeThreshold, Dimension = "queue.depth", Comparison = SloComparison.AtLeast, GaugeAggregate = SloGaugeAggregate.Max, Percentile = 99, TargetValue = 5, WindowSeconds = 3600, Enabled = true }, Ct);

        var update = new SloDefinition { Id = id, Name = "Depth", Kind = SloKind.MetricThreshold, Dimension = "orders.failed", Comparison = SloComparison.AtLeast, GaugeAggregate = SloGaugeAggregate.Max, Percentile = 99, TargetValue = 5, WindowSeconds = 3600, Enabled = true };
        SloValidation.TryValidate(update, out var error).ShouldBeTrue(error);
        await Command().Upsert(update, Ct);

        var o = (await Query().GetObjective(id, Ct)).ShouldNotBeNull();
        o.Comparison.ShouldBeNull();
        o.GaugeAggregate.ShouldBeNull();
        o.Percentile.ShouldBeNull();
    }

    private static void ConfigureEditable(SloOptions o) => o
        .AddMetricThreshold("threshold", "http.server.request.duration", 0, tagFilter: "status=5*")
        .AddMetricGap("gap", "payments.received", "payments.processed", 0)
        .AddMetricRatio("ratio", "payment.results", "result", "success", "error", 0.99);

    private static string EditableNameFor(string field) => field switch
    {
        nameof(SloDefinition.TagFilter) => "threshold",
        nameof(SloDefinition.SecondaryMetric) => "gap",
        _ => "ratio",
    };

    private static void ApplyDashboardEdit(SloDefinition row, string field)
    {
        switch (field)
        {
            case nameof(SloDefinition.TagFilter):
                row.TagFilter = "edited";
                break;
            case nameof(SloDefinition.SecondaryMetric):
                row.SecondaryMetric = "edited";
                break;
            default:
                row.RatioTag = "edited";
                break;
        }
    }

    private static string? ReadEditedField(SloDefinition row, string field) => field switch
    {
        nameof(SloDefinition.TagFilter) => row.TagFilter,
        nameof(SloDefinition.SecondaryMetric) => row.SecondaryMetric,
        _ => row.RatioTag,
    };

    private static SloDefinition Objective(string name, SloKind kind, string dimension, double target)
        => new() { Name = name, Kind = kind, Dimension = dimension, TargetValue = target, WindowSeconds = 3600, Enabled = true };

    private async Task SeedEvaluationAsync(int id, SloState state)
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<SloEvaluation>().Add(new SloEvaluation { SloDefinitionId = id, State = state, BudgetRemaining = state == SloState.Breaching ? -0.5 : 1.0, LastEvaluatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync(Ct);
    }

    private SloCommandService<TestContext> Command()
        => new(_fixture.CreateContext(), TimeProvider.System, NullLogger<SloCommandService<TestContext>>.Instance);

    private SloQueryService<TestContext> Query() => new(_fixture.CreateContext());

    private async Task SeedFromConfigAsync(Action<SloOptions> configure, Func<CancellationToken, Task>? beforeSave = null, CapturingLoggerProvider? logs = null)
    {
        var options = new SloOptions();
        configure(options);
        var services = new ServiceCollection();
        services.AddScoped(_ => _fixture.CreateContext());
        services.AddScoped<IMetricQueryService>(_ => new MetricQueryService<TestContext>(_fixture.CreateContext(), Options.Create(new WarpConfiguration())));
        services.AddSingleton(TestTasks.QueriesFor(_fixture.CreateContext()));
        await using var provider = services.BuildServiceProvider();
        var logger = logs is null
            ? NullLogger<SloSeeder<TestContext>>.Instance
            : new CapturingLoggerFactory(logs).CreateLogger<SloSeeder<TestContext>>();
        var seeder = new SloSeeder<TestContext>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            TimeProvider.System,
            Options.Create(new WarpConfiguration()),
            logger)
        {
            BeforeSave = beforeSave,
        };

        await seeder.StartAsync(Ct);
    }

    private async Task<HttpStatusCode> PostObjectiveAsync(object body)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.WebHost.UseDefaultServiceProvider(o => o.ValidateScopes = true);
        builder.Services.AddSingleton(Options.Create(new WarpConfiguration { HourlyStatisticsRetention = TimeSpan.FromDays(7) }));
        builder.Services.AddScoped<IMetricQueryService>(_ => new MetricQueryService<TestContext>(_fixture.CreateContext(), Options.Create(new WarpConfiguration())));
        builder.Services.AddScoped<ISloCommandService>(_ => Command());

        await using var app = builder.Build();
        app.MapWarpApiEndpoints(new WarpDashboardOptions(), []);
        await app.StartAsync(CancellationToken.None);
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/warp/api/slo", body, Ct);
        await app.StopAsync(CancellationToken.None);

        return response.StatusCode;
    }

    private static object MetricRequest(SloKind kind, string instrument, double target, string? ratioTag = null, string? goodValues = null, string? badValues = null, int? baselineDays = null, string? tagFilter = null, string? secondaryMetric = null, int? percentile = null, SloComparison? comparison = null, SloGaugeAggregate? gaugeAggregate = null)
        => new
        {
            id = 0,
            name = $"{kind} {instrument}",
            kind = (int)kind,
            dimension = instrument,
            application = (string?)null,
            targetValue = target,
            percentile,
            windowSeconds = 3600,
            enabled = true,
            tagFilter,
            secondaryMetric,
            secondaryTagFilter = (string?)null,
            ratioTag,
            goodValues,
            badValues,
            baselineDays,
            comparison = (int?)comparison,
            gaugeAggregate = (int?)gaugeAggregate,
        };

    private async Task SeedInstrumentAsync(string name, MetricKind kind)
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<MetricInstrument>().Add(new MetricInstrument { Name = name, Meter = "Test.Meter", Kind = kind, FirstSeenAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync(Ct);
    }
}
