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
using Warp.Worker;

namespace Warp.Tests.Worker;

[Trait("Category", "NoDb")]
public class WarpDispatcherSignalWakeupTests
{
    [TimedFact]
    public async Task ExecuteAsync_SignalJobEnqueued_BypassesBackoffWait()
    {
        // The dispatcher-mode twin of WarpWorkerSignalWakeupTests. A job enqueued in this process
        // must wake an idle dispatcher at once, the same as a bare worker (rule 2.9). The dispatcher
        // used to be woken only through DispatcherRegistry, which only the DB-push listener calls, so
        // without UseDatabasePush() it sat out its whole backoff first: the dispatcher benchmarks took
        // ~21 s to drain 1,000 jobs against ~2.3 s single-worker.
        //
        // The claim is mocked to return nothing, so the dispatcher backs off into a 10s wait. The test
        // fires the signal mid-wait and requires the next claim within 2s, which the wall clock alone
        // cannot explain.
        var claims = 0;
        var firstWaitStarted = new TaskCompletionSource();
        using var secondClaimSeen = new SemaphoreSlim(0, 1);
        var signals = new ServerTaskSignals<TestContext>();

        var queries = new Mock<IWarpSqlQueries<TestContext>>();
        queries
            .Setup(x => x.ClaimEnqueuedJobsAsync(
                It.IsAny<DbContext>(),
                It.IsAny<string[]>(),
                It.IsAny<Guid>(),
                It.IsAny<DateTime>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                if (Interlocked.Increment(ref claims) == 1)
                {
                    firstWaitStarted.TrySetResult();
                }
                else
                {
                    secondClaimSeen.Release();
                }

                return [];
            });

        var services = new ServiceCollection();
        services.AddSingleton(queries.Object);
        services.AddSingleton(Mock.Of<IWarpServerContext>());
        await using var provider = services.BuildServiceProvider();

        var groupConfig = new WorkerGroupConfiguration
        {
            WorkerCount = 1,
            Queues = ["default"],

            // Long enough that wall-clock cannot explain a sub-second wake-up.
            PollingInterval = TimeSpan.FromSeconds(10),
            MaxPollingInterval = TimeSpan.FromSeconds(30),
            PollingIntervalFactor = 2.0,
        };

        using var dispatcher = new WarpDispatcher<TestContext>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WarpDispatcher<TestContext>>.Instance,
            Options.Create(new WarpServerConfiguration()),
            groupConfig,
            TimeProvider.System,
            new PauseStateHolder(),
            Guid.NewGuid(),
            new DispatcherRegistry(),
            signals);

        await dispatcher.StartAsync(CancellationToken.None);
        try
        {
            await firstWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), Xunit.TestContext.Current.CancellationToken);

            var signalAt = DateTime.UtcNow;
            signals.SignalJobEnqueued();

            var observed = await secondClaimSeen.WaitAsync(TimeSpan.FromSeconds(2), Xunit.TestContext.Current.CancellationToken);
            observed.ShouldBeTrue("dispatcher should claim again within 2s of SignalJobEnqueued; its configured wait is 10s");
            (DateTime.UtcNow - signalAt).ShouldBeLessThan(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }
}
