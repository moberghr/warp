using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Events;
using Warp.Core.Handlers;
using Warp.Core.NoRestart;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker.Services;

namespace Warp.Tests.Features.NoRestart;

[GenerateDatabaseTests]
public abstract class NoRestartCrashRecoveryTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected NoRestartCrashRecoveryTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string SerializeCanBeRestarted(bool value)
    {
        var dict = new Dictionary<string, object>();
        var meta = MetadataFactory.Create<ICanBeRestartedMetadata>(dict);
        meta.CanBeRestarted = value;

        return MetadataSerializer.Serialize((Dictionary<string, object>)(object)meta)!;
    }

    private static async Task<Guid> InsertStaleJob(IDatabaseFixture fixture, string? metadata)
    {
        var ctx = fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Processing,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            LastKeepAlive = DateTime.UtcNow.AddMinutes(-10),
            Metadata = metadata,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        return jobId;
    }

    [TimedFact]
    public async Task RequeueStaleJobs_StaleJob_NoMetadata_DefaultTrue_Requeued()
    {
        var jobId = await InsertStaleJob(_fixture, metadata: null);

        var result = await TestTasks
            .CreateStaleJobRecovery(_fixture.CreateContext(), TimeProvider.System, TimeSpan.FromMinutes(5), restartByDefault: true)
            .RecoverStaleJobsAsync(CancellationToken.None);

        result.Requeued.ShouldBe(1);
        result.Failed.ShouldBe(0);
        var job = await _fixture.CreateContext().Set<Job>().FirstAsync(x => x.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Enqueued);
        job.ExpireAt.ShouldBeNull();
    }

    [TimedFact]
    public async Task RequeueStaleJobs_StaleJob_NoMetadata_DefaultFalse_Failed()
    {
        var jobId = await InsertStaleJob(_fixture, metadata: null);

        var result = await TestTasks
            .CreateStaleJobRecovery(_fixture.CreateContext(), TimeProvider.System, TimeSpan.FromMinutes(5), restartByDefault: false)
            .RecoverStaleJobsAsync(CancellationToken.None);

        result.Failed.ShouldBe(1);
        result.Requeued.ShouldBe(0);
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstAsync(x => x.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Failed);
        job.ExpireAt.ShouldBeNull();

        var counter = await readCtx.Set<Counter>().FirstOrDefaultAsync(x => x.Key == "stats:failed", Xunit.TestContext.Current.CancellationToken);
        counter.ShouldNotBeNull();
        counter.Value.ShouldBe(1);

        var log = await readCtx.Set<JobLog>()
            .Where(x => x.JobId == jobId)
            .Where(x => x.EventType == "Failed")
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);
        log.ShouldNotBeNull();
        log.Level.ShouldBe("Error");
        log.Message.ShouldContain("opted out of restart");
    }

    // Rule 6.11: a job recovery fails can be what completes its message, and the Orchestrator only
    // checks the parents a wake names, so recovery has to name them.
    [TimedFact]
    public async Task RequeueStaleJobs_FailedChildOfAMessage_NamesTheParentForTheOrchestrator()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var messageId = Guid.NewGuid();
        var jobId = await InsertStaleJob(_fixture, metadata: SerializeCanBeRestarted(false));
        await using (var seed = _fixture.CreateContext())
        {
            seed.Set<Job>().Add(new Job { Id = messageId, Kind = JobKind.Message, CurrentState = State.Processing, CreateTime = DateTime.UtcNow, ScheduleTime = DateTime.UtcNow, Queue = "default" });
            await seed.SaveChangesAsync(ct);
            await seed.Set<Job>().Where(x => x.Id == jobId).ExecuteUpdateAsync(x => x.SetProperty(y => y.ParentJobId, messageId), ct);
        }

        var signals = new ServerTaskSignals<TestContext>();
        signals.TakeFinalizedParents(TimeSpan.FromDays(1));

        await TestTasks
            .CreateStaleJobRecovery(_fixture.CreateContext(), TimeProvider.System, TimeSpan.FromMinutes(5), restartByDefault: true, signals: signals)
            .RecoverStaleJobsAsync(ct);

        var (parents, sweep) = signals.TakeFinalizedParents(TimeSpan.FromDays(1));
        parents.ShouldBe([messageId]);
        sweep.ShouldBeFalse();
    }

    [TimedFact]
    public async Task RequeueStaleJobs_StaleJob_MetadataTrue_DefaultFalse_Requeued()
    {
        var jobId = await InsertStaleJob(_fixture, metadata: SerializeCanBeRestarted(true));

        await TestTasks
            .CreateStaleJobRecovery(_fixture.CreateContext(), TimeProvider.System, TimeSpan.FromMinutes(5), restartByDefault: false)
            .RecoverStaleJobsAsync(CancellationToken.None);

        var job = await _fixture.CreateContext().Set<Job>().FirstAsync(x => x.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Enqueued);
    }

    [TimedFact]
    public async Task RequeueStaleJobs_StaleJob_MetadataFalse_DefaultTrue_Failed()
    {
        var jobId = await InsertStaleJob(_fixture, metadata: SerializeCanBeRestarted(false));

        await TestTasks
            .CreateStaleJobRecovery(_fixture.CreateContext(), TimeProvider.System, TimeSpan.FromMinutes(5), restartByDefault: true)
            .RecoverStaleJobsAsync(CancellationToken.None);

        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstAsync(x => x.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Failed);
        job.ExpireAt.ShouldBeNull();

        var counter = await readCtx.Set<Counter>().FirstOrDefaultAsync(x => x.Key == "stats:failed", Xunit.TestContext.Current.CancellationToken);
        counter.ShouldNotBeNull();
        counter.Value.ShouldBe(1);
    }

    [TimedFact]
    public async Task RequeueStaleJobs_StaleJob_CancellationPending_NoRestartMetadata_Deleted()
    {
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Processing,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            LastKeepAlive = DateTime.UtcNow.AddMinutes(-10),
            CancellationMode = CancellationMode.Graceful,
            Metadata = SerializeCanBeRestarted(false),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await TestTasks
            .CreateStaleJobRecovery(_fixture.CreateContext(), TimeProvider.System, TimeSpan.FromMinutes(5), restartByDefault: true)
            .RecoverStaleJobsAsync(CancellationToken.None);

        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstAsync(x => x.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Deleted);
        job.CancellationMode.ShouldBe(CancellationMode.None);
        job.ExpireAt.ShouldNotBeNull();
    }

    [TimedFact]
    public async Task RequeueStaleJobs_MixedJobs_EachRoutedCorrectly()
    {
        var ctx = _fixture.CreateContext();
        var requeuedId = Guid.NewGuid();
        var failedId = Guid.NewGuid();
        var defaultId = Guid.NewGuid();
        var stale = DateTime.UtcNow.AddMinutes(-10);
        ctx.Set<Job>().Add(new Job
        {
            Id = requeuedId,
            Kind = JobKind.Job,
            CurrentState = State.Processing,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            LastKeepAlive = stale,
            Metadata = SerializeCanBeRestarted(true),
        });
        ctx.Set<Job>().Add(new Job
        {
            Id = failedId,
            Kind = JobKind.Job,
            CurrentState = State.Processing,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            LastKeepAlive = stale,
            Metadata = SerializeCanBeRestarted(false),
        });
        ctx.Set<Job>().Add(new Job
        {
            Id = defaultId,
            Kind = JobKind.Job,
            CurrentState = State.Processing,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            LastKeepAlive = stale,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await TestTasks
            .CreateStaleJobRecovery(_fixture.CreateContext(), TimeProvider.System, TimeSpan.FromMinutes(5), restartByDefault: true)
            .RecoverStaleJobsAsync(CancellationToken.None);

        result.Total.ShouldBe(3);
        result.Requeued.ShouldBe(2);
        result.Failed.ShouldBe(1);
        var readCtx = _fixture.CreateContext();
        (await readCtx.Set<Job>().FirstAsync(x => x.Id == requeuedId, Xunit.TestContext.Current.CancellationToken)).CurrentState.ShouldBe(State.Enqueued);
        (await readCtx.Set<Job>().FirstAsync(x => x.Id == failedId, Xunit.TestContext.Current.CancellationToken)).CurrentState.ShouldBe(State.Failed);
        (await readCtx.Set<Job>().FirstAsync(x => x.Id == defaultId, Xunit.TestContext.Current.CancellationToken)).CurrentState.ShouldBe(State.Enqueued);
    }
}
