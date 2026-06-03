using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core.BackgroundServices;
using Warp.Core.Data.Entities;
using Warp.Tests.Fixtures;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.BackgroundServices;

[GenerateDatabaseTests]
public abstract class OrphanDefinitionCleanupTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;
    private static readonly Guid ServerA = Guid.NewGuid();
    private static readonly Guid ServerB = Guid.NewGuid();

    protected OrphanDefinitionCleanupTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetAsync();
        await _fixture.SeedServerAsync(ServerA, "orphan-test-a");
        await _fixture.SeedServerAsync(ServerB, "orphan-test-b");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private ExpirationCleanup<TestContext> CreateCleanup(TimeSpan? orphanGrace = null, TimeProvider? time = null)
    {
        var config = new WarpWorkerConfiguration
        {
            BackgroundServiceDefinitionOrphanGrace = orphanGrace ?? TimeSpan.FromMinutes(2),
        };

        return new ExpirationCleanup<TestContext>(
            _fixture.CreateContext(),
            time ?? TimeProvider.System,
            Options.Create(config));
    }

    private async Task InsertDefinitionAsync(string name, DateTime lastSeenAt, DateTime? firstSeenAt = null)
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<BackgroundServiceDefinition>().Add(new BackgroundServiceDefinition
        {
            Name = name,
            DeclaredScope = ServiceScope.PerServer,
            FirstSeenAt = firstSeenAt ?? lastSeenAt,
            LastSeenAt = lastSeenAt,
        });

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
    }

    private async Task InsertInstanceAsync(Guid serverId, string serviceName)
    {
        var ctx = _fixture.CreateContext();
        var now = DateTime.UtcNow;
        ctx.Set<BackgroundServiceInstance>().Add(new BackgroundServiceInstance
        {
            ServerId = serverId,
            ServiceName = serviceName,
            DeclaredScope = ServiceScope.PerServer,
            Status = BackgroundServiceStatus.Running,
            StartedAt = now,
            LastHeartbeatAt = now,
            RestartCount = 0,
        });

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
    }

    [TimedFact]
    public async Task Cleanup_DefinitionWithNoInstance_OlderThanGrace_Deleted()
    {
        var staleLastSeen = DateTime.UtcNow.AddMinutes(-10);
        await InsertDefinitionAsync("OrphanedService", staleLastSeen);

        var cleanup = CreateCleanup();

        await cleanup.CleanupOrphanedBackgroundServiceDefinitionsAsync(Xunit.TestContext.Current.CancellationToken);

        var assertCtx = _fixture.CreateContext();
        var remaining = await assertCtx.Set<BackgroundServiceDefinition>()
            .Where(x => x.Name == "OrphanedService")
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

        remaining.ShouldBeNull();
    }

    [TimedFact]
    public async Task Cleanup_DefinitionWithLiveInstance_Kept()
    {
        var ancientLastSeen = DateTime.UtcNow.AddDays(-30);
        await InsertDefinitionAsync("ActiveService", ancientLastSeen);
        await InsertInstanceAsync(ServerA, "ActiveService");

        var cleanup = CreateCleanup();

        await cleanup.CleanupOrphanedBackgroundServiceDefinitionsAsync(Xunit.TestContext.Current.CancellationToken);

        var assertCtx = _fixture.CreateContext();
        var remaining = await assertCtx.Set<BackgroundServiceDefinition>()
            .Where(x => x.Name == "ActiveService")
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

        remaining.ShouldNotBeNull();
    }

    [TimedFact]
    public async Task Cleanup_DefinitionWithNoInstance_WithinGrace_Kept()
    {
        var recentLastSeen = DateTime.UtcNow.AddSeconds(-30);
        await InsertDefinitionAsync("RecentlyOrphanedService", recentLastSeen);

        var cleanup = CreateCleanup(orphanGrace: TimeSpan.FromMinutes(2));

        await cleanup.CleanupOrphanedBackgroundServiceDefinitionsAsync(Xunit.TestContext.Current.CancellationToken);

        var assertCtx = _fixture.CreateContext();
        var remaining = await assertCtx.Set<BackgroundServiceDefinition>()
            .Where(x => x.Name == "RecentlyOrphanedService")
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

        remaining.ShouldNotBeNull();
    }

    [TimedFact]
    public async Task Cleanup_DefinitionWithInstanceOnDifferentServer_Kept()
    {
        // Server A's instance gone (departed), but server B still hosts it. Definition must stay.
        var ancientLastSeen = DateTime.UtcNow.AddDays(-1);
        await InsertDefinitionAsync("MultiServerService", ancientLastSeen);
        await InsertInstanceAsync(ServerB, "MultiServerService");

        var cleanup = CreateCleanup();

        await cleanup.CleanupOrphanedBackgroundServiceDefinitionsAsync(Xunit.TestContext.Current.CancellationToken);

        var assertCtx = _fixture.CreateContext();
        var remaining = await assertCtx.Set<BackgroundServiceDefinition>()
            .Where(x => x.Name == "MultiServerService")
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

        remaining.ShouldNotBeNull();
    }

    // Paired tests pinning that the orphan predicate uses LastSeenAt, NOT FirstSeenAt.
    //
    // 1. Old FirstSeenAt + RECENT LastSeenAt + no Instance → kept (LastSeenAt within grace).
    //    A predicate accidentally filtering on FirstSeenAt would delete this row.
    // 2. RECENT FirstSeenAt + OLD LastSeenAt + no Instance → deleted (LastSeenAt past grace).
    //    A predicate accidentally filtering on FirstSeenAt would keep this row.
    //
    // Together they fail if anyone swaps the field in either direction.
    [TimedFact]
    public async Task Cleanup_OldFirstSeenAt_RecentLastSeenAt_Kept()
    {
        var oldFirstSeen = DateTime.UtcNow.AddDays(-365);
        var recentLastSeen = DateTime.UtcNow.AddSeconds(-1);
        await InsertDefinitionAsync("LongRunningService", recentLastSeen, oldFirstSeen);

        var cleanup = CreateCleanup();

        await cleanup.CleanupOrphanedBackgroundServiceDefinitionsAsync(Xunit.TestContext.Current.CancellationToken);

        var assertCtx = _fixture.CreateContext();
        var remaining = await assertCtx.Set<BackgroundServiceDefinition>()
            .Where(x => x.Name == "LongRunningService")
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

        remaining.ShouldNotBeNull();
    }

    [TimedFact]
    public async Task Cleanup_RecentFirstSeenAt_OldLastSeenAt_Deleted()
    {
        var recentFirstSeen = DateTime.UtcNow.AddSeconds(-1);
        var oldLastSeen = DateTime.UtcNow.AddMinutes(-10);
        await InsertDefinitionAsync("RestamperOrphan", oldLastSeen, recentFirstSeen);

        var cleanup = CreateCleanup();

        await cleanup.CleanupOrphanedBackgroundServiceDefinitionsAsync(Xunit.TestContext.Current.CancellationToken);

        var assertCtx = _fixture.CreateContext();
        var remaining = await assertCtx.Set<BackgroundServiceDefinition>()
            .Where(x => x.Name == "RestamperOrphan")
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

        remaining.ShouldBeNull();
    }

    [TimedFact]
    public async Task Cleanup_MixedOrphanAndActive_OnlyOrphanDeleted()
    {
        var staleLastSeen = DateTime.UtcNow.AddMinutes(-10);
        await InsertDefinitionAsync("OldRenamed", staleLastSeen);
        await InsertDefinitionAsync("StillRunning", staleLastSeen);
        await InsertInstanceAsync(ServerA, "StillRunning");

        var cleanup = CreateCleanup();

        await cleanup.CleanupOrphanedBackgroundServiceDefinitionsAsync(Xunit.TestContext.Current.CancellationToken);

        var assertCtx = _fixture.CreateContext();
        var names = await assertCtx.Set<BackgroundServiceDefinition>()
            .Select(d => d.Name)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        names.ShouldContain("StillRunning");
        names.ShouldNotContain("OldRenamed");
    }
}
