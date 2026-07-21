using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Endpoints;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Services;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Endpoints;

/// <summary>
/// Dashboard-backend coverage for the inbound-endpoint query service — the inbound mirror of
/// <c>AdapterEndpointTestsBase</c>. Counts, error rates and average latency come from the merged
/// <see cref="Statistic"/> / <see cref="Counter"/> aggregates (so they survive <see cref="EndpointCallLog"/>
/// deletion); last-failure timestamps and the recent-calls list read the retained log rows. The endpoint
/// LIST is discovered from the aggregate keys — there is no endpoint-definition table. Each test drives
/// exactly one public method (§4.8); the detail/call-detail id is taken from
/// <c>GetEndpoints().Single().Id</c> rather than hand-encoded.
/// </summary>
[GenerateDatabaseTests]
public abstract class EndpointQueryTestsBase : IAsyncLifetime
{
    private static readonly string Route = EndpointCounterKeys.NormalizeRoute("GET", "/orders");

    private readonly IDatabaseFixture _fixture;

    protected EndpointQueryTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task GetEndpoints_ReturnsItemWithAggregatedStats()
    {
        var seed = _fixture.CreateContext();
        AddOutcomeCounters(seed, EndpointCounterKeys.Total(Route, "success"), 2);
        AddOutcomeCounters(seed, EndpointCounterKeys.Total(Route, "failed"), 1);
        AddDurationCounter(seed, EndpointCounterKeys.Total(Route, EndpointCounterKeys.DurationToken), 60);
        seed.Set<EndpointCallLog>().Add(CallLog(AdapterCallOutcome.Success, durationMs: 10));
        seed.Set<EndpointCallLog>().Add(CallLog(AdapterCallOutcome.Success, durationMs: 20));
        seed.Set<EndpointCallLog>().Add(CallLog(AdapterCallOutcome.Failed, durationMs: 30));
        await seed.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var list = await CreateService().GetEndpoints(Xunit.TestContext.Current.CancellationToken);

        var item = list.ShouldHaveSingleItem();
        item.Id.ShouldNotBeNullOrEmpty();
        item.Method.ShouldBe("GET");
        item.RouteTemplate.ShouldBe("/orders");
        item.TotalCalls.ShouldBe(3);
        item.ErrorCount.ShouldBe(1);
        item.ErrorRate.ShouldBe(1d / 3d, 0.001);
        item.AvgDurationMs.ShouldBe(20);
    }

    [TimedFact]
    public async Task GetEndpointDetail_ReturnsGroupsRecentCallsAndLastFailure()
    {
        var failedAt = DateTime.UtcNow;

        var seed = _fixture.CreateContext();
        AddOutcomeCounters(seed, EndpointCounterKeys.Total(Route, "success"), 2);
        AddOutcomeCounters(seed, EndpointCounterKeys.Total(Route, "failed"), 1);
        AddDurationCounter(seed, EndpointCounterKeys.Total(Route, EndpointCounterKeys.DurationToken), 60);
        AddOutcomeCounters(seed, EndpointCounterKeys.Group(Route, "shop-eu", "success"), 1);
        AddOutcomeCounters(seed, EndpointCounterKeys.Group(Route, "shop-eu", "failed"), 1);
        seed.Set<EndpointCallLog>().Add(CallLog(AdapterCallOutcome.Success, durationMs: 10, group: "shop-eu", timestamp: failedAt.AddMinutes(-1), remoteIp: "10.0.0.1", user: "alice"));
        seed.Set<EndpointCallLog>().Add(CallLog(AdapterCallOutcome.Failed, durationMs: 20, group: "shop-eu", timestamp: failedAt));
        seed.Set<EndpointCallLog>().Add(CallLog(AdapterCallOutcome.Success, durationMs: 30, timestamp: failedAt.AddMinutes(-2)));
        await seed.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var id = (await CreateService().GetEndpoints(Xunit.TestContext.Current.CancellationToken)).Single().Id;

        var detail = await CreateService().GetEndpointDetail(id, Xunit.TestContext.Current.CancellationToken);

        detail.ShouldNotBeNull();
        detail.Method.ShouldBe("GET");
        detail.RouteTemplate.ShouldBe("/orders");
        detail.TotalCalls.ShouldBe(3);
        detail.ErrorCount.ShouldBe(1);

        var group = detail.Groups.ShouldHaveSingleItem();
        group.Group.ShouldBe("shop-eu");
        group.Calls.ShouldBe(2);
        group.Errors.ShouldBe(1);
        group.LastFailureAt.ShouldNotBeNull();
        group.LastFailureAt.Value.ShouldBe(failedAt, TimeSpan.FromSeconds(1));

        detail.RecentCalls.Count.ShouldBe(3);
        var withCaller = detail.RecentCalls.Single(x => string.Equals(x.RemoteIp, "10.0.0.1", StringComparison.Ordinal));
        withCaller.User.ShouldBe("alice");
    }

