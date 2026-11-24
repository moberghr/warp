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

// The dispatcher's own loop, with the claim mocked: the dispatcher-mode twin of WarpWorkerResilienceTests.
// What the loop does with claimed jobs is covered against a real database in WarpDispatcherUnclaimTests.
[Trait("Category", "NoDb")]
public class WarpDispatcherResilienceTests
{
    private static readonly TimeSpan Floor = TimeSpan.FromMilliseconds(10);

    [TimedFact]
    public async Task ExecuteAsync_ClaimThrows_KeepsRunningAndClaimsAgain()
    {
        // The first claim fails the way a dropped connection would. The loop must log it, wait the
        // floor interval and claim again. The backoff ceiling is 30s, so a second claim inside 2s
        // also shows the failure was not counted as an empty poll.
        var claims = 0;
        using var secondClaimSeen = new SemaphoreSlim(0, 1);
        var queries = MockClaim(() =>
        {
            if (Interlocked.Increment(ref claims) == 1)
            {
                throw new InvalidOperationException("connection lost");
            }

            if (claims == 2)
            {
                secondClaimSeen.Release();
            }

            return [];
        });

        await using var provider = BuildProvider(queries);
        using var dispatcher = CreateDispatcher(provider, new PauseStateHolder(), Guid.NewGuid());

        await dispatcher.StartAsync(CancellationToken.None);
        try
        {
            var observed = await secondClaimSeen.WaitAsync(TimeSpan.FromSeconds(2), Xunit.TestContext.Current.CancellationToken);
            observed.ShouldBeTrue("the dispatcher stopped claiming after one failed claim");
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }

        // Stopping completes the channel, which is what lets the group's workers leave their read loop.
        await dispatcher.JobReader.Completion.WaitAsync(TimeSpan.FromSeconds(2), Xunit.TestContext.Current.CancellationToken);
    }

    [TimedFact]
    public async Task ExecuteAsync_WhileGroupIsPaused_DoesNotClaimUntilResumed()
    {
        using var claimSeen = new SemaphoreSlim(0, int.MaxValue);
        var queries = MockClaim(() =>
        {
            claimSeen.Release();

            return [];
        });

        var groupId = Guid.NewGuid();
        var pauseState = new PauseStateHolder();
        pauseState.Update(serverPaused: false, new Dictionary<Guid, bool> { [groupId] = true });

        await using var provider = BuildProvider(queries);
        using var dispatcher = CreateDispatcher(provider, pauseState, groupId);

        await dispatcher.StartAsync(CancellationToken.None);
        try
        {
            // A paused dispatcher waits one PollingInterval (10 ms here) and checks its pause state again,
            // so 200 ms covers about twenty checks, none of which may claim.
            var claimedWhilePaused = await claimSeen.WaitAsync(TimeSpan.FromMilliseconds(200), Xunit.TestContext.Current.CancellationToken);
            claimedWhilePaused.ShouldBeFalse("a paused group must not claim jobs");

            pauseState.Update(serverPaused: false, new Dictionary<Guid, bool> { [groupId] = false });

            var claimedAfterResume = await claimSeen.WaitAsync(TimeSpan.FromSeconds(2), Xunit.TestContext.Current.CancellationToken);
            claimedAfterResume.ShouldBeTrue("the dispatcher did not start claiming after the group was resumed");
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }

    private static Mock<IWarpSqlQueries<TestContext>> MockClaim(Func<List<Job>> claim)
    {
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
            .Returns(() => Task.FromResult(claim()));

        return queries;
    }

    private static ServiceProvider BuildProvider(Mock<IWarpSqlQueries<TestContext>> queries)
    {
        var services = new ServiceCollection();
        services.AddSingleton(queries.Object);
        services.AddSingleton<IWarpServerContext>(new InMemoryServerContext());

        return services.BuildServiceProvider();
    }

    private static WarpDispatcher<TestContext> CreateDispatcher(ServiceProvider provider, PauseStateHolder pauseState, Guid groupId) =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WarpDispatcher<TestContext>>.Instance,
            Options.Create(new WarpServerConfiguration()),
            new WorkerGroupConfiguration
            {
                WorkerCount = 1,
                Queues = ["default"],
                PollingInterval = Floor,
                MaxPollingInterval = TimeSpan.FromSeconds(30),
                PollingIntervalFactor = 2.0,
            },
            TimeProvider.System,
            pauseState,
            groupId,
            new DispatcherRegistry(),
            new ServerTaskSignals<TestContext>());
}
