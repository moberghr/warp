using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Endpoints;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Endpoints;

/// <summary>
/// Endpoint-flusher persistence coverage — the inbound mirror of <c>AdapterCounterTestsBase</c>.
/// <c>PersistBatchAsync</c> stamps the normalized identity (uppercase method + constraint-stripped
/// template) onto the <see cref="EndpointCallLog"/> row and writes the write-optimised <see cref="Counter"/>
/// rows (§6.2): a per-outcome COUNT and a duration-SUM per route (+ per group when the request carried
/// one). Counters are unconditional even when the log row is suppressed, so success denominators are never
/// lost. Each test drives exactly one public method (§4.8).
/// </summary>
[GenerateDatabaseTests]
public abstract class EndpointFlusherTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected EndpointFlusherTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task Persist_LogRow_StoresNormalizedMethodAndTemplate()
    {
        // Drive with a lowercase method + inline route constraint; the stored identity is normalized once so
        // the log row shares the exact colon-free identity of the counter keys (the detail page joins on it).
        await PersistAsync(Record("get", "/orders/{id:int}", AdapterCallOutcome.Success));

        var row = await _fixture.CreateContext().Set<EndpointCallLog>()
            .SingleAsync(Xunit.TestContext.Current.CancellationToken);

        row.Method.ShouldBe("GET");
        row.RouteTemplate.ShouldBe("/orders/{id}");
    }

    [TimedFact]
    public async Task Persist_WritesTotalCountAndDurationCounters()
    {
        await PersistAsync(Record("get", "/orders/{id:int}", AdapterCallOutcome.Success, durationMs: 42.4));

        var route = EndpointCounterKeys.NormalizeRoute("get", "/orders/{id:int}");

        (await CounterValueAsync(EndpointCounterKeys.Total(route, "success"))).ShouldBe(1);
        (await CounterValueAsync(EndpointCounterKeys.Total(route, EndpointCounterKeys.DurationToken))).ShouldBe(42);
    }

    [TimedFact]
    public async Task Persist_WritesLatencyHistogramBucket()
    {
        // 42ms rounds into the 50ms bucket (the smallest bound >= 42); no other bucket is touched.
        await PersistAsync(Record("GET", "/orders", AdapterCallOutcome.Success, durationMs: 42.4));

        var route = EndpointCounterKeys.NormalizeRoute("GET", "/orders");

        (await CounterValueAsync(EndpointCounterKeys.Pct(route, 50))).ShouldBe(1);
        (await CounterValueAsync(EndpointCounterKeys.Pct(route, 25))).ShouldBe(0);
    }

    [TimedFact]
    public async Task Persist_FailedOutcome_WritesFailedCounter()
    {
        await PersistAsync(Record("GET", "/orders", AdapterCallOutcome.Failed));

        var route = EndpointCounterKeys.NormalizeRoute("GET", "/orders");

        (await CounterValueAsync(EndpointCounterKeys.Total(route, "failed"))).ShouldBe(1);
    }

    [TimedFact]
    public async Task Persist_GroupSet_WritesGroupCountAndDurationCounters()
    {
        await PersistAsync(Record("GET", "/orders", AdapterCallOutcome.Success, group: "shop-1", durationMs: 30));

        var route = EndpointCounterKeys.NormalizeRoute("GET", "/orders");

        (await CounterValueAsync(EndpointCounterKeys.Group(route, "shop-1", "success"))).ShouldBe(1);
        (await CounterValueAsync(EndpointCounterKeys.Group(route, "shop-1", EndpointCounterKeys.DurationToken))).ShouldBe(30);
    }

    [TimedFact]
    public async Task Persist_GrouplessCall_WritesNoGroupCounter()
    {
        await PersistAsync(Record("GET", "/orders", AdapterCallOutcome.Success));

        var route = EndpointCounterKeys.NormalizeRoute("GET", "/orders");
        var prefix = $"{EndpointCounterKeys.Prefix}:{route}:grp:";

        var groupKeys = await _fixture.CreateContext().Set<Counter>()
            .Where(x => x.Key.StartsWith(prefix))
            .CountAsync(Xunit.TestContext.Current.CancellationToken);

        groupKeys.ShouldBe(0);
    }

    [TimedFact]
    public async Task Persist_SuppressLog_SkipsLogRowButWritesCounters()
    {
        // FailuresOnly success: no call-log ROW, but the counters stay so success denominators survive.
        await PersistAsync(Record("GET", "/orders", AdapterCallOutcome.Success, suppressLog: true));

        var route = EndpointCounterKeys.NormalizeRoute("GET", "/orders");

        (await _fixture.CreateContext().Set<EndpointCallLog>().CountAsync(Xunit.TestContext.Current.CancellationToken)).ShouldBe(0);
        (await CounterValueAsync(EndpointCounterKeys.Total(route, "success"))).ShouldBe(1);
    }

    [TimedFact]
    public async Task Persist_WritesHourlyHistoryCounters()
    {
        var timestamp = new DateTime(2026, 7, 20, 14, 37, 0, DateTimeKind.Utc);
        await PersistAsync(Record("GET", "/orders", AdapterCallOutcome.Failed, durationMs: 30, timestamp: timestamp));

        var route = EndpointCounterKeys.NormalizeRoute("GET", "/orders");
        var hour = EndpointCounterKeys.HourBucket(timestamp);

        (await CounterValueAsync(EndpointCounterKeys.History(route, "failed", hour))).ShouldBe(1);
        (await CounterValueAsync(EndpointCounterKeys.History(route, EndpointCounterKeys.DurationToken, hour))).ShouldBe(30);
    }

    [TimedFact]
    public async Task Persist_ExpireAt_PersistedToRow()
    {
        var expireAt = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        await PersistAsync(Record("GET", "/orders", AdapterCallOutcome.Success, expireAt: expireAt));

        var row = await _fixture.CreateContext().Set<EndpointCallLog>()
            .SingleAsync(Xunit.TestContext.Current.CancellationToken);

        row.ExpireAt.ShouldNotBeNull();
        row.ExpireAt.Value.ShouldBe(expireAt, TimeSpan.FromSeconds(1));
    }

    private static EndpointCallRecord Record(
        string method,
        string routeTemplate,
        AdapterCallOutcome outcome,
        string? group = null,
        double durationMs = 5,
        bool suppressLog = false,
        DateTime? expireAt = null,
        DateTime? timestamp = null)
        => new()
        {
            Method = method,
            RouteTemplate = routeTemplate,
            Operation = "Probe",
            GroupName = group,
            Timestamp = timestamp ?? DateTime.UtcNow,
            DurationMs = durationMs,
            Outcome = outcome,
            MachineName = "test-host",
            SuppressLog = suppressLog,
            ExpireAt = expireAt,
        };

    private async Task PersistAsync(params EndpointCallRecord[] records)
    {
        await EndpointCallFlusher<TestContext>.PersistBatchAsync(
            _fixture.CreateContext(),
            records,
            new WarpConfiguration(),
            TimeProvider.System,
            Xunit.TestContext.Current.CancellationToken);
    }

    private async Task<long> CounterValueAsync(string key)
    {
        return await _fixture.CreateContext().Set<Counter>()
            .Where(x => x.Key == key)
            .SumAsync(x => (long)x.Value, Xunit.TestContext.Current.CancellationToken);
    }
}
