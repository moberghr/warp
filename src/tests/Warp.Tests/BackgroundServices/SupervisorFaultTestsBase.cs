using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Warp.Core.BackgroundServices;
using Warp.Core.Data.Entities;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.BackgroundServices;
using Warp.Worker.BackgroundServices;

namespace Warp.Tests.BackgroundServices;

[GenerateDatabaseTests]
public abstract class SupervisorFaultTestsBase : IntegrationTestBase
{
    protected SupervisorFaultTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact(15_000)]
    public async Task SetStatusRunningThrowsOnce_SupervisorFaultLogged_ServiceRunsOnNextIteration()
    {
        var injector = new StateServiceFaultInjector { ThrowsRemaining = 1 };
        var counter = new CountingServiceState();

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.AddBackgroundService<TestContext, CountingService>(),
            configureServices: services =>
            {
                services.AddSingleton(counter);
                services.AddSingleton(injector);

                // Last scoped registration wins on GetRequiredService. The real
                // BackgroundServiceStateService<TestContext> is still in the descriptor list
                // and is the concrete type the decorator delegates to.
                services.AddScoped<IBackgroundServiceStateService>(sp =>
                    new FaultInjectingStateService(
                        ActivatorUtilities.CreateInstance<BackgroundServiceStateService<TestContext>>(sp),
                        sp.GetRequiredService<StateServiceFaultInjector>()));
            });

        // The supervisor's first iteration must trip the injector: SetStatus(Running) throws
        // → outer catch logs LogSupervisorFault (Lifecycle / Error). Wait for the log row to
        // land in the DB so we know the supervisor took the new fault path, not the old
        // silent-swallow path.
        await WarpTestServer.WaitUntil(
            async () =>
            {
                var ctx = Fixture.CreateContext();
                return await ctx.Set<BackgroundServiceLog>()
                    .Where(x => x.ServerId == server.ServerId)
                    .Where(x => x.ServiceName == nameof(CountingService))
                    .Where(x => x.Source == BackgroundServiceLogSource.Lifecycle)
                    .Where(x => x.Level == LogLevel.Error)
                    .Where(x => x.Message.StartsWith("Supervisor faulted"))
                    .AnyAsync(Xunit.TestContext.Current.CancellationToken);
            },
            timeout: TimeSpan.FromSeconds(10),
            ct: Xunit.TestContext.Current.CancellationToken);

        // After backoff (1s) the supervisor retries; the injector is exhausted, SetStatus(Running)
        // succeeds, and CountingService runs. The counter incrementing is the proof that user
        // code reached ExecuteAsync after the supervisor recovered.
        await WarpTestServer.WaitUntil(
            () => Task.FromResult(Volatile.Read(ref counter.Count) > 0),
            timeout: TimeSpan.FromSeconds(10),
            ct: Xunit.TestContext.Current.CancellationToken);

        // RestartCount is reserved for user-code faults. A supervisor fault must NOT bump it.
        var instance = await Fixture.CreateContext().Set<BackgroundServiceInstance>()
            .Where(x => x.ServerId == server.ServerId)
            .Where(x => x.ServiceName == nameof(CountingService))
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

        instance.ShouldNotBeNull();
        instance.RestartCount.ShouldBe(0, "supervisor faults must not increment the user-fault counter");
    }
}

public sealed class StateServiceFaultInjector
{
    private int _throwsRemaining;

    public int ThrowsRemaining
    {
        get => Volatile.Read(ref _throwsRemaining);
        set => Volatile.Write(ref _throwsRemaining, value);
    }

    public bool ConsumeIfRemaining()
    {
        // Atomic decrement-if-positive — multiple supervisors / pipeline behaviors hitting
        // the injector concurrently must not all consume the same allotment.
        while (true)
        {
            var current = Volatile.Read(ref _throwsRemaining);
            if (current <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _throwsRemaining, current - 1, current) == current)
            {
                return true;
            }
        }
    }
}

internal sealed class FaultInjectingStateService : IBackgroundServiceStateService
{
    private readonly IBackgroundServiceStateService _inner;
    private readonly StateServiceFaultInjector _injector;

    public FaultInjectingStateService(IBackgroundServiceStateService inner, StateServiceFaultInjector injector)
    {
        _inner = inner;
        _injector = injector;
    }

    public Task<RegistrationOutcome> RegisterAsync(string serviceName, ServiceScope declaredScope, CancellationToken ct)
        => _inner.RegisterAsync(serviceName, declaredScope, ct);

    public Task SetStatusAsync(string serviceName, BackgroundServiceStatus status, CancellationToken ct)
    {
        if (status == BackgroundServiceStatus.Running && _injector.ConsumeIfRemaining())
        {
            throw new InvalidOperationException("Injected DB failure on SetStatusAsync(Running)");
        }

        return _inner.SetStatusAsync(serviceName, status, ct);
    }

    public Task RecordFaultAsync(string serviceName, Exception ex, CancellationToken ct)
        => _inner.RecordFaultAsync(serviceName, ex, ct);

    public Task ResetRestartCountAsync(string serviceName, CancellationToken ct)
        => _inner.ResetRestartCountAsync(serviceName, ct);

    public Task DeleteAsync(string serviceName, CancellationToken ct)
        => _inner.DeleteAsync(serviceName, ct);

    public Task<ServiceScope?> GetDefinedScopeAsync(string serviceName, CancellationToken ct)
        => _inner.GetDefinedScopeAsync(serviceName, ct);
}
