using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.CircuitBreaker;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Handlers.Generated;
using Warp.Core.Logging;
using Warp.Core.Retry;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Features.CircuitBreaker;

/// <summary>
/// Pins the breaker's failure counting under the DOCUMENTED registration order — breaker OUTER of retry
/// ("Circuit Breaker short-circuits before Retry", circuit-breaker.md).
/// <para>
/// The breaker's catch skips counting whenever an inner behaviour set a <c>JobOutcome</c>, treating "a
/// decision was made" as "not a raw dependency failure". Retry's reschedules are rightly skipped that way
/// — the attempt will run again. But retry EXHAUSTION now stamps an outcome too
/// (<c>Failed/RetryExhausted</c>), and that outcome IS the raw dependency failure, reported by the retry
/// budget that just spent itself on it. Skipping it makes the breaker blind to every failure of every
/// retried job: a downstream outage where all jobs exhaust their retries never opens the circuit, and the
/// retry storm the breaker exists to stop runs unthrottled. Nothing else covers this because the shared
/// test host registers the opposite (retry-outer) order, where the breaker sees the exception before retry
/// has stamped anything.
/// </para>
/// </summary>
[GenerateDatabaseTests]
public abstract class CircuitBreakerRetryOrderingTestsBase : IAsyncLifetime
{
    private static readonly Guid ServerId = Guid.NewGuid();
    private static readonly Guid WorkerId = Guid.NewGuid();

    private readonly IDatabaseFixture _fixture;

    protected CircuitBreakerRetryOrderingTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetAsync();

        var ctx = _fixture.CreateContext();
        ctx.Set<Server>().Add(new Server
        {
            Id = ServerId,
            StartedTime = DateTime.UtcNow,
            LastHeartbeatTime = DateTime.UtcNow,
            ServiceCount = 1,
        });
        ctx.Set<Warp.Core.Data.Entities.Worker>().Add(new Warp.Core.Data.Entities.Worker
        {
            Id = WorkerId,
            ServerId = ServerId,
            StartedTime = DateTime.UtcNow,
            LastHeartbeatTime = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task BreakerOuterOfRetry_RetryExhaustion_CountsAsBreakerFailure()
    {
        // Arrange — one retry taken immediately, then exhaustion on the second attempt.
        var queue = $"cb-ordering-{Guid.NewGuid():N}";
        var jobId = Guid.NewGuid();
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(ThrowExceptionRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new ThrowExceptionRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = queue,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateBreakerOuterWorker(queue);

        // Attempt 1 fails and reschedules (Delays = [] ⇒ straight back to Enqueued). The reschedule outcome
        // is rightly NOT counted — the attempt is not settled, the job will run again.
        await worker.GetAndProcessJob(CancellationToken.None);

        // Act — attempt 2 has no budget left: the failure is terminal.
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert — the job failed for real, and the breaker saw it.
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>()
            .AsNoTracking()
            .FirstAsync(x => x.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Failed);

        var state = await readCtx.Set<CircuitBreakerState>()
            .AsNoTracking()
            .Where(x => x.GroupKey == nameof(ThrowExceptionRequest))
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);
        state.ShouldNotBeNull("the terminal failure of a retried job is a dependency failure the breaker must record");
        state.FailureCount.ShouldBe(1);
    }

    // Mirrors OutcomeMetricsTestsBase.CreateWorker, except the breaker is registered BEFORE retry — DI
    // insertion order is outer→inner, so this is the breaker-outer arrangement the docs describe. The
    // shared WarpTestServer host registers retry first and cannot exercise this shape.
    private WarpWorkerService<TestContext> CreateBreakerOuterWorker(string queue)
    {
        var queues = new[] { queue };
        var services = new ServiceCollection();
        services.AddWarpMediator();
        services.AddLogging(builder => builder.AddProvider(new JobLoggerProvider()));
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());
        services.AddTestServerContext<TestContext>();
        services.AddSingleton<CounterService>();
        services.AddSingleton<MultiHandlerCounter>();
        services.AddSingleton<ActivityCapture>();
        services.AddSingleton(new BarrierSignal());
        services.AddScoped<JobContext>();
        services.AddScoped<IJobContext>(x => x.GetRequiredService<JobContext>());
        services.TryAddSingleton(TimeProvider.System);

        // CircuitBreakerStore requires the provider-registered exception classifier; without it the whole
        // behavior-chain resolution throws, the worker swallows that as a handler failure, and the test
        // fails for a reason that has nothing to do with what it pins.
        services.AddSingleton(Moq.Mock.Of<Warp.Core.Data.IDatabaseExceptionClassifier>());

        var builder = new WarpBuilder<TestContext>(services);
        builder.AddCircuitBreaker(o =>
        {
            // High enough that the circuit never OPENS during the test — open-circuit rescheduling would
            // change the second attempt's path. Counting is what is under test, not opening.
            o.Threshold = 1000;
            o.Duration = TimeSpan.FromHours(1);
        });
        builder.AddRetry(o =>
        {
            o.MaxRetries = 1;
            o.Delays = [];
        });

        var workerConfig = new OptionsWrapper<WarpServerConfiguration>(new WarpServerConfiguration
        {
            WorkerCount = 1,
            ServerId = ServerId,
            Queues = queues,
            EnableHandlerLogging = true,
        });
        services.AddSingleton<IOptions<WarpServerConfiguration>>(workerConfig);
        services.AddSingleton<IOptions<WarpConfiguration>>(workerConfig);

        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var groupConfig = new WorkerGroupConfiguration
        {
            WorkerCount = 1,
            Queues = queues,
        };

        return new WarpWorkerService<TestContext>(
            WorkerId,
            scopeFactory,
            new NullLogger<WarpWorkerService<TestContext>>(),
            workerConfig,
            groupConfig,
            TimeProvider.System,
            TestTasks.QueriesFromScope<TestContext>(scopeFactory),
            TestTasks.NullTransport,
            TestTasks.NullSignals);
    }
}
