using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Services;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Applications;

/// <summary>
/// Batch 7 coverage for <see cref="ApplicationQueryService{TContext}"/>: the unified instance roster
/// (Server ∪ ApplicationInstance → InstanceView), per-app detail + version/environment spread, single
/// instance detail with recent lifecycle events, live-vs-stale classification, and resolution in an
/// AddWarp-only (no server) DI graph.
/// </summary>
[GenerateDatabaseTests(SerializeInCollection = "HeavyIntegration")]
public abstract class ApplicationQueryTestsBase : IAsyncLifetime
{
    private static readonly TimeSpan StaleGrace = TimeSpan.FromMinutes(2);

    private readonly IDatabaseFixture _fixture;

    protected ApplicationQueryTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task GetApplications_GroupsInstancesAcrossBothTables_WithCountsAndLiveness()
    {
        // "orders": one live server + one stale non-server instance.
        await InsertServerAsync("orders", version: "1.0.0", environment: "prod", live: true, cpu: 12.5, memory: 1000);
        await InsertInstanceAsync("orders", version: "1.1.0", environment: "prod", live: false, cpu: 99, memory: 9999);

        // "billing": one live non-server instance.
        await InsertInstanceAsync("billing", version: "2.0.0", environment: "staging", live: true, cpu: 5, memory: 500);

        // A server WITHOUT an application must not participate (feature is opt-in).
        await InsertServerAsync(application: null, version: null, environment: null, live: true, cpu: 1, memory: 1);

        var roster = await CreateService().GetApplications(Ct);

        roster.Count.ShouldBe(2);

        var orders = roster.Single(x => string.Equals(x.Name, "orders", StringComparison.Ordinal));
        orders.InstanceCount.ShouldBe(2);
        orders.LiveInstanceCount.ShouldBe(1);

        // Only the live server reports CPU/RAM toward the rollup; the stale instance is excluded.
        orders.TotalCpuUsagePercent.ShouldBe(12.5);
        orders.TotalMemoryWorkingSetBytes.ShouldBe(1000);
        orders.Versions.ShouldBe(["1.0.0", "1.1.0"]);
        orders.Environments.ShouldBe(["prod"]);

        var billing = roster.Single(x => string.Equals(x.Name, "billing", StringComparison.Ordinal));
        billing.InstanceCount.ShouldBe(1);
        billing.LiveInstanceCount.ShouldBe(1);
    }

    [TimedFact]
    public async Task GetApplicationDetail_ReturnsUnifiedInstances_WithServerFlagAndSpread()
    {
        await InsertServerAsync("orders", version: "1.0.0", environment: "prod", live: true, cpu: 10, memory: 100);
        await InsertInstanceAsync("orders", version: "1.1.0", environment: "test", live: true, cpu: 20, memory: 200);

        var detail = await CreateService().GetApplicationDetail("orders", Ct);

        detail.ShouldNotBeNull();
        detail!.Name.ShouldBe("orders");
        detail.Instances.Count.ShouldBe(2);
        detail.Instances.Count(x => x.IsServer).ShouldBe(1);
        detail.Instances.Count(x => !x.IsServer).ShouldBe(1);
        detail.Instances.ShouldAllBe(x => string.Equals(x.Application, "orders", StringComparison.Ordinal));
        detail.Versions.ShouldBe(["1.0.0", "1.1.0"]);
        detail.Environments.ShouldBe(["prod", "test"]);
    }

    [TimedFact]
    public async Task GetApplicationDetail_UnknownApplication_ReturnsNull()
    {
        await InsertInstanceAsync("orders", version: null, environment: null, live: true, cpu: null, memory: null);

        (await CreateService().GetApplicationDetail("nope", Ct)).ShouldBeNull();
    }

