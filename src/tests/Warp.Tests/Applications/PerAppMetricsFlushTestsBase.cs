using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core;
using Warp.Core.Adapters;
using Warp.Core.Data.Entities;
using Warp.Core.Endpoints;
using Warp.Core.Enums;
using Warp.Core.Services;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;

namespace Warp.Tests.Applications;

/// <summary>
/// DB coverage for the disjoint per-application counter family (§8.19 multi-app observability). Flushing
/// adapter / endpoint records with <see cref="WarpConfiguration.ApplicationName"/> set writes the per-app
/// <see cref="Counter"/> rows IN ADDITION to the existing app-agnostic keys (byte-for-byte unchanged); the
/// per-app keys fold into <see cref="Statistic"/> rows via <c>CounterAggregator</c> and read back through
/// the query services. With <c>ApplicationName</c> null, no per-app keys are emitted (behaviour unchanged).
/// A fresh context per arrange/act/assert phase (§4.8); each test drives one public method.
/// </summary>
[GenerateDatabaseTests(SerializeInCollection = "HeavyIntegration")]
public abstract class PerAppMetricsFlushTestsBase : IAsyncLifetime
{
    private const string AppName = "reporting-api";

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private readonly IDatabaseFixture _fixture;

    protected PerAppMetricsFlushTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task AdapterFlush_ApplicationSet_WritesPerAppAndAppAgnosticCounters()
    {
        await PersistAdapterAsync(AppName, AdapterRecord("vendor", "GetOrders", AdapterCallOutcome.Success, durationMs: 42.4));

        // App-agnostic keys still written byte-for-byte (back-compat) ...
        (await CounterValueAsync(AdapterCounterKeys.Total("vendor", "success"))).ShouldBe(1);
        (await CounterValueAsync(AdapterCounterKeys.Total("vendor", AdapterCounterKeys.DurationToken))).ShouldBe(42);

        // ... PLUS the disjoint per-app keys.
        (await CounterValueAsync(AdapterCounterKeys.AppTotal(AppName, "vendor", "success"))).ShouldBe(1);
        (await CounterValueAsync(AdapterCounterKeys.AppTotal(AppName, "vendor", AdapterCounterKeys.DurationToken))).ShouldBe(42);
    }

    [TimedFact]
    public async Task AdapterFlush_ApplicationNull_WritesNoPerAppCounters()
    {
        await PersistAdapterAsync(application: null, AdapterRecord("vendor", "GetOrders", AdapterCallOutcome.Success));

        var perApp = await _fixture.CreateContext().Set<Counter>()
            .Where(x => x.Key.StartsWith(AdapterCounterKeys.AppPrefix + ":"))
            .CountAsync(Ct);

        perApp.ShouldBe(0);

        // App-agnostic behaviour unchanged.
        (await CounterValueAsync(AdapterCounterKeys.Total("vendor", "success"))).ShouldBe(1);
    }

    [TimedFact]
    public async Task AdapterFlush_ApplicationSet_WritesPerAppHourlyHistory()
    {
        var timestamp = new DateTime(2026, 7, 20, 14, 37, 0, DateTimeKind.Utc);
        await PersistAdapterAsync(AppName, AdapterRecord("vendor", "GetOrders", AdapterCallOutcome.Throttled, durationMs: 30, timestamp: timestamp));

        var hour = AdapterCounterKeys.HourBucket(timestamp);

        (await CounterValueAsync(AdapterCounterKeys.AppHistory(AppName, "vendor", "throttled", hour))).ShouldBe(1);
        (await CounterValueAsync(AdapterCounterKeys.AppHistory(AppName, "vendor", AdapterCounterKeys.DurationToken, hour))).ShouldBe(30);
    }

    [TimedFact]
    public async Task AdapterAggregator_FoldsPerAppCounters_IntoStatistics()
    {
        await PersistAdapterAsync(
            AppName,
            AdapterRecord("vendor", "GetOrders", AdapterCallOutcome.Success),
            AdapterRecord("vendor", "GetOrders", AdapterCallOutcome.Success),
            AdapterRecord("vendor", "GetOrders", AdapterCallOutcome.Failed));

        await TestTasks.CreateCounterAggregator(_fixture.CreateContext()).AggregateCountersAsync(Ct);

        (await StatisticValueAsync(AdapterCounterKeys.AppTotal(AppName, "vendor", "success"))).ShouldBe(2);
        (await StatisticValueAsync(AdapterCounterKeys.AppTotal(AppName, "vendor", "failed"))).ShouldBe(1);

        // The app-agnostic Statistic is folded unchanged alongside.
        (await StatisticValueAsync(AdapterCounterKeys.Total("vendor", "success"))).ShouldBe(2);
    }

