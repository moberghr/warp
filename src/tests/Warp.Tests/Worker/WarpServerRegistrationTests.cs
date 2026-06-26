using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Tests.Fixtures;
using Warp.Worker;

namespace Warp.Tests.Worker;

[GenerateDatabaseTests]
public abstract class WarpServerRegistrationTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected WarpServerRegistrationTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task StartAsync_WritesServerRow()
    {
        // Arrange
        var serverId = Guid.NewGuid();
        var (registration, _) = CreateRegistration(serverId);

        // Act
        await registration.StartAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert
        var readCtx = _fixture.CreateContext();
        var server = await readCtx.Set<Server>().FirstOrDefaultAsync(x => x.Id == serverId, Xunit.TestContext.Current.CancellationToken);
        server.ShouldNotBeNull();
        server.ServiceCount.ShouldBe(2);
    }

    [TimedFact]
    public async Task StartAsync_WritesWorkerGroupAndWorkerRows()
    {
        // Arrange
        var serverId = Guid.NewGuid();
        var (registration, _) = CreateRegistration(serverId);

        // Act
        await registration.StartAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert
        var readCtx = _fixture.CreateContext();
        var groups = await readCtx.Set<Warp.Core.Data.Entities.WorkerGroup>()
            .Where(x => x.ServerId == serverId)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        groups.Count.ShouldBe(1);
        groups[0].WorkerCount.ShouldBe(2);

        var workers = await readCtx.Set<Warp.Core.Data.Entities.Worker>()
            .Where(x => x.ServerId == serverId)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        workers.Count.ShouldBe(2);
    }

    [TimedFact]
    public async Task StartAsync_PopulatesRegistrationState()
    {
        // Arrange
        var serverId = Guid.NewGuid();
        var (registration, state) = CreateRegistration(serverId);

        // Act
        await registration.StartAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert
        state.Groups.Count.ShouldBe(1);
        state.Groups[0].WorkerIds.Count.ShouldBe(2);
        state.Groups[0].GroupEntityId.ShouldNotBe(Guid.Empty);
    }

    [TimedFact]
    public async Task StopAsync_RemovesServerAndWorkerAndWorkerGroupRows()
    {
        // Arrange
        var serverId = Guid.NewGuid();
        var (registration, _) = CreateRegistration(serverId);
        await registration.StartAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        await registration.StopAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert
        var readCtx = _fixture.CreateContext();
        var server = await readCtx.Set<Server>().FirstOrDefaultAsync(x => x.Id == serverId, Xunit.TestContext.Current.CancellationToken);
        server.ShouldBeNull();

        var workers = await readCtx.Set<Warp.Core.Data.Entities.Worker>()
            .Where(x => x.ServerId == serverId)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        workers.Count.ShouldBe(0);

        // WorkerGroup rows must not be orphaned — Server→WorkerGroup FK has no DB-level
        // cascade, so StopAsync is the only thing that cleans them up on graceful shutdown.
        var workerGroups = await readCtx.Set<Warp.Core.Data.Entities.WorkerGroup>()
            .Where(x => x.ServerId == serverId)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        workerGroups.Count.ShouldBe(0);
    }

    [TimedFact]
    public async Task StartAsync_ZeroWorkerCount_WritesServerButNoWorkerOrGroupRows()
    {
        // Arrange — an explicit zero-worker group (the per-group skip), exercised directly on
        // WarpServerRegistration. (A service-only server reaches this via RunWorker = false, not
        // WorkerCount = 0; AddWarpServer rejects RunWorker = true with zero workers.)
        var serverId = Guid.NewGuid();
        var (registration, state) = CreateRegistration(serverId, c => c.WorkerCount = 0);

        // Act
        await registration.StartAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert — the Server row exists (FK target for background-service rows) but no
        // WorkerGroup/Worker rows and no group registrations.
        var readCtx = _fixture.CreateContext();
        var server = await readCtx.Set<Server>().FirstOrDefaultAsync(x => x.Id == serverId, Xunit.TestContext.Current.CancellationToken);
        server.ShouldNotBeNull();
        server.ServiceCount.ShouldBe(0);

        var groups = await readCtx.Set<Warp.Core.Data.Entities.WorkerGroup>()
            .Where(x => x.ServerId == serverId)
            .CountAsync(Xunit.TestContext.Current.CancellationToken);
        groups.ShouldBe(0);

        var workers = await readCtx.Set<Warp.Core.Data.Entities.Worker>()
            .Where(x => x.ServerId == serverId)
            .CountAsync(Xunit.TestContext.Current.CancellationToken);
        workers.ShouldBe(0);

        state.Groups.Count.ShouldBe(0);
    }

    [TimedFact]
    public async Task StartAsync_MixedGroups_SkipsZeroGroup_KeepsExplicitGroup()
    {
        // Arrange — zero-worker implicit default group plus one explicit group with workers.
        // The skip must apply only to the zero-count group.
        var serverId = Guid.NewGuid();
        var (registration, state) = CreateRegistration(serverId, c =>
        {
            c.WorkerCount = 0;
            c.AddWorkerGroup(g => g.WorkerCount = 3);
        });

        // Act
        await registration.StartAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert — only the explicit (3-worker) group materialises.
        var readCtx = _fixture.CreateContext();
        var groups = await readCtx.Set<Warp.Core.Data.Entities.WorkerGroup>()
            .Where(x => x.ServerId == serverId)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        groups.Count.ShouldBe(1);
        groups[0].WorkerCount.ShouldBe(3);

        var workers = await readCtx.Set<Warp.Core.Data.Entities.Worker>()
            .Where(x => x.ServerId == serverId)
            .CountAsync(Xunit.TestContext.Current.CancellationToken);
        workers.ShouldBe(3);

        state.Groups.Count.ShouldBe(1);
        state.Groups[0].WorkerIds.Count.ShouldBe(3);

        var server = await readCtx.Set<Server>().FirstOrDefaultAsync(x => x.Id == serverId, Xunit.TestContext.Current.CancellationToken);
        server.ShouldNotBeNull();
        server.ServiceCount.ShouldBe(3);
    }

    [TimedFact]
    public async Task StartAsync_RunWorkerFalse_NoGroupsEvenWithWorkerCountSet()
    {
        // Arrange — RunWorker = false must suppress all worker groups/rows regardless of a
        // non-zero WorkerCount (the guard is independent of the per-group zero-skip).
        var serverId = Guid.NewGuid();
        var (registration, state) = CreateRegistration(serverId, c =>
        {
            c.WorkerCount = 5;
            c.RunWorker = false;
        });

        // Act
        await registration.StartAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert
        var readCtx = _fixture.CreateContext();
        var server = await readCtx.Set<Server>().FirstOrDefaultAsync(x => x.Id == serverId, Xunit.TestContext.Current.CancellationToken);
        server.ShouldNotBeNull();
        server.ServiceCount.ShouldBe(0);

        var groups = await readCtx.Set<Warp.Core.Data.Entities.WorkerGroup>()
            .Where(x => x.ServerId == serverId)
            .CountAsync(Xunit.TestContext.Current.CancellationToken);
        groups.ShouldBe(0);

        var workers = await readCtx.Set<Warp.Core.Data.Entities.Worker>()
            .Where(x => x.ServerId == serverId)
            .CountAsync(Xunit.TestContext.Current.CancellationToken);
        workers.ShouldBe(0);

        state.Groups.Count.ShouldBe(0);
    }

    [TimedFact]
    public async Task StopAsync_NoServerRow_GracefulNoOp()
    {
        // Arrange — registration that never ran StartAsync
        var serverId = Guid.NewGuid();
        var (registration, _) = CreateRegistration(serverId);

        // Act
        await registration.StopAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert — no server row was created as a side effect, no exception thrown
        var readCtx = _fixture.CreateContext();
        var server = await readCtx.Set<Server>().FirstOrDefaultAsync(x => x.Id == serverId, Xunit.TestContext.Current.CancellationToken);
        server.ShouldBeNull();
    }

    private (WarpServerRegistration<TestContext> Registration, ServerRegistrationState State) CreateRegistration(
        Guid serverId,
        Action<WarpServerConfiguration>? configure = null)
    {
        var configuration = new WarpServerConfiguration
        {
            ServerId = serverId,
            WorkerCount = 2,
        };
        configure?.Invoke(configuration);
        var config = Options.Create(configuration);
        var services = new ServiceCollection();
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());
        services.AddScoped<Warp.Core.IWarpServerContext>(x => new Warp.Tests.Helpers.TestServerContext(x.GetRequiredService<TestContext>()));
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var state = new ServerRegistrationState();
        var pauseStateHolder = new PauseStateHolder();
        var registration = new WarpServerRegistration<TestContext>(
            config,
            scopeFactory,
            TimeProvider.System,
            pauseStateHolder,
            state);
        return (registration, state);
    }
}