    [TimedFact]
    public async Task GetEndpointDetail_UnknownId_ReturnsNull()
    {
        var detail = await CreateService().GetEndpointDetail("bm90LXJlYWw", Xunit.TestContext.Current.CancellationToken);

        detail.ShouldBeNull();
    }

    [TimedFact]
    public async Task GetCallDetail_ReturnsCapturedPayloadsAndCallerMetadata()
    {
        var call = CallLog(AdapterCallOutcome.Failed, durationMs: 12, remoteIp: "10.0.0.9", user: "bob");
        call.RequestHeaders = "X-Api-Key: ***";
        call.ResponseHeaders = "Content-Type: application/json";
        call.RequestBody = "request-body";
        call.ResponseBody = "response-body";
        call.ExceptionType = typeof(InvalidOperationException).FullName;
        call.ExceptionMessage = "boom";
        var traceId = Guid.NewGuid();
        call.TraceId = traceId;
        call.TagsJson = "{\"userId\":\"bob\"}";

        var seed = _fixture.CreateContext();
        AddOutcomeCounters(seed, EndpointCounterKeys.Total(Route, "failed"), 1);
        seed.Set<EndpointCallLog>().Add(call);

        // A job spawned during the request shares its trace id — the request→jobs drill-down.
        seed.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Type = "SendEmail",
            Queue = "default",
            CurrentState = State.Completed,
            TraceId = traceId,
            ScheduleTime = DateTime.UtcNow,
        });
        await seed.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var id = (await CreateService().GetEndpoints(Xunit.TestContext.Current.CancellationToken)).Single().Id;

        var detail = await CreateService().GetCallDetail(id, call.Id, Xunit.TestContext.Current.CancellationToken);

        detail.ShouldNotBeNull();
        detail.Id.ShouldBe(call.Id);
        detail.RequestHeaders.ShouldBe("X-Api-Key: ***");
        detail.ResponseHeaders.ShouldBe("Content-Type: application/json");
        detail.RequestBody.ShouldBe("request-body");
        detail.ResponseBody.ShouldBe("response-body");
        detail.ExceptionType.ShouldBe(typeof(InvalidOperationException).FullName);
        detail.ExceptionMessage.ShouldBe("boom");
        detail.RemoteIp.ShouldBe("10.0.0.9");
        detail.User.ShouldBe("bob");
        detail.TraceId.ShouldBe(traceId);
        detail.TagsJson.ShouldBe("{\"userId\":\"bob\"}");

        var relatedJob = detail.RelatedJobs.ShouldHaveSingleItem();
        relatedJob.Type.ShouldBe("SendEmail");
        relatedJob.State.ShouldBe(State.Completed);
    }

    [TimedFact]
    public async Task GetCallDetail_UnknownId_ReturnsNull()
    {
        var seed = _fixture.CreateContext();
        AddOutcomeCounters(seed, EndpointCounterKeys.Total(Route, "success"), 1);
        await seed.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var id = (await CreateService().GetEndpoints(Xunit.TestContext.Current.CancellationToken)).Single().Id;

        var detail = await CreateService().GetCallDetail(id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        detail.ShouldBeNull();
    }

    [TimedFact]
    public async Task GetEndpoints_Latency_FromAggregates_SurvivesLogDeletion()
    {
        // Average latency is derived from the duration-sum ÷ count aggregates, not raw EndpointCallLog rows —
        // so it persists after logs are swept. No call-log rows are seeded here (simulating deleted logs).
        var seed = _fixture.CreateContext();
        AddOutcomeCounters(seed, EndpointCounterKeys.Total(Route, "success"), 3);
        AddDurationCounter(seed, EndpointCounterKeys.Total(Route, EndpointCounterKeys.DurationToken), 30);
        await seed.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var list = await CreateService().GetEndpoints(Xunit.TestContext.Current.CancellationToken);

        var item = list.ShouldHaveSingleItem();
        item.TotalCalls.ShouldBe(3);
        item.AvgDurationMs.ShouldBe(10);
    }

    [TimedFact]
    public async Task GetEndpointDetail_OtherEndpointsPresent_ScopesStatsToRequestedRoute()
    {
        // The detail load is scoped to "endpoint:{route}:" so it never materialises every endpoint's stat
        // rows. This pins that the scoping stays CORRECT: a second endpoint's totals/histogram must not bleed
        // into the requested route's detail (totals + percentiles stay clean).
        var other = EndpointCounterKeys.NormalizeRoute("POST", "/charge");

        var seed = _fixture.CreateContext();
        AddOutcomeCounters(seed, EndpointCounterKeys.Total(Route, "success"), 2);
        AddBucketCounter(seed, EndpointCounterKeys.Pct(Route, 50), 2);

        // "/charge" data that must be excluded from the "/orders" detail.
        AddOutcomeCounters(seed, EndpointCounterKeys.Total(other, "failed"), 99);
        AddBucketCounter(seed, EndpointCounterKeys.Pct(other, 10000), 99);
        await seed.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var id = (await CreateService().GetEndpoints(Xunit.TestContext.Current.CancellationToken))
            .Single(x => string.Equals(x.RouteTemplate, "/orders", StringComparison.Ordinal)).Id;

        var detail = await CreateService().GetEndpointDetail(id, Xunit.TestContext.Current.CancellationToken);

        detail.ShouldNotBeNull();
        detail.TotalCalls.ShouldBe(2);
        detail.ErrorCount.ShouldBe(0);

        // p99 stays in the 50ms bucket — "/charge"'s 99 calls in the 10000ms bucket must not shift it.
        detail.P99DurationMs.ShouldBe(50);
    }

    [TimedFact]
    public async Task GetEndpointDetail_Percentiles_ComputedFromHistogramBuckets()
    {
        // 100 samples across the latency histogram: 90 ≤50ms, 5 ≤100ms, 4 ≤500ms, 1 ≤10000ms. Walking
        // cumulative bucket counts: p90 (ceil .9*100=90) lands in the 50ms bucket, p95 (95) in 100ms, p99
        // (99) in 500ms.
        var seed = _fixture.CreateContext();
        AddOutcomeCounters(seed, EndpointCounterKeys.Total(Route, "success"), 100);
        AddBucketCounter(seed, EndpointCounterKeys.Pct(Route, 50), 90);
        AddBucketCounter(seed, EndpointCounterKeys.Pct(Route, 100), 5);
        AddBucketCounter(seed, EndpointCounterKeys.Pct(Route, 500), 4);
        AddBucketCounter(seed, EndpointCounterKeys.Pct(Route, 10000), 1);
        await seed.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var id = (await CreateService().GetEndpoints(Xunit.TestContext.Current.CancellationToken)).Single().Id;

        var detail = await CreateService().GetEndpointDetail(id, Xunit.TestContext.Current.CancellationToken);

        detail.ShouldNotBeNull();
        detail.P90DurationMs.ShouldBe(50);
        detail.P95DurationMs.ShouldBe(100);
        detail.P99DurationMs.ShouldBe(500);
    }

    [TimedFact]
    public async Task GetEndpointDetail_History_BuiltFromHourlyAggregates_OrderedOldestFirst()
    {
        // Two hourly buckets seeded directly (no call-log rows) so this proves the series comes from the
        // durable aggregates and survives log deletion. Hour 1: 3 success + 1 failed, duration sum 80 →
        // 4 calls, 25% errors, avg 20ms. Hour 2: 2 success, duration sum 30 → avg 15ms, 0% errors.
        var h1 = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc);
        var h2 = new DateTime(2026, 7, 20, 11, 0, 0, DateTimeKind.Utc);
        var b1 = EndpointCounterKeys.HourBucket(h1);
        var b2 = EndpointCounterKeys.HourBucket(h2);

        var seed = _fixture.CreateContext();
        AddOutcomeCounters(seed, EndpointCounterKeys.Total(Route, "success"), 1);
        AddOutcomeCounters(seed, EndpointCounterKeys.History(Route, "success", b1), 3);
        AddOutcomeCounters(seed, EndpointCounterKeys.History(Route, "failed", b1), 1);
        AddDurationCounter(seed, EndpointCounterKeys.History(Route, EndpointCounterKeys.DurationToken, b1), 80);
        AddOutcomeCounters(seed, EndpointCounterKeys.History(Route, "success", b2), 2);
        AddDurationCounter(seed, EndpointCounterKeys.History(Route, EndpointCounterKeys.DurationToken, b2), 30);
        await seed.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var id = (await CreateService().GetEndpoints(Xunit.TestContext.Current.CancellationToken)).Single().Id;

        var detail = await CreateService().GetEndpointDetail(id, Xunit.TestContext.Current.CancellationToken);

        detail.ShouldNotBeNull();
        detail.History.Count.ShouldBe(2);

        var first = detail.History[0];
        first.Hour.ShouldBe(h1);
        first.Calls.ShouldBe(4);
        first.Errors.ShouldBe(1);
        first.ErrorRate.ShouldBe(0.25, 0.001);
        first.AvgDurationMs.ShouldBe(20);

        var second = detail.History[1];
        second.Hour.ShouldBe(h2);
        second.Calls.ShouldBe(2);
        second.Errors.ShouldBe(0);
        second.AvgDurationMs.ShouldBe(15);
    }

    [TimedFact]
    public async Task GetGlobalHistory_AggregatesAcrossAllEndpoints()
    {
        // Two routes, same hour → one global point summing both. GET /a: 2 success + dur 40; POST /b:
        // 1 failed + dur 20. Global: 3 calls, 1 error, avg (40+20)/3 = 20ms.
        var hour = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc);
        var b = EndpointCounterKeys.HourBucket(hour);
        var routeA = EndpointCounterKeys.NormalizeRoute("GET", "/a");
        var routeB = EndpointCounterKeys.NormalizeRoute("POST", "/b");

        var seed = _fixture.CreateContext();
        AddOutcomeCounters(seed, EndpointCounterKeys.History(routeA, "success", b), 2);
        AddDurationCounter(seed, EndpointCounterKeys.History(routeA, EndpointCounterKeys.DurationToken, b), 40);
        AddOutcomeCounters(seed, EndpointCounterKeys.History(routeB, "failed", b), 1);
        AddDurationCounter(seed, EndpointCounterKeys.History(routeB, EndpointCounterKeys.DurationToken, b), 20);
        await seed.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var history = await CreateService().GetGlobalHistory(Xunit.TestContext.Current.CancellationToken);

        var point = history.ShouldHaveSingleItem();
        point.Hour.ShouldBe(hour);
        point.Calls.ShouldBe(3);
        point.Errors.ShouldBe(1);
        point.AvgDurationMs.ShouldBe(20);
    }

    private static EndpointCallLog CallLog(
        AdapterCallOutcome outcome,
        double durationMs,
        string? group = null,
        DateTime? timestamp = null,
        string? remoteIp = null,
        string? user = null)
        => new()
        {
            Method = "GET",
            RouteTemplate = "/orders",
            Operation = "GetOrders",
            GroupName = group,
            Timestamp = timestamp ?? DateTime.UtcNow,
            DurationMs = durationMs,
            Outcome = outcome,
            RemoteIp = remoteIp,
            User = user,
            MachineName = "test-host",
        };

    private static void AddOutcomeCounters(TestContext context, string key, int count)
    {
        for (var i = 0; i < count; i++)
        {
            context.Set<Counter>().Add(new Counter { Key = key, Value = 1 });
        }
    }

    // The duration-sum counter in milliseconds that backs average latency. One row carrying the summed
    // milliseconds, which the query divides by the outcome-count total (all from aggregates) so average
    // latency survives call-log deletion.
    private static void AddDurationCounter(TestContext context, string key, int totalMs)
    {
        context.Set<Counter>().Add(new Counter { Key = key, Value = totalMs });
    }

    // A latency-histogram bucket counter: the summed call count that fell into one bucket bound. The query
    // walks these cumulatively (over the ascending bucket bounds) to derive p90/p95/p99.
    private static void AddBucketCounter(TestContext context, string key, int count)
    {
        context.Set<Counter>().Add(new Counter { Key = key, Value = count });
    }

    private EndpointQueryService<TestContext> CreateService() => new(_fixture.CreateContext());
}
