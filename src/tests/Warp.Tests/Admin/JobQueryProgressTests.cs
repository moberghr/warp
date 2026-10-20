using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Models;
using Warp.Core.Services;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Admin;

[GenerateDatabaseTests]
public abstract class JobQueryProgressTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected JobQueryProgressTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task GetJobDetailById_SharedTraceId_ProjectsOriginRequest()
    {
        var traceId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            TraceId = traceId,
        });
        ctx.Set<EndpointCallLog>().Add(new EndpointCallLog
        {
            Id = Guid.NewGuid(),
            Method = "GET",
            RouteTemplate = "/orders/{id}",
            Operation = "GetOrder",
            User = "alice",
            Timestamp = DateTime.UtcNow,
            Outcome = AdapterCallOutcome.Success,
            MachineName = "test",
            TraceId = traceId,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = new JobQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var detail = await svc.GetJobDetailById(jobId);

        detail.ShouldNotBeNull();
        detail.Origin.ShouldNotBeNull();
        detail.Origin.Method.ShouldBe("GET");
        detail.Origin.RouteTemplate.ShouldBe("/orders/{id}");
        detail.Origin.User.ShouldBe("alice");
        detail.Origin.EndpointId.ShouldNotBeNullOrEmpty();
    }

    [TimedFact]
    public async Task GetJobDetailById_HandlerOutputAndProgressRows_ExcludedFromDetail()
    {
        var jobId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(CompletedJob(jobId));
        ctx.Set<JobLog>().Add(Log(jobId, "Processing", now, "picked up"));
        ctx.Set<JobLog>().Add(Log(jobId, "Log", now.AddSeconds(1), "handler output"));
        ctx.Set<JobLog>().Add(Progress(jobId, "download", now.AddSeconds(2), 40));
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = new JobQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var detail = await svc.GetJobDetailById(jobId);

        detail.ShouldNotBeNull();
        detail.Logs.ShouldHaveSingleItem().EventType.ShouldBe("Processing");
    }

    [TimedFact]
    public async Task GetJobDetailById_LargeFanOut_ListsFirstFiftyAndCountsAll()
    {
        var jobId = Guid.NewGuid();
        var start = DateTime.UtcNow.AddMinutes(-10);
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(CompletedJob(jobId));
        for (var i = 0; i < UnifiedJobDetailModel.MaxRelatedJobs + 10; i++)
        {
            var spawned = CompletedJob(Guid.NewGuid());
            spawned.SpawnedByJobId = jobId;
            spawned.CreateTime = start.AddSeconds(i);
            ctx.Set<Job>().Add(spawned);

            var continuation = CompletedJob(Guid.NewGuid());
            continuation.ParentJobId = jobId;
            continuation.CreateTime = start.AddSeconds(i);
            ctx.Set<Job>().Add(continuation);
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = new JobQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var detail = await svc.GetJobDetailById(jobId);

        detail.ShouldNotBeNull();
        detail.SpawnedJobs.Count.ShouldBe(UnifiedJobDetailModel.MaxRelatedJobs);
        detail.SpawnedJobCount.ShouldBe(UnifiedJobDetailModel.MaxRelatedJobs + 10);
        detail.Continuations.Count.ShouldBe(UnifiedJobDetailModel.MaxRelatedJobs);
        detail.ContinuationCount.ShouldBe(UnifiedJobDetailModel.MaxRelatedJobs + 10);
    }

    [TimedFact]
    public async Task GetTraceGraph_TraceLargerThanTheCap_ReturnsEarliestJobsAndFlagsTruncation()
    {
        var traceId = Guid.NewGuid();
        var start = DateTime.UtcNow.AddHours(-1);
        var ctx = _fixture.CreateContext();
        for (var i = 0; i < TraceGraphModel.MaxJobs + 1; i++)
        {
            var job = CompletedJob(Guid.NewGuid());
            job.TraceId = traceId;
            job.CreateTime = start.AddMilliseconds(i);
            ctx.Set<Job>().Add(job);
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = new JobQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var graph = await svc.GetTraceGraph(traceId);

        graph.Jobs.Count.ShouldBe(TraceGraphModel.MaxJobs);
        graph.IsTruncated.ShouldBeTrue();
        graph.Jobs[0].CreateTime.ShouldBe(start, TimeSpan.FromMilliseconds(1));
    }

    [TimedFact]
    public async Task GetTraceGraph_SmallTrace_ReturnsAllJobsUntruncated()
    {
        var traceId = Guid.NewGuid();
        var ctx = _fixture.CreateContext();
        for (var i = 0; i < 3; i++)
        {
            var job = CompletedJob(Guid.NewGuid());
            job.TraceId = traceId;
            ctx.Set<Job>().Add(job);
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = new JobQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var graph = await svc.GetTraceGraph(traceId);

        graph.Jobs.Count.ShouldBe(3);
        graph.IsTruncated.ShouldBeFalse();
    }

    private static Job CompletedJob(Guid id)
    {
        return new Job
        {
            Id = id,
            Kind = JobKind.Job,
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        };
    }

    private static JobLog Log(Guid jobId, string eventType, DateTime timestamp, string message)
    {
        return new JobLog
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            EventType = eventType,
            Timestamp = timestamp,
            Level = "Information",
            Message = message,
        };
    }

    private static JobLog Progress(Guid jobId, string name, DateTime timestamp, short value)
    {
        return new JobLog
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            EventType = "Progress",
            Name = name,
            Value = value,
            Timestamp = timestamp,
            Level = "Information",
            Message = string.Empty,
        };
    }
}
