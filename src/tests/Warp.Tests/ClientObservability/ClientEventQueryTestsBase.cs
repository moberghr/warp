using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Metrics;
using Warp.Core.Services;
using Warp.Tests.Fixtures;

namespace Warp.Tests.ClientObservability;

/// <summary>
/// Read side of client observability (§8.27): <see cref="ClientEventQueryService{TContext}"/> folds the durable
/// <c>clientevent:</c> aggregates into a summary (counts, error rate, vital p75, top errors) and reads the raw
/// event stream. The summary tests seed only <see cref="Statistic"/> rows (no <see cref="ClientEventLog"/>) —
/// proving the metrics survive raw-row cleanup — and pin the CLS ÷1000 unscale. Both providers.
/// </summary>
[GenerateDatabaseTests]
public abstract class ClientEventQueryTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected ClientEventQueryTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private ClientEventQueryService<TestContext> Service() => new(_fixture.CreateContext(), new LocalMetricSource<TestContext>(_fixture.CreateContext()));

    [TimedFact]
    public async Task GetSummary_FoldsCountsErrorRateAndVitalP75_FromAggregatesOnly()
    {
        await SeedStatsAsync(
            ("clientevent:total:error:count", 3),
            ("clientevent:total:log:count", 5),
            ("clientevent:total:event:count", 2),
            ("clientevent:name:error:TypeError:count", 3),
            ("clientevent:vital:LCP:count", 4),
            ("clientevent:vital:LCP:dur", 8000),        // avg = 2000
            ("clientevent:vital:LCP:pct:2500", 4));     // all samples ≤ 2500 ⇒ p75 = 2500

        var summary = await Service().GetSummary(application: null, Ct);

        summary.ErrorCount.ShouldBe(3);
        summary.LogCount.ShouldBe(5);
        summary.EventCount.ShouldBe(2);
        summary.ErrorRate.ShouldBe(3.0 / 10, tolerance: 0.001);   // 3 errors / 10 total
        summary.TopErrors.ShouldContain(x => x.Name == "TypeError" && x.Count == 3);

        var lcp = summary.Vitals.Single(x => string.Equals(x.Name, "LCP", StringComparison.Ordinal));
        lcp.AvgValue.ShouldBe(2000);
        lcp.P75Value.ShouldBe(2500);
    }

    [TimedFact]
    public async Task GetSummary_P75_PicksBucketByCumulativeWalk_NotMax()
    {
        // 10 samples: 8 at/below 500ms, 2 up at 5000ms. p75 = the 7.5th sample ⇒ still in the 500 bucket.
        await SeedStatsAsync(
            ("clientevent:vital:LCP:count", 10),
            ("clientevent:vital:LCP:dur", 20000),
            ("clientevent:vital:LCP:pct:500", 8),
            ("clientevent:vital:LCP:pct:5000", 2));

        var summary = await Service().GetSummary(application: null, Ct);

        var lcp = summary.Vitals.Single(x => string.Equals(x.Name, "LCP", StringComparison.Ordinal));
        lcp.P75Value.ShouldBe(500);   // the cumulative walk stops at 500, not the max bucket 5000
    }

    [TimedFact]
    public async Task GetApplications_ReturnsDistinctSortedFromAppSlice()
    {
        await SeedStatsAsync(
            ("clientevent-app:shop:total:error:count", 1),
            ("clientevent-app:admin:total:log:count", 1),
            ("clientevent-app:shop:total:event:count", 1));

        (await Service().GetApplications(Ct)).ShouldBe(["admin", "shop"]);
    }

    [TimedFact]
    public async Task GetEvents_FiltersByApplicationAndSession()
    {
        var ctx = _fixture.CreateContext();
        var a = Row(ClientEventType.Log, "shop");
        a.SessionId = "s1";
        var b = Row(ClientEventType.Log, "shop");
        b.SessionId = "s2";
        var c = Row(ClientEventType.Log, "other");
        c.SessionId = "s1";
        ctx.Set<ClientEventLog>().AddRange(a, b, c);
        await ctx.SaveChangesAsync(Ct);

        (await Service().GetEvents(new ClientEventFilter { Application = "shop" }, Ct)).Total.ShouldBe(2);
        (await Service().GetEvents(new ClientEventFilter { SessionId = "s1" }, Ct)).Total.ShouldBe(2);
        (await Service().GetEvents(new ClientEventFilter { Application = "shop", SessionId = "s1" }, Ct)).Total.ShouldBe(1);
    }

    [TimedFact]
    public async Task GetSummary_ClsVital_UnscaledBackToUnitless()
    {
        await SeedStatsAsync(
            ("clientevent:vital:CLS:count", 2),
            ("clientevent:vital:CLS:dur", 300),        // scaled sum 300 / 2 = 150 → ÷1000 = 0.15
            ("clientevent:vital:CLS:pct:200", 2));     // bucket 200 → ÷1000 = 0.2

        var summary = await Service().GetSummary(application: null, Ct);

        var cls = summary.Vitals.Single(x => string.Equals(x.Name, "CLS", StringComparison.Ordinal));
        cls.AvgValue.ShouldBe(0.15, tolerance: 0.001);
        cls.P75Value.ShouldBe(0.2, tolerance: 0.001);
    }

    [TimedFact]
    public async Task GetSummary_PerApplication_ReadsDisjointAppSlice()
    {
        await SeedStatsAsync(
            ("clientevent:total:error:count", 100),                  // global — must NOT bleed into the app view
            ("clientevent-app:shop:total:error:count", 4),
            ("clientevent-app:shop:total:log:count", 1));

        var summary = await Service().GetSummary(application: "shop", Ct);

        summary.Application.ShouldBe("shop");
        summary.ErrorCount.ShouldBe(4);
        summary.LogCount.ShouldBe(1);
    }

    [TimedFact]
    public async Task GetEvents_FiltersByTypeAndPages()
    {
        var ctx = _fixture.CreateContext();
        for (var i = 0; i < 3; i++)
        {
            ctx.Set<ClientEventLog>().Add(Row(ClientEventType.Error, "shop"));
        }

        ctx.Set<ClientEventLog>().Add(Row(ClientEventType.Log, "shop"));
        await ctx.SaveChangesAsync(Ct);

        var page = await Service().GetEvents(new ClientEventFilter { Type = ClientEventType.Error, PageSize = 2 }, Ct);

        page.Total.ShouldBe(3);          // total matching the filter
        page.Items.Count.ShouldBe(2);    // first page
        page.Items.ShouldAllBe(x => x.Type == ClientEventType.Error);
    }

    [TimedFact]
    public async Task GetEvent_ReturnsFullDetail()
    {
        var ctx = _fixture.CreateContext();
        var row = Row(ClientEventType.Error, "shop");
        row.Stack = "at foo";
        row.Properties = "{\"a\":1}";
        ctx.Set<ClientEventLog>().Add(row);
        await ctx.SaveChangesAsync(Ct);

        var detail = await Service().GetEvent(row.Id, Ct);

        detail.ShouldNotBeNull();
        detail!.Stack.ShouldBe("at foo");
        detail.Properties.ShouldBe("{\"a\":1}");
    }

    [TimedFact]
    public async Task GetSession_MergesClientEventsWithServerCallsByTraceId()
    {
        var trace = Guid.NewGuid();
        var ctx = _fixture.CreateContext();

        // A client request event carrying a trace id, plus a plain client log, both in session s1.
        var request = Row(ClientEventType.Request, "shop");
        request.SessionId = "s1";
        request.TraceId = trace;
        request.Name = "GET";
        request.Timestamp = new DateTime(2026, 7, 26, 8, 0, 0, DateTimeKind.Utc);
        var log = Row(ClientEventType.Log, "shop");
        log.SessionId = "s1";
        log.Timestamp = new DateTime(2026, 7, 26, 8, 0, 1, DateTimeKind.Utc);
        ctx.Set<ClientEventLog>().AddRange(request, log);

        // The server endpoint call that request triggered — stamped with the session id (from baggage) and the
        // shared trace id (for the job-waterfall drill-down).
        ctx.Set<EndpointCallLog>().Add(new EndpointCallLog
        {
            Method = "GET",
            RouteTemplate = "/api/orders",
            Operation = "GetOrders",
            Timestamp = new DateTime(2026, 7, 26, 8, 0, 0, 500, DateTimeKind.Utc),
            DurationMs = 12,
            Outcome = Warp.Core.Enums.AdapterCallOutcome.Success,
            StatusCode = 200,
            MachineName = "srv",
            TraceId = trace,
            Session = "s1",
        });
        await ctx.SaveChangesAsync(Ct);

        var session = await Service().GetSession("s1", Ct);

        session.ShouldNotBeNull();
        session!.Entries.Count.ShouldBe(3);                                  // 2 client + 1 server
        session.Entries.Count(x => string.Equals(x.Kind, "endpoint", StringComparison.Ordinal)).ShouldBe(1);
        session.Entries.ShouldContain(x => string.Equals(x.Kind, "endpoint", StringComparison.Ordinal) && x.TraceId == trace && x.Route == "/api/orders");

        // Merged in timestamp order: request (08:00:00) → server call (08:00:00.5) → log (08:00:01).
        session.Entries[0].Type.ShouldBe(ClientEventType.Request);
        session.Entries[1].Kind.ShouldBe("endpoint");
        session.Entries[2].Type.ShouldBe(ClientEventType.Log);
    }

    [TimedFact]
    public async Task GetSession_UnknownSession_ReturnsNull()
    {
        (await Service().GetSession("nope", Ct)).ShouldBeNull();
    }

    private static ClientEventLog Row(ClientEventType type, string application) => new()
    {
        Application = application,
        Type = type,
        Timestamp = DateTime.UtcNow,
        ReceivedAt = DateTime.UtcNow,
    };

    private async Task SeedStatsAsync(params (string Key, long Value)[] rows)
    {
        var ctx = _fixture.CreateContext();
        foreach (var (key, value) in rows)
        {
            ctx.Set<Statistic>().Add(new Statistic { Key = key, Value = value });
        }

        await ctx.SaveChangesAsync(Ct);
    }
}
