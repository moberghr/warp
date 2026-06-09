using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core.BackgroundServices;
using Warp.Core.Data.Entities;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.BackgroundServices;
using Warp.Worker.Services;

namespace Warp.Tests.BackgroundServices;

/// <summary>
/// End-to-end proof that <c>AddWarpServer</c> + <c>opt.DisableWorker()</c> (service-only deployment)
/// runs <see cref="Warp.Core.BackgroundServices.WarpBackgroundService"/> instances against a real
/// database with NO job worker: a Server row is created (FK target) but zero Worker/WorkerGroup
/// rows, the background-service lifecycle (start → graceful-shutdown delete) works exactly as it
/// does under the full worker, Singleton lease coordination works, and the shared
/// <c>ExpirationCleanup</c> server task fires.
/// <para>
/// Boots through <see cref="WarpTestServer"/> (the standard integration harness) with the worker
/// disabled, so the service-only path exercises the same host bootstrap as production rather than a
/// bespoke copy.
/// </para>
/// </summary>
[GenerateDatabaseTests]
public abstract class ServiceOnlyHostTestsBase : IntegrationTestBase
{
    protected ServiceOnlyHostTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact(15_000)]
    public async Task ServiceOnly_PerServerService_ReachesUserCode()
    {
        var barrier = new BackgroundServiceBarrierSignal();

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.DisableWorker();
                cfg.AddBackgroundService<BarrierPinnedService>();
            },
            configureServices: services => services.AddSingleton(barrier));

        var reached = await barrier.Running.WaitAsync(
            TimeSpan.FromSeconds(8),
            Xunit.TestContext.Current.CancellationToken);

        reached.ShouldBeTrue("BarrierPinnedService should reach ExecuteAsync under a service-only host within 8s");

        barrier.CanFinish.Release();
    }

    [TimedFact(15_000)]
    public async Task ServiceOnly_CreatesServerRow_NoWorkerRows()
    {
        var barrier = new BackgroundServiceBarrierSignal();

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.DisableWorker();
                cfg.AddBackgroundService<BarrierPinnedService>();
            },
            configureServices: services => services.AddSingleton(barrier));

        // Wait for full startup (service in ExecuteAsync) so WarpServerRegistration has run.
        await barrier.Running.WaitAsync(Xunit.TestContext.Current.CancellationToken);

        var ctx = Fixture.CreateContext();

        var serverExists = await ctx.Set<Server>()
            .Where(x => x.Id == server.ServerId)
            .AnyAsync(Xunit.TestContext.Current.CancellationToken);
        serverExists.ShouldBeTrue("a Server row is the FK target for background-service rows");

        var workerCount = await ctx.Set<Warp.Core.Data.Entities.Worker>()
            .Where(x => x.ServerId == server.ServerId)
            .CountAsync(Xunit.TestContext.Current.CancellationToken);
        workerCount.ShouldBe(0, "a service-only server runs no workers");

        var workerGroupCount = await ctx.Set<WorkerGroup>()
            .Where(x => x.ServerId == server.ServerId)
            .CountAsync(Xunit.TestContext.Current.CancellationToken);
        workerGroupCount.ShouldBe(0, "a service-only server registers no worker groups");

        barrier.CanFinish.Release();
    }

    [TimedFact(15_000)]
    public async Task ServiceOnly_GracefulShutdown_DeletesInstanceRow()
    {
        var barrier = new BackgroundServiceBarrierSignal();

        var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.DisableWorker();
                cfg.AddBackgroundService<BarrierPinnedService>();
            },
            configureServices: services => services.AddSingleton(barrier));

        var serverId = server.ServerId;

        await barrier.Running.WaitAsync(Xunit.TestContext.Current.CancellationToken);

        // Release so ExecuteAsync can return on cancellation, then stop the host — the supervisor
        // deletes the instance row on graceful exit.
        barrier.CanFinish.Release();
        await server.DisposeAsync();

        var instance = await Fixture.CreateContext().Set<BackgroundServiceInstance>()
            .Where(x => x.ServerId == serverId)
            .Where(x => x.ServiceName == nameof(BarrierPinnedService))
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

        instance.ShouldBeNull("instance row should be deleted on graceful shutdown");
    }

    [TimedFact(15_000)]
    public async Task ServiceOnly_SingletonService_AcquiresLease()
    {
        var barrier = new SingletonBarrierSignal();

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.DisableWorker();
                cfg.AddBackgroundService<SingletonBarrierService>();
                cfg.BackgroundServiceAcquirePollInterval = TimeSpan.FromMilliseconds(200);
                cfg.BackgroundServiceLeaseTtl = TimeSpan.FromSeconds(30);
            },
            configureServices: services => services.AddSingleton(barrier));

        var reached = await barrier.Running.WaitAsync(
            TimeSpan.FromSeconds(8),
            Xunit.TestContext.Current.CancellationToken);
        reached.ShouldBeTrue("the singleton service must acquire the lease and enter ExecuteAsync under a service-only host");

        // The lease row must exist and be held by this server — proving the lease coordinator +
        // Heartbeat renewal path is wired in the service-only tier, not just the full worker.
        var lease = await Fixture.CreateContext().Set<BackgroundServiceLease>()
            .Where(x => x.ServiceName == nameof(SingletonBarrierService))
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

        lease.ShouldNotBeNull("a BackgroundServiceLease row must exist for the singleton holder");
        lease.HolderServerId.ShouldBe(server.ServerId);

        barrier.CanFinish.Release();
    }

    [TimedFact(15_000)]
    public async Task ServiceOnly_ExpirationCleanup_RemovesOrphanedDefinition()
    {
        var barrier = new BackgroundServiceBarrierSignal();

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.DisableWorker();
                cfg.AddBackgroundService<BarrierPinnedService>();
            },
            configureServices: services => services.AddSingleton(barrier));

        await barrier.Running.WaitAsync(Xunit.TestContext.Current.CancellationToken);

        // Seed an orphaned definition: no live Instance references it, and LastSeenAt is well past
        // the orphan grace (default 2 min).
        var seedCtx = Fixture.CreateContext();
        var staleAt = DateTime.UtcNow.AddMinutes(-30);
        seedCtx.Set<BackgroundServiceDefinition>().Add(new BackgroundServiceDefinition
        {
            Name = "Orphaned.ServiceOnly.Service",
            DeclaredScope = ServiceScope.PerServer,
            FirstSeenAt = staleAt,
            LastSeenAt = staleAt,
        });
        await seedCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Drive the shared ExpirationCleanup task once under the service-only host — proves it is
        // registered AND executes in this tier (not just the full worker).
        await server.RunServerTaskOnceAsync<ExpirationCleanup<TestContext>>();

        var stillExists = await Fixture.CreateContext().Set<BackgroundServiceDefinition>()
            .Where(x => x.Name == "Orphaned.ServiceOnly.Service")
            .AnyAsync(Xunit.TestContext.Current.CancellationToken);

        stillExists.ShouldBeFalse("ExpirationCleanup should GC the orphaned definition under a service-only host");

        barrier.CanFinish.Release();
    }
}