    [TimedFact]
    public async Task AdapterQuery_GetStatsByApplication_ReturnsAggregatesFromStatistics()
    {
        await PersistAdapterAsync(
            AppName,
            AdapterRecord("vendor", "GetOrders", AdapterCallOutcome.Success, durationMs: 10),
            AdapterRecord("vendor", "GetOrders", AdapterCallOutcome.Failed, durationMs: 30));

        await TestTasks.CreateCounterAggregator(_fixture.CreateContext()).AggregateCountersAsync(Ct);

        var service = new AdapterQueryService<TestContext>(_fixture.CreateContext());

        (await service.GetApplications(Ct)).ShouldBe([AppName]);

        var stats = await service.GetAdapterStatsByApplication(AppName, Ct);

        var vendor = stats.ShouldHaveSingleItem();
        vendor.Application.ShouldBe(AppName);
        vendor.Adapter.ShouldBe("vendor");
        vendor.Calls.ShouldBe(2);
        vendor.Errors.ShouldBe(1);
        vendor.ErrorRate.ShouldBe(0.5);
        vendor.AvgDurationMs.ShouldBe(20); // (10 + 30) / 2
    }

    [TimedFact]
    public async Task EndpointFlush_ApplicationSet_WritesPerAppAndAppAgnosticCounters()
    {
        await PersistEndpointAsync(AppName, EndpointRecord("get", "/orders/{id:int}", AdapterCallOutcome.Success, durationMs: 42.4));

        var route = EndpointCounterKeys.NormalizeRoute("get", "/orders/{id:int}");

        (await CounterValueAsync(EndpointCounterKeys.Total(route, "success"))).ShouldBe(1);
        (await CounterValueAsync(EndpointCounterKeys.AppTotal(AppName, route, "success"))).ShouldBe(1);
        (await CounterValueAsync(EndpointCounterKeys.AppTotal(AppName, route, EndpointCounterKeys.DurationToken))).ShouldBe(42);
    }

    [TimedFact]
    public async Task EndpointFlush_ApplicationNull_WritesNoPerAppCounters()
    {
        await PersistEndpointAsync(application: null, EndpointRecord("GET", "/orders", AdapterCallOutcome.Success));

        var perApp = await _fixture.CreateContext().Set<Counter>()
            .Where(x => x.Key.StartsWith(EndpointCounterKeys.AppPrefix + ":"))
            .CountAsync(Ct);

        perApp.ShouldBe(0);
    }

    [TimedFact]
    public async Task EndpointFlush_SameRouteTwoApps_KeepsIdentitiesDistinct()
    {
        var route = EndpointCounterKeys.NormalizeRoute("GET", "/orders");

        await PersistEndpointAsync("app-a", EndpointRecord("GET", "/orders", AdapterCallOutcome.Success));
        await PersistEndpointAsync("app-b", EndpointRecord("GET", "/orders", AdapterCallOutcome.Success));

        // Application is part of endpoint identity: the same route under two apps stays two distinct keys.
        (await CounterValueAsync(EndpointCounterKeys.AppTotal("app-a", route, "success"))).ShouldBe(1);
        (await CounterValueAsync(EndpointCounterKeys.AppTotal("app-b", route, "success"))).ShouldBe(1);
    }

    [TimedFact]
    public async Task EndpointQuery_GetStatsByApplication_ReturnsAggregatesFromStatistics()
    {
        await PersistEndpointAsync(
            AppName,
            EndpointRecord("get", "/orders/{id:int}", AdapterCallOutcome.Success, durationMs: 10),
            EndpointRecord("get", "/orders/{id:int}", AdapterCallOutcome.Failed, durationMs: 30));

        await TestTasks.CreateCounterAggregator(_fixture.CreateContext()).AggregateCountersAsync(Ct);

        var service = new EndpointQueryService<TestContext>(_fixture.CreateContext());

        (await service.GetApplications(Ct)).ShouldBe([AppName]);

        var stats = await service.GetEndpointStatsByApplication(AppName, Ct);

        var endpoint = stats.ShouldHaveSingleItem();
        endpoint.Application.ShouldBe(AppName);
        endpoint.Route.ShouldBe("GET /orders/{id}");
        endpoint.Method.ShouldBe("GET");
        endpoint.RouteTemplate.ShouldBe("/orders/{id}");
        endpoint.Calls.ShouldBe(2);
        endpoint.Errors.ShouldBe(1);
        endpoint.ErrorRate.ShouldBe(0.5);
        endpoint.AvgDurationMs.ShouldBe(20);
    }

