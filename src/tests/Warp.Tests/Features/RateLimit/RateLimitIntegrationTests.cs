using Shouldly;
using Warp.Core.Enums;
using Warp.Core.Helper;
using Warp.Core.RateLimit;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Features.RateLimit;

[GenerateDatabaseTests(SerializeInCollection = "HeavyIntegration")]
public abstract class RateLimitIntegrationTestsBase : IntegrationTestBase
{
    protected RateLimitIntegrationTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task GivenThreeJobsAtFixedLimitOfTwo_WhenSkipMode_ThenExactlyOneIsCancelled()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();

        var key = $"int-skip-{Guid.NewGuid():N}";

        // Three jobs, limit = 2. Worker pickup order is non-deterministic so we cannot say
        // *which* one gets cancelled — only that exactly one of the three does, and the log
        // message names the rate-limit key. See feedback_no_spray_n_tests.
        var job1Id = await publisher.Enqueue(new UnitRequest(), new JobParameters().WithRateLimit(key, count: 2, window: TimeSpan.FromMinutes(5)));
        var job2Id = await publisher.Enqueue(new UnitRequest(), new JobParameters().WithRateLimit(key, count: 2, window: TimeSpan.FromMinutes(5)));
        var job3Id = await publisher.Enqueue(new UnitRequest(), new JobParameters().WithRateLimit(key, count: 2, window: TimeSpan.FromMinutes(5)));
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForCompletion();

        var jobIds = new[] { job1Id, job2Id, job3Id };
        var states = new List<State>();
        foreach (var id in jobIds)
        {
            states.Add((await server.GetJob(id)).CurrentState);
        }

        states.Count(s => s == State.Completed).ShouldBe(2);
        states.Count(s => s == State.Deleted).ShouldBe(1);

        var deletedId = jobIds[states.IndexOf(State.Deleted)];
        var logs = await server.GetJobLogs(deletedId);
        logs.ShouldContain(l => l.EventType == "Deleted" && l.Message.Contains(key, StringComparison.Ordinal) && l.Message.Contains("Cancelled", StringComparison.Ordinal));
    }

    [TimedFact]
    public async Task GivenJobAtLimit_WhenWaitMode_ThenOneThrottledOneCompletes()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();

        var key = $"int-wait-{Guid.NewGuid():N}";

        // Limit one per minute, two jobs. Whichever worker wins the lock-and-bucket race
        // runs; the other gets Throttled and rescheduled. The test does not assume which
        // of the two wins — only that one completed and one was rescheduled.
        var job1Id = await publisher.Enqueue(new UnitRequest(), new JobParameters().WithRateLimit(key, count: 1, window: TimeSpan.FromMinutes(1), mode: RateLimitMode.Wait));
        var job2Id = await publisher.Enqueue(new UnitRequest(), new JobParameters().WithRateLimit(key, count: 1, window: TimeSpan.FromMinutes(1), mode: RateLimitMode.Wait));
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Wait for both jobs to settle (Completed or Scheduled). WaitForCompletion treats
        // Scheduled as still active, which is wrong here — the throttled job is supposed
        // to land in Scheduled and stay there.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        Warp.Core.Entities.Job? job1 = null;
        Warp.Core.Entities.Job? job2 = null;
        while (DateTime.UtcNow < deadline)
        {
            job1 = await server.GetJob(job1Id);
            job2 = await server.GetJob(job2Id);
            var settled1 = job1.CurrentState is State.Completed or State.Scheduled or State.Deleted or State.Failed;
            var settled2 = job2.CurrentState is State.Completed or State.Scheduled or State.Deleted or State.Failed;
            if (settled1 && settled2)
            {
                break;
            }

            await Task.Delay(50, Xunit.TestContext.Current.CancellationToken);
        }

        job1.ShouldNotBeNull();
        job2.ShouldNotBeNull();

        var jobs = new[] { job1, job2 };
        jobs.Count(j => j.CurrentState == State.Completed).ShouldBe(1);
        jobs.Count(j => j.CurrentState == State.Scheduled).ShouldBe(1);

        var throttled = jobs.First(j => j.CurrentState == State.Scheduled);
        throttled.ScheduleTime.ShouldBeGreaterThan(DateTime.UtcNow);
        throttled.ExpireAt.ShouldBeNull();

        var logs = await server.GetJobLogs(throttled.Id);

        // Rate-limit reschedules to a future time → state is Scheduled. The explanatory
        // "Throttled — '{key}'..." message lives in Message; EventType is the literal state.
        logs.ShouldContain(l => l.EventType == "Scheduled" && l.Message.Contains("Throttled", StringComparison.Ordinal) && l.Message.Contains(key, StringComparison.Ordinal));

        // Cancel the throttled job so the fixture doesn't leave a long-scheduled row behind.
        var cmd = server.CreateCommandService();
        await cmd.DeleteJob(throttled.Id);
    }

    [TimedFact]
    public async Task HandlerDeclaredRateLimit_SkipMode_SameOutcomeAndLogAsContractDeclared()
    {
        // SC2 (addon policy axis): the REQUEST carries no attribute — [RateLimit("handler-rl", 1,
        // 3600)] sits on the HANDLER, is resolved at first execution and stamped into metadata.
        // The first job consumes the window's single token and completes; a second job published
        // after it must be cancelled with the same outcome and log shape as a contract-declared
        // rate limit, and its row must carry the stamped policy.
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();

        var job1Id = await publisher.Enqueue(new HandlerRateLimitRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        await server.WaitForJobState(job1Id, State.Completed);

        var publisher2 = server.CreatePublisher();
        var job2Id = await publisher2.Enqueue(new HandlerRateLimitRequest());
        await publisher2.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        await server.WaitForJobState(job2Id, State.Deleted);

        var logs = await server.GetJobLogs(job2Id);
        logs.ShouldContain(l => l.EventType == "Deleted" && l.Message.Contains("handler-rl", StringComparison.Ordinal));

        var job2 = await server.GetJob(job2Id);
        job2.Metadata.ShouldNotBeNull();
        job2.Metadata.ShouldContain("RateLimitKey");
        job2.Metadata.ShouldContain("handler-rl");
    }
}
