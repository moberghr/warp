using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.Adapters;

/// <summary>
/// <c>ExpirationCleanup</c> coverage for the adapter tables (SC6): expired <see cref="AdapterCallLog"/>
/// rows are deleted past their stamped <c>ExpireAt</c>; orphaned <see cref="AdapterDefinition"/> rows
/// are deleted once <c>LastSeenAt</c> is older than <c>AdapterDefinitionOrphanGrace</c>.
/// </summary>
[GenerateDatabaseTests]
public abstract class AdapterCleanupTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected AdapterCleanupTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task Cleanup_ExpiredCallLog_Deleted()
    {
        await InsertCallLogAsync("expired", DateTime.UtcNow.AddHours(-1));

        await CreateCleanup().CleanupExpiredAdapterCallLogsAsync(Xunit.TestContext.Current.CancellationToken);

        (await CallLogExistsAsync("expired")).ShouldBeFalse();
    }

    [TimedFact]
    public async Task Cleanup_UnexpiredCallLog_Kept()
    {
        await InsertCallLogAsync("future", DateTime.UtcNow.AddHours(1));

        await CreateCleanup().CleanupExpiredAdapterCallLogsAsync(Xunit.TestContext.Current.CancellationToken);

        (await CallLogExistsAsync("future")).ShouldBeTrue();
    }

    [TimedFact]
    public async Task Cleanup_CallLogWithNullExpireAt_Kept()
    {
        await InsertCallLogAsync("never", expireAt: null);

        await CreateCleanup().CleanupExpiredAdapterCallLogsAsync(Xunit.TestContext.Current.CancellationToken);

        (await CallLogExistsAsync("never")).ShouldBeTrue();
    }

    [TimedFact]
    public async Task Cleanup_StaleDefinition_OlderThanGrace_Deleted()
    {
        await InsertDefinitionAsync("stale-vendor", DateTime.UtcNow.AddMinutes(-10));

        await CreateCleanup(orphanGrace: TimeSpan.FromMinutes(2)).CleanupOrphanedAdapterDefinitionsAsync(Xunit.TestContext.Current.CancellationToken);

        (await DefinitionExistsAsync("stale-vendor")).ShouldBeFalse();
    }

    [TimedFact]
    public async Task Cleanup_RecentDefinition_WithinGrace_Kept()
    {
        await InsertDefinitionAsync("live-vendor", DateTime.UtcNow.AddSeconds(-30));

        await CreateCleanup(orphanGrace: TimeSpan.FromMinutes(2)).CleanupOrphanedAdapterDefinitionsAsync(Xunit.TestContext.Current.CancellationToken);

        (await DefinitionExistsAsync("live-vendor")).ShouldBeTrue();
    }

    [TimedFact]
    public async Task Cleanup_StaleDefinition_PastDefaultGrace_Deleted()
    {
        // LastSeenAt 40 min old — well past the DEFAULT 30-min AdapterDefinitionOrphanGrace. Deletion is
        // otherwise only proven with an explicit 2-min override; this pins that the shipped default also
        // reaps a genuinely orphaned definition.
        await InsertDefinitionAsync("orphan-vendor", DateTime.UtcNow.AddMinutes(-40));

        await CreateCleanup(orphanGrace: new WarpServerConfiguration().AdapterDefinitionOrphanGrace)
            .CleanupOrphanedAdapterDefinitionsAsync(Xunit.TestContext.Current.CancellationToken);

        (await DefinitionExistsAsync("orphan-vendor")).ShouldBeFalse();
    }

    [TimedFact]
    public async Task Cleanup_ActiveDefinition_InRefreshBand_SurvivesUnderDefaultGrace()
    {
        // LastSeenAt at 3 min: past the old 2-min grace but still within the flusher's 5-min lazy
        // LastSeenAt refresh window. The default grace (30 min) must keep the definition so an
        // actively-used adapter is never deleted + re-inserted during the refresh band.
        await InsertDefinitionAsync("active-vendor", DateTime.UtcNow.AddMinutes(-3));

        await CreateCleanup(orphanGrace: new WarpServerConfiguration().AdapterDefinitionOrphanGrace)
            .CleanupOrphanedAdapterDefinitionsAsync(Xunit.TestContext.Current.CancellationToken);

        (await DefinitionExistsAsync("active-vendor")).ShouldBeTrue();
    }

    [TimedFact]
    public async Task Cleanup_Definition_AtRefreshBandEdge_SurvivesUnderDefaultGrace()
    {
        // LastSeenAt one second inside the flusher's 5-min lazy-refresh band (4m59s old). The default grace
        // (well beyond 5 min) must keep it so a still-active adapter is never deleted at the band edge.
        await InsertDefinitionAsync("edge-vendor", DateTime.UtcNow.AddMinutes(-5).AddSeconds(1));

        await CreateCleanup(orphanGrace: new WarpServerConfiguration().AdapterDefinitionOrphanGrace)
            .CleanupOrphanedAdapterDefinitionsAsync(Xunit.TestContext.Current.CancellationToken);

        (await DefinitionExistsAsync("edge-vendor")).ShouldBeTrue();
    }

    [TimedFact]
    public async Task Cleanup_ExpiredBacklogBeyondBatchSize_FullyDrainedInBoundedBatches()
    {
        // Volume guard: the sweep deletes in ExpirationBatchSize id batches (bounded statements), but a
        // backlog larger than one batch — the first run after an outage or a retention change — must still
        // fully drain within a single tick, not linger one batch per tick.
        for (var i = 0; i < 5; i++)
        {
            await InsertCallLogAsync($"backlog-{i}", DateTime.UtcNow.AddHours(-1));
        }

        await InsertCallLogAsync("alive", DateTime.UtcNow.AddHours(1));

        var deleted = await CreateCleanup(batchSize: 2).CleanupExpiredAdapterCallLogsAsync(Xunit.TestContext.Current.CancellationToken);

        deleted.ShouldBe(5);
        (await _fixture.CreateContext().Set<AdapterCallLog>().CountAsync(Xunit.TestContext.Current.CancellationToken)).ShouldBe(1);
        (await CallLogExistsAsync("alive")).ShouldBeTrue();
    }

    [TimedFact]
    public async Task CleanupByCount_PerAdapterOverride_KeepsNewestN()
    {
        await InsertDefinitionAsync("hot", DateTime.UtcNow, retentionCount: 2);
        for (var i = 0; i < 5; i++)
        {
            await InsertCallLogAsync("hot", expireAt: null, timestamp: DateTime.UtcNow.AddMinutes(-10 + i));
        }

        var deleted = await CreateCleanup().CleanupAdapterCallLogsByCountAsync(Ct);

        deleted.ShouldBe(3);
        (await CallLogCountAsync("hot")).ShouldBe(2);
    }

    [TimedFact]
    public async Task CleanupByCount_GlobalCap_AppliedWhenNoOverride()
    {
        await InsertDefinitionAsync("warm", DateTime.UtcNow);
        for (var i = 0; i < 5; i++)
        {
            await InsertCallLogAsync("warm", expireAt: null, timestamp: DateTime.UtcNow.AddMinutes(-10 + i));
        }

        var deleted = await CreateCleanup(callLogRetentionCount: 3).CleanupAdapterCallLogsByCountAsync(Ct);

        deleted.ShouldBe(2);
        (await CallLogCountAsync("warm")).ShouldBe(3);
    }

    [TimedFact]
    public async Task CleanupByCount_PerAdapterOverride_BeatsGlobal()
    {
        await InsertDefinitionAsync("special", DateTime.UtcNow, retentionCount: 1);
        for (var i = 0; i < 4; i++)
        {
            await InsertCallLogAsync("special", expireAt: null, timestamp: DateTime.UtcNow.AddMinutes(-10 + i));
        }

        await CreateCleanup(callLogRetentionCount: 10).CleanupAdapterCallLogsByCountAsync(Ct);

        (await CallLogCountAsync("special")).ShouldBe(1);
    }

    [TimedFact]
    public async Task CleanupByCount_NoCapConfigured_KeepsAll()
    {
        await InsertDefinitionAsync("unbounded", DateTime.UtcNow);
        for (var i = 0; i < 4; i++)
        {
            await InsertCallLogAsync("unbounded", expireAt: null);
        }

        var deleted = await CreateCleanup().CleanupAdapterCallLogsByCountAsync(Ct);

        deleted.ShouldBe(0);
        (await CallLogCountAsync("unbounded")).ShouldBe(4);
    }

    [TimedFact]
    public async Task CleanupByCount_ZeroCap_KeepsAll()
    {
        // A count cap of 0 means "off" (like null), NOT "delete everything each tick while the flusher
        // keeps re-inserting" — that would be a thrash footgun.
        await InsertDefinitionAsync("zerocap", DateTime.UtcNow, retentionCount: 0);
        for (var i = 0; i < 4; i++)
        {
            await InsertCallLogAsync("zerocap", expireAt: null);
        }

        var deleted = await CreateCleanup().CleanupAdapterCallLogsByCountAsync(Ct);

        deleted.ShouldBe(0);
        (await CallLogCountAsync("zerocap")).ShouldBe(4);
    }

    [TimedFact]
    public async Task CleanupByCount_DeletesOldestByTimestamp_KeepsNewest()
    {
        await InsertDefinitionAsync("ordered", DateTime.UtcNow, retentionCount: 1);
        await InsertCallLogAsync("ordered", expireAt: null, timestamp: DateTime.UtcNow.AddMinutes(-5), operation: "old");
        await InsertCallLogAsync("ordered", expireAt: null, timestamp: DateTime.UtcNow.AddMinutes(-1), operation: "new");

        await CreateCleanup().CleanupAdapterCallLogsByCountAsync(Ct);

        var remaining = await _fixture.CreateContext().Set<AdapterCallLog>()
            .Where(x => x.AdapterName == "ordered")
            .Select(x => x.Operation)
            .ToListAsync(Ct);
        remaining.ShouldHaveSingleItem().ShouldBe("new");
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private ExpirationCleanup<TestContext> CreateCleanup(TimeSpan? orphanGrace = null, int? batchSize = null, int? callLogRetentionCount = null)
    {
        var configuration = new WarpServerConfiguration
        {
            AdapterDefinitionOrphanGrace = orphanGrace ?? TimeSpan.FromMinutes(2),
            ExpirationBatchSize = batchSize ?? new WarpServerConfiguration().ExpirationBatchSize,
            AdapterCallLogRetentionCount = callLogRetentionCount,
        };

        return new ExpirationCleanup<TestContext>(
            new TestServerContext(_fixture.CreateContext()),
            TimeProvider.System,
            Options.Create(configuration));
    }

    private async Task InsertCallLogAsync(string adapterName, DateTime? expireAt, DateTime? timestamp = null, string operation = "GetOrders")
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<AdapterCallLog>().Add(new AdapterCallLog
        {
            AdapterName = adapterName,
            Operation = operation,
            Timestamp = timestamp ?? DateTime.UtcNow,
            DurationMs = 5,
            Attempts = 1,
            Outcome = AdapterCallOutcome.Success,
            MachineName = "test-host",
            ExpireAt = expireAt,
        });

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
    }

    private async Task InsertDefinitionAsync(string name, DateTime lastSeenAt, int? retentionCount = null)
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<AdapterDefinition>().Add(new AdapterDefinition
        {
            Name = name,
            FirstSeenAt = lastSeenAt,
            LastSeenAt = lastSeenAt,
            CallLogRetentionCount = retentionCount,
        });

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
    }

    private async Task<int> CallLogCountAsync(string adapterName)
    {
        return await _fixture.CreateContext().Set<AdapterCallLog>()
            .CountAsync(x => x.AdapterName == adapterName, Xunit.TestContext.Current.CancellationToken);
    }

    private async Task<bool> CallLogExistsAsync(string adapterName)
    {
        return await _fixture.CreateContext().Set<AdapterCallLog>()
            .AnyAsync(x => x.AdapterName == adapterName, Xunit.TestContext.Current.CancellationToken);
    }

    private async Task<bool> DefinitionExistsAsync(string name)
    {
        return await _fixture.CreateContext().Set<AdapterDefinition>()
            .AnyAsync(x => x.Name == name, Xunit.TestContext.Current.CancellationToken);
    }
}
