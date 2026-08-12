using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Warp.Core;
using Warp.Core.Adapters;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Services;
using Warp.Tests.Fixtures;
using Warp.UI;
using Warp.UI.Endpoints;

namespace Warp.Tests.Adapters;

/// <summary>
/// Dashboard-backend coverage for the Adapters feature (SC7): the <see cref="IAdapterQueryService"/>
/// payloads (list stats, detail operations/groups/recent-calls + policy-conflict flag, call detail with
/// captured payloads) against a real database, plus the <c>GET /api/addons</c> <c>adapters</c> flag in
/// both registration shapes (with and without <c>AddAdapters()</c>). Counts come from the merged
/// <see cref="Statistic"/>/<see cref="Counter"/> rows and average latency from <see cref="AdapterCallLog"/>,
/// so the seed writes both. Each test drives exactly one public method (§4.8).
/// </summary>
[GenerateDatabaseTests]
public abstract class AdapterEndpointTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected AdapterEndpointTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public void AddAdapters_RegistersRecordingMarker()
    {
        // The addons flag gates on IAdapterRecordingMarker — only AddAdapters() registers it. (IWarpAdapters
        // is now always registered by AddWarp for unconditional telemetry, §2.15, so it can't gate the flag.)
        var services = new ServiceCollection();

        new WarpBuilder<TestContext>(services).AddAdapters();

        services.Any(x => x.ServiceType == typeof(IAdapterRecordingMarker)).ShouldBeTrue();
    }

    [TimedFact]
    public async Task GetAddons_AdaptersRegistered_FlagTrue()
    {
        var (app, client) = await CreateAddonsHost(registerAdapters: true);
        try
        {
            var info = await client.GetFromJsonAsync<WarpAddonsInfo>("/warp/api/addons", Xunit.TestContext.Current.CancellationToken);

            info.ShouldNotBeNull();
            info.Adapters.ShouldBeTrue();
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task GetAddons_AdaptersNotRegistered_FlagFalse()
    {
        var (app, client) = await CreateAddonsHost(registerAdapters: false);
        try
        {
            var response = await client.GetAsync("/warp/api/addons", Xunit.TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var info = await response.Content.ReadFromJsonAsync<WarpAddonsInfo>(Xunit.TestContext.Current.CancellationToken);
            info.ShouldNotBeNull();
            info.Adapters.ShouldBeFalse();
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task GetAdapters_ReturnsDefinitionWithAggregatedStats()
    {
        var seed = _fixture.CreateContext();
        seed.Set<AdapterDefinition>().Add(Definition("vendor"));
        AddOutcomeCounters(seed, AdapterCounterKeys.Total("vendor", "success"), 2);
        AddOutcomeCounters(seed, AdapterCounterKeys.Total("vendor", "failed"), 1);
        AddDurationCounter(seed, AdapterCounterKeys.Total("vendor", AdapterCounterKeys.DurationToken), 60);
        seed.Set<AdapterCallLog>().Add(CallLog("vendor", "GetOrders", AdapterCallOutcome.Success, durationMs: 10));
        seed.Set<AdapterCallLog>().Add(CallLog("vendor", "GetOrders", AdapterCallOutcome.Success, durationMs: 20));
        seed.Set<AdapterCallLog>().Add(CallLog("vendor", "GetOrders", AdapterCallOutcome.Failed, durationMs: 30));
        await seed.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var list = await CreateService().GetAdapters(Xunit.TestContext.Current.CancellationToken);

        var item = list.ShouldHaveSingleItem();
        item.Name.ShouldBe("vendor");
        item.TotalCalls.ShouldBe(3);
        item.ErrorCount.ShouldBe(1);
        item.ErrorRate.ShouldBe(1d / 3d, 0.001);
        item.AvgDurationMs.ShouldBe(20);
        item.HasPolicyConflict.ShouldBeFalse();
    }

    [TimedFact]
    public async Task GetAdapterDetail_ReturnsOperationsGroupsAndConflictFlag()
    {
        var failedAt = DateTime.UtcNow;

        var seed = _fixture.CreateContext();
        seed.Set<AdapterDefinition>().Add(Definition("vendor", hasPolicyConflict: true));
        AddOutcomeCounters(seed, AdapterCounterKeys.Operation("vendor", "GetOrders", "success"), 2);
        AddOutcomeCounters(seed, AdapterCounterKeys.Operation("vendor", "GetOrders", "failed"), 1);
        AddOutcomeCounters(seed, AdapterCounterKeys.Group("vendor", "shop-eu", "success"), 1);
        AddOutcomeCounters(seed, AdapterCounterKeys.Group("vendor", "shop-eu", "failed"), 1);
        seed.Set<AdapterCallLog>().Add(CallLog("vendor", "GetOrders", AdapterCallOutcome.Success, durationMs: 10, group: "shop-eu", timestamp: failedAt.AddMinutes(-1)));
        seed.Set<AdapterCallLog>().Add(CallLog("vendor", "GetOrders", AdapterCallOutcome.Failed, durationMs: 20, group: "shop-eu", timestamp: failedAt));
        seed.Set<AdapterCallLog>().Add(CallLog("vendor", "GetOrders", AdapterCallOutcome.Success, durationMs: 30));
        await seed.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var detail = await CreateService().GetAdapterDetail("vendor", Xunit.TestContext.Current.CancellationToken);

        detail.ShouldNotBeNull();
        detail.HasPolicyConflict.ShouldBeTrue();

        var operation = detail.Operations.ShouldHaveSingleItem();
        operation.Operation.ShouldBe("GetOrders");
        operation.Calls.ShouldBe(3);
        operation.Errors.ShouldBe(1);

        var group = detail.Groups.ShouldHaveSingleItem();
        group.Group.ShouldBe("shop-eu");
        group.Calls.ShouldBe(2);
        group.Errors.ShouldBe(1);
        group.LastFailureAt.ShouldNotBeNull();
        group.LastFailureAt.Value.ShouldBe(failedAt, TimeSpan.FromSeconds(1));

        detail.RecentCalls.Count.ShouldBe(3);
    }

    [TimedFact]
    public async Task GetAdapterDetail_OtherAdaptersPresent_ScopesStatsToRequestedAdapter()
    {
        // The detail load is scoped to "adapter:{name}:" so it never materialises every adapter's stat rows.
        // This pins that the scoping stays CORRECT: a second adapter with its own totals/operations/histogram
        // must not bleed into the requested adapter's detail (totals, operations, percentiles all stay clean).
        var seed = _fixture.CreateContext();
        seed.Set<AdapterDefinition>().Add(Definition("vendor"));
        seed.Set<AdapterDefinition>().Add(Definition("other"));
        AddOutcomeCounters(seed, AdapterCounterKeys.Total("vendor", "success"), 2);
        AddOutcomeCounters(seed, AdapterCounterKeys.Operation("vendor", "GetOrders", "success"), 2);
        AddBucketCounter(seed, AdapterCounterKeys.Pct("vendor", 50), 2);

        // "other" adapter data that must be excluded from the "vendor" detail.
        AddOutcomeCounters(seed, AdapterCounterKeys.Total("other", "success"), 99);
        AddOutcomeCounters(seed, AdapterCounterKeys.Operation("other", "Charge", "failed"), 99);
        AddBucketCounter(seed, AdapterCounterKeys.Pct("other", 5000), 99);
        await seed.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var detail = await CreateService().GetAdapterDetail("vendor", Xunit.TestContext.Current.CancellationToken);

        detail.ShouldNotBeNull();
        detail.TotalCalls.ShouldBe(2);
        detail.Operations.ShouldHaveSingleItem().Operation.ShouldBe("GetOrders");

        // The p50 bucket has vendor's 2 calls — "other"'s 99 in the 5000ms bucket must not shift the percentile.
        detail.P99DurationMs.ShouldBe(50);
    }

    [TimedFact]
    public async Task GetAdapterDetail_History_BuiltFromHourlyAggregates_OrderedOldestFirst()
    {
        // Two hourly buckets seeded directly (no call-log rows) so this proves the series comes from the
        // durable aggregates and survives log deletion. Hour 1: 3 success + 1 throttled, duration sum 80 →
        // 4 calls, 25% errors (throttled counts as error), avg 20ms. Hour 2: 2 success, sum 30 → avg 15ms.
        var h1 = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc);
        var h2 = new DateTime(2026, 7, 20, 11, 0, 0, DateTimeKind.Utc);
        var b1 = AdapterCounterKeys.HourBucket(h1);
        var b2 = AdapterCounterKeys.HourBucket(h2);

        var seed = _fixture.CreateContext();
        seed.Set<AdapterDefinition>().Add(Definition("vendor"));
        AddOutcomeCounters(seed, AdapterCounterKeys.History("vendor", "success", b1), 3);
        AddOutcomeCounters(seed, AdapterCounterKeys.History("vendor", "throttled", b1), 1);
        AddDurationCounter(seed, AdapterCounterKeys.History("vendor", AdapterCounterKeys.DurationToken, b1), 80);
        AddOutcomeCounters(seed, AdapterCounterKeys.History("vendor", "success", b2), 2);
        AddDurationCounter(seed, AdapterCounterKeys.History("vendor", AdapterCounterKeys.DurationToken, b2), 30);
        await seed.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var detail = await CreateService().GetAdapterDetail("vendor", Xunit.TestContext.Current.CancellationToken);

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
    public async Task GetGlobalHistory_AggregatesAcrossAllAdapters()
    {
        // Two adapters, same hour → one global point summing both. vendor-a: 2 success + dur 40; vendor-b:
        // 1 failed + dur 20. Global: 3 calls, 1 error, avg (40+20)/3 = 20ms. No AdapterDefinition needed —
        // the global overview reads the durable counters, not the definitions.
        var hour = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc);
        var b = AdapterCounterKeys.HourBucket(hour);

        var seed = _fixture.CreateContext();
        AddOutcomeCounters(seed, AdapterCounterKeys.History("vendor-a", "success", b), 2);
        AddDurationCounter(seed, AdapterCounterKeys.History("vendor-a", AdapterCounterKeys.DurationToken, b), 40);
        AddOutcomeCounters(seed, AdapterCounterKeys.History("vendor-b", "failed", b), 1);
        AddDurationCounter(seed, AdapterCounterKeys.History("vendor-b", AdapterCounterKeys.DurationToken, b), 20);
        await seed.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var history = await CreateService().GetGlobalHistory(Xunit.TestContext.Current.CancellationToken);

        var point = history.ShouldHaveSingleItem();
        point.Hour.ShouldBe(hour);
        point.Calls.ShouldBe(3);
        point.Errors.ShouldBe(1);
        point.AvgDurationMs.ShouldBe(20);
    }

    [TimedFact]
    public async Task GetAdapterDetail_RecentCallsIdenticalTimestamp_OrdersByIdDescendingAsStableTiebreaker()
    {
        // SMALL-5: OrderByDescending(Timestamp) alone leaves the recent-calls order non-deterministic when
        // Timestamp ties (and, at the RecentCalls cap, which rows survive the Take arbitrary).
        // ThenByDescending(Id) gives a total order — the returned list matches the deterministic ordering.
        var timestamp = DateTime.UtcNow;

        var seed = _fixture.CreateContext();
        seed.Set<AdapterDefinition>().Add(Definition("vendor"));
        for (var i = 0; i < 5; i++)
        {
            seed.Set<AdapterCallLog>().Add(CallLog("vendor", "GetOrders", AdapterCallOutcome.Success, durationMs: 10, timestamp: timestamp));
        }

        await seed.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var detail = await CreateService().GetAdapterDetail("vendor", Xunit.TestContext.Current.CancellationToken);

        detail.ShouldNotBeNull();

        // The same total order the service applies (Timestamp desc, then Id desc).
        var expected = await _fixture.CreateContext().Set<AdapterCallLog>()
            .Where(x => x.AdapterName == "vendor")
            .OrderByDescending(x => x.Timestamp)
            .ThenByDescending(x => x.Id)
            .Select(x => x.Id)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        detail.RecentCalls.Select(x => x.Id).ShouldBe(expected);
    }

    [TimedFact]
    public async Task GetAdapterDetail_Percentiles_ComputedFromHistogramBuckets()
    {
        // 100 samples across the latency histogram: 90 ≤50ms, 5 ≤100ms, 4 ≤500ms, 1 ≤10000ms. Walking
        // cumulative bucket counts: p90 (ceil .9*100=90) lands in the 50ms bucket, p95 (95) in 100ms, p99
        // (99) in 500ms.
        var seed = _fixture.CreateContext();
        seed.Set<AdapterDefinition>().Add(Definition("vendor"));
        AddBucketCounter(seed, AdapterCounterKeys.Pct("vendor", 50), 90);
        AddBucketCounter(seed, AdapterCounterKeys.Pct("vendor", 100), 5);
        AddBucketCounter(seed, AdapterCounterKeys.Pct("vendor", 500), 4);
        AddBucketCounter(seed, AdapterCounterKeys.Pct("vendor", 10000), 1);
        await seed.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var detail = await CreateService().GetAdapterDetail("vendor", Xunit.TestContext.Current.CancellationToken);

        detail.ShouldNotBeNull();
        detail.P90DurationMs.ShouldBe(50);
        detail.P95DurationMs.ShouldBe(100);
        detail.P99DurationMs.ShouldBe(500);
    }

    [TimedFact]
    public async Task GetAdapterDetail_UnknownName_ReturnsNull()
    {
        var detail = await CreateService().GetAdapterDetail("missing", Xunit.TestContext.Current.CancellationToken);

        detail.ShouldBeNull();
    }

    [TimedFact]
    public async Task GetAdapterDetail_ConfiguredGroupLabel_FlowsThroughFromDefinition()
    {
        var seed = _fixture.CreateContext();
        var definition = Definition("vendor");
        definition.GroupLabel = "Shop";
        seed.Set<AdapterDefinition>().Add(definition);
        await seed.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var detail = await CreateService().GetAdapterDetail("vendor", Xunit.TestContext.Current.CancellationToken);

        detail.ShouldNotBeNull();
        detail.GroupLabel.ShouldBe("Shop");
    }

    [TimedFact]
    public async Task GetAdapterDetail_NoGroupLabel_DefaultsToGroup()
    {
        var seed = _fixture.CreateContext();
        seed.Set<AdapterDefinition>().Add(Definition("vendor"));
        await seed.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var detail = await CreateService().GetAdapterDetail("vendor", Xunit.TestContext.Current.CancellationToken);

        detail.ShouldNotBeNull();
        detail.GroupLabel.ShouldBe("Group");
    }

    [TimedFact]
    public async Task GetAdapters_Latency_FromAggregates_SurvivesLogDeletion()
    {
        // Item 2: average latency is derived from the duration-sum ÷ count aggregates, not raw AdapterCallLog
        // rows — so it persists after logs are swept, exactly like counts and error rate. No call-log rows
        // are seeded here (simulating deleted logs); the aggregate alone must still yield the average.
        var seed = _fixture.CreateContext();
        seed.Set<AdapterDefinition>().Add(Definition("vendor"));
        AddOutcomeCounters(seed, AdapterCounterKeys.Total("vendor", "success"), 3);
        AddDurationCounter(seed, AdapterCounterKeys.Total("vendor", AdapterCounterKeys.DurationToken), 30);
        await seed.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var list = await CreateService().GetAdapters(Xunit.TestContext.Current.CancellationToken);

        var item = list.ShouldHaveSingleItem();
        item.TotalCalls.ShouldBe(3);
        item.AvgDurationMs.ShouldBe(10);
    }

    [TimedFact]
    public async Task GetCallDetail_ReturnsCapturedPayloads()
    {
        var call = CallLog("vendor", "GetOrders", AdapterCallOutcome.Failed, durationMs: 12);
        call.RequestBody = "request-body";
        call.ResponseBody = "response-body";
        call.RequestHeaders = "X-Api-Key: ***";
        call.ExceptionType = typeof(InvalidOperationException).FullName;
        call.ExceptionMessage = "boom";

        var seed = _fixture.CreateContext();
        seed.Set<AdapterCallLog>().Add(call);
        await seed.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var detail = await CreateService().GetCallDetail("vendor", call.Id, Xunit.TestContext.Current.CancellationToken);

        detail.ShouldNotBeNull();
        detail.Id.ShouldBe(call.Id);
        detail.RequestBody.ShouldBe("request-body");
        detail.ResponseBody.ShouldBe("response-body");
        detail.RequestHeaders.ShouldBe("X-Api-Key: ***");
        detail.ExceptionType.ShouldBe(typeof(InvalidOperationException).FullName);
        detail.ExceptionMessage.ShouldBe("boom");
    }

    [TimedFact]
    public async Task GetCallDetail_UnknownId_ReturnsNull()
    {
        var detail = await CreateService().GetCallDetail("vendor", Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        detail.ShouldBeNull();
    }

    [TimedFact]
    public async Task GetAdaptersEndpoint_ReturnsAggregatedStatsJson()
    {
        var seed = _fixture.CreateContext();
        seed.Set<AdapterDefinition>().Add(Definition("vendor"));
        AddOutcomeCounters(seed, AdapterCounterKeys.Total("vendor", "success"), 2);
        AddOutcomeCounters(seed, AdapterCounterKeys.Total("vendor", "failed"), 1);
        AddDurationCounter(seed, AdapterCounterKeys.Total("vendor", AdapterCounterKeys.DurationToken), 60);
        seed.Set<AdapterCallLog>().Add(CallLog("vendor", "GetOrders", AdapterCallOutcome.Success, durationMs: 10));
        seed.Set<AdapterCallLog>().Add(CallLog("vendor", "GetOrders", AdapterCallOutcome.Failed, durationMs: 30));
        await seed.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var (app, client) = await CreateEndpointHost();
        try
        {
            var list = await client.GetFromJsonAsync<List<AdapterListItemModel>>("/warp/api/adapters", Xunit.TestContext.Current.CancellationToken);

            var item = list.ShouldNotBeNull().ShouldHaveSingleItem();
            item.Name.ShouldBe("vendor");
            item.TotalCalls.ShouldBe(3);
            item.ErrorCount.ShouldBe(1);
            item.AvgDurationMs.ShouldBe(20);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task GetAdapterDetailEndpoint_KnownName_ReturnsDetailJson()
    {
        var seed = _fixture.CreateContext();
        seed.Set<AdapterDefinition>().Add(Definition("vendor", hasPolicyConflict: true));
        AddOutcomeCounters(seed, AdapterCounterKeys.Operation("vendor", "GetOrders", "success"), 2);
        seed.Set<AdapterCallLog>().Add(CallLog("vendor", "GetOrders", AdapterCallOutcome.Success, durationMs: 15));
        await seed.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var (app, client) = await CreateEndpointHost();
        try
        {
            var detail = await client.GetFromJsonAsync<AdapterDetailModel>("/warp/api/adapters/vendor", Xunit.TestContext.Current.CancellationToken);

            detail.ShouldNotBeNull();
            detail.Name.ShouldBe("vendor");
            detail.HasPolicyConflict.ShouldBeTrue();
            detail.Operations.ShouldHaveSingleItem().Operation.ShouldBe("GetOrders");
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task GetAdapterDetailEndpoint_UnknownName_Returns404()
    {
        var (app, client) = await CreateEndpointHost();
        try
        {
            var response = await client.GetAsync("/warp/api/adapters/missing", Xunit.TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task GetCallDetailEndpoint_KnownId_ReturnsCapturedPayloadsJson()
    {
        var call = CallLog("vendor", "GetOrders", AdapterCallOutcome.Failed, durationMs: 12);
        call.ResponseBody = "response-body";
        call.ExceptionMessage = "boom";

        var seed = _fixture.CreateContext();
        seed.Set<AdapterCallLog>().Add(call);
        await seed.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var (app, client) = await CreateEndpointHost();
        try
        {
            var detail = await client.GetFromJsonAsync<AdapterCallDetailModel>($"/warp/api/adapters/vendor/calls/{call.Id}", Xunit.TestContext.Current.CancellationToken);

            detail.ShouldNotBeNull();
            detail.Id.ShouldBe(call.Id);
            detail.ResponseBody.ShouldBe("response-body");
            detail.ExceptionMessage.ShouldBe("boom");
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task GetCallDetailEndpoint_UnknownId_Returns404()
    {
        var (app, client) = await CreateEndpointHost();
        try
        {
            var response = await client.GetAsync($"/warp/api/adapters/vendor/calls/{Guid.NewGuid()}", Xunit.TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    private async Task<(WebApplication App, HttpClient Client)> CreateEndpointHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.WebHost.UseDefaultServiceProvider(o => o.ValidateScopes = true);

        // Back the always-registered query service with the fixture database so the real route templates,
        // binding, and JSON serialization in WarpEndpoints are exercised end-to-end (not the service alone).
        var fixture = _fixture;
        builder.Services.AddScoped<IAdapterQueryService>(_ => new AdapterQueryService<TestContext>(fixture.CreateContext()));

        var app = builder.Build();
        app.MapWarpApiEndpoints(new WarpUIOptions(), []);

        await app.StartAsync(CancellationToken.None);

        return (app, app.GetTestClient());
    }

    private static async Task<(WebApplication App, HttpClient Client)> CreateAddonsHost(bool registerAdapters)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.WebHost.UseDefaultServiceProvider(o => o.ValidateScopes = true);

        if (registerAdapters)
        {
            // The addons flag gates on IAdapterRecordingMarker (only AddAdapters registers it); IWarpAdapters
            // is now always registered by AddWarp for unconditional telemetry, so it can't gate the flag.
            builder.Services.AddSingleton(Mock.Of<IAdapterRecordingMarker>());
        }

        var app = builder.Build();
        app.MapWarpApiEndpoints(new WarpUIOptions(), []);

        await app.StartAsync(CancellationToken.None);

        return (app, app.GetTestClient());
    }

    private static AdapterDefinition Definition(string name, bool hasPolicyConflict = false)
        => new()
        {
            Name = name,
            FirstSeenAt = DateTime.UtcNow.AddHours(-1),
            LastSeenAt = DateTime.UtcNow,
            ConfigSummary = "test",
            HasPolicyConflict = hasPolicyConflict,
        };

    private static AdapterCallLog CallLog(
        string adapter,
        string operation,
        AdapterCallOutcome outcome,
        double durationMs,
        string? group = null,
        DateTime? timestamp = null)
        => new()
        {
            AdapterName = adapter,
            Operation = operation,
            GroupName = group,
            Timestamp = timestamp ?? DateTime.UtcNow,
            DurationMs = durationMs,
            Attempts = 1,
            Outcome = outcome,
            MachineName = "test-host",
        };

    private static void AddOutcomeCounters(TestContext context, string key, int count)
    {
        for (var i = 0; i < count; i++)
        {
            context.Set<Counter>().Add(new Counter { Key = key, Value = 1 });
        }
    }

    // The duration-sum counter in milliseconds that backs average latency for Item 2. One row carrying the
    // summed milliseconds, which the query divides by the outcome-count total (all from aggregates) so that
    // average latency survives call-log deletion.
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

    private AdapterQueryService<TestContext> CreateService() => new(_fixture.CreateContext());
}
