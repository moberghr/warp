using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core.Data;
using Warp.Core.Data.Entities;
using Warp.Core.Notifications;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker;

namespace Warp.Tests.Worker;

/// <summary>
/// Verifies that <see cref="WarpDispatcherHost{TContext}"/> and
/// <see cref="WarpSingleWorkerHost{TContext}"/> respect the
/// <see cref="WarpServerConfiguration.UseDispatcher"/> flag — each no-ops when its mode is
/// not selected, and starts + stops cleanly when it is. The full end-to-end behavior of workers
/// actually fetching and processing jobs is covered by the integration tests; these unit tests
/// just pin the mode-branching contract so a future refactor can't silently break it.
/// </summary>
[GenerateDatabaseTests]
public abstract class WorkerHostModeTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected WorkerHostModeTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task DispatcherHost_UseDispatcherFalse_IsSilentNoOp()
    {
        // Arrange — state pre-populated as though registration ran
        var state = PopulateState(groupEntityId: Guid.NewGuid(), workerCount: 1);
        var host = CreateDispatcherHost(useDispatcher: false, state);

        // Act
        await host.StartAsync(Xunit.TestContext.Current.CancellationToken);
        await host.StopAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert — the wrong-mode host must not write any side-effect rows. WarpServerRegistration
        // owns Server/Worker/WorkerGroup insertion; a host that accidentally duplicates that work
        // would show up here.
        await AssertNoServerSideEffectsAsync();
    }

    [TimedFact]
    public async Task DispatcherHost_UseDispatcherTrue_CompletesLifecycleWithoutThrowing()
    {
        // Arrange
        var state = PopulateState(groupEntityId: Guid.NewGuid(), workerCount: 1);
        var host = CreateDispatcherHost(useDispatcher: true, state);

        // Act — pre-cancelled token short-circuits each BackgroundService.ExecuteAsync at
        // its first stoppingToken check, so we exercise the dispatcher + worker constructors
        // and the StartAsync/StopAsync round-trip without running the polling loop. The full
        // processing path is covered by the integration tests; this test pins the
        // mode-branching contract.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await host.StartAsync(cts.Token);
        await host.StopAsync(cts.Token);

        // Assert — host consumes state, doesn't produce it; no Server/Worker/WorkerGroup rows.
        await AssertNoServerSideEffectsAsync();
    }

    [TimedFact]
    public async Task SingleWorkerHost_UseDispatcherTrue_IsSilentNoOp()
    {
        // Arrange
        var state = PopulateState(groupEntityId: Guid.NewGuid(), workerCount: 1);
        var host = CreateSingleWorkerHost(useDispatcher: true, state);

        // Act
        await host.StartAsync(Xunit.TestContext.Current.CancellationToken);
        await host.StopAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert
        await AssertNoServerSideEffectsAsync();
    }

    [TimedFact]
    public async Task SingleWorkerHost_UseDispatcherFalse_CompletesLifecycleWithoutThrowing()
    {
        // Arrange
        var state = PopulateState(groupEntityId: Guid.NewGuid(), workerCount: 1);
        var host = CreateSingleWorkerHost(useDispatcher: false, state);

        // Act — pre-cancelled token short-circuits the worker's polling loop on entry.
        // See DispatcherHost_UseDispatcherTrue for the full rationale.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await host.StartAsync(cts.Token);
        await host.StopAsync(cts.Token);

        // Assert
        await AssertNoServerSideEffectsAsync();
    }

    [TimedFact]
    public async Task SingleWorkerHost_EmptyState_SilentNoOp()
    {
        // Arrange — state never populated (gap where registration was skipped)
        var state = new ServerRegistrationState();
        var host = CreateSingleWorkerHost(useDispatcher: false, state);

        // Act — silent no-op is the documented behavior; we pin it here so a future change
        // that raises an exception on empty state is a deliberate, visible decision.
        await host.StartAsync(Xunit.TestContext.Current.CancellationToken);
        await host.StopAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert
        state.Groups.Count.ShouldBe(0);
    }

    private WarpDispatcherHost<TestContext> CreateDispatcherHost(bool useDispatcher, ServerRegistrationState state)
    {
        var config = Options.Create(new WarpServerConfiguration
        {
            UseDispatcher = useDispatcher,
            WorkerCount = 1,
        });
        return new WarpDispatcherHost<TestContext>(
            config,
            BuildScopeFactory(),
            TimeProvider.System,
            new PauseStateHolder(),
            new NullNotificationTransport(),
            state,
            TestTasks.NullSignals,
            new DispatcherRegistry(),
            NullLoggerFactory.Instance,
            NullExceptionClassifier.Instance);
    }

    private sealed class NullExceptionClassifier : IDatabaseExceptionClassifier
    {
        public static readonly NullExceptionClassifier Instance = new();

        public bool IsUniqueConstraintViolation(DbUpdateException ex) => false;

        public bool IsTransientDeadlock(Exception ex) => false;
    }

    private WarpSingleWorkerHost<TestContext> CreateSingleWorkerHost(bool useDispatcher, ServerRegistrationState state)
    {
        var config = Options.Create(new WarpServerConfiguration
        {
            UseDispatcher = useDispatcher,
            WorkerCount = 1,
        });
        var scopeFactory = BuildScopeFactory();
        return new WarpSingleWorkerHost<TestContext>(
            config,
            scopeFactory,
            TimeProvider.System,
            new PauseStateHolder(),
            new NullNotificationTransport(),
            TestTasks.QueriesFromScope<TestContext>(scopeFactory),
            state,
            TestTasks.NullSignals,
            NullLoggerFactory.Instance);
    }

    private async Task AssertNoServerSideEffectsAsync()
    {
        var ctx = _fixture.CreateContext();
        var servers = await ctx.Set<Server>().CountAsync(Xunit.TestContext.Current.CancellationToken);
        servers.ShouldBe(0, "Worker hosts must consume ServerRegistrationState — not duplicate WarpServerRegistration's DB inserts.");

        var workerGroups = await ctx.Set<Warp.Core.Data.Entities.WorkerGroup>().CountAsync(Xunit.TestContext.Current.CancellationToken);
        workerGroups.ShouldBe(0);

        var workers = await ctx.Set<Warp.Core.Data.Entities.Worker>().CountAsync(Xunit.TestContext.Current.CancellationToken);
        workers.ShouldBe(0);
    }

    private IServiceScopeFactory BuildScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());
        services.AddTestServerContext<TestContext>();
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static ServerRegistrationState PopulateState(Guid groupEntityId, int workerCount)
    {
        var state = new ServerRegistrationState();
        var workerIds = Enumerable.Range(0, workerCount).Select(_ => Guid.NewGuid()).ToList();

        state.Set(
        [
            new ServerRegistrationState.GroupRegistration(
                new WorkerGroupConfiguration { WorkerCount = workerCount, Queues = ["default"] },
                groupEntityId,
                workerIds),
        ]);
        return state;
    }
}