    [TimedFact]
    public async Task AdapterQuery_GetStatsByApplication_ColonBearingAppName_MatchesSanitizedKeys()
    {
        // C1 regression guard. The write side sanitizes ':' → '-' in the app segment of the counter key
        // (AdapterCounterKeys.AppTotal). The read side MUST apply the SAME transform before building its
        // prefix filter, or a colon-bearing app name queries a prefix ("adapter-app:team:orders:") that
        // never matches the stored (sanitized) keys ("adapter-app:team-orders:…") and silently returns
        // empty. Without the read-side sanitize this ShouldHaveSingleItem fails (empty result).
        await PersistAdapterAsync(
            "team:orders",
            AdapterRecord("vendor", "GetOrders", AdapterCallOutcome.Success, durationMs: 10),
            AdapterRecord("vendor", "GetOrders", AdapterCallOutcome.Failed, durationMs: 30));

        await TestTasks.CreateCounterAggregator(_fixture.CreateContext()).AggregateCountersAsync(Ct);

        var service = new AdapterQueryService<TestContext>(_fixture.CreateContext());

        var stats = await service.GetAdapterStatsByApplication("team:orders", Ct);

        var vendor = stats.ShouldHaveSingleItem();
        vendor.Adapter.ShouldBe("vendor");
        vendor.Calls.ShouldBe(2);
        vendor.Errors.ShouldBe(1);
    }

    [TimedFact]
    public async Task EndpointQuery_GetStatsByApplication_ColonBearingAppName_MatchesSanitizedKeys()
    {
        // C1 regression guard (endpoint mirror). Same sanitize agreement between EndpointCounterKeys.AppTotal
        // (write) and the read-side prefix filter; a colon-bearing app name must resolve to the sanitized
        // keys, not silently return empty.
        await PersistEndpointAsync(
            "team:orders",
            EndpointRecord("GET", "/orders", AdapterCallOutcome.Success, durationMs: 10),
            EndpointRecord("GET", "/orders", AdapterCallOutcome.Failed, durationMs: 30));

        await TestTasks.CreateCounterAggregator(_fixture.CreateContext()).AggregateCountersAsync(Ct);

        var service = new EndpointQueryService<TestContext>(_fixture.CreateContext());

        var stats = await service.GetEndpointStatsByApplication("team:orders", Ct);

        var endpoint = stats.ShouldHaveSingleItem();
        endpoint.Route.ShouldBe("GET /orders");
        endpoint.Calls.ShouldBe(2);
        endpoint.Errors.ShouldBe(1);
    }

    private static AdapterCallRecord AdapterRecord(string adapter, string operation, AdapterCallOutcome outcome, double durationMs = 5, DateTime? timestamp = null)
        => new()
        {
            AdapterName = adapter,
            Operation = operation,
            Timestamp = timestamp ?? DateTime.UtcNow,
            DurationMs = durationMs,
            Attempts = 1,
            Outcome = outcome,
            MachineName = "test-host",
        };

    private static EndpointCallRecord EndpointRecord(string method, string routeTemplate, AdapterCallOutcome outcome, double durationMs = 5, DateTime? timestamp = null)
        => new()
        {
            Method = method,
            RouteTemplate = routeTemplate,
            Operation = "Probe",
            Timestamp = timestamp ?? DateTime.UtcNow,
            DurationMs = durationMs,
            Outcome = outcome,
            MachineName = "test-host",
        };

    private async Task PersistAdapterAsync(string? application, params AdapterCallRecord[] records)
    {
        await AdapterCallFlusher<TestContext>.PersistBatchAsync(
            _fixture.CreateContext(),
            records,
            new AdapterRegistry(),
            new WarpConfiguration { ApplicationName = application },
            TimeProvider.System,
            Ct);
    }

    private async Task PersistEndpointAsync(string? application, params EndpointCallRecord[] records)
    {
        await EndpointCallFlusher<TestContext>.PersistBatchAsync(
            _fixture.CreateContext(),
            records,
            new WarpConfiguration { ApplicationName = application },
            TimeProvider.System,
            Ct);
    }

    private async Task<long> CounterValueAsync(string key)
    {
        return await _fixture.CreateContext().Set<Counter>()
            .Where(x => x.Key == key)
            .SumAsync(x => (long)x.Value, Ct);
    }

    private async Task<long> StatisticValueAsync(string key)
    {
        return await _fixture.CreateContext().Set<Statistic>()
            .Where(x => x.Key == key)
            .Select(x => x.Value)
            .FirstOrDefaultAsync(Ct);
    }
}
