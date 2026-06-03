using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Warp.Core.BackgroundServices;
using Warp.Core.Data.Entities;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.BackgroundServices;

namespace Warp.Tests.BackgroundServices;

[GenerateDatabaseTests]
public abstract class ConfigurationMismatchTestsBase : IntegrationTestBase
{
    protected ConfigurationMismatchTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// Pre-seed a PerServer Definition row, then start a server that declares Singleton.
    /// The supervisor must not invoke user code — instance row should have ConfigurationMismatch.
    /// </summary>
    [TimedFact(15_000)]
    public async Task ScopeMismatch_DefinitionPerServer_HostDeclaresSingleton_StatusConfigurationMismatch()
    {
        var state = new CountingServiceState();

        // Seed: Definition with PerServer scope.
        var seedCtx = Fixture.CreateContext();
        seedCtx.Set<BackgroundServiceDefinition>().Add(new BackgroundServiceDefinition
        {
            Name = nameof(MismatchSingletonCountingService),
            DeclaredScope = ServiceScope.PerServer,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });
        await seedCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.AddBackgroundService<MismatchSingletonCountingService>(),
            configureServices: services => services.AddSingleton(state));

        // The supervisor should detect the mismatch and write ConfigurationMismatch status.
        await server.WaitForBackgroundServiceState(
            nameof(MismatchSingletonCountingService),
            BackgroundServiceStatus.ConfigurationMismatch,
            TimeSpan.FromSeconds(8));

        // Assert user code was NOT invoked.
        state.Count.ShouldBe(0);
    }

    [TimedFact(15_000)]
    public async Task ScopeMismatch_DefinitionSingleton_HostDeclaresPerServer_StatusConfigurationMismatch()
    {
        var state = new CountingServiceState();

        // Seed: Definition with Singleton scope.
        var seedCtx = Fixture.CreateContext();
        seedCtx.Set<BackgroundServiceDefinition>().Add(new BackgroundServiceDefinition
        {
            Name = nameof(MismatchPerServerCountingService),
            DeclaredScope = ServiceScope.Singleton,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });
        await seedCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.AddBackgroundService<MismatchPerServerCountingService>(),
            configureServices: services => services.AddSingleton(state));

        await server.WaitForBackgroundServiceState(
            nameof(MismatchPerServerCountingService),
            BackgroundServiceStatus.ConfigurationMismatch,
            TimeSpan.FromSeconds(8));

        state.Count.ShouldBe(0);
    }

    [TimedFact(15_000)]
    public async Task ScopeMismatch_DefinitionPerServer_HostDeclaresSingleton_LifecycleLogErrorWritten()
    {
        var state = new CountingServiceState();

        // Seed: Definition with PerServer scope.
        var seedCtx = Fixture.CreateContext();
        seedCtx.Set<BackgroundServiceDefinition>().Add(new BackgroundServiceDefinition
        {
            Name = nameof(MismatchSingletonCountingService),
            DeclaredScope = ServiceScope.PerServer,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });
        await seedCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.AddBackgroundService<MismatchSingletonCountingService>(),
            configureServices: services => services.AddSingleton(state));

        // Wait for the mismatch status to be written — proves the supervisor reached the mismatch branch.
        await server.WaitForBackgroundServiceState(
            nameof(MismatchSingletonCountingService),
            BackgroundServiceStatus.ConfigurationMismatch,
            TimeSpan.FromSeconds(8));

        // The supervisor also calls LogConfigurationMismatch which enqueues a Lifecycle/Error log.
        // Wait for the collector to flush the row to the DB (the flush fires every ~1s).
        await WarpTestServer.WaitUntil(
            async () =>
            {
                var logCtx = Fixture.CreateContext();

                return await logCtx.Set<BackgroundServiceLog>()
                    .Where(x => x.ServiceName == nameof(MismatchSingletonCountingService))
                    .Where(x => x.Source == BackgroundServiceLogSource.Lifecycle)
                    .Where(x => x.Level == LogLevel.Error)
                    .AnyAsync(Xunit.TestContext.Current.CancellationToken);
            },
            timeout: TimeSpan.FromSeconds(8),
            ct: Xunit.TestContext.Current.CancellationToken);

        var readCtx = Fixture.CreateContext();
        var mismatchLog = await readCtx.Set<BackgroundServiceLog>()
            .Where(x => x.ServiceName == nameof(MismatchSingletonCountingService))
            .Where(x => x.Source == BackgroundServiceLogSource.Lifecycle)
            .Where(x => x.Level == LogLevel.Error)
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

        mismatchLog.ShouldNotBeNull("A Lifecycle/Error row must be written for the configuration mismatch");

        // The message format from LogConfigurationMismatch: "Configuration mismatch: declared scope
        // is {declaredScope} but definition row has {storedScope}; service will not start..."
        mismatchLog!.Message.ShouldContain("Singleton");
        mismatchLog.Message.ShouldContain("PerServer");
    }

    [TimedFact(15_000)]
    public async Task ScopeMismatch_SupervisorRefusesToStart_UserCodeCounterStaysZero()
    {
        var state = new CountingServiceState();

        var seedCtx = Fixture.CreateContext();
        seedCtx.Set<BackgroundServiceDefinition>().Add(new BackgroundServiceDefinition
        {
            Name = nameof(MismatchSingletonCountingService),
            DeclaredScope = ServiceScope.PerServer,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });
        await seedCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.AddBackgroundService<MismatchSingletonCountingService>(),
            configureServices: services => services.AddSingleton(state));

        await server.WaitForBackgroundServiceState(
            nameof(MismatchSingletonCountingService),
            BackgroundServiceStatus.ConfigurationMismatch,
            TimeSpan.FromSeconds(8));

        // Counter must remain at zero — user code was never called.
        Volatile.Read(ref state.Count).ShouldBe(0);
    }
}

/// <summary>
/// Singleton-scope service used to trigger a mismatch against a PerServer Definition row.
/// </summary>
public sealed class MismatchSingletonCountingService : WarpBackgroundService
{
    private readonly CountingServiceState _state;

    public MismatchSingletonCountingService(CountingServiceState state)
    {
        _state = state;
    }

    public override ServiceScope Scope => ServiceScope.Singleton;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Interlocked.Increment(ref _state.Count);
            await Task.Yield();
        }
    }
}

/// <summary>
/// PerServer-scope service used to trigger a mismatch against a Singleton Definition row.
/// </summary>
public sealed class MismatchPerServerCountingService : WarpBackgroundService
{
    private readonly CountingServiceState _state;

    public MismatchPerServerCountingService(CountingServiceState state)
    {
        _state = state;
    }

    public override ServiceScope Scope => ServiceScope.PerServer;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Interlocked.Increment(ref _state.Count);
            await Task.Yield();
        }
    }
}