    [TimedFact]
    public async Task GetInstanceDetail_NonServerInstance_ReturnsViewAndRecentEvents()
    {
        var instanceId = await InsertInstanceAsync("orders", version: "1.0.0", environment: "prod", live: true, cpu: 7, memory: 70);
        await InsertLogAsync(instanceId, "orders", ApplicationInstanceEventType.Registered, DateTime.UtcNow.AddMinutes(-2));
        await InsertLogAsync(instanceId, "orders", ApplicationInstanceEventType.HeartbeatLost, DateTime.UtcNow.AddMinutes(-1));

        // A log for a DIFFERENT instance must not leak into this timeline.
        await InsertLogAsync(Guid.NewGuid(), "orders", ApplicationInstanceEventType.Stopped, DateTime.UtcNow);

        var detail = await CreateService().GetInstanceDetail("orders", instanceId, Ct);

        detail.ShouldNotBeNull();
        detail!.Instance.Id.ShouldBe(instanceId);
        detail.Instance.IsServer.ShouldBeFalse();
        detail.Instance.IsLive.ShouldBeTrue();
        detail.RecentEvents.Count.ShouldBe(2);

        // Newest first.
        detail.RecentEvents[0].EventType.ShouldBe(ApplicationInstanceEventType.HeartbeatLost);
        detail.RecentEvents[1].EventType.ShouldBe(ApplicationInstanceEventType.Registered);
    }

    [TimedFact]
    public async Task GetInstanceDetail_ServerInstance_ReturnsServerView()
    {
        var serverId = await InsertServerAsync("orders", version: "1.0.0", environment: "prod", live: true, cpu: 3, memory: 30);

        var detail = await CreateService().GetInstanceDetail("orders", serverId, Ct);

        detail.ShouldNotBeNull();
        detail!.Instance.Id.ShouldBe(serverId);
        detail.Instance.IsServer.ShouldBeTrue();
        detail.Instance.Version.ShouldBe("1.0.0");
    }

    [TimedFact]
    public async Task GetInstanceDetail_UnknownInstance_ReturnsNull()
    {
        await InsertInstanceAsync("orders", version: null, environment: null, live: true, cpu: null, memory: null);

        (await CreateService().GetInstanceDetail("orders", Guid.NewGuid(), Ct)).ShouldBeNull();
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private ApplicationQueryService<TestContext> CreateService()
    {
        return new ApplicationQueryService<TestContext>(
            _fixture.CreateContext(),
            TimeProvider.System,
            Options.Create(new WarpConfiguration { ApplicationInstanceStaleGrace = StaleGrace }));
    }

    private async Task<Guid> InsertServerAsync(string? application, string? version, string? environment, bool live, double? cpu, long? memory)
    {
        var id = Guid.NewGuid();
        var heartbeat = live ? DateTime.UtcNow.AddSeconds(-5) : DateTime.UtcNow.Subtract(StaleGrace).AddMinutes(-5);

        var ctx = _fixture.CreateContext();
        ctx.Set<Server>().Add(new Server
        {
            Id = id,
            ServerName = "host-" + id.ToString("N")[..6],
            Application = application,
            Version = version,
            Environment = environment,
            StartedTime = DateTime.UtcNow.AddHours(-1),
            LastHeartbeatTime = heartbeat,
            CpuUsagePercent = cpu,
            MemoryWorkingSetBytes = memory,
        });

        await ctx.SaveChangesAsync(Ct);

        return id;
    }

    private async Task<Guid> InsertInstanceAsync(string application, string? version, string? environment, bool live, double? cpu, long? memory)
    {
        var id = Guid.NewGuid();
        var heartbeat = live ? DateTime.UtcNow.AddSeconds(-5) : DateTime.UtcNow.Subtract(StaleGrace).AddMinutes(-5);

        var ctx = _fixture.CreateContext();
        ctx.Set<ApplicationInstance>().Add(new ApplicationInstance
        {
            Id = id,
            ApplicationName = application,
            MachineName = "host-" + id.ToString("N")[..6],
            StartedAt = DateTime.UtcNow.AddHours(-1),
            LastHeartbeatAt = heartbeat,
            CpuUsagePercent = cpu,
            MemoryWorkingSetBytes = memory,
            Version = version,
            Environment = environment,
        });

        await ctx.SaveChangesAsync(Ct);

        return id;
    }

    private async Task InsertLogAsync(Guid instanceId, string application, ApplicationInstanceEventType eventType, DateTime timestamp)
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<ApplicationInstanceLog>().Add(new ApplicationInstanceLog
        {
            InstanceId = instanceId,
            ApplicationName = application,
            Timestamp = timestamp,
            EventType = eventType,
        });

        await ctx.SaveChangesAsync(Ct);
    }
}
