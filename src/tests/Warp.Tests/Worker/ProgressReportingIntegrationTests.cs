using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Handlers.Generated;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Worker;

[GenerateDatabaseTests]
public abstract class ProgressReportingIntegrationTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;
    private static readonly Guid ServerId = Guid.NewGuid();
    private static readonly Guid WorkerId = Guid.NewGuid();
    private static readonly string[] DefaultQueues = ["default"];

    protected ProgressReportingIntegrationTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

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

    private WarpWorkerService<TestContext> CreateWorker()
    {
        var services = new ServiceCollection();
        services.AddWarpMediator();
        services.AddLogging();
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());
        services.AddSingleton<CounterService>();
        services.AddSingleton<MultiHandlerCounter>();
        services.AddScoped<Warp.Core.Handlers.JobContext>();
        services.AddScoped<Warp.Core.Handlers.IJobContext>(x => x.GetRequiredService<Warp.Core.Handlers.JobContext>());

        var workerConfig = new OptionsWrapper<WarpServerConfiguration>(new WarpServerConfiguration
        {
            WorkerCount = 1,
            ServerId = ServerId,
            Queues = DefaultQueues,
            EnableHandlerLogging = true,
        });
        services.AddSingleton<IOptions<WarpServerConfiguration>>(workerConfig);
        services.AddSingleton<IOptions<WarpConfiguration>>(workerConfig);

        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var groupConfig = new WorkerGroupConfiguration
        {
            WorkerCount = 1,
            Queues = DefaultQueues,
        };

        return new WarpWorkerService<TestContext>(
            WorkerId,
            scopeFactory,
            new NullLogger<WarpWorkerService<TestContext>>(),
            workerConfig,
            groupConfig,
            TimeProvider.System,
            Warp.Tests.Helpers.TestTasks.QueriesFromScope<TestContext>(scopeFactory),
            Warp.Tests.Helpers.TestTasks.NullTransport,
            Warp.Tests.Helpers.TestTasks.NullSignals);
    }

    private async Task<Guid> EnqueueJob<T>(T payload)
        where T : IJob
    {
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(T).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(payload),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        return jobId;
    }

    [TimedFact]
    public async Task Handler_ReportsProgress_WrittenAsJobLogRows()
    {
        var jobId = await EnqueueJob(new ProgressReportingRequest());
        var worker = CreateWorker();

        await worker.GetAndProcessJob(CancellationToken.None);

        var readCtx = _fixture.CreateContext();
        var progressRows = await readCtx.Set<JobLog>()
            .Where(x => x.JobId == jobId)
            .Where(x => x.EventType == "Progress")
            .OrderBy(x => x.Timestamp)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        progressRows.ShouldNotBeEmpty();
        progressRows.ShouldAllBe(x => string.Equals(x.Name, "download", StringComparison.Ordinal));

        // Three distinct values reported: 25, 50, 100. Final drain captures whatever the
        // dictionary holds when the handler exits, so the final value should be 100.
        progressRows.ShouldContain(x => x.Value == 100);
    }

    [TimedFact]
    public async Task Handler_ReportsMultipleNamedBars_AllPersisted()
    {
        var jobId = await EnqueueJob(new MultiBarProgressRequest());
        var worker = CreateWorker();

        await worker.GetAndProcessJob(CancellationToken.None);

        var readCtx = _fixture.CreateContext();
        var progressRows = await readCtx.Set<JobLog>()
            .Where(x => x.JobId == jobId)
            .Where(x => x.EventType == "Progress")
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        progressRows.Select(x => x.Name).ToHashSet(StringComparer.Ordinal).ShouldBe(["download", "process", "upload"], ignoreOrder: true);
        progressRows.Single(x => string.Equals(x.Name, "download", StringComparison.Ordinal)).Value.ShouldBe((short)100);
        progressRows.Single(x => string.Equals(x.Name, "process", StringComparison.Ordinal)).Value.ShouldBe((short)50);
        progressRows.Single(x => string.Equals(x.Name, "upload", StringComparison.Ordinal)).Value.ShouldBe((short)10);
    }

    [TimedFact]
    public async Task Handler_DoesNotReportProgress_NoProgressRowsCreated()
    {
        var jobId = await EnqueueJob(new NoProgressRequest());
        var worker = CreateWorker();

        await worker.GetAndProcessJob(CancellationToken.None);

        var readCtx = _fixture.CreateContext();
        var progressRows = await readCtx.Set<JobLog>()
            .Where(x => x.JobId == jobId)
            .Where(x => x.EventType == "Progress")
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        progressRows.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task Handler_ReportsSameValueRepeatedly_WritesAtMostOneRowPerBar()
    {
        var jobId = await EnqueueJob(new DedupProgressRequest());
        var worker = CreateWorker();

        await worker.GetAndProcessJob(CancellationToken.None);

        var readCtx = _fixture.CreateContext();
        var progressRows = await readCtx.Set<JobLog>()
            .Where(x => x.JobId == jobId)
            .Where(x => x.EventType == "Progress")
            .Where(x => x.Name == "step")
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        progressRows.Count.ShouldBe(1);
        progressRows[0].Value.ShouldBe((short)42);
    }

    [TimedFact]
    public async Task Handler_ReportsProgress_LeavesOtherEventTypesNameAndValueNull()
    {
        var jobId = await EnqueueJob(new ProgressReportingRequest());
        var worker = CreateWorker();

        await worker.GetAndProcessJob(CancellationToken.None);

        var readCtx = _fixture.CreateContext();
        var nonProgressRows = await readCtx.Set<JobLog>()
            .Where(x => x.JobId == jobId)
            .Where(x => x.EventType != "Progress")
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        nonProgressRows.ShouldNotBeEmpty();
        nonProgressRows.ShouldAllBe(x => x.Name == null && x.Value == null);
    }

    [TimedFact]
    public async Task Handler_ReportsProgressThenThrows_ProgressRowSurvivesInFailedJob()
    {
        var jobId = await EnqueueJob(new ThrowAfterProgressRequest());
        var worker = CreateWorker();

        await worker.GetAndProcessJob(CancellationToken.None);

        var readCtx = _fixture.CreateContext();

        // Job ends Failed (no retry attribute on the handler).
        var job = await readCtx.Set<Job>().FirstAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Failed);

        // The exception-handling branch must have drained the collector via SaveProgressRows.
        var progressRows = await readCtx.Set<JobLog>()
            .Where(x => x.JobId == jobId)
            .Where(x => x.EventType == "Progress")
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        progressRows.Count.ShouldBe(1);
        progressRows[0].Name.ShouldBe("phase");
        progressRows[0].Value.ShouldBe((short)50);
    }
}
