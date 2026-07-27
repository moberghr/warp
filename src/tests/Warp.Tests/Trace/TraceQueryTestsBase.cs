using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Helper;
using Warp.Core.Services;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Trace;

/// <summary>
/// Unified trace view (§8.28): <see cref="TraceQueryService{TContext}.GetTrace"/> unions the rows Warp already
/// persists — client request, server endpoint call, jobs (tree via SpawnedByJobId), and outbound adapter calls
/// — into one ordered span set for a trace id. Pins that all four sources join on the trace id (including the
/// adapter's 32-hex string form), the job parent link, start ordering, and the error count. Both providers.
/// </summary>
[GenerateDatabaseTests]
public abstract class TraceQueryTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected TraceQueryTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task GetTrace_UnionsClientEndpointJobsAndAdapterCalls()
    {
        var trace = Guid.NewGuid();
        var basis = new DateTime(2026, 7, 27, 9, 0, 0, DateTimeKind.Utc);
        var ctx = _fixture.CreateContext();

        // 1) browser request (client)
        ctx.Set<ClientEventLog>().Add(new ClientEventLog { Application = "shop", Type = ClientEventType.Request, Name = "POST", Url = "/api/checkout", Value = 30, TraceId = trace, Timestamp = basis, ReceivedAt = basis });

        // 2) server endpoint call
        ctx.Set<EndpointCallLog>().Add(new EndpointCallLog { Method = "POST", RouteTemplate = "/api/checkout", Operation = "Checkout", Outcome = AdapterCallOutcome.Success, StatusCode = 200, MachineName = "srv", DurationMs = 28, TraceId = trace, Timestamp = basis.AddMilliseconds(5) });

        // 3) two jobs (child spawned by parent), one failed → drives the error count
        var parentJob = JobHelper.CreateJob(message: "{}", type: "PlaceOrder", scheduleTime: null, queue: "default", parentId: null, state: State.Completed, now: basis.AddMilliseconds(10));
        parentJob.TraceId = trace;
        var childJob = JobHelper.CreateJob(message: "{}", type: "SendEmail", scheduleTime: null, queue: "default", parentId: null, state: State.Failed, now: basis.AddMilliseconds(20));
        childJob.TraceId = trace;
        childJob.SpawnedByJobId = parentJob.Id;
        ctx.Set<Job>().AddRange(parentJob, childJob);

        // 4) an outbound adapter call the job made — trace id stored as the 32-hex string
        ctx.Set<AdapterCallLog>().Add(new AdapterCallLog { AdapterName = "payments", Operation = "Charge", Outcome = AdapterCallOutcome.Success, MachineName = "srv", DurationMs = 15, TraceId = trace.ToString("N"), Timestamp = basis.AddMilliseconds(30) });

        await ctx.SaveChangesAsync(Ct);

        var result = await new TraceQueryService<TestContext>(_fixture.CreateContext()).GetTrace(trace, Ct);

        result.ShouldNotBeNull();
        result!.ClientCount.ShouldBe(1);
        result.EndpointCount.ShouldBe(1);
        result.JobCount.ShouldBe(2);
        result.AdapterCount.ShouldBe(1);          // the 32-hex string join matched
        result.ErrorCount.ShouldBe(1);            // the failed child job
        result.Spans.Count.ShouldBe(5);

        // Ordered by start; the child job links to its spawning parent.
        result.Spans[0].Source.ShouldBe("client");
        result.Spans[1].Source.ShouldBe("endpoint");
        result.Spans.ShouldContain(x => string.Equals(x.Source, "adapter", StringComparison.Ordinal) && string.Equals(x.Name, "payments.Charge", StringComparison.Ordinal));
        result.Spans.Single(x => string.Equals(x.Source, "job", StringComparison.Ordinal) && string.Equals(x.Name, "SendEmail", StringComparison.Ordinal)).ParentId.ShouldBe(parentJob.Id);

        // Per section 8.28 a job span has no clean execution duration, only a create time, so its bar is a
        // placeholder with a null duration, whereas the other sources all carry precise timing.
        result.Spans.Where(x => string.Equals(x.Source, "job", StringComparison.Ordinal)).ShouldAllBe(x => x.DurationMs == null);
        result.Spans.Single(x => string.Equals(x.Source, "adapter", StringComparison.Ordinal)).DurationMs.ShouldNotBeNull();
    }

    [TimedFact]
    public async Task GetTrace_UnknownTrace_ReturnsNull()
    {
        (await new TraceQueryService<TestContext>(_fixture.CreateContext()).GetTrace(Guid.NewGuid(), Ct)).ShouldBeNull();
    }

    [TimedFact]
    public async Task GetTrace_LargeFanOut_CapsPerSourceAndFlagsTruncated()
    {
        var trace = Guid.NewGuid();
        var basis = new DateTime(2026, 7, 27, 9, 0, 0, DateTimeKind.Utc);
        var ctx = _fixture.CreateContext();

        // A batch/message that fans out to more than the per-source cap (500) — all children share the trace id.
        for (var i = 0; i < 600; i++)
        {
            var job = JobHelper.CreateJob(message: "{}", type: "FanOut", scheduleTime: null, queue: "default", parentId: null, state: State.Completed, now: basis.AddMilliseconds(i));
            job.TraceId = trace;
            ctx.Set<Job>().Add(job);
        }

        await ctx.SaveChangesAsync(Ct);

        var result = await new TraceQueryService<TestContext>(_fixture.CreateContext()).GetTrace(trace, Ct);

        result.ShouldNotBeNull();
        result!.JobCount.ShouldBe(500);           // capped, not all 600 loaded
        result.Spans.Count.ShouldBe(500);
        result.IsTruncated.ShouldBeTrue();
    }
}
