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
using Warp.Core.Services;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.Observability;

[GenerateDatabaseTests]
public abstract class HourlyStatsTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;
    private static readonly Guid ServerId = Guid.NewGuid();
    private static readonly Guid WorkerId = Guid.NewGuid();
    private static readonly string[] DefaultQueues = ["default"];

    protected HourlyStatsTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

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
        services.AddScoped<Warp.Core.IWarpServerContext>(x => new Warp.Tests.Helpers.TestServerContext(x.GetRequiredService<TestContext>()));
        services.AddSingleton<CounterService>();
        services.AddSingleton<MultiHandlerCounter>();
        services.AddScoped<Warp.Core.Handlers.JobContext>();
        services.AddScoped<Warp.Core.Handlers.IJobContext>(x => x.GetRequiredService<Warp.Core.Handlers.JobContext>());

        var workerConfig = new OptionsWrapper<WarpServerConfiguration>(new WarpServerConfiguration
        {
            WorkerCount = 1,
            ServerId = ServerId,
            Queues = DefaultQueues,
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

    [TimedFact]
    public async Task GetAndProcessJob_CompletedJob_IncrementsHourlySucceededStat()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker();

        // Act
        await worker.GetAndProcessJob(CancellationToken.None);

        await Warp.Tests.Helpers.TestTasks.CreateCounterAggregator(_fixture.CreateContext()).AggregateCountersAsync(CancellationToken.None);

        // Assert
        var hourKey = $"stats:succeeded:{DateTime.UtcNow:yyyy-MM-dd-HH}";
        var stat = await _fixture.CreateContext().Set<Statistic>()
            .FindAsync([hourKey], Xunit.TestContext.Current.CancellationToken);

        stat.ShouldNotBeNull();
        stat.Value.ShouldBeGreaterThanOrEqualTo(1);
    }

    [TimedFact]
    public async Task GetAndProcessJob_FailedJob_IncrementsHourlyFailedStat()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(ThrowExceptionRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new ThrowExceptionRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker();

        // Act
        await worker.GetAndProcessJob(CancellationToken.None);

        await Warp.Tests.Helpers.TestTasks.CreateCounterAggregator(_fixture.CreateContext()).AggregateCountersAsync(CancellationToken.None);

        // Assert
        var hourKey = $"stats:failed:{DateTime.UtcNow:yyyy-MM-dd-HH}";
        var stat = await _fixture.CreateContext().Set<Statistic>()
            .FindAsync([hourKey], Xunit.TestContext.Current.CancellationToken);

        stat.ShouldNotBeNull();
        stat.Value.ShouldBeGreaterThanOrEqualTo(1);
    }

    [TimedFact]
    public async Task GetAndProcessJob_MultipleJobs_HourlyStatsAccumulate()
    {
        // Arrange — create 3 jobs
        var ctx = _fixture.CreateContext();
        for (var i = 0; i < 3; i++)
        {
            ctx.Set<Job>().Add(new Job
            {
                Id = Guid.NewGuid(),
                Kind = JobKind.Job,
                CurrentState = State.Enqueued,
                Type = typeof(UnitRequest).AssemblyQualifiedName,
                Message = JsonSerializer.Serialize(new UnitRequest()),
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
            });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker();

        // Act
        await worker.GetAndProcessJob(CancellationToken.None);
        await worker.GetAndProcessJob(CancellationToken.None);
        await worker.GetAndProcessJob(CancellationToken.None);

        await Warp.Tests.Helpers.TestTasks.CreateCounterAggregator(_fixture.CreateContext()).AggregateCountersAsync(CancellationToken.None);

        // Assert
        var hourKey = $"stats:succeeded:{DateTime.UtcNow:yyyy-MM-dd-HH}";
        var stat = await _fixture.CreateContext().Set<Statistic>()
            .FindAsync([hourKey], Xunit.TestContext.Current.CancellationToken);

        stat.ShouldNotBeNull();
        stat.Value.ShouldBe(3);
    }

    [TimedFact]
    public async Task GetWarpStatus_IncludesHistoricalTotals()
    {
        // Arrange — insert stats
        var ctx = _fixture.CreateContext();
        ctx.Set<Statistic>().Add(new Statistic { Key = "stats:succeeded", Value = 42 });
        ctx.Set<Statistic>().Add(new Statistic { Key = "stats:failed", Value = 7 });
        ctx.Set<Statistic>().Add(new Statistic { Key = "stats:deleted", Value = 3 });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var status = await svc.GetWarpStatus();

        // Assert
        status.TotalSucceeded.ShouldBe(42);
        status.TotalFailed.ShouldBe(7);
        status.TotalDeleted.ShouldBe(3);
    }

    [TimedFact]
    public async Task GetCounters_MergesStatisticsAndPendingCounters()
    {
        // Arrange — Statistic table has aggregated values, Counter table has pending writes for
        // the same key. The merged view must sum them.
        var ctx = _fixture.CreateContext();
        ctx.Set<Statistic>().Add(new Statistic { Key = "stats:succeeded", Value = 100 });
        ctx.Set<Statistic>().Add(new Statistic { Key = "stats:failed", Value = 5 });
        ctx.Set<Counter>().Add(new Counter { Key = "stats:succeeded", Value = 3 });
        ctx.Set<Counter>().Add(new Counter { Key = "stats:succeeded", Value = 2 });
        ctx.Set<Counter>().Add(new Counter { Key = "stats:requeued", Value = 4 });
        ctx.Set<Counter>().Add(new Counter { Key = "addon:custom-metric", Value = 17 });

        // Hourly-bucket rows (internal accounting for the chart) — must be hidden from the page.
        ctx.Set<Statistic>().Add(new Statistic { Key = "stats:succeeded:2026-05-07-10", Value = 50 });
        ctx.Set<Counter>().Add(new Counter { Key = "stats:failed:2026-05-07-11", Value = 2 });

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var counters = await svc.GetCounters();

        // Assert — merged + sorted alphabetically
        counters.Count.ShouldBe(4);

        var succeeded = counters.Single(x => string.Equals(x.Key, "stats:succeeded", StringComparison.Ordinal));
        succeeded.Value.ShouldBe(105);

        var failed = counters.Single(x => string.Equals(x.Key, "stats:failed", StringComparison.Ordinal));
        failed.Value.ShouldBe(5);

        var requeued = counters.Single(x => string.Equals(x.Key, "stats:requeued", StringComparison.Ordinal));
        requeued.Value.ShouldBe(4);

        // Addons can write arbitrary keys; they show up too.
        var custom = counters.Single(x => string.Equals(x.Key, "addon:custom-metric", StringComparison.Ordinal));
        custom.Value.ShouldBe(17);

        // Sorted by key
        counters.Select(x => x.Key).ShouldBe(["addon:custom-metric", "stats:failed", "stats:requeued", "stats:succeeded"]);
    }

    [TimedFact]
    public async Task GetCountersHistory_ReturnsHourlyPointsForAllPrefixes()
    {
        // Arrange — insert hourly rows for built-ins + an addon-defined key, plus an out-of-window row.
        var ctx = _fixture.CreateContext();
        var now = DateTime.UtcNow;
        var oneHourAgo = now.AddHours(-1).ToString("yyyy-MM-dd-HH");
        var twoHoursAgo = now.AddHours(-2).ToString("yyyy-MM-dd-HH");
        var outOfWindow = now.AddHours(-100).ToString("yyyy-MM-dd-HH");

        ctx.Set<Statistic>().Add(new Statistic { Key = $"stats:succeeded:{twoHoursAgo}", Value = 10 });
        ctx.Set<Statistic>().Add(new Statistic { Key = $"stats:succeeded:{oneHourAgo}", Value = 20 });
        ctx.Set<Statistic>().Add(new Statistic { Key = $"stats:failed:{oneHourAgo}", Value = 3 });
        ctx.Set<Statistic>().Add(new Statistic { Key = $"stats:requeued:{oneHourAgo}", Value = 1 });
        ctx.Set<Statistic>().Add(new Statistic { Key = $"addon:custom-metric:{oneHourAgo}", Value = 7 });

        // Pending Counter row for the same hour as a Statistic row — must merge.
        ctx.Set<Counter>().Add(new Counter { Key = $"stats:succeeded:{oneHourAgo}", Value = 5 });

        // Out-of-window — must be excluded.
        ctx.Set<Statistic>().Add(new Statistic { Key = $"stats:succeeded:{outOfWindow}", Value = 999 });

        // Rolled-up (non-hourly) — must be excluded; the chart endpoint only returns hourly rows.
        ctx.Set<Statistic>().Add(new Statistic { Key = "stats:succeeded", Value = 9999 });

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var history = await svc.GetCountersHistory(24);

        // Assert — out-of-window and rolled-up rows are excluded
        history.Any(p => p.Value == 999).ShouldBeFalse("out-of-window row must not be returned");
        history.Any(p => p.Value == 9999).ShouldBeFalse("rolled-up row must not be returned");

        // Each hourly row surfaces with its base key (no date suffix) and merged value.
        var succeededRecent = history.Single(p => string.Equals(p.Key, "stats:succeeded", StringComparison.Ordinal) && p.Value == 25);
        succeededRecent.ShouldNotBeNull();

        var succeededOlder = history.Single(p => string.Equals(p.Key, "stats:succeeded", StringComparison.Ordinal) && p.Value == 10);
        succeededOlder.ShouldNotBeNull();

        history.Any(p => string.Equals(p.Key, "stats:failed", StringComparison.Ordinal) && p.Value == 3).ShouldBeTrue();
        history.Any(p => string.Equals(p.Key, "stats:requeued", StringComparison.Ordinal) && p.Value == 1).ShouldBeTrue();

        // Addon-defined hourly metrics surface alongside built-ins.
        history.Any(p => string.Equals(p.Key, "addon:custom-metric", StringComparison.Ordinal) && p.Value == 7).ShouldBeTrue();
    }
}
