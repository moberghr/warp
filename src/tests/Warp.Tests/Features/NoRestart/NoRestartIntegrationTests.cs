using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.NoRestart;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Features.NoRestart;

[GenerateDatabaseTests]
public abstract class NoRestartIntegrationTestsBase : IntegrationTestBase
{
    protected NoRestartIntegrationTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    private static string SerializeCanBeRestarted(bool value)
    {
        var dict = new Dictionary<string, object>();
        var meta = MetadataFactory.Create<ICanBeRestartedMetadata>(dict);
        meta.CanBeRestarted = value;

        return MetadataSerializer.Serialize((Dictionary<string, object>)(object)meta)!;
    }

    [TimedFact]
    public async Task GivenStaleNoRestartJob_WhenRecovered_ThenMarkedFailed()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);

        // Build metadata through the generated proxy so the serialized key matches what
        // the stale-recovery reader expects. Bypassing MetadataFactory couples the test to
        // whatever string the generator happens to produce today — a rename would silently
        // pass here while production breaks.
        var metadata = SerializeCanBeRestarted(false);
        var staleKeepAlive = DateTime.UtcNow.AddMinutes(-10);

        var ctx = Fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Processing,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            LastKeepAlive = staleKeepAlive,
            Metadata = metadata,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForJobState(jobId, State.Failed);

        var job = await server.GetJob(jobId);
        job.CurrentState.ShouldBe(State.Failed);
        job.ExpireAt.ShouldBeNull();

        var logs = await server.GetJobLogs(jobId);
        logs.ShouldContain(l => l.EventType == "Failed" && l.Message.Contains("opted out of restart"));
    }

    [TimedFact]
    public async Task GivenNoRestartAttributeJob_WhenPublishedAndStale_ThenMarkedFailed()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);

        // Publish through the real pipeline so NoRestartPublishBehavior writes CanBeRestarted=false.
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new NoRestartAttributeRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Let the worker run it to completion so we know the behavior wrote metadata that persisted.
        await server.WaitForJobState(jobId, State.Completed);
        var published = await server.GetJob(jobId);
        published.Metadata.ShouldNotBeNull();
        var publishedMeta = MetadataSerializer.Deserialize(published.Metadata);
        publishedMeta[nameof(ICanBeRestartedMetadata.CanBeRestarted)].ShouldBe(false);

        // Force the completed job back into Processing with a stale keep-alive — simulates a
        // crashed worker. The stale recovery task must now honor the metadata and mark it Failed.
        var ctx = Fixture.CreateContext();
        await ctx.Set<Job>()
            .Where(x => x.Id == jobId)
            .ExecuteUpdateAsync(
                x => x
                    .SetProperty(p => p.CurrentState, State.Processing)
                    .SetProperty(p => p.LastKeepAlive, DateTime.UtcNow.AddMinutes(-10)),
                Xunit.TestContext.Current.CancellationToken);

        await server.WaitForJobState(jobId, State.Failed);

        var job = await server.GetJob(jobId);
        job.CurrentState.ShouldBe(State.Failed);
        job.ExpireAt.ShouldBeNull();
    }
}
