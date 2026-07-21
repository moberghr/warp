using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.Endpoints;

/// <summary>
/// <c>ExpirationCleanup</c> coverage for the inbound-endpoint call-log table — the inbound mirror of
/// <c>AdapterCleanupTestsBase</c>. Expired <see cref="EndpointCallLog"/> rows are deleted past their
/// stamped <c>ExpireAt</c>; the count cap keeps the newest N rows per endpoint (method + route template),
/// per-endpoint and by <c>Timestamp</c>. There is no endpoint-definition table (endpoints are discovered
/// from traffic), so the count cap is global-only. Each test drives exactly one public method (§4.8).
/// </summary>
[GenerateDatabaseTests]
public abstract class EndpointCleanupTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected EndpointCleanupTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task Cleanup_ExpiredCallLog_Deleted()
    {
        await InsertCallLogAsync("GET", "/expired", expireAt: DateTime.UtcNow.AddHours(-1));

        await CreateCleanup().CleanupExpiredEndpointCallLogsAsync(Ct);

        (await CallLogCountAsync("GET", "/expired")).ShouldBe(0);
    }

    [TimedFact]
    public async Task Cleanup_UnexpiredCallLog_Kept()
    {
        await InsertCallLogAsync("GET", "/future", expireAt: DateTime.UtcNow.AddHours(1));

        await CreateCleanup().CleanupExpiredEndpointCallLogsAsync(Ct);

        (await CallLogCountAsync("GET", "/future")).ShouldBe(1);
    }

    [TimedFact]
    public async Task Cleanup_CallLogWithNullExpireAt_Kept()
    {
        await InsertCallLogAsync("GET", "/never", expireAt: null);

        await CreateCleanup().CleanupExpiredEndpointCallLogsAsync(Ct);

        (await CallLogCountAsync("GET", "/never")).ShouldBe(1);
    }

    [TimedFact]
    public async Task CleanupByCount_GlobalCap_KeepsNewestN()
    {
        for (var i = 0; i < 5; i++)
        {
            await InsertCallLogAsync("GET", "/hot", expireAt: null, timestamp: DateTime.UtcNow.AddMinutes(-10 + i));
        }

        var deleted = await CreateCleanup(callLogRetentionCount: 2).CleanupEndpointCallLogsByCountAsync(Ct);

        deleted.ShouldBe(3);
        (await CallLogCountAsync("GET", "/hot")).ShouldBe(2);
    }

    [TimedFact]
    public async Task CleanupByCount_IsPerEndpoint_TrimsEachIndependently()
    {
        for (var i = 0; i < 4; i++)
        {
            await InsertCallLogAsync("GET", "/a", expireAt: null, timestamp: DateTime.UtcNow.AddMinutes(-10 + i));
        }

        for (var i = 0; i < 2; i++)
        {
            await InsertCallLogAsync("POST", "/b", expireAt: null, timestamp: DateTime.UtcNow.AddMinutes(-10 + i));
        }

        await CreateCleanup(callLogRetentionCount: 3).CleanupEndpointCallLogsByCountAsync(Ct);

        (await CallLogCountAsync("GET", "/a")).ShouldBe(3);
        (await CallLogCountAsync("POST", "/b")).ShouldBe(2);
    }

    [TimedFact]
    public async Task CleanupByCount_NoCapConfigured_KeepsAll()
    {
        for (var i = 0; i < 4; i++)
        {
            await InsertCallLogAsync("GET", "/unbounded", expireAt: null);
        }

        var deleted = await CreateCleanup().CleanupEndpointCallLogsByCountAsync(Ct);

        deleted.ShouldBe(0);
        (await CallLogCountAsync("GET", "/unbounded")).ShouldBe(4);
    }

    [TimedFact]
    public async Task CleanupByCount_DeletesOldestByTimestamp_KeepsNewest()
    {
        await InsertCallLogAsync("GET", "/ordered", expireAt: null, timestamp: DateTime.UtcNow.AddMinutes(-5), operation: "old");
        await InsertCallLogAsync("GET", "/ordered", expireAt: null, timestamp: DateTime.UtcNow.AddMinutes(-1), operation: "new");

        await CreateCleanup(callLogRetentionCount: 1).CleanupEndpointCallLogsByCountAsync(Ct);

        var remaining = await _fixture.CreateContext().Set<EndpointCallLog>()
            .Where(x => x.RouteTemplate == "/ordered")
            .Select(x => x.Operation)
            .ToListAsync(Ct);
        remaining.ShouldHaveSingleItem().ShouldBe("new");
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private ExpirationCleanup<TestContext> CreateCleanup(int? batchSize = null, int? callLogRetentionCount = null)
    {
        var configuration = new WarpServerConfiguration
        {
            ExpirationBatchSize = batchSize ?? new WarpServerConfiguration().ExpirationBatchSize,
            EndpointCallLogRetentionCount = callLogRetentionCount,
        };

        return new ExpirationCleanup<TestContext>(
            new TestServerContext(_fixture.CreateContext()),
            TimeProvider.System,
            Options.Create(configuration));
    }

    private async Task InsertCallLogAsync(string method, string routeTemplate, DateTime? expireAt, DateTime? timestamp = null, string operation = "GetOrders")
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<EndpointCallLog>().Add(new EndpointCallLog
        {
            Method = method,
            RouteTemplate = routeTemplate,
            Operation = operation,
            Timestamp = timestamp ?? DateTime.UtcNow,
            DurationMs = 5,
            Outcome = AdapterCallOutcome.Success,
            MachineName = "test-host",
            ExpireAt = expireAt,
        });

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
    }

    private async Task<int> CallLogCountAsync(string method, string routeTemplate)
    {
        return await _fixture.CreateContext().Set<EndpointCallLog>()
            .Where(x => x.Method == method)
            .Where(x => x.RouteTemplate == routeTemplate)
            .CountAsync(Xunit.TestContext.Current.CancellationToken);
    }
}
