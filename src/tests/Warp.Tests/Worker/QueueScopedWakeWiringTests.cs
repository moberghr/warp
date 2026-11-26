using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Queries;
using Warp.Core.Entities;
using Warp.Core.Events;
using Warp.Tests.Helpers;
using Warp.Worker;

namespace Warp.Tests.Worker;

// The queue-scoped JobEnqueued wake (rule 6.3) through the real WarpDispatcher / WarpWorker wiring: two groups
// on different queues, both asleep on a 10 s backoff, and wakes for one queue only. Every claim comes back empty,
// so a group claims again only when something woke it. Group "b" is asserted to have claimed exactly twice: once
// at start, once for its own wake. Correct wiring cannot claim a third time inside the test (its next timeout is
// 20 s away); a group woken for every queue claims for the "a" wakes too.
[Trait("Category", "NoDb")]
public class QueueScopedWakeWiringTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(2);

    [TimedFact]
    public async Task WarpDispatcher_EnqueueToAnotherGroupsQueue_DoesNotWakeIt()
    {
        var claims = new ClaimLog();
        var signals = new ServerTaskSignals<TestContext>();
        var registry = new DispatcherRegistry();

        var queries = new Mock<IWarpSqlQueries<TestContext>>();
        queries
            .Setup(x => x.ClaimEnqueuedJobsAsync(
                It.IsAny<DbContext>(),
                It.IsAny<string[]>(),
                It.IsAny<Guid>(),
                It.IsAny<DateTime>(),
                It.IsAny<int>(),
                It.IsAny<ClaimWatermark?>(),
                It.IsAny<CancellationToken>()))
            .Returns((DbContext _, string[] queues, Guid _, DateTime _, int _, ClaimWatermark? _, CancellationToken _) =>
            {
                claims.Record(queues[0]);

                return Task.FromResult(new List<Job>());
            });

        var services = new ServiceCollection();
        services.AddSingleton(queries.Object);

        // Scoped: the two dispatchers claim concurrently and a DbContext is not thread-safe.
        services.AddScoped<IWarpServerContext, InMemoryServerContext>();
        await using var provider = services.BuildServiceProvider();

        using var dispatcherA = CreateDispatcher(provider, "a", registry, signals);
        using var dispatcherB = CreateDispatcher(provider, "b", registry, signals);

        await dispatcherA.StartAsync(CancellationToken.None);
        await dispatcherB.StartAsync(CancellationToken.None);
        try
        {
            await claims.WaitForAsync("a", 1);
            await claims.WaitForAsync("b", 1);

            // In-process enqueue (ServerTaskSignals) and cross-process push (IDispatcherWake), both for "a".
            signals.SignalJobEnqueued("a");
            await claims.WaitForAsync("a", 2);
            registry.Signal("a");
            await claims.WaitForAsync("a", 3);

            signals.SignalJobEnqueued("b");
            await claims.WaitForAsync("b", 2);

            signals.SignalJobEnqueued("a");
            await claims.WaitForAsync("a", 4);
        }
        finally
        {
            await dispatcherA.StopAsync(CancellationToken.None);
            await dispatcherB.StopAsync(CancellationToken.None);
        }

        claims.Count("b").ShouldBe(2, "group b must be woken only by the enqueue to its own queue");
    }

    [TimedFact]
    public async Task WarpWorker_EnqueueToAnotherGroupsQueue_DoesNotWakeIt()
    {
        // Single-worker mode subscribes the same way (UseDispatcher = false).
        var claims = new ClaimLog();
        var signals = new ServerTaskSignals<TestContext>();

        using var workerA = CreateWorker("a", claims, signals);
        using var workerB = CreateWorker("b", claims, signals);

        await workerA.StartAsync(CancellationToken.None);
        await workerB.StartAsync(CancellationToken.None);
        try
        {
            await claims.WaitForAsync("a", 1);
            await claims.WaitForAsync("b", 1);

            signals.SignalJobEnqueued("a");
            await claims.WaitForAsync("a", 2);

            signals.SignalJobEnqueued("b");
            await claims.WaitForAsync("b", 2);

            signals.SignalJobEnqueued("a");
            await claims.WaitForAsync("a", 3);
        }
        finally
        {
            await workerA.StopAsync(CancellationToken.None);
            await workerB.StopAsync(CancellationToken.None);
        }

        claims.Count("b").ShouldBe(2, "group b must be woken only by the enqueue to its own queue");
    }

    private static WorkerGroupConfiguration Group(string queue) => new()
    {
        WorkerCount = 1,
        Queues = [queue],

        // Long enough that the wall clock cannot explain a claim inside the test.
        PollingInterval = TimeSpan.FromSeconds(10),
        MaxPollingInterval = TimeSpan.FromSeconds(30),
        PollingIntervalFactor = 2.0,
    };

    private static WarpDispatcher<TestContext> CreateDispatcher(
        ServiceProvider provider,
        string queue,
        DispatcherRegistry registry,
        ServerTaskSignals<TestContext> signals) => new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WarpDispatcher<TestContext>>.Instance,
            Options.Create(new WarpServerConfiguration()),
            Group(queue),
            TimeProvider.System,
            new PauseStateHolder(),
            Guid.NewGuid(),
            registry,
            signals);

    private static WarpWorker<TestContext> CreateWorker(string queue, ClaimLog claims, ServerTaskSignals<TestContext> signals)
    {
        var service = new Mock<IWarpWorkerService>();
        service
            .Setup(x => x.GetAndProcessJob(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                claims.Record(queue);

                return Task.FromResult(false);
            });

        return new WarpWorker<TestContext>(
            service.Object,
            NullLogger<WarpWorker<TestContext>>.Instance,
            Group(queue),
            new PauseStateHolder(),
            TimeProvider.System,
            Guid.NewGuid(),
            signals);
    }

    private sealed class ClaimLog
    {
        private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _claimed = new(StringComparer.Ordinal);

        public void Record(string queue)
        {
            _counts.AddOrUpdate(queue, 1, (_, x) => x + 1);
            Claimed(queue).Release();
        }

        public int Count(string queue) => _counts.GetValueOrDefault(queue);

        public async Task WaitForAsync(string queue, int count)
        {
            while (Count(queue) < count)
            {
                var claimed = await Claimed(queue).WaitAsync(Step, Xunit.TestContext.Current.CancellationToken);
                claimed.ShouldBeTrue($"queue {queue} should reach claim {count} within {Step.TotalSeconds}s; its backoff is 10s");
            }
        }

        private SemaphoreSlim Claimed(string queue) => _claimed.GetOrAdd(queue, _ => new SemaphoreSlim(0));
    }
}
