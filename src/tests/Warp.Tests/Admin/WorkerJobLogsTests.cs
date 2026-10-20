using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Services;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Admin;

[GenerateDatabaseTests]
public abstract class WorkerJobLogsTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected WorkerJobLogsTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task GetWorkerJobLogs_FirstPage_NewestFirstWithJobTypesAndOlderPageFlagged()
    {
        var workerId = Guid.NewGuid();
        var otherWorkerId = Guid.NewGuid();
        var orderJob = Guid.NewGuid();
        var invoiceJob = Guid.NewGuid();
        var start = DateTime.UtcNow.AddMinutes(-1);
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(NewJob(orderJob, "Acme.PlaceOrder"));
        ctx.Set<Job>().Add(NewJob(invoiceJob, "Acme.SendInvoice"));
        ctx.Set<JobLog>().Add(Log(orderJob, workerId, start.AddSeconds(1), "order 1"));
        ctx.Set<JobLog>().Add(Log(invoiceJob, workerId, start.AddSeconds(2), "invoice 1"));
        ctx.Set<JobLog>().Add(Log(orderJob, workerId, start.AddSeconds(3), "order 2"));
        ctx.Set<JobLog>().Add(Log(orderJob, otherWorkerId, start.AddSeconds(4), "other worker"));
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var page = await svc.GetWorkerJobLogs(workerId, new BaseListRequest { Page = 0, PageSize = 2 });

        page.Items.Select(x => (x.Message, x.JobType)).ShouldBe([("order 2", "Acme.PlaceOrder"), ("invoice 1", "Acme.SendInvoice")]);
        page.HasMore.ShouldBeTrue();
    }

    [TimedFact]
    public async Task GetWorkerJobLogs_LastPage_HasNoMore()
    {
        var workerId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var start = DateTime.UtcNow.AddMinutes(-1);
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(NewJob(jobId, "Acme.PlaceOrder"));
        ctx.Set<JobLog>().Add(Log(jobId, workerId, start.AddSeconds(1), "first"));
        ctx.Set<JobLog>().Add(Log(jobId, workerId, start.AddSeconds(2), "second"));
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var page = await svc.GetWorkerJobLogs(workerId, new BaseListRequest { Page = 1, PageSize = 1 });

        page.Items.ShouldHaveSingleItem().Message.ShouldBe("first");
        page.HasMore.ShouldBeFalse();
    }

    private static Job NewJob(Guid id, string type)
    {
        return new Job
        {
            Id = id,
            Kind = JobKind.Job,
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            Type = type,
        };
    }

    private static JobLog Log(Guid jobId, Guid workerId, DateTime timestamp, string message)
    {
        return new JobLog
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            WorkerId = workerId,
            EventType = "Processing",
            Timestamp = timestamp,
            Level = "Information",
            Message = message,
        };
    }
}
