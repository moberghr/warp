using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;

namespace Warp.Tests.Worker;

// Pins the contract of IWarpSqlQueries.HeartbeatAsync: a single round-trip that updates
// last_heartbeat_time / memory / cpu AND returns the server's paused_at AND every worker
// group's pause state in one shot. Heartbeat depends on the full snapshot for
// PauseStateHolder refresh, so regressions here would silently break Pause/Resume.
[GenerateDatabaseTests]
public abstract class HeartbeatSqlQueryTestsBase : IAsyncLifetime
{
    private static readonly TimeSpan Skew = TimeSpan.FromHours(3);
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(30);

    private readonly IDatabaseFixture _fixture;

    protected HeartbeatSqlQueryTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task HeartbeatAsync_ExistingServerNoGroups_UpdatesAndReturnsEmptyGroups()
    {
        var serverId = await SeedServerAsync(pausedAt: null);
        var queries = TestTasks.QueriesFor(_fixture.CreateContext());
        var now = DateTime.UtcNow;

        await using var ctx = _fixture.CreateContext();
        var result = await queries.HeartbeatAsync(ctx, serverId, now, memoryBytes: 1234L, cpuPercent: 42.0, writeLocalTime: false, default);

        result.ShouldNotBeNull();
        result.ServerPausedAt.ShouldBeNull();
        result.GroupPaused.ShouldBeEmpty();

        // The row was actually written — read it back through a fresh context.
        await using var readCtx = _fixture.CreateContext();
        var persisted = await readCtx.Set<Server>().AsNoTracking().FirstAsync(s => s.Id == serverId);
        persisted.LastHeartbeatTime.ShouldBe(now, TimeSpan.FromSeconds(1));
        persisted.MemoryWorkingSetBytes.ShouldBe(1234L);
        persisted.CpuUsagePercent.ShouldBe(42.0);
    }

    [TimedFact]
    public async Task HeartbeatAsync_PausedServer_ReturnsPausedAt()
    {
        var pausedAt = DateTime.UtcNow.AddMinutes(-5);
        var serverId = await SeedServerAsync(pausedAt: pausedAt);
        var queries = TestTasks.QueriesFor(_fixture.CreateContext());

        await using var ctx = _fixture.CreateContext();
        var result = await queries.HeartbeatAsync(ctx, serverId, DateTime.UtcNow, memoryBytes: null, cpuPercent: null, writeLocalTime: false, default);

        result.ShouldNotBeNull();
        result.ServerPausedAt.ShouldNotBeNull();
        result.ServerPausedAt!.Value.ShouldBe(pausedAt, TimeSpan.FromSeconds(1));
    }

    [TimedFact]
    public async Task HeartbeatAsync_MissingServer_ReturnsNull()
    {
        var queries = TestTasks.QueriesFor(_fixture.CreateContext());

        await using var ctx = _fixture.CreateContext();
        var result = await queries.HeartbeatAsync(ctx, Guid.NewGuid(), DateTime.UtcNow, memoryBytes: null, cpuPercent: null, writeLocalTime: false, default);

        result.ShouldBeNull();
    }

    [TimedFact]
    public async Task HeartbeatAsync_NullMemoryAndCpu_PreservesExistingValues()
    {
        var serverId = await SeedServerAsync(pausedAt: null, memoryBytes: 999L, cpuPercent: 17.5);
        var queries = TestTasks.QueriesFor(_fixture.CreateContext());

        await using var ctx = _fixture.CreateContext();
        await queries.HeartbeatAsync(ctx, serverId, DateTime.UtcNow, memoryBytes: null, cpuPercent: null, writeLocalTime: false, default);

        await using var readCtx = _fixture.CreateContext();
        var persisted = await readCtx.Set<Server>().AsNoTracking().FirstAsync(s => s.Id == serverId);
        persisted.MemoryWorkingSetBytes.ShouldBe(999L);
        persisted.CpuUsagePercent.ShouldBe(17.5);
    }

    [TimedFact]
    public async Task HeartbeatAsync_WithGroups_ReturnsPauseStatePerGroup()
    {
        var serverId = await SeedServerAsync(pausedAt: null);
        var pausedGroupId = await SeedGroupAsync(serverId, pausedAt: DateTime.UtcNow);
        var activeGroupId = await SeedGroupAsync(serverId, pausedAt: null);

        var queries = TestTasks.QueriesFor(_fixture.CreateContext());
        await using var ctx = _fixture.CreateContext();
        var result = await queries.HeartbeatAsync(ctx, serverId, DateTime.UtcNow, null, null, writeLocalTime: false, default);

        result.ShouldNotBeNull();
        result.GroupPaused.Count.ShouldBe(2);
        result.GroupPaused[pausedGroupId].ShouldBeTrue();
        result.GroupPaused[activeGroupId].ShouldBeFalse();
    }

    [TimedFact]
    public async Task HeartbeatAsync_GroupsOnOtherServers_NotReturned()
    {
        var myServer = await SeedServerAsync(pausedAt: null);
        var otherServer = await SeedServerAsync(pausedAt: null);
        await SeedGroupAsync(otherServer, pausedAt: DateTime.UtcNow);

        var queries = TestTasks.QueriesFor(_fixture.CreateContext());
        await using var ctx = _fixture.CreateContext();
        var result = await queries.HeartbeatAsync(ctx, myServer, DateTime.UtcNow, null, null, writeLocalTime: false, default);

        result.ShouldNotBeNull();
        result.GroupPaused.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task HeartbeatAsync_DatabaseTime_WritesTheDatabasesClockNotTheOneItIsPassed()
    {
        // Arrange — this process's clock is three hours ahead of the database's.
        var serverId = await SeedServerAsync(pausedAt: null);
        await SeedServiceAsync(serverId, "ClockSvc", leaseExpiresAt: DateTime.UtcNow.AddSeconds(10));
        var queries = TestTasks.QueriesFor(_fixture.CreateContext());
        var local = DateTime.UtcNow.Add(Skew);

        // Act
        await using var ctx = _fixture.CreateContext();
        var result = await queries.HeartbeatAsync(ctx, serverId, local, null, null, writeLocalTime: false, default);

        // Assert — every time written is the database's; the offset is stored either way.
        result.ShouldNotBeNull();
        result.RenewedBackgroundServiceLeases.ShouldContain("ClockSvc");
        var (server, instance, lease) = await ReadWrittenTimesAsync(serverId, "ClockSvc");
        server.LastHeartbeatTime.ShouldBe(DateTime.UtcNow, Tolerance);
        server.ClockOffsetMs!.Value.ShouldBe(-Skew.TotalMilliseconds, Tolerance.TotalMilliseconds);
        instance.ShouldBe(DateTime.UtcNow, Tolerance);
        lease.ShouldBe(DateTime.UtcNow.AddSeconds(30), Tolerance);
    }

    [TimedFact]
    public async Task HeartbeatAsync_LocalTimeWhileAServerFromBefore80IsAlive_WritesTheClockItIsPassedAs77Did()
    {
        // Arrange — rule 2.20: a 7.7 server compares its own clock, so the heartbeat writes this process's, three
        // hours ahead, for the server, its instances and its leases (a lease due on that clock in 10 s).
        var serverId = await SeedServerAsync(pausedAt: null);
        var local = DateTime.UtcNow.Add(Skew);
        await SeedServiceAsync(serverId, "ClockSvc", leaseExpiresAt: local.AddSeconds(10));
        var queries = TestTasks.QueriesFor(_fixture.CreateContext());

        // Act
        await using var ctx = _fixture.CreateContext();
        var result = await queries.HeartbeatAsync(ctx, serverId, local, null, null, writeLocalTime: true, default);

        // Assert — the local time is written, while the offset and the database's clock are still measured.
        result.ShouldNotBeNull();
        result.RenewedBackgroundServiceLeases.ShouldContain("ClockSvc");
        result.DatabaseUtcNow!.Value.ShouldBe(DateTime.UtcNow, Tolerance);
        var (server, instance, lease) = await ReadWrittenTimesAsync(serverId, "ClockSvc");
        server.LastHeartbeatTime.ShouldBe(local, TimeSpan.FromSeconds(1));
        server.ClockOffsetMs!.Value.ShouldBe(-Skew.TotalMilliseconds, Tolerance.TotalMilliseconds);
        instance.ShouldBe(local, TimeSpan.FromSeconds(1));
        lease.ShouldBe(local.AddSeconds(30), TimeSpan.FromSeconds(1));
    }

    private async Task SeedServiceAsync(Guid serverId, string name, DateTime leaseExpiresAt)
    {
        await using var ctx = _fixture.CreateContext();
        ctx.Set<BackgroundServiceDefinition>().Add(new BackgroundServiceDefinition
        {
            Name = name,
            DeclaredScope = Warp.Core.BackgroundServices.ServiceScope.Singleton,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });
        ctx.Set<BackgroundServiceInstance>().Add(new BackgroundServiceInstance
        {
            ServerId = serverId,
            ServiceName = name,
            Status = Warp.Core.BackgroundServices.BackgroundServiceStatus.Running,
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            LastHeartbeatAt = DateTime.UtcNow.AddMinutes(-1),
        });
        ctx.Set<BackgroundServiceLease>().Add(new BackgroundServiceLease
        {
            ServiceName = name,
            HolderServerId = serverId,
            LeaseExpiresAt = leaseExpiresAt,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task<(Server Server, DateTime Instance, DateTime Lease)> ReadWrittenTimesAsync(Guid serverId, string name)
    {
        await using var ctx = _fixture.CreateContext();
        var server = await ctx.Set<Server>().AsNoTracking().FirstAsync(x => x.Id == serverId);
        var instance = await ctx.Set<BackgroundServiceInstance>()
            .Where(x => x.ServerId == serverId)
            .Select(x => x.LastHeartbeatAt)
            .FirstAsync();
        var lease = await ctx.Set<BackgroundServiceLease>()
            .Where(x => x.ServiceName == name)
            .Select(x => x.LeaseExpiresAt)
            .FirstAsync();

        return (server, instance, lease);
    }

    private async Task<Guid> SeedServerAsync(DateTime? pausedAt, long? memoryBytes = null, double? cpuPercent = null)
    {
        await using var ctx = _fixture.CreateContext();
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow.AddSeconds(-10);
        ctx.Set<Server>().Add(new Server
        {
            Id = id,
            StartedTime = now,
            LastHeartbeatTime = now,
            PausedAt = pausedAt,
            MemoryWorkingSetBytes = memoryBytes,
            CpuUsagePercent = cpuPercent,
        });
        await ctx.SaveChangesAsync();

        return id;
    }

    private async Task<Guid> SeedGroupAsync(Guid serverId, DateTime? pausedAt)
    {
        await using var ctx = _fixture.CreateContext();
        var group = new WorkerGroup
        {
            Id = Guid.NewGuid(),
            ServerId = serverId,
            PausedAt = pausedAt,
            WorkerCount = 1,
        };
        ctx.Set<WorkerGroup>().Add(group);
        await ctx.SaveChangesAsync();

        return group.Id;
    }
}
